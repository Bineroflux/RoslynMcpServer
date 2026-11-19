using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Delete;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;

namespace RoslynMcp.Core.Query.Utilities;

/// <summary>
/// The search behind <c>find_unused_symbols</c>: source types and members that nothing references
/// outside their own declaration.
/// </summary>
/// <remarks>
/// <para>
/// A reference inside the symbol's own declaration (a recursive call, a type naming itself, its
/// own documentation comment) is not a use; every other reference Roslyn finds is, a
/// <c>nameof</c>, a <c>cref</c> and a reference from source-generated code included. A field or
/// auto-property whose only references are writes (assignments, <c>out</c> arguments, object
/// initializers, but not <c>++</c>, <c>+=</c> or <c>ref</c>, which read too) is reported as
/// write-only: deleting it means deleting those writes as well. A type is
/// used when it or one of its members or nested types is referenced from outside the type, so a
/// static class whose extension methods are called is used. A member or nested type of a type that
/// is reported itself is not listed separately (deleting the type removes it).
/// </para>
/// <para>
/// Excluded, because something other than a reference uses them: overrides, interface
/// implementations (implicit, explicit, or on behalf of a derived type), the entry point and the
/// types containing it, operators and conversions, finalizers, static constructors, parameterless
/// constructors (derived constructors, <c>new()</c> constraints and serializers call them
/// implicitly), primary constructors and the properties of positional records, partial methods and
/// properties (the other part is often generated), <c>extern</c> members, enum members, and symbols
/// declared only in generated code. Unless attributed symbols are included: symbols with
/// attributes (test frameworks, serializers, interop, module initializers), types containing
/// one, and fields of types with <c>[StructLayout]</c>, <c>[InlineArray]</c> or
/// <c>[Serializable]</c>. Attributes that only inform the compiler or debugger (nullable
/// analysis, <c>[Obsolete]</c>, <c>[MethodImpl]</c>, <c>[Debugger*]</c>) don't count.
/// </para>
/// <para>
/// For speed, a private symbol is searched only in the declarations of the type it is private to,
/// a file-local one in its file, and one not visible outside its assembly only in its project and
/// the projects its <c>InternalsVisibleTo</c> attributes name. The searches run in parallel. A file
/// compiled by several projects (target frameworks) reports a symbol only when it is unused in
/// all of them.
/// </para>
/// </remarks>
internal sealed class UnusedSymbolSearch
{
    private readonly Solution _solution;
    private readonly Filter _filter;
    private readonly ConcurrentDictionary<ProjectId, Task<IImmutableSet<Document>>> _assemblyDocuments = new();

    public UnusedSymbolSearch(Solution solution, Filter filter)
    {
        _solution = solution;
        _filter = filter;
    }

    /// <summary>Which unused symbols are reported.</summary>
    /// <param name="Kind">Only this kind, or every kind.</param>
    /// <param name="File">Only symbols declared in this file.</param>
    /// <param name="IncludePublic">Also symbols visible outside their assembly.</param>
    /// <param name="IncludeAttributed">Also attributed symbols (and fields of layout types).</param>
    /// <param name="IncludeWriteOnly">Also fields and auto-properties that are only written.</param>
    public sealed record Filter(
        Contracts.Enums.SymbolKind? Kind, FileScope? File, bool IncludePublic, bool IncludeAttributed, bool IncludeWriteOnly);

    /// <summary>
    /// The unused symbols declared in <paramref name="projects"/>, unordered, and the number of
    /// symbols whose references were searched.
    /// </summary>
    public async Task<(IReadOnlyList<UnusedSymbolEntry> Unused, int Checked)> RunAsync(
        IReadOnlyList<Project> projects,
        CancellationToken cancellationToken)
    {
        var candidates = new List<Candidate>();
        foreach (var project in projects)
            candidates.AddRange(await CollectAsync(project, cancellationToken));

        var searched = candidates.Where(c => c.Searched).ToList();
        await Parallel.ForEachAsync(
            searched,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount },
            async (candidate, token) => (candidate.Outside, candidate.OnlyWrites) = await FindOutsideReferencesAsync(candidate, token));

