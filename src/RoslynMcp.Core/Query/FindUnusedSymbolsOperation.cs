using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query.Base;
using RoslynMcp.Core.Query.Utilities;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Query;

/// <summary>
/// Finds source types and members that nothing references outside their own declaration:
/// candidates for deletion, since reflection, serialization, XAML/Razor bindings and other
/// assemblies are invisible to it. See <see cref="UnusedSymbolSearch"/> for what counts as a use
/// and what is left out.
/// </summary>
public sealed class FindUnusedSymbolsOperation : QueryOperationBase<FindUnusedSymbolsParams, FindUnusedSymbolsResult>
{
    private const int DefaultMaxResults = 100;

    /// <inheritdoc />
    public FindUnusedSymbolsOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(FindUnusedSymbolsParams @params)
    {
        if (@params.MaxResults is < 1)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "maxResults must be >= 1.");

        QueryScope.ValidateProjectPath(@params.ProjectPath);
        QueryScope.ValidateFilePath(@params.SourceFile, "sourceFile");
        ParseKindFilter(@params.KindFilter);
    }

    /// <inheritdoc />
    protected override async Task<QueryResult<FindUnusedSymbolsResult>> ExecuteCoreAsync(
        Guid operationId,
        FindUnusedSymbolsParams @params,
        CancellationToken cancellationToken)
    {
        var solution = Context.Solution;
        var projectScope = QueryScope.ResolveProject(solution, @params.ProjectPath);
        var fileScope = QueryScope.ResolveFile(solution, @params.SourceFile, "sourceFile");

        // The projects declaring what is reported: the project path's, and those compiling the file.
        IEnumerable<Project> projects = projectScope?.Projects ?? solution.Projects;
        if (fileScope is not null)
        {
            var fileProjects = fileScope.Documents.Select(d => d.Project.Id).ToHashSet();
            projects = projects.Where(p => fileProjects.Contains(p.Id));
        }

        var search = new UnusedSymbolSearch(
            solution,
            new UnusedSymbolSearch.Filter(
                ParseKindFilter(@params.KindFilter),
                fileScope,
                @params.IncludePublic ?? false,
                @params.IncludeAttributed ?? false));
        var (unused, checkedCount) = await search.RunAsync(projects.ToList(), cancellationToken);

        var ordered = ResultOrder.ByLocation(unused, e => e.File, e => e.Line, e => e.Column, e => e.FullyQualifiedName);
        var symbols = ordered.Take(@params.MaxResults ?? DefaultMaxResults).ToList();

        return QueryResult<FindUnusedSymbolsResult>.Succeeded(operationId, new FindUnusedSymbolsResult
        {
            Symbols = symbols,
            TotalCount = ordered.Count,
            Truncated = ordered.Count > symbols.Count,
            SymbolsChecked = checkedCount
        });
    }

    /// <summary>The kind filter, which names a type or member kind.</summary>
    private static Contracts.Enums.SymbolKind? ParseKindFilter(string? kindFilter)
    {
        var kind = SymbolKindFilter.Parse(kindFilter);
        if (kind is Contracts.Enums.SymbolKind.Local or Contracts.Enums.SymbolKind.Parameter or Contracts.Enums.SymbolKind.Namespace)
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSymbolKind,
                $"Invalid kindFilter '{kindFilter}': find_unused_symbols reports types and members. Valid values: " +
                "Class, Struct, Interface, Enum, Record, Delegate, Method, Property, Field, Event, Constant");
        }

        return kind;
    }
}
