using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Signature;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Delete;

/// <summary>
/// Deletes the declaration of a type or member when nothing outside it references it, and fails
/// with the references otherwise.
/// </summary>
/// <remarks>
/// <para>
/// Every reference Roslyn finds blocks the deletion, <c>nameof</c> and documentation-comment
/// <c>cref</c>s included (a cref is reported as such: deleting the symbol would leave a broken
/// cref, which is only a warning). References inside the deleted declaration don't count; for a
/// type that covers all its partial parts, and a reference to one of its members from outside
/// blocks it. A constructor that a derived class calls implicitly (no <c>: base(...)</c>) is
/// referenced there.
/// </para>
/// <para>
/// Refused: members that override or implement another member or are overridden or implemented
/// (deleting part of a family breaks the rest), and symbols whose uses are implicit or whose
/// deletion changes other declarations: the entry point, static constructors, finalizers,
/// implicit conversions, primary constructors, positional record properties, enum members
/// whose deletion renumbers the members after them, and symbols declared in metadata or
/// generated code.
/// </para>
/// <para>
/// All partial parts go; of <c>int a, b;</c> only the variable. Documentation and other comments
/// directly above a declaration go with it (see <see cref="DeclarationRemoval.Span"/>). A file
/// left without declarations (a type that was alone in it) is deleted. A using directive that
/// only the deleted code used goes too (the compiler reports it unnecessary, CS8019, afterwards
/// but not before; usings that were unused already stay). Compiler errors the
/// deletion introduces anyway in the touched files (or, for a constructor, in the files using
/// its type, such as a <c>new()</c> constraint) are returned as warnings.
/// </para>
/// </remarks>
public sealed class SafeDeleteOperation : RefactoringOperationBase<SafeDeleteParams>
{
    private const int ListedReferences = 20;
    private const int ReferencesInMessage = 5;