        // A symbol of a file compiled by several projects is reported once, when every one of
        // them leaves it unused; it is write-only when one of them writes it.
        var verdicts = new Dictionary<string, (int Instances, int Unused, int WriteOnly, Candidate First)>(StringComparer.Ordinal);
        foreach (var candidate in candidates.Where(c => c.Reportable))
        {
            var usage = await GetUsageAsync(candidate, cancellationToken);
            var (unused, writeOnly) = (usage == Usage.Unreferenced ? 1 : 0, usage == Usage.WriteOnly ? 1 : 0);
            verdicts[candidate.Key] = verdicts.TryGetValue(candidate.Key, out var verdict)
                ? (verdict.Instances + 1, verdict.Unused + unused, verdict.WriteOnly + writeOnly, verdict.First)
                : (1, unused, writeOnly, candidate);
        }

        var reported = verdicts.Values.Where(v => v.Unused + v.WriteOnly == v.Instances).Select(v => v.First).ToList();
        var writeOnlyKeys = verdicts.Where(v => v.Value.WriteOnly > 0).Select(v => v.Key).ToHashSet(StringComparer.Ordinal);

        // A member or nested type of a reported type goes when the type goes; it is not listed
        // on its own.
        var unusedTypes = reported.Where(c => c.Symbol is INamedTypeSymbol).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var entries = reported
            .Where(c => !ContainingTypes(c.Symbol).Any(type => unusedTypes.Contains(KeyOf(type))))
            .Select(c => CreateEntry(c, writeOnlyKeys.Contains(c.Key)))
            .ToList();

