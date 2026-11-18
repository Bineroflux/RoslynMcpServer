using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Query.Base;
using RoslynMcp.Core.Query.Utilities;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Query;

/// <summary>
/// Finds all callers of a symbol using Roslyn's SymbolFinder.FindCallersAsync().
/// </summary>
public sealed class FindCallersOperation : QueryOperationBase<FindCallersParams, FindCallersResult>
{
    /// <inheritdoc />
    public FindCallersOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(FindCallersParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, PathResolver.GetCSharpFileRejectionReason(@params.SourceFile));

        if (!@params.Line.HasValue && string.IsNullOrWhiteSpace(@params.SymbolName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "Either line/column or symbolName must be provided.");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Column number must be >= 1.");

        if (@params.MaxResults.HasValue && @params.MaxResults.Value < 1)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "maxResults must be >= 1.");

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        QueryScope.ValidateFilePath(@params.CallerFile, "callerFile");
        QueryScope.ValidateProjectPath(@params.ProjectPath);
    }

    /// <inheritdoc />
    protected override async Task<QueryResult<FindCallersResult>> ExecuteCoreAsync(
        Guid operationId,
        FindCallersParams @params,
        CancellationToken cancellationToken)
    {
        var resolved = await SymbolResolver.ResolveSymbolAsync(
            @params.SourceFile, @params.SymbolName, @params.Line, @params.Column, cancellationToken);

        var symbol = resolved.Symbol;

        // The scopes filter every call site before it is counted, and narrow the documents searched.
        var fileScope = QueryScope.ResolveFile(Context.Solution, @params.CallerFile, "callerFile");
        var projectScope = QueryScope.ResolveProject(Context.Solution, @params.ProjectPath);
        var searchDocuments = await QueryScope.GetSearchDocumentsAsync(fileScope, projectScope, cancellationToken);

        var callerResults = searchDocuments is null
            ? await SymbolFinder.FindCallersAsync(symbol, Context.Solution, cancellationToken)
            : await SymbolFinder.FindCallersAsync(symbol, Context.Solution, searchDocuments, cancellationToken);

        var maxResults = @params.MaxResults ?? int.MaxValue;

        var callSites = new List<(ISymbol Caller, string CallerName, Location Location, FileLinePositionSpan Span)>();
        foreach (var caller in callerResults)
        {
            var callerName = caller.CallingSymbol.ToDisplayString();
            foreach (var location in caller.Locations)
            {
                if (!location.IsInSource) continue;

                var lineSpan = location.GetLineSpan();
                if (fileScope is not null && !fileScope.ContainsFile(lineSpan.Path)) continue;
                if (projectScope is not null &&
                    !projectScope.Contains(Context.Solution.GetDocument(location.SourceTree), lineSpan.Path))
                {
                    continue;
                }

                callSites.Add((caller.CallingSymbol, callerName, location, lineSpan));
            }
        }

        // SymbolFinder's order varies between calls; sort by call site so maxResults keeps
        // the same first N every time.
        var ordered = ResultOrder.ByLocation(
            callSites,
            c => c.Span.Path,
            c => c.Span.StartLinePosition.Line,
            c => c.Span.StartLinePosition.Character,
            c => c.CallerName);
        var totalCount = ordered.Count;

        var callers = new List<CallerInfo>();
        foreach (var (caller, callerName, location, lineSpan) in ordered.Take(maxResults))
        {
            callers.Add(new CallerInfo
            {
                CallerName = caller.Name,
                CallerFullyQualifiedName = callerName,
                File = lineSpan.Path,
                Line = lineSpan.StartLinePosition.Line + 1,
                Column = lineSpan.StartLinePosition.Character + 1,
                Snippet = await GetSnippetAsync(location, cancellationToken)
            });
        }

        var result = new FindCallersResult
        {
            SymbolName = symbol.Name,
            FullyQualifiedName = symbol.ToDisplayString(),
            Callers = callers,
            TotalCount = totalCount,
            Truncated = totalCount > callers.Count
        };

        return QueryResult<FindCallersResult>.Succeeded(operationId, result);
    }

    private static async Task<string?> GetSnippetAsync(Location location, CancellationToken cancellationToken)
    {
        if (!location.IsInSource || location.SourceTree == null) return null;

        var text = await location.SourceTree.GetTextAsync(cancellationToken);
        var lineSpan = location.GetLineSpan();
        var lineIndex = lineSpan.StartLinePosition.Line;

        if (lineIndex < 0 || lineIndex >= text.Lines.Count) return null;
        return text.Lines[lineIndex].ToString().Trim();
    }
}