    /// <summary>
    /// Creates a new safe delete operation.
    /// </summary>
    public SafeDeleteOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(SafeDeleteParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.SymbolName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "symbolName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, PathResolver.GetCSharpFileRejectionReason(@params.SourceFile));

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (@params.Line is < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column is < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Column number must be >= 1.");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        SafeDeleteParams @params,
        CancellationToken cancellationToken)
    {
        var solution = Context.Solution;
        var symbol = Normalize(await ResolveAsync(@params, cancellationToken));
        var declarations = SymbolDeclarations.Nodes(symbol, cancellationToken);
        var documents = DeclaringDocuments(symbol, declarations, solution, cancellationToken);
        var compilation = await documents[0].Project.GetCompilationAsync(cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not compile the declaring project.");

        CheckDeletable(symbol, compilation, cancellationToken);
        await CheckFamilyAsync(symbol, compilation, solution, cancellationToken);
        await CheckEnumValuesAsync(symbol, documents[0], cancellationToken);

        var references = await FindReferencesAsync(symbol, declarations, solution, cancellationToken);
        if (references.Count > 0)
            throw StillReferenced(symbol, references);

        var (newSolution, edits) = await RemoveAsync(solution, declarations, cancellationToken);

        // Usings only the deleted code needed go too; those that were unused before stay.
        var (cleaned, usingsRemoved) = await NewlyReportedUsings.RemoveAsync(
            solution,
            newSolution,
            edits.Where(e => !e.DeletesFile).Select(e => e.Document.Id),
            [NewlyReportedUsings.UnnecessaryUsing],
            includeGlobal: false,
            cancellationToken);
        newSolution = cleaned;
        var warnings = await FindWarningsAsync(symbol, solution, newSolution, edits, cancellationToken);

        if (@params.Preview)
        {
            await PreviewCommitAsync(newSolution, cancellationToken);
            return new RefactoringResult
            {
                Success = true,
                OperationId = operationId,
                Preview = true,
                PendingChanges = [.. CreatePendingChanges(symbol, edits), .. usingsRemoved.Select(UsingRemoval)],
                UsingDirectivesRemoved = usingsRemoved.Count,
                Warnings = warnings
            };
        }

        var commit = await CommitChangesAsync(newSolution, cancellationToken);
        var location = declarations[0].GetLocation().GetLineSpan();

        return new RefactoringResult
        {
            Success = true,
            OperationId = operationId,
            Changes = new FileChanges
            {
                FilesModified = commit.FilesModified.Distinct(PathResolver.PathComparer).ToList(),
                FilesCreated = commit.FilesCreated,
                FilesDeleted = commit.FilesDeleted.Distinct(PathResolver.PathComparer).ToList()
            },
            Symbol = new Contracts.Models.SymbolInfo
            {
                Name = DisplayName(symbol),
                FullyQualifiedName = symbol.ToDisplayString(),
                Kind = SymbolKindMapper.Map(symbol),
                PreviousLocation = new SymbolLocation
                {
                    File = location.Path,
                    Line = location.StartLinePosition.Line + 1,
                    Column = location.StartLinePosition.Character + 1
                }
            },
            UsingDirectivesRemoved = usingsRemoved.Count,
            Warnings = warnings
        };
    }

    private static PendingChange UsingRemoval(NewlyReportedUsings.Removed removed) => new()
    {
        File = removed.File,
        ChangeType = ChangeKind.Modify,
        Description = $"Remove '{removed.Directive}', which only the deleted code used",
        BeforeSnippet = removed.Directive,
        StartLine = removed.Line,
        EndLine = removed.Line
    };

    /// <summary>
    /// The symbol the request names. A constructor or finalizer is named by its type's name and
    /// picked by its line (any line from its first modifier to its name); everything else goes
    /// through the general resolver, by position or by name.
    /// </summary>
    private async Task<ISymbol> ResolveAsync(SafeDeleteParams @params, CancellationToken cancellationToken)
    {
        var name = @params.SymbolName.Trim();
        if (@params.Line is { } line)
        {
            var document = GetDocumentOrThrow(@params.SourceFile);
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var model = await document.GetSemanticModelAsync(cancellationToken);
            var text = await document.GetTextAsync(cancellationToken);
            if (root != null && model != null && line <= text.Lines.Count)
            {
                var lineNumber = line - 1;
                var constructors = root.DescendantNodes(text.Lines[lineNumber].Span)
                    .OfType<BaseMethodDeclarationSyntax>()
                    .Where(d => d switch
                    {
                        ConstructorDeclarationSyntax c => c.Identifier.ValueText == name && Covers(c, c.Identifier),
                        DestructorDeclarationSyntax f => f.Identifier.ValueText == name && Covers(f, f.Identifier),
                        _ => false
                    })
                    .ToList();
                if (constructors.Count == 1)
                    return model.GetDeclaredSymbol(constructors[0], cancellationToken)!;

                bool Covers(SyntaxNode declaration, SyntaxToken identifier) =>
                    text.Lines.GetLineFromPosition(declaration.SpanStart).LineNumber <= lineNumber &&
                    lineNumber <= text.Lines.GetLineFromPosition(identifier.SpanStart).LineNumber;
            }
        }

        var resolved = await Context.CreateGeneralSymbolResolver().ResolveSymbolAsync(
            @params.SourceFile, name, @params.Line, @params.Column, cancellationToken);
        if (resolved.IsCandidate)
        {
            throw new RefactoringException(
                ErrorCodes.SymbolNotFound,
                $"'{name}' does not bind to one symbol (the file has compile errors); fix them before deleting it.");
        }

        return resolved.Symbol;
    }

    /// <summary>
    /// The declaration the request means: a property or event for an accessor, the definition of
    /// a constructed, reduced or partial member.
    /// </summary>
    private static ISymbol Normalize(ISymbol symbol)
    {
        if (symbol is IMethodSymbol { AssociatedSymbol: IPropertySymbol or IEventSymbol } accessor)
            symbol = accessor.AssociatedSymbol!;
        if (symbol is IMethodSymbol { ReducedFrom: { } reducedFrom })
            symbol = reducedFrom;

        return symbol.OriginalDefinition switch
        {
            IMethodSymbol { PartialDefinitionPart: { } definition } => definition,
            IPropertySymbol { PartialDefinitionPart: { } definition } => definition,
            var definition => definition
        };
    }

    /// <summary>The documents of the declarations, which must all be the user's own source.</summary>
    private static List<Document> DeclaringDocuments(
        ISymbol symbol,
        IReadOnlyList<SyntaxNode> declarations,
        Solution solution,
        CancellationToken cancellationToken)
    {
        if (symbol is not (INamedTypeSymbol or IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol) ||
            symbol is INamedTypeSymbol { TypeKind: not (TypeKind.Class or TypeKind.Struct or TypeKind.Interface or TypeKind.Enum or TypeKind.Delegate) } ||
            symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction })
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"'{symbol.ToDisplayString()}' is a {symbol.Kind.ToString().ToLowerInvariant()}; safe_delete deletes types and their members.");
        }