        return (entries, searched.Select(c => c.Key).Distinct(StringComparer.Ordinal).Count());
    }

    private static IEnumerable<INamedTypeSymbol> ContainingTypes(ISymbol symbol)
    {
        for (var type = symbol.ContainingType; type != null; type = type.ContainingType)
            yield return type;
    }

    /// <summary>Identifies a symbol across the projects compiling its file.</summary>
    private static string KeyOf(ISymbol symbol) =>
        $"{symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString()}|{symbol.ContainingAssembly?.Name}";

    private async Task<List<Candidate>> CollectAsync(Project project, CancellationToken cancellationToken)
    {
        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation == null)
            return [];

        var userTrees = new HashSet<SyntaxTree>();
        foreach (var document in project.Documents)
        {
            if (await document.GetSyntaxTreeAsync(cancellationToken) is { } tree && !GeneratedCode.IsGenerated(tree, cancellationToken))
                userTrees.Add(tree);
        }

        var context = new ProjectContext(project, compilation, userTrees, compilation.GetEntryPoint(cancellationToken));
        var candidates = new List<Candidate>();
        var namespaces = new Stack<INamespaceSymbol>([compilation.Assembly.GlobalNamespace]);
        while (namespaces.Count > 0)
        {
            var ns = namespaces.Pop();
            foreach (var nested in ns.GetNamespaceMembers())
                namespaces.Push(nested);
            foreach (var type in ns.GetTypeMembers())
                VisitType(type, [], context, candidates, cancellationToken);
        }

        return candidates;
    }

    /// <summary>
    /// Adds <paramref name="type"/> and its members as candidates. A member or nested type of a
    /// reported type is searched even when it is not reported itself, since its uses make the
    /// type used.
    /// </summary>
    private void VisitType(
        INamedTypeSymbol type,
        IReadOnlyList<Candidate> reportedTypes,
        ProjectContext context,
        List<Candidate> candidates,
        CancellationToken cancellationToken)
    {
        var enclosing = reportedTypes;
        if (IsEligible(type, context))
        {
            var candidate = Add(type, reportedTypes, context, candidates, cancellationToken);
            if (candidate.Reportable)
                enclosing = [.. reportedTypes, candidate];
        }

        foreach (var member in type.GetMembers())
        {
            if (member is INamedTypeSymbol nested)
                VisitType(nested, enclosing, context, candidates, cancellationToken);
            else if (IsEligible(member, context))
                Add(member, enclosing, context, candidates, cancellationToken);
        }
    }

    private Candidate Add(
        ISymbol symbol,
        IReadOnlyList<Candidate> reportedTypes,
        ProjectContext context,
        List<Candidate> candidates,
        CancellationToken cancellationToken)
    {
        var reportable = (_filter.Kind is not { } kind || SymbolKindMapper.Map(symbol) == kind) &&
            (_filter.File is null || symbol.Locations.Any(l => l.IsInSource && _filter.File.ContainsFile(l.SourceTree?.FilePath)));
        var declarations = SymbolDeclarations.Nodes(symbol, cancellationToken);
        var candidate = new Candidate(symbol, context, declarations)
        {
            Reportable = reportable,
            Searched = reportable || reportedTypes.Count > 0,
            CanBeWriteOnly = _filter.IncludeWriteOnly && CanBeWriteOnly(symbol, declarations)
        };

        foreach (var type in reportedTypes)
            type.Dependents.Add(candidate);

        candidates.Add(candidate);
        return candidate;
    }

    /// <summary>Whether <paramref name="symbol"/> is a symbol this search reports when unused.</summary>
    private bool IsEligible(ISymbol symbol, ProjectContext context)
    {
        if (symbol.IsImplicitlyDeclared || !symbol.DeclaringSyntaxReferences.Any(r => context.UserTrees.Contains(r.SyntaxTree)))
            return false;

        if (!_filter.IncludeAttributed && HasAttributes(symbol))
            return false;

        var eligible = symbol switch
        {
            INamedTypeSymbol type =>
                type.TypeKind is TypeKind.Class or TypeKind.Struct or TypeKind.Interface or TypeKind.Enum or TypeKind.Delegate &&
                !Contains(type, context.EntryPoint) &&
                (_filter.IncludeAttributed || !HasAttributedMember(type)),
            IMethodSymbol method => method.MethodKind switch
            {
                MethodKind.Ordinary =>
                    !SymbolEqualityComparer.Default.Equals(method, context.EntryPoint) &&
                    !method.IsOverride && !method.IsExtern && !IsPartial(method) && !ImplementsInterfaceMember(method),
                MethodKind.Constructor => method.Parameters.Length > 0 && !method.IsExtern && !IsPrimaryConstructor(method),
                _ => false
            },
            IPropertySymbol property =>
                !property.IsOverride && property.ExplicitInterfaceImplementations.IsEmpty && !property.IsExtern &&
                property.PartialDefinitionPart == null && property.PartialImplementationPart == null &&
                !property.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is ParameterSyntax) &&
                !ImplementsInterfaceMember(property),
            IFieldSymbol field =>
                field.ContainingType.TypeKind != TypeKind.Enum &&
                (_filter.IncludeAttributed || !field.ContainingType.GetAttributes().Any(IsLayoutAttribute)),
            IEventSymbol @event =>
                !@event.IsOverride && @event.ExplicitInterfaceImplementations.IsEmpty && !@event.IsExtern &&
                !ImplementsInterfaceMember(@event),
            _ => false
        };

        return eligible && (_filter.IncludePublic || !SymbolVisibility.IsVisibleOutsideAssembly(symbol));
    }

    /// <summary>
    /// Whether <paramref name="symbol"/> holds a value that writes alone may leave unread: a
    /// field that is no constant, or an auto-property (whose accessors have no code of their own
    /// that a write would run).
    /// </summary>
    private static bool CanBeWriteOnly(ISymbol symbol, IReadOnlyList<SyntaxNode> declarations) => symbol switch
    {
        IFieldSymbol field => !field.IsConst,
        IPropertySymbol { IsIndexer: false, SetMethod: not null } => declarations.All(d =>
            d is PropertyDeclarationSyntax { ExpressionBody: null, AccessorList: { } accessors } &&
            accessors.Accessors.All(a => a.Body == null && a.ExpressionBody == null)),
        _ => false
    };

    private static bool IsPartial(IMethodSymbol method) =>
        method.IsPartialDefinition || method.PartialDefinitionPart != null || method.PartialImplementationPart != null;

    private static bool IsPrimaryConstructor(IMethodSymbol constructor) =>
        constructor.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is TypeDeclarationSyntax);

    private static bool Contains(INamedTypeSymbol type, IMethodSymbol? member)
    {
        for (var containing = member?.ContainingType; containing != null; containing = containing.ContainingType)
        {
            if (SymbolEqualityComparer.Default.Equals(containing, type))
                return true;
        }

        return false;
    }

    /// <summary>Whether <paramref name="member"/> implements a member of an interface its own type lists.</summary>
    private static bool ImplementsInterfaceMember(ISymbol member) =>
        member.ContainingType.AllInterfaces
            .SelectMany(i => i.GetMembers(member.Name))
            .Any(m => SymbolEqualityComparer.Default.Equals(member.ContainingType.FindImplementationForInterfaceMember(m), member));

    private static bool HasAttributes(ISymbol symbol) =>
        symbol.GetAttributes().Any(a => !IsInert(a)) ||
        (symbol is IMethodSymbol method && method.GetReturnTypeAttributes().Any(a => !IsInert(a)));

    private static bool HasAttributedMember(INamedTypeSymbol type) =>
        type.GetMembers().Any(member => !member.IsImplicitlyDeclared &&
            (HasAttributes(member) || (member is INamedTypeSymbol nested && HasAttributedMember(nested))));

    /// <summary>
    /// Whether an attribute only informs the compiler or debugger, so it does not suggest that
    /// something outside the code uses the symbol.
    /// </summary>
    private static bool IsInert(AttributeData attribute)
    {
        if (attribute.AttributeClass is not { } type)
            return false;

        return type.ContainingNamespace?.ToDisplayString() switch
        {
            "System.Diagnostics.CodeAnalysis" =>
                type.Name is not ("DynamicallyAccessedMembersAttribute" or "DynamicDependencyAttribute"),
            "System.Diagnostics" => type.Name.StartsWith("Debugger", StringComparison.Ordinal),
            "System" => type.Name == "ObsoleteAttribute",
            "System.Runtime.CompilerServices" => type.Name == "MethodImplAttribute",
            _ => false
        };
    }

    private static bool IsLayoutAttribute(AttributeData attribute) =>
        attribute.AttributeClass?.ToDisplayString() is
            "System.Runtime.InteropServices.StructLayoutAttribute" or
            "System.Runtime.CompilerServices.InlineArrayAttribute" or
            "System.SerializableAttribute";

    /// <summary>
    /// The references to the candidate outside its own declaration, and whether every one of
    /// them only writes it. The name of an explicit interface implementation, which Roslyn
    /// reports as a reference to the interface member, is a declaration, not a use.
    /// </summary>
    private async Task<(IReadOnlyList<Location> Outside, bool OnlyWrites)> FindOutsideReferencesAsync(Candidate candidate, CancellationToken cancellationToken)
    {
        var documents = await SearchDocumentsAsync(candidate, cancellationToken);
        var referenced = documents is null
            ? await SymbolFinder.FindReferencesAsync(candidate.Symbol, _solution, cancellationToken)
            : await SymbolFinder.FindReferencesAsync(candidate.Symbol, _solution, documents, cancellationToken);

        var outside = new List<Location>();
        var onlyWrites = true;
        foreach (var reference in referenced.SelectMany(r => r.Locations))
        {
            var location = reference.Location;
            if (location.IsInSource &&
                !SymbolDeclarations.Contains(candidate.Declarations, location) &&
                !IsExplicitImplementationName(location, cancellationToken))
            {
                outside.Add(location);
                onlyWrites &= IsWriteOnly(reference, cancellationToken);
            }
        }

        return (outside, onlyWrites && outside.Count > 0);
    }

    /// <summary>
    /// Whether a reference only writes the symbol: the target of a simple assignment (also in an
    /// object initializer, through <c>this.</c> or <c>?.</c>, or as an element of a deconstructing
    /// tuple) or an <c>out</c> argument. A compound assignment, <c>++</c>, a <c>ref</c> argument,
    /// <c>nameof</c>, a cref, a member access on it and an implicit reference read it (or name it).
    /// </summary>
    private static bool IsWriteOnly(ReferenceLocation reference, CancellationToken cancellationToken)
    {
        if (reference.IsImplicit ||
            reference.Location.SourceTree!.GetRoot(cancellationToken).FindToken(reference.Location.SourceSpan.Start).Parent is not SimpleNameSyntax name)
        {
            return false;
        }

        SyntaxNode target = name.Parent switch
        {
            MemberAccessExpressionSyntax access when access.Name == name => access,
            MemberBindingExpressionSyntax binding => binding,
            _ => name
        };
        while (target.Parent is ParenthesizedExpressionSyntax)
            target = target.Parent;

        // An element of a tuple that a deconstruction assigns to.
        while (target.Parent is ArgumentSyntax { RefKindKeyword.RawKind: 0, Parent: TupleExpressionSyntax tuple })
            target = tuple;

        return target.Parent switch
        {
            AssignmentExpressionSyntax assignment => assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) && assignment.Left == target,
            ArgumentSyntax argument => argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword),
            _ => false
        };
    }

    private static bool IsExplicitImplementationName(Location location, CancellationToken cancellationToken)
    {
        var token = location.SourceTree!.GetRoot(cancellationToken).FindToken(location.SourceSpan.Start);
        return token.Parent switch
        {
            MethodDeclarationSyntax { ExplicitInterfaceSpecifier: not null } method => method.Identifier == token,
            PropertyDeclarationSyntax { ExplicitInterfaceSpecifier: not null } property => property.Identifier == token,
            EventDeclarationSyntax { ExplicitInterfaceSpecifier: not null } @event => @event.Identifier == token,
            IndexerDeclarationSyntax { ExplicitInterfaceSpecifier: not null } indexer => indexer.ThisKeyword == token,
            _ => false
        };
    }

    /// <summary>
    /// The documents that can reference the candidate, or null for the whole solution: the
    /// declarations of the type it is private to, the file of a file-local type, or the
    /// candidate's project and its friends when it is not visible outside its assembly.
    /// </summary>
    private async Task<IImmutableSet<Document>?> SearchDocumentsAsync(Candidate candidate, CancellationToken cancellationToken)
    {
        var symbol = candidate.Symbol;
        var scopeType = SymbolVisibility.FileLocalType(symbol) ?? SymbolVisibility.PrivateDomain(symbol);
        if (scopeType != null)
        {
            var documents = scopeType.DeclaringSyntaxReferences.Select(r => _solution.GetDocument(r.SyntaxTree)).ToList();

            // A part in generated code is not a document of the solution; search the whole project.
            if (documents.All(d => d != null))
                return ImmutableHashSet.CreateRange(documents.OfType<Document>());
        }
        else if (SymbolVisibility.IsVisibleOutsideAssembly(symbol))
        {
            return null;
        }

        var project = candidate.Context.Project;
        return await _assemblyDocuments.GetOrAdd(
            project.Id,
            _ => AssemblyDocumentsAsync(project, candidate.Context.Compilation, cancellationToken));
    }

    /// <summary>
    /// The documents of <paramref name="project"/> and of the projects that depend on it and are
    /// named by its <c>InternalsVisibleTo</c> attributes, source-generated ones included.
    /// </summary>
    private async Task<IImmutableSet<Document>> AssemblyDocumentsAsync(
        Project project,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        var friends = compilation.Assembly.GetAttributes()
            .Where(a => a.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.InternalsVisibleToAttribute")
            .Select(a => a.ConstructorArguments.FirstOrDefault().Value as string)
            .OfType<string>()
            .Select(name => name.Split(',')[0].Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var projects = new List<Project> { project };
        if (friends.Count > 0)
        {
            projects.AddRange(_solution.GetProjectDependencyGraph()
                .GetProjectsThatTransitivelyDependOnThisProject(project.Id)
                .Select(_solution.GetProject)
                .OfType<Project>()
                .Where(p => friends.Contains(p.AssemblyName)));
        }

        var documents = ImmutableHashSet.CreateBuilder<Document>();
        foreach (var searched in projects)
        {
            documents.UnionWith(searched.Documents);
            documents.UnionWith(await searched.GetSourceGeneratedDocumentsAsync(cancellationToken));
        }

        return documents.ToImmutable();
    }

    /// <summary>How a candidate is used outside its own declaration.</summary>
    private enum Usage
    {
        Used,
        Unreferenced,
        WriteOnly
    }

    private async Task<Usage> GetUsageAsync(Candidate candidate, CancellationToken cancellationToken)
    {
        var usage = candidate.Outside!.Count == 0 ? Usage.Unreferenced
            : candidate.CanBeWriteOnly && candidate.OnlyWrites ? Usage.WriteOnly
            : Usage.Used;
        if (usage == Usage.Used)
            return usage;

        if (candidate.Symbol is INamedTypeSymbol)
        {
            return candidate.Dependents.All(d => d.Outside!.All(l => SymbolDeclarations.Contains(candidate.Declarations, l)))
                ? usage
                : Usage.Used;
        }

        // A public member of a class that can be derived from may implement an interface on
        // behalf of a derived class that lists it; only now is that worth a search.
        if (candidate.Symbol is { IsStatic: false, DeclaredAccessibility: Accessibility.Public, ContainingType: { TypeKind: TypeKind.Class, IsSealed: false } } and not IMethodSymbol { MethodKind: MethodKind.Constructor })
        {
            var implemented = await SymbolFinder.FindImplementedInterfaceMembersAsync(candidate.Symbol, _solution, cancellationToken: cancellationToken);
            return implemented.Any() ? Usage.Used : usage;
        }

        return usage;
    }

    private UnusedSymbolEntry CreateEntry(Candidate candidate, bool writeOnly)
    {
        var symbol = candidate.Symbol;
        var location = symbol.Locations
            .Where(l => l.IsInSource && candidate.Context.UserTrees.Contains(l.SourceTree!))
            .Where(l => _filter.File is null || _filter.File.ContainsFile(l.SourceTree?.FilePath))
            .Select(l => (Location: l, Span: l.GetLineSpan()))
            .OrderBy(l => l.Span.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.Location.SourceSpan.Start)
            .First();

        return new UnusedSymbolEntry
        {
            Name = symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ? symbol.ContainingType.Name : symbol.Name,
            FullyQualifiedName = symbol.ToDisplayString(),
            Kind = SymbolKindMapper.Map(symbol),
            ContainingType = symbol.ContainingType?.ToDisplayString(),
            Accessibility = AccessibilityText(symbol.DeclaredAccessibility),
            File = location.Span.Path,
            Line = location.Span.StartLinePosition.Line + 1,
            Column = location.Span.StartLinePosition.Character + 1,
            Reason = writeOnly ? UnusedSymbolEntry.WriteOnly : UnusedSymbolEntry.Unreferenced
        };
    }

    private static string AccessibilityText(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Protected => "protected",
        Accessibility.Private => "private",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        _ => accessibility.ToString().ToLowerInvariant()
    };

    private sealed record ProjectContext(
        Project Project,
        Compilation Compilation,
        HashSet<SyntaxTree> UserTrees,
        IMethodSymbol? EntryPoint);

    private sealed class Candidate(ISymbol symbol, ProjectContext context, IReadOnlyList<SyntaxNode> declarations)
    {
        public ISymbol Symbol { get; } = symbol;

        public ProjectContext Context { get; } = context;

        /// <summary>The declaration nodes; a reference inside them is not a use.</summary>
        public IReadOnlyList<SyntaxNode> Declarations { get; } = declarations;

        /// <summary>Identifies the symbol across the projects compiling its file.</summary>
        public string Key { get; } = KeyOf(symbol);

        /// <summary>Whether it passes the kind and file filters.</summary>
        public required bool Reportable { get; init; }

        /// <summary>Whether its references are searched.</summary>
        public required bool Searched { get; init; }

        /// <summary>For a reported type: its members and nested types, whose uses are its uses.</summary>
        public List<Candidate> Dependents { get; } = [];

        /// <summary>Whether writes alone may leave it unread (a field or an auto-property).</summary>
        public required bool CanBeWriteOnly { get; init; }

        /// <summary>The references outside its declaration, once searched.</summary>
        public IReadOnlyList<Location>? Outside { get; set; }

        /// <summary>Whether <see cref="Outside"/> has references, and all of them only write it.</summary>
        public bool OnlyWrites { get; set; }
    }
}
