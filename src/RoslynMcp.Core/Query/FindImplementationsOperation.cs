using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Query.Base;
using RoslynMcp.Core.Query.Utilities;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Query;

/// <summary>
/// Finds the implementations of a symbol: the types implementing an interface, the classes
/// derived from a class, the implementations of an interface member, or the overrides of a
/// virtual, abstract or override member.
/// </summary>
public sealed class FindImplementationsOperation : QueryOperationBase<FindImplementationsParams, FindImplementationsResult>
{
    /// <inheritdoc />
    public FindImplementationsOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(FindImplementationsParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, PathResolver.GetCSharpFileRejectionReason(@params.SourceFile));

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (!@params.Line.HasValue && string.IsNullOrWhiteSpace(@params.SymbolName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "Either line/column or symbolName must be provided.");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Column number must be >= 1.");

        if (@params.MaxResults.HasValue && @params.MaxResults.Value < 1)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "maxResults must be >= 1.");
    }

    /// <inheritdoc />
    protected override async Task<QueryResult<FindImplementationsResult>> ExecuteCoreAsync(
        Guid operationId,
        FindImplementationsParams @params,
        CancellationToken cancellationToken)
    {
        var resolved = await SymbolResolver.ResolveSymbolAsync(
            @params.SourceFile, @params.SymbolName, @params.Line, @params.Column, cancellationToken);

        var symbol = resolved.Symbol;
        var implementations = new List<ImplementationInfo>();
        var maxResults = @params.MaxResults ?? int.MaxValue;

        // Find implementations based on symbol kind
        var implSymbols = await FindImplementationsAsync(symbol, @params.Transitive ?? true, cancellationToken);

        // SymbolFinder's order varies between calls; sort by location so maxResults keeps
        // the same first N every time. Implementations without a source location (from
        // metadata) are counted but not listed.
        var ordered = ResultOrder.SymbolsByLocation(implSymbols);
        var totalCount = ordered.Count;

        foreach (var impl in ordered)
        {
            if (implementations.Count >= maxResults) break;

            var location = impl.Locations.FirstOrDefault(l => l.IsInSource);
            if (location == null) continue;

            var lineSpan = location.GetLineSpan();
            implementations.Add(new ImplementationInfo
            {
                Name = impl.Name,
                FullyQualifiedName = impl.ToDisplayString(),
                Kind = SymbolKindMapper.Map(impl),
                File = lineSpan.Path,
                Line = lineSpan.StartLinePosition.Line + 1,
                Column = lineSpan.StartLinePosition.Character + 1
            });
        }

        var result = new FindImplementationsResult
        {
            SymbolName = symbol.Name,
            FullyQualifiedName = symbol.ToDisplayString(),
            Implementations = implementations,
            TotalCount = totalCount,
            Truncated = totalCount > implementations.Count
        };

        return QueryResult<FindImplementationsResult>.Succeeded(operationId, result);
    }

    /// <summary>
    /// The implementations of <paramref name="symbol"/>. SymbolFinder.FindImplementationsAsync
    /// only knows interfaces and their members, and finds nothing for a class or a virtual or
    /// abstract member; those get their derived classes and overrides instead.
    /// </summary>
    private async Task<IEnumerable<ISymbol>> FindImplementationsAsync(
        ISymbol symbol, bool transitive, CancellationToken cancellationToken)
    {
        if (symbol is INamedTypeSymbol { TypeKind: TypeKind.Class } type)
        {
            return await SymbolFinder.FindDerivedClassesAsync(
                type, Context.Solution, transitive, cancellationToken: cancellationToken);
        }

        if (symbol is not INamedTypeSymbol &&
            symbol.ContainingType is { TypeKind: TypeKind.Class } &&
            (symbol.IsVirtual || symbol.IsAbstract || symbol.IsOverride))
        {
            // FindOverridesAsync also finds overrides of overrides. Non-transitive keeps
            // those that override the member itself.
            var overrides = await SymbolFinder.FindOverridesAsync(
                symbol, Context.Solution, cancellationToken: cancellationToken);
            return transitive ? overrides : overrides.Where(o => IsSameMember(OverriddenMember(o), symbol));
        }

        return transitive
            ? await SymbolFinder.FindImplementationsAsync(symbol, Context.Solution, cancellationToken: cancellationToken)
            : await FindDirectImplementationsAsync(symbol, cancellationToken);
    }

    private static ISymbol? OverriddenMember(ISymbol member) => member switch
    {
        IMethodSymbol method => method.OverriddenMethod,
        IPropertySymbol property => property.OverriddenProperty,
        IEventSymbol @event => @event.OverriddenEvent,
        _ => null
    };

    /// <summary>
    /// Whether two symbols are the same member. One may come from another project's
    /// compilation, and a generic type's member counts as its definition.
    /// </summary>
    private static bool IsSameMember(ISymbol? candidate, ISymbol member) =>
        candidate is not null && SymbolIdentity.SameDeclaration(candidate, member);

    /// <summary>
    /// The implementations of interface or interface member <paramref name="symbol"/> by types
    /// that list the interface themselves, leaving out those through a base class or a derived
    /// interface. For an interface that is SymbolFinder's non-transitive search; for an
    /// interface member, each such type's implementation of it (which may be inherited from
    /// its base class). Other symbols have no direct/indirect distinction and get the regular
    /// search.
    /// </summary>
    private async Task<IEnumerable<ISymbol>> FindDirectImplementationsAsync(ISymbol symbol, CancellationToken cancellationToken)
    {
        if (symbol is INamedTypeSymbol type)
        {
            return await SymbolFinder.FindImplementationsAsync(
                type, Context.Solution, transitive: false, cancellationToken: cancellationToken);
        }

        if (symbol.ContainingType is not { TypeKind: TypeKind.Interface } containingInterface)
        {
            return await SymbolFinder.FindImplementationsAsync(
                symbol, Context.Solution, cancellationToken: cancellationToken);
        }

        var implementingTypes = await SymbolFinder.FindImplementationsAsync(
            containingInterface, Context.Solution, transitive: false, cancellationToken: cancellationToken);

        // Several types can share one implementation, e.g. inherited from a common base class,
        // also when they are in other projects (or target frameworks) that see it as other symbols.
        var implementations = new List<ISymbol>();
        var seen = new HashSet<ISymbol>(SymbolIdentity.Declarations);
        foreach (var implementingType in implementingTypes)
        {
            if (FindImplementationOf(implementingType, symbol) is { } implementation && seen.Add(implementation.OriginalDefinition))
                implementations.Add(implementation);
        }

        return implementations;
    }

    /// <summary>
    /// <paramref name="type"/>'s implementation of interface member <paramref name="member"/>.
    /// The type can come from another project's compilation, so the interface is matched by
    /// its documentation ID and assembly rather than by symbol identity, and a generic
    /// interface by the construction the type implements.
    /// </summary>
    private static ISymbol? FindImplementationOf(INamedTypeSymbol type, ISymbol member)
    {
        foreach (var implemented in type.AllInterfaces)
        {
            if (!SymbolIdentity.SameDeclaration(implemented, member.ContainingType))
                continue;

            var constructedMember = implemented.GetMembers(member.Name)
                .FirstOrDefault(m => SymbolIdentity.SameDeclaration(m, member));
            if (constructedMember is not null &&
                type.FindImplementationForInterfaceMember(constructedMember) is { } implementation)
            {
                return implementation;
            }
        }

        return null;
    }
}