        if (symbol.IsImplicitlyDeclared || declarations.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.CannotDelete,
                symbol.IsImplicitlyDeclared
                    ? $"'{symbol.ToDisplayString()}' is declared implicitly by the compiler."
                    : $"'{symbol.ToDisplayString()}' is declared in {symbol.ContainingAssembly?.Name ?? "metadata"}, not in source.");
        }

        var documents = new List<Document>();
        foreach (var declaration in declarations)
        {
            var document = solution.GetDocument(declaration.SyntaxTree);
            if (document == null || GeneratedCode.IsGenerated(declaration.SyntaxTree, cancellationToken))
            {
                throw new RefactoringException(
                    ErrorCodes.CannotDelete,
                    $"'{symbol.ToDisplayString()}' is declared in generated code ({declaration.SyntaxTree.FilePath}).");
            }

            documents.Add(document);
        }

        return documents;
    }

    /// <summary>Refuses symbols whose uses are implicit or whose deletion changes other declarations.</summary>
    private static void CheckDeletable(ISymbol symbol, Compilation compilation, CancellationToken cancellationToken)
    {
        var name = symbol.ToDisplayString();
        var reason = symbol switch
        {
            IMethodSymbol { MethodKind: MethodKind.StaticConstructor } =>
                "a static constructor runs implicitly, so nothing references it",
            IMethodSymbol { MethodKind: MethodKind.Destructor } =>
                "a finalizer runs implicitly, so nothing references it",
            IMethodSymbol { MethodKind: MethodKind.Conversion, Name: WellKnownMemberNames.ImplicitConversionName } =>
                "an implicit conversion is used without being named, so its uses cannot all be found",
            IMethodSymbol { MethodKind: MethodKind.Constructor } constructor when
                constructor.DeclaringSyntaxReferences.Any(r => r.GetSyntax(cancellationToken) is TypeDeclarationSyntax) =>
                "it is the primary constructor; remove the type's parameter list instead",
            IPropertySymbol property when
                property.DeclaringSyntaxReferences.Any(r => r.GetSyntax(cancellationToken) is ParameterSyntax) =>
                "it is declared by a record parameter; remove the parameter instead",
            IMethodSymbol method when SymbolEqualityComparer.Default.Equals(method, compilation.GetEntryPoint(cancellationToken)) =>
                "it is the program's entry point",
            _ => null
        };

        if (reason != null)
            throw new RefactoringException(ErrorCodes.CannotDelete, $"'{name}' cannot be deleted: {reason}.");
    }

    /// <summary>Refuses a member of an override / implementation family, naming the other members.</summary>
    private static async Task CheckFamilyAsync(ISymbol symbol, Compilation compilation, Solution solution, CancellationToken cancellationToken)
    {
        if (symbol is not (IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation } or IPropertySymbol or IEventSymbol))
            return;

        var related = await MemberFamily.FindRelatedAsync(symbol, compilation, solution, cancellationToken);
        if (related.Count == 0)
            return;

        var described = related
            .Select(member => $"{Relation(symbol, member)} '{member.ToDisplayString()}'")
            .Order(StringComparer.Ordinal)
            .ToList();

        throw new RefactoringException(
            ErrorCodes.MemberInFamily,
            $"'{symbol.ToDisplayString()}' cannot be deleted alone: it {string.Join(", ", described)}. " +
            "Deleting one member of an override / implementation family breaks the others.",
            new Dictionary<string, object> { ["family"] = related.Select(m => m.ToDisplayString()).Order(StringComparer.Ordinal).ToList() });
    }

    private static string Relation(ISymbol symbol, ISymbol member)
    {
        var symbolInInterface = symbol.ContainingType.TypeKind == TypeKind.Interface;
        var memberInInterface = member.ContainingType.TypeKind == TypeKind.Interface;
        if (memberInInterface && !symbolInInterface)
            return "implements";
        if (symbolInInterface && !memberInInterface)
            return "is implemented by";
        if (Overrides(symbol, member))
            return "overrides";
        if (Overrides(member, symbol))
            return "is overridden by";
        return "belongs to the same family as";
    }

    private static bool Overrides(ISymbol member, ISymbol candidateBase)
    {
        for (var current = Overridden(member); current != null; current = Overridden(current))
        {
            if (current.GetDocumentationCommentId() == candidateBase.GetDocumentationCommentId())
                return true;
        }

        return false;

        static ISymbol? Overridden(ISymbol symbol) => symbol switch
        {
            IMethodSymbol method => method.OverriddenMethod,
            IPropertySymbol property => property.OverriddenProperty,
            IEventSymbol @event => @event.OverriddenEvent,
            _ => null
        };
    }

    /// <summary>
    /// Refuses an enum member whose deletion would change the value of the members after it,
    /// which take theirs implicitly from it.
    /// </summary>
    private static async Task CheckEnumValuesAsync(ISymbol symbol, Document document, CancellationToken cancellationToken)
    {
        if (symbol is not IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } field)
            return;

        var declaration = (EnumMemberDeclarationSyntax)SymbolDeclarations.Nodes(field, cancellationToken)[0];
        var members = ((EnumDeclarationSyntax)declaration.Parent!).Members;
        var index = members.IndexOf(declaration);
        if (index == members.Count - 1 || members[index + 1].EqualsValue != null)
            return;

        var model = await document.GetSemanticModelAsync(cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not analyze file.");
        var next = (IFieldSymbol)model.GetDeclaredSymbol(members[index + 1], cancellationToken)!;
        var previousValue = index == 0
            ? -1m
            : System.Convert.ToDecimal(((IFieldSymbol)model.GetDeclaredSymbol(members[index - 1], cancellationToken)!).ConstantValue);
        if (System.Convert.ToDecimal(next.ConstantValue) == previousValue + 1)
            return;

        var shifted = members.Skip(index + 1).TakeWhile(m => m.EqualsValue == null).Select(m => m.Identifier.ValueText);
        throw new RefactoringException(
            ErrorCodes.CannotDelete,
            $"'{field.ToDisplayString()}' cannot be deleted: {string.Join(", ", shifted)} would get other values. " +
            "Give the following member an explicit value first.");
    }

    /// <summary>
    /// The references that block the deletion: every reference to the symbol (to a type, also to
    /// its members) outside the deleted declarations, plus the derived-class constructors that
    /// call a deleted constructor implicitly.
    /// </summary>
    private static async Task<List<BlockingReference>> FindReferencesAsync(
        ISymbol symbol,
        IReadOnlyList<SyntaxNode> declarations,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var deleted = new HashSet<ISymbol>(DeclarationIdentity.Instance);
        var searched = new List<ISymbol>();
        AddDeleted(symbol, deleted, searched);

        var found = new ConcurrentBag<ReferencedSymbol>();
        await Parallel.ForEachAsync(searched, cancellationToken, async (s, token) =>
        {
            foreach (var referenced in await SymbolFinder.FindReferencesAsync(s, solution, token))
                found.Add(referenced);
        });

        var references = new List<BlockingReference>();
        var seen = new HashSet<(string, TextSpan)>();
        foreach (var referenced in found.Where(r => deleted.Contains(r.Definition)))
        {
            foreach (var location in referenced.Locations)
            {
                if (!location.Location.IsInSource || SymbolDeclarations.Contains(declarations, location.Location))
                    continue;

                var path = location.Document.FilePath ?? location.Location.SourceTree?.FilePath ?? "";
                if (seen.Add((path, location.Location.SourceSpan)))
                    references.Add(BlockingReference.Create(path, location.Location, Kind(location), cancellationToken));
            }
        }

        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor)
            references.AddRange(await FindImplicitCallsAsync(constructor, solution, cancellationToken));

        return [.. references
            .DistinctBy(r => (r.File, r.Line, r.Column))
            .OrderBy(r => r.File, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Line).ThenBy(r => r.Column)];
    }

    /// <summary>
    /// Adds what deleting <paramref name="symbol"/> removes to <paramref name="deleted"/>, and to
    /// <paramref name="searched"/> what must be searched: the symbol and, for a type, its members
    /// and nested types (accessors are found through their property or event).
    /// </summary>
    private static void AddDeleted(ISymbol symbol, HashSet<ISymbol> deleted, List<ISymbol> searched)
    {
        deleted.Add(symbol);
        if (!symbol.IsImplicitlyDeclared && symbol is not IMethodSymbol { AssociatedSymbol: not null })
            searched.Add(symbol);

        switch (symbol)
        {
            case INamedTypeSymbol type:
                foreach (var member in type.GetMembers())
                    AddDeleted(member, deleted, searched);
                break;
            case IPropertySymbol property:
                foreach (var accessor in new[] { property.GetMethod, property.SetMethod }.OfType<IMethodSymbol>())
                    deleted.Add(accessor);
                break;
            case IEventSymbol @event:
                foreach (var accessor in new[] { @event.AddMethod, @event.RemoveMethod, @event.RaiseMethod }.OfType<IMethodSymbol>())
                    deleted.Add(accessor);
                break;
        }
    }

    private static string Kind(ReferenceLocation location)
    {
        if (location.IsImplicit)
            return "implicit";

        var root = location.Location.SourceTree!.GetRoot();
        var node = root.FindNode(location.Location.SourceSpan, findInsideTrivia: true, getInnermostNodeForTie: true);
        if (node.AncestorsAndSelf().Any(n => n is CrefSyntax))
            return "cref";

        return node.Ancestors().OfType<InvocationExpressionSyntax>()
            .Any(i => i.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" } && i.ArgumentList.Span.Contains(location.Location.SourceSpan))
            ? "nameof"
            : "reference";
    }

    /// <summary>
    /// The constructors of derived classes that call <paramref name="constructor"/> without
    /// naming it: an implicit default constructor, or one without a constructor initializer.
    /// </summary>
    private static async Task<List<BlockingReference>> FindImplicitCallsAsync(
        IMethodSymbol constructor,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var calls = new List<BlockingReference>();
        if (constructor.ContainingType.TypeKind != TypeKind.Class)
            return calls;

        var derivedClasses = await SymbolFinder.FindDerivedClassesAsync(
            constructor.ContainingType, solution, transitive: false, cancellationToken: cancellationToken);
        foreach (var derived in derivedClasses)
        {
            foreach (var derivedConstructor in derived.InstanceConstructors)
            {
                if (derivedConstructor.IsImplicitlyDeclared)
                {
                    if (DeclarationIdentity.Instance.Equals(ImplicitlyCalledConstructor(constructor.ContainingType), constructor) &&
                        derived.Locations.FirstOrDefault(l => l.IsInSource) is { } typeLocation)
                    {
                        calls.Add(BlockingReference.Create(typeLocation.SourceTree!.FilePath, typeLocation, "implicit", cancellationToken));
                    }

                    continue;
                }

                foreach (var reference in derivedConstructor.DeclaringSyntaxReferences)
                {
                    if (await reference.GetSyntaxAsync(cancellationToken) is not ConstructorDeclarationSyntax { Initializer: null } declaration ||
                        solution.GetDocument(declaration.SyntaxTree) is not { } document ||
                        await document.GetSemanticModelAsync(cancellationToken) is not { } model ||
                        model.GetOperation(declaration, cancellationToken) is not IConstructorBodyOperation { Initializer: { } initializer })
                    {
                        continue;
                    }

                    if (initializer.DescendantsAndSelf().OfType<IInvocationOperation>()
                        .Any(call => DeclarationIdentity.Instance.Equals(call.TargetMethod, constructor)))
                    {
                        calls.Add(BlockingReference.Create(document.FilePath ?? "", declaration.Identifier.GetLocation(), "implicit", cancellationToken));
                    }
                }
            }
        }

        return calls;
    }

    /// <summary>The constructor of <paramref name="type"/> a derived class calls without arguments.</summary>
    private static IMethodSymbol? ImplicitlyCalledConstructor(INamedTypeSymbol type) =>
        type.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 0)
        ?? type.InstanceConstructors.FirstOrDefault(c => c.Parameters.All(p => p.IsOptional || p.IsParams));

    private static RefactoringException StillReferenced(ISymbol symbol, List<BlockingReference> references)
    {
        var crefs = references.Count(r => r.Kind == "cref");
        var listed = string.Join(", ", references.Take(ReferencesInMessage).Select(r => $"{r.File}({r.Line},{r.Column})"));
        if (references.Count > ReferencesInMessage)
            listed += $" and {references.Count - ReferencesInMessage} more";

        return new RefactoringException(
            ErrorCodes.SymbolStillReferenced,
            $"'{symbol.ToDisplayString()}' is still referenced in {references.Count} place{(references.Count == 1 ? "" : "s")}" +
            (crefs > 0 ? $" ({crefs} in documentation-comment crefs)" : "") +
            $": {listed}. Remove or change these references first.",
            new Dictionary<string, object>
            {
                ["totalCount"] = references.Count,
                ["crefCount"] = crefs,
                ["references"] = references.Take(ListedReferences).ToList()
            });
    }

    /// <summary>
    /// Removes the declarations from every document compiling their files; a file left with no
    /// declarations is removed from the solution.
    /// </summary>
    private static async Task<(Solution Solution, List<FileEdit> Edits)> RemoveAsync(
        Solution solution,
        IReadOnlyList<SyntaxNode> declarations,
        CancellationToken cancellationToken)
    {
        var result = solution;
        var edits = new List<FileEdit>();
        foreach (var group in declarations.GroupBy(d => d.SyntaxTree))
        {
            var document = solution.GetDocument(group.Key)!;
            var text = await document.GetTextAsync(cancellationToken);
            var spans = group.Select(node => DeclarationRemoval.Span(text, node)).ToList();
            var newText = DeclarationRemoval.Remove(text, spans);

            var parseOptions = group.Key.Options;
            var deletesFile = DeclarationRemoval.DeclaresNothing(
                (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(newText, (CSharpParseOptions)parseOptions, cancellationToken: cancellationToken).GetRoot(cancellationToken));

            // A file compiled by several projects (target frameworks, links) is one document per project.
            var ids = document.FilePath == null ? [document.Id] : solution.GetDocumentIdsWithFilePath(document.FilePath);
            result = deletesFile
                ? result.RemoveDocuments(ids)
                : ids.Aggregate(result, (s, id) => s.WithDocumentText(id, newText));

            edits.Add(new FileEdit(document, text, group.Zip(spans).ToList(), deletesFile));
        }

        return (result, edits);
    }

    /// <summary>
    /// The compiler errors the deletion introduces in the touched files and, for a constructor,
    /// in the files using its type and its derived classes; and a note when a class loses its last
    /// constructor.
    /// </summary>
    private static async Task<IReadOnlyList<string>?> FindWarningsAsync(
        ISymbol symbol,
        Solution before,
        Solution after,
        List<FileEdit> edits,
        CancellationToken cancellationToken)
    {
        var documents = new HashSet<DocumentId>(edits
            .Select(e => e.Document)
            .SelectMany(d => d.FilePath == null ? [d.Id] : before.GetDocumentIdsWithFilePath(d.FilePath)));
        var notes = new List<string>();

        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor)
        {
            var type = constructor.ContainingType;
            foreach (var reference in await SymbolFinder.FindReferencesAsync(type, before, cancellationToken))
                documents.UnionWith(reference.Locations.Select(l => l.Document.Id));
            if (type.TypeKind == TypeKind.Class)
            {
                foreach (var derived in await SymbolFinder.FindDerivedClassesAsync(type, before, transitive: false, cancellationToken: cancellationToken))
                    documents.UnionWith(derived.DeclaringSyntaxReferences.Select(r => before.GetDocument(r.SyntaxTree)?.Id).OfType<DocumentId>());

                if (!type.IsStatic && type.InstanceConstructors.All(c => c.IsImplicitlyDeclared || DeclarationIdentity.Instance.Equals(c, constructor)) &&
                    !(constructor.Parameters.Length == 0 && constructor.DeclaredAccessibility == (type.IsAbstract ? Accessibility.Protected : Accessibility.Public)))
                {
                    notes.Add($"'{type.ToDisplayString()}' has no constructor left, so the compiler gives it a " +
                        $"{(type.IsAbstract ? "protected" : "public")} parameterless one.");
                }
            }
        }

        // The deletion removes lines, so the errors below it move; they are matched without their line.
        var errors = await IntroducedErrors.FindAsync(before, after, documents, cancellationToken, matchLines: false);
        var warnings = (errors ?? []).Concat(notes).ToList();
        return warnings.Count == 0 ? null : warnings;
    }

    private static List<PendingChange> CreatePendingChanges(ISymbol symbol, List<FileEdit> edits)
    {
        var changes = new List<PendingChange>();
        foreach (var edit in edits)
        {
            var path = edit.Document.FilePath ?? edit.Document.Name;
            if (edit.DeletesFile)
            {
                changes.Add(new PendingChange
                {
                    File = path,
                    ChangeType = ChangeKind.Delete,
                    Description = $"Delete the file: '{DisplayName(symbol)}' is all it declares"
                });
                continue;
            }

            foreach (var (node, span) in edit.Removals)
            {
                var lines = edit.Text.Lines.GetLinePositionSpan(span);
                var nodeLine = edit.Text.Lines.GetLineFromPosition(node.SpanStart);
                changes.Add(new PendingChange
                {
                    File = path,
                    ChangeType = ChangeKind.Modify,
                    Description = $"Delete {SymbolKindMapper.Map(symbol).ToString().ToLowerInvariant()} '{DisplayName(symbol)}'",
                    BeforeSnippet = edit.Text.ToString(TextSpan.FromBounds(node.SpanStart, Math.Min(node.Span.End, nodeLine.End))).Trim(),
                    StartLine = lines.Start.Line + 1,
                    EndLine = lines.End.Character == 0 ? lines.End.Line : lines.End.Line + 1
                });
            }
        }

        return changes;
    }

    private static string DisplayName(ISymbol symbol) =>
        symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ? symbol.ContainingType.Name : symbol.Name;

    /// <summary>The removals in one file.</summary>
    private sealed record FileEdit(Document Document, SourceText Text, List<(SyntaxNode Node, TextSpan Span)> Removals, bool DeletesFile);

    /// <summary>
    /// Equality of declarations across compilations: the same symbol, or the same declaration ID
    /// in the same assembly (a project referenced through metadata yields other symbol instances).
    /// </summary>
    private sealed class DeclarationIdentity : IEqualityComparer<ISymbol>
    {
        public static readonly DeclarationIdentity Instance = new();

        public bool Equals(ISymbol? left, ISymbol? right)
        {
            if (left is null || right is null)
                return left is null && right is null;

            return SymbolEqualityComparer.Default.Equals(left.OriginalDefinition, right.OriginalDefinition) ||
                (left.OriginalDefinition.GetDocumentationCommentId() is { } id &&
                 id == right.OriginalDefinition.GetDocumentationCommentId() &&
                 left.ContainingAssembly?.Name == right.ContainingAssembly?.Name);
        }

        public int GetHashCode(ISymbol symbol) =>
            symbol.OriginalDefinition.GetDocumentationCommentId() is { } id
                ? StringComparer.Ordinal.GetHashCode(id)
                : SymbolEqualityComparer.Default.GetHashCode(symbol.OriginalDefinition);
    }
}

/// <summary>A reference that keeps <c>safe_delete</c> from deleting a symbol.</summary>
/// <param name="File">File of the reference.</param>
/// <param name="Line">1-based line.</param>
/// <param name="Column">1-based column.</param>
/// <param name="Kind">
/// <c>reference</c>, <c>nameof</c>, <c>cref</c> (a documentation comment), or <c>implicit</c>
/// (e.g. a derived constructor calling a base constructor without naming it).
/// </param>
/// <param name="Snippet">The line of the reference, trimmed.</param>
internal sealed record BlockingReference(string File, int Line, int Column, string Kind, string Snippet)
{
    public static BlockingReference Create(string file, Location location, string kind, CancellationToken cancellationToken)
    {
        var span = location.GetLineSpan();
        var text = location.SourceTree!.GetText(cancellationToken);
        return new BlockingReference(
            file,
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            kind,
            text.Lines[span.StartLinePosition.Line].ToString().Trim());
    }
}
