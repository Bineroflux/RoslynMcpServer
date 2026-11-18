using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query.Base;
using RoslynMcp.Core.Query.Utilities;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Query;

/// <summary>
/// Searches for symbols by name pattern across all projects in the solution.
/// Uses Roslyn's Compilation.GetSymbolsWithName for efficient symbol lookup.
/// </summary>
public sealed class SearchSymbolsOperation : QueryOperationBase<SearchSymbolsParams, SearchSymbolsResult>
{
    /// <inheritdoc />
    public SearchSymbolsOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(SearchSymbolsParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.Query))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "query is required.");

        if (@params.MaxResults.HasValue && @params.MaxResults.Value < 1)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "maxResults must be >= 1.");

        QueryScope.ValidateProjectPath(@params.ProjectPath);
        QueryScope.ValidateFilePath(@params.SourceFile, "sourceFile");
    }

    /// <inheritdoc />
    protected override async Task<QueryResult<SearchSymbolsResult>> ExecuteCoreAsync(
        Guid operationId,
        SearchSymbolsParams @params,
        CancellationToken cancellationToken)
    {
        var query = @params.Query;
        var maxResults = @params.MaxResults ?? 50;
        var kindFilter = SymbolKindFilter.Parse(@params.KindFilter);
        var exactMatch = @params.ExactMatch ?? false;
        var comparison = @params.CaseSensitive == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var namespaceFilter = NormalizeNamespaceFilter(@params.NamespaceFilter);
        var projectScope = QueryScope.ResolveProject(Context.Solution, @params.ProjectPath);
        var fileScope = QueryScope.ResolveFile(Context.Solution, @params.SourceFile, "sourceFile");

        var entries = new List<SymbolSearchEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var totalCount = 0;

        // Determine the SymbolFilter based on kindFilter
        var symbolFilter = kindFilter.HasValue
            ? GetSymbolFilter(kindFilter.Value)
            : SymbolFilter.All;

        // Compile only the projects the scopes leave: the project path's, and those compiling the file.
        IEnumerable<Project> projects = projectScope?.Projects ?? Context.Solution.Projects;
        if (fileScope is not null)
        {
            var fileProjects = fileScope.Documents.Select(d => d.Project.Id).ToHashSet();
            projects = projects.Where(p => fileProjects.Contains(p.Id));
        }

        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null) continue;

            // The name overload is an exact, case-sensitive lookup in the declaration index;
            // anything else needs the predicate.
            var symbols = exactMatch && comparison == StringComparison.Ordinal
                ? compilation.GetSymbolsWithName(query, symbolFilter, cancellationToken)
                : compilation.GetSymbolsWithName(
                    exactMatch
                        ? name => string.Equals(name, query, comparison)
                        : name => name.Contains(query, comparison),
                    symbolFilter,
                    cancellationToken);

            foreach (var symbol in symbols)
            {
                // Skip compiler-generated symbols
                if (symbol.IsImplicitlyDeclared) continue;
                if (!symbol.CanBeReferencedByName) continue;

                // Apply kind filter
                if (kindFilter.HasValue && SymbolKindMapper.Map(symbol) != kindFilter.Value)
                    continue;

                if (namespaceFilter is not null && !IsInNamespace(symbol, namespaceFilter))
                    continue;

                // With a sourceFile, report the declaration in that file (any part of a partial type).
                var location = fileScope is null
                    ? symbol.Locations.FirstOrDefault(l => l.IsInSource)
                    : symbol.Locations.FirstOrDefault(l => l.IsInSource && fileScope.ContainsFile(l.SourceTree?.FilePath));
                if (location == null) continue;

                // Skip duplicates (same symbol can appear in multiple compilations). Tracked
                // apart from the returned entries so totalCount stays right past maxResults.
                var fqn = symbol.ToDisplayString();
                if (!seen.Add(fqn)) continue;

                totalCount++;

                if (entries.Count < maxResults)
                {
                    var lineSpan = location.GetLineSpan();
                    entries.Add(new SymbolSearchEntry
                    {
                        Name = symbol.Name,
                        FullyQualifiedName = fqn,
                        Kind = SymbolKindMapper.Map(symbol),
                        File = lineSpan.Path,
                        Line = lineSpan.StartLinePosition.Line + 1,
                        Column = lineSpan.StartLinePosition.Character + 1,
                        ContainerName = symbol.ContainingType?.Name ?? symbol.ContainingNamespace?.ToDisplayString()
                    });
                }
            }
        }

        var result = new SearchSymbolsResult
        {
            Query = query,
            Symbols = entries,
            TotalCount = totalCount,
            Truncated = totalCount > entries.Count
        };

        return QueryResult<SearchSymbolsResult>.Succeeded(operationId, result);
    }

    /// <summary>
    /// The namespace filter without a <c>global::</c> prefix or surrounding dots, or null
    /// when there is none.
    /// </summary>
    private static string? NormalizeNamespaceFilter(string? namespaceFilter)
    {
        if (string.IsNullOrWhiteSpace(namespaceFilter))
            return null;

        var trimmed = namespaceFilter.Trim();
        if (trimmed.StartsWith("global::", StringComparison.Ordinal))
            trimmed = trimmed["global::".Length..];

        trimmed = trimmed.Trim('.');
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// Whether <paramref name="symbol"/> is declared in <paramref name="namespaceName"/> or a
    /// namespace nested in it, comparing whole segments case-insensitively. A namespace symbol
    /// is tested by its own name, so the filter also finds the namespace it names.
    /// </summary>
    private static bool IsInNamespace(ISymbol symbol, string namespaceName)
    {
        var ns = symbol as INamespaceSymbol ?? symbol.ContainingNamespace;
        if (ns is null || ns.IsGlobalNamespace)
            return false;

        var name = ns.ToDisplayString();
        return name.Equals(namespaceName, StringComparison.OrdinalIgnoreCase)
            || (name.Length > namespaceName.Length
                && name[namespaceName.Length] == '.'
                && name.StartsWith(namespaceName, StringComparison.OrdinalIgnoreCase));
    }

    private static SymbolFilter GetSymbolFilter(Contracts.Enums.SymbolKind kind)
    {
        return kind switch
        {
            Contracts.Enums.SymbolKind.Class or
            Contracts.Enums.SymbolKind.Struct or
            Contracts.Enums.SymbolKind.Interface or
            Contracts.Enums.SymbolKind.Enum or
            Contracts.Enums.SymbolKind.Record or
            Contracts.Enums.SymbolKind.Delegate => SymbolFilter.Type,
            Contracts.Enums.SymbolKind.Namespace => SymbolFilter.Namespace,
            _ => SymbolFilter.Member
        };
    }
}
