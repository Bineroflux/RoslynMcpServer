using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Contracts.Errors;

namespace RoslynMcp.Core.Refactoring.Signature;

/// <summary>
/// The override / implementation family of a method or property: the members that must keep
/// one signature, because changing one of them alone leaves the others no longer overriding
/// or implementing it. Starting from a member, the family takes in what it overrides (up to
/// the base-most definition), its overrides (transitively, in every project), the interface
/// members it implements implicitly or explicitly, and their other implementations, until
/// nothing new turns up.
/// </summary>
/// <remarks>
/// Each member also knows how a type written in the starting member's context reads in its
/// own context: <c>List&lt;T&gt;</c> for <c>Repository&lt;T&gt;.Get</c> is
/// <c>List&lt;User&gt;</c> for the override in <c>UserRepository : Repository&lt;User&gt;</c>.
/// </remarks>
internal sealed class MemberFamily
{
    private static readonly IEqualityComparer<ITypeParameterSymbol> TypeParameterComparer = SymbolEqualityComparer.Default;

    private MemberFamily(IReadOnlyList<FamilyMember> members) => Members = members;

    /// <summary>The members, the starting one included, in file and position order.</summary>
    public IReadOnlyList<FamilyMember> Members { get; }

    /// <summary>Whether <paramref name="symbol"/>, from any compilation of the solution, is a member.</summary>
    public bool Contains(ISymbol symbol) => Members.Any(member => Same(member.Symbol, symbol));

    /// <summary>The references to every member, each location once.</summary>
    public async Task<IReadOnlyList<ReferenceLocation>> FindReferencesAsync(Solution solution, CancellationToken cancellationToken)
    {
        var locations = new List<ReferenceLocation>();
        var seen = new HashSet<(DocumentId, Microsoft.CodeAnalysis.Text.TextSpan)>();
        foreach (var member in Members)
        {
            foreach (var location in (await SymbolFinder.FindReferencesAsync(member.Symbol, solution, cancellationToken))
                .SelectMany(r => r.Locations))
            {
                if (seen.Add((location.Document.Id, location.Location.SourceSpan)))
                    locations.Add(location);
            }
        }

        return locations;
    }

    /// <summary>
    /// The documents a change of the family's signature can break: where the members are
    /// declared, where their types are declared (an implementing or derived type), and where
    /// they are referenced.
    /// </summary>
    public IReadOnlyCollection<DocumentId> AffectedDocuments(Solution solution, IEnumerable<ReferenceLocation> references)
    {
        var documents = new HashSet<DocumentId>(references.Select(r => r.Document.Id));
        foreach (var member in Members)
        {
            foreach (var declaration in member.Declarations.Concat(member.Symbol.ContainingType.DeclaringSyntaxReferences))
            {
                if (solution.GetDocument(declaration.SyntaxTree) is { } document)
                    documents.Add(document.Id);
            }
        }

        return documents;
    }

    /// <summary>
    /// Finds the family of <paramref name="target"/>. Fails when a member is declared only in
    /// metadata (e.g. <c>object.ToString</c>, <c>IDisposable.Dispose</c>) or in generated code:
    /// that member cannot be changed, so no member of its family can.
    /// </summary>
    /// <param name="target">The method or property to start from.</param>
    /// <param name="compilation">The compilation of <paramref name="target"/>.</param>
    /// <param name="solution">The solution to search.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<MemberFamily> FindAsync(
        ISymbol target,
        Compilation compilation,
        Solution solution,
        CancellationToken cancellationToken)
    {
        target = Normalize(target);
        var maps = new Dictionary<ISymbol, Dictionary<ITypeParameterSymbol, ITypeSymbol>>(MemberIdentity.Instance)
        {
            [target] = VisibleTypeParameters(target).ToDictionary(t => t, t => (ITypeSymbol)t, TypeParameterComparer)
        };
        var queue = new Queue<ISymbol>([target]);
        var unchangeable = new SortedSet<string>(StringComparer.Ordinal);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (WhyNotEditable(node, solution) is { } reason)
            {
                unchangeable.Add($"'{node.ToDisplayString()}' ({reason})");
                continue;
            }

            foreach (var (neighbor, edge) in await RelatedAsync(node, solution, cancellationToken))
            {
                if (maps.ContainsKey(neighbor))
                    continue;

                var map = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(TypeParameterComparer);
                foreach (var (parameter, type) in maps[node])
                {
                    if (Substitute(type, edge, compilation) is { } mapped)
                        map[parameter] = mapped;
                }

                maps[neighbor] = map;
                queue.Enqueue(neighbor);
            }
        }

        if (unchangeable.Count > 0)
        {
            throw new RefactoringException(
                ErrorCodes.FamilyMemberNotInSource,
                $"'{target.ToDisplayString()}' must keep the signature of {string.Join(", ", unchangeable)}, " +
                "which cannot be changed.");
        }

        var members = maps
            .Select(entry => new FamilyMember(entry.Key, Declarations(entry.Key), entry.Value, compilation))
            .OrderBy(member => member.Declarations[0].SyntaxTree.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(member => member.Declarations[0].Span.Start)
            .ToList();

        return new MemberFamily(members);
    }

    /// <summary>
    /// The members directly related to <paramref name="node"/>, each with the map that writes a
    /// type of <paramref name="node"/>'s context in the related member's context. A map leaves
    /// out the type parameters that have no counterpart there.
    /// </summary>
    /// <remarks>
    /// A member seen from another project's compilation can be a metadata symbol (Roslyn may
    /// reference a project through an emitted skeleton), so related members are mapped back to
    /// their source definitions and compared by identity rather than by symbol equality.
    /// </remarks>
    private static async Task<List<(ISymbol Member, Dictionary<ITypeParameterSymbol, ITypeSymbol> Map)>> RelatedAsync(
        ISymbol node,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var related = new List<(ISymbol, Dictionary<ITypeParameterSymbol, ITypeSymbol>)>();

        if (Overridden(node) is { } overridden)
        {
            var definition = await SourceDefinitionAsync(overridden, solution, cancellationToken);
            related.Add((definition, Inverse(definition, overridden, node)));
        }

        // The direct overrides; the indirect ones are reached through them.
        if (node.IsVirtual || node.IsAbstract || node.IsOverride)
        {
            foreach (var candidate in await SymbolFinder.FindOverridesAsync(node, solution, cancellationToken: cancellationToken))
            {
                if (Overridden(candidate) is { } view && Same(view, node))
                    related.Add((Normalize(candidate), Forward(node, view, candidate)));
            }
        }

        foreach (var view in ExplicitImplementations(node))
        {
            var definition = await SourceDefinitionAsync(view, solution, cancellationToken);
            related.Add((definition, Inverse(definition, view, node)));
        }

        if (node.ContainingType.TypeKind == TypeKind.Interface)
        {
            if (ExplicitImplementations(node).Length == 0)
            {
                foreach (var implementation in await SymbolFinder.FindImplementationsAsync(node, solution, cancellationToken: cancellationToken))
                {
                    var view = InterfaceView(node, implementation);
                    related.Add((Normalize(implementation), view == null
                        ? MethodTypeParameters(node, implementation)
                        : Forward(node, view, implementation)));
                }
            }

            return related;
        }

        if (node.DeclaredAccessibility != Accessibility.Public)
            return related;

        // The interface members it implements implicitly, also on behalf of a derived type
        // that lists an interface its own type does not.
        IEnumerable<INamedTypeSymbol> types = [node.ContainingType];
        if (node.ContainingType.TypeKind == TypeKind.Class && !node.ContainingType.IsSealed)
        {
            types = types.Concat(await SymbolFinder.FindDerivedClassesAsync(
                node.ContainingType, solution, transitive: true, cancellationToken: cancellationToken));
        }

        foreach (var type in types)
        {
            foreach (var view in type.AllInterfaces.SelectMany(i => i.GetMembers(node.Name)).Where(m => m.Kind == node.Kind))
            {
                if (type.FindImplementationForInterfaceMember(view) is { } implementation && Same(implementation, node))
                {
                    var definition = await SourceDefinitionAsync(view, solution, cancellationToken);
                    related.Add((definition, Same(type, node.ContainingType)
                        ? Inverse(definition, view, node)
                        : MethodTypeParameters(node, definition)));
                }
            }
        }

        return related;
    }

    /// <summary>
    /// The interface member as the type of <paramref name="implementation"/> implements it, or
    /// null when that type does not list the interface itself.
    /// </summary>
    private static ISymbol? InterfaceView(ISymbol interfaceMember, ISymbol implementation)
    {
        var type = implementation.ContainingType;
        return type.AllInterfaces
            .Where(i => Same(i, interfaceMember.ContainingType))
            .SelectMany(i => i.GetMembers(interfaceMember.Name))
            .FirstOrDefault(view => Same(view, interfaceMember)
                && type.FindImplementationForInterfaceMember(view) is { } found
                && Same(found, implementation));
    }

    /// <summary>
    /// Maps the type parameters of <paramref name="definition"/> to what they stand for in
    /// <paramref name="other"/>'s context; <paramref name="view"/> is the definition as
    /// <paramref name="other"/>'s type sees it (e.g. <c>Base&lt;int&gt;.M</c>).
    /// </summary>
    private static Dictionary<ITypeParameterSymbol, ITypeSymbol> Forward(ISymbol definition, ISymbol view, ISymbol other)
    {
        var map = MethodTypeParameters(definition, other);
        foreach (var (parameter, argument) in TypeArgumentPairs(definition, view))
            map[parameter] = argument;
        return map;
    }

    /// <summary>
    /// The reverse of <see cref="Forward"/>: maps <paramref name="other"/>'s type parameters to
    /// those of <paramref name="definition"/>, where a type argument is a bare type parameter.
    /// </summary>
    private static Dictionary<ITypeParameterSymbol, ITypeSymbol> Inverse(ISymbol definition, ISymbol view, ISymbol other)
    {
        var map = MethodTypeParameters(other, definition);
        foreach (var (parameter, argument) in TypeArgumentPairs(definition, view))
        {
            if (argument is ITypeParameterSymbol otherParameter)
                map.TryAdd(otherParameter, parameter);
        }

        return map;
    }

    /// <summary>Pairs the method type parameters of two related methods by position.</summary>
    private static Dictionary<ITypeParameterSymbol, ITypeSymbol> MethodTypeParameters(ISymbol from, ISymbol to)
    {
        var map = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(TypeParameterComparer);
        if (from.OriginalDefinition is IMethodSymbol fromMethod && to.OriginalDefinition is IMethodSymbol toMethod)
        {
            foreach (var (parameter, counterpart) in fromMethod.TypeParameters.Zip(toMethod.TypeParameters))
                map[parameter] = counterpart;
        }

        return map;
    }

    /// <summary>
    /// The type parameters of the types containing <paramref name="definition"/>, each paired
    /// with its type argument in <paramref name="view"/>.
    /// </summary>
    private static IEnumerable<(ITypeParameterSymbol, ITypeSymbol)> TypeArgumentPairs(ISymbol definition, ISymbol view)
    {
        for (INamedTypeSymbol? type = definition.ContainingType, constructed = view.ContainingType;
             type != null && constructed != null;
             type = type.ContainingType, constructed = constructed.ContainingType)
        {
            foreach (var pair in type.TypeParameters.Zip(constructed.TypeArguments))
                yield return pair;
        }
    }

    /// <summary>
    /// <paramref name="type"/> with its type parameters replaced through <paramref name="map"/>,
    /// or null when it mentions one the map leaves out (or one inside a pointer, function
    /// pointer or generic containing type, which are not rewritten).
    /// </summary>
    internal static ITypeSymbol? Substitute(
        ITypeSymbol type,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map,
        Compilation compilation)
    {
        if (!MentionsTypeParameter(type))
            return type;

        switch (type)
        {
            case ITypeParameterSymbol parameter:
                if (!map.TryGetValue(parameter, out var mapped))
                    return null;
                // T? with T = int is int; with T = string it is string?.
                return parameter.NullableAnnotation == NullableAnnotation.Annotated && !mapped.IsValueType
                    ? mapped.WithNullableAnnotation(NullableAnnotation.Annotated)
                    : mapped;

            case IArrayTypeSymbol array:
                return Substitute(array.ElementType, map, compilation) is { } element
                    ? compilation.CreateArrayTypeSymbol(element, array.Rank, element.NullableAnnotation)
                        .WithNullableAnnotation(array.NullableAnnotation)
                    : null;

            case INamedTypeSymbol { IsTupleType: true } tuple when !MentionsTypeParameter(tuple.ContainingType):
                return SubstituteTuple(tuple, map, compilation);

            case INamedTypeSymbol named when !MentionsTypeParameter(named.ContainingType):
                return SubstituteTypeArguments(named, map, compilation);

            default:
                return null;
        }
    }

    private static ITypeSymbol? SubstituteTuple(
        INamedTypeSymbol tuple,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map,
        Compilation compilation)
    {
        var elements = tuple.TupleElements.Select(e => Substitute(e.Type, map, compilation)).ToList();
        if (elements.Any(e => e == null))
            return null;

        return compilation.CreateTupleTypeSymbol(
                elements.ToImmutableArray()!,
                tuple.TupleElements.Select(e => e.IsExplicitlyNamedTupleElement ? e.Name : null).ToImmutableArray(),
                elementNullableAnnotations: elements.Select(e => e!.NullableAnnotation).ToImmutableArray())
            .WithNullableAnnotation(tuple.NullableAnnotation);
    }

    private static ITypeSymbol? SubstituteTypeArguments(
        INamedTypeSymbol named,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> map,
        Compilation compilation)
    {
        var arguments = named.TypeArguments.Select(a => Substitute(a, map, compilation)).ToList();
        if (arguments.Any(a => a == null))
            return null;

        return named.ConstructedFrom
            .Construct(arguments.ToImmutableArray()!, arguments.Select(a => a!.NullableAnnotation).ToImmutableArray())
            .WithNullableAnnotation(named.NullableAnnotation);
    }

    private static bool MentionsTypeParameter(ITypeSymbol? type) => type switch
    {
        null => false,
        ITypeParameterSymbol => true,
        IArrayTypeSymbol array => MentionsTypeParameter(array.ElementType),
        IPointerTypeSymbol pointer => MentionsTypeParameter(pointer.PointedAtType),
        IFunctionPointerTypeSymbol pointer => MentionsTypeParameter(pointer.Signature.ReturnType)
            || pointer.Signature.Parameters.Any(p => MentionsTypeParameter(p.Type)),
        INamedTypeSymbol named => named.TypeArguments.Any(MentionsTypeParameter) || MentionsTypeParameter(named.ContainingType),
        _ => false
    };

    /// <summary>Why <paramref name="member"/> cannot be edited, or null when it can.</summary>
    private static string? WhyNotEditable(ISymbol member, Solution solution)
    {
        var declarations = Declarations(member);
        if (declarations.Count == 0)
            return $"declared in {member.ContainingAssembly?.Name ?? "metadata"}";

        return declarations.Any(d => solution.GetDocument(d.SyntaxTree) == null)
            ? "declared in generated code"
            : null;
    }

    /// <summary>The declarations of <paramref name="member"/>, both parts of a partial member included.</summary>
    private static IReadOnlyList<SyntaxReference> Declarations(ISymbol member)
    {
        ISymbol? implementationPart = member switch
        {
            IMethodSymbol method => method.PartialImplementationPart,
            IPropertySymbol property => property.PartialImplementationPart,
            _ => null
        };

        return member.DeclaringSyntaxReferences
            .Concat(implementationPart?.DeclaringSyntaxReferences ?? [])
            .ToList();
    }

    /// <summary>The definition of <paramref name="member"/>: unconstructed, and the defining part of a partial member.</summary>
    private static ISymbol Normalize(ISymbol member) => member.OriginalDefinition switch
    {
        IMethodSymbol { PartialDefinitionPart: { } definition } => definition,
        IPropertySymbol { PartialDefinitionPart: { } definition } => definition,
        var definition => definition
    };

    /// <summary>
    /// <paramref name="symbol"/>'s definition in source, also when <paramref name="symbol"/>
    /// comes from a compilation that sees its project as metadata.
    /// </summary>
    private static async Task<ISymbol> SourceDefinitionAsync(ISymbol symbol, Solution solution, CancellationToken cancellationToken)
    {
        var definition = Normalize(symbol);
        if (definition.Locations.Any(l => l.IsInSource))
            return definition;

        if (await SymbolFinder.FindSourceDefinitionAsync(definition, solution, cancellationToken) is { } source)
            return Normalize(source);

        // The declaration ID in the project that builds the symbol's assembly.
        if (definition.GetDocumentationCommentId() is { } id)
        {
            foreach (var project in solution.Projects.Where(p => p.AssemblyName == definition.ContainingAssembly?.Name))
            {
                if (await project.GetCompilationAsync(cancellationToken) is { } compilation &&
                    DocumentationCommentId.GetFirstSymbolForDeclarationId(id, compilation) is { } declared)
                {
                    return Normalize(declared);
                }
            }
        }

        return definition;
    }

    /// <summary>
    /// Whether two symbols are the same declaration, also when they come from different
    /// compilations (one of which may see the other's project as metadata).
    /// </summary>
    private static bool Same(ISymbol left, ISymbol right) => MemberIdentity.Instance.Equals(left, right);

    /// <summary>
    /// Equality of declarations across compilations: the same symbol, or the same declaration
    /// ID in the same assembly.
    /// </summary>
    private sealed class MemberIdentity : IEqualityComparer<ISymbol>
    {
        public static readonly MemberIdentity Instance = new();

        public bool Equals(ISymbol? left, ISymbol? right)
        {
            if (left is null || right is null)
                return left is null && right is null;

            left = Normalize(left);
            right = Normalize(right);
            return SymbolEqualityComparer.Default.Equals(left, right)
                || (left.GetDocumentationCommentId() is { } id
                    && id == right.GetDocumentationCommentId()
                    && left.ContainingAssembly?.Name == right.ContainingAssembly?.Name);
        }

        public int GetHashCode(ISymbol symbol) =>
            Normalize(symbol).GetDocumentationCommentId() is { } id
                ? StringComparer.Ordinal.GetHashCode(id)
                : SymbolEqualityComparer.Default.GetHashCode(Normalize(symbol));
    }

    private static ISymbol? Overridden(ISymbol member) => member switch
    {
        IMethodSymbol method => method.OverriddenMethod,
        IPropertySymbol property => property.OverriddenProperty,
        _ => null
    };

    private static ImmutableArray<ISymbol> ExplicitImplementations(ISymbol member) => member switch
    {
        IMethodSymbol method => method.ExplicitInterfaceImplementations.CastArray<ISymbol>(),
        IPropertySymbol property => property.ExplicitInterfaceImplementations.CastArray<ISymbol>(),
        _ => []
    };

    private static IEnumerable<ITypeParameterSymbol> VisibleTypeParameters(ISymbol member)
    {
        var own = member is IMethodSymbol method ? method.TypeParameters : [];
        var containing = new List<ITypeParameterSymbol>();
        for (var type = member.ContainingType; type != null; type = type.ContainingType)
            containing.AddRange(type.TypeParameters);
        return own.Concat(containing);
    }
}

/// <summary>A member of a <see cref="MemberFamily"/>.</summary>
internal sealed class FamilyMember
{
    private readonly IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> _typeMap;
    private readonly Compilation _compilation;

    internal FamilyMember(
        ISymbol symbol,
        IReadOnlyList<SyntaxReference> declarations,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeMap,
        Compilation compilation)
    {
        Symbol = symbol;
        Declarations = declarations;
        _typeMap = typeMap;
        _compilation = compilation;
    }

    /// <summary>The member (the defining part of a partial member).</summary>
    public ISymbol Symbol { get; }

    /// <summary>Its declarations: one, or both parts of a partial member.</summary>
    public IReadOnlyList<SyntaxReference> Declarations { get; }

    /// <summary>
    /// <paramref name="type"/>, written in the starting member's context, as it reads in this
    /// member's context; null when it mentions a type parameter that has no counterpart here.
    /// </summary>
    public ITypeSymbol? Translate(ITypeSymbol type) => MemberFamily.Substitute(type, _typeMap, _compilation);
}
