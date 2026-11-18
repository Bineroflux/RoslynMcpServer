using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Query.Base;
using RoslynMcp.Core.Query.Utilities;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Query;

/// <summary>
/// Finds all references to a symbol across the solution.
/// Delegates to Roslyn's SymbolFinder.FindReferencesAsync.
/// </summary>
public sealed class FindReferencesOperation : QueryOperationBase<FindReferencesParams, FindReferencesResult>
{
    /// <inheritdoc />
    public FindReferencesOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(FindReferencesParams @params)
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

        QueryScope.ValidateFilePath(@params.ReferenceFile, "referenceFile");
        QueryScope.ValidateProjectPath(@params.ProjectPath);
    }

    /// <inheritdoc />
    protected override async Task<QueryResult<FindReferencesResult>> ExecuteCoreAsync(
        Guid operationId,
        FindReferencesParams @params,
        CancellationToken cancellationToken)
    {
        // Resolve the symbol
        var resolved = await SymbolResolver.ResolveSymbolAsync(
            @params.SourceFile, @params.SymbolName, @params.Line, @params.Column, cancellationToken);

        var symbol = resolved.Symbol;

        // When resolved as candidates, search all of them; otherwise just the one symbol.
        var symbolsToSearch = resolved.IsCandidate
            ? resolved.AllCandidates
            : [symbol];

        var maxResults = @params.MaxResults ?? int.MaxValue;
        var includeDeclaration = @params.IncludeDeclaration ?? true;

        // The scopes filter every location before it is counted, so totalCount and
        // truncated describe the filtered set. They also narrow the documents searched.
        var fileScope = QueryScope.ResolveFile(Context.Solution, @params.ReferenceFile, "referenceFile");
        var projectScope = QueryScope.ResolveProject(Context.Solution, @params.ProjectPath);
        var searchDocuments = await QueryScope.GetSearchDocumentsAsync(fileScope, projectScope, cancellationToken);

        bool InScope(Document? document, string path) =>
            (fileScope is null || fileScope.ContainsFile(path)) &&
            (projectScope is null || projectScope.Contains(document, path));

        var referencedSymbols = new List<ReferencedSymbol>();
        foreach (var searchSymbol in symbolsToSearch)
        {
            referencedSymbols.AddRange(searchDocuments is null
                ? await SymbolFinder.FindReferencesAsync(searchSymbol, Context.Solution, cancellationToken)
                : await SymbolFinder.FindReferencesAsync(searchSymbol, Context.Solution, searchDocuments, cancellationToken));
        }

        // Every definition is collected before any usage, so a position reported both ways
        // (e.g. a constructor's name, which also references its type) is a definition no
        // matter in which order SymbolFinder returned the symbols.
        var seen = new HashSet<(string file, int line, int col)>();
        var definitions = new List<FoundLocation>();
        foreach (var defLocation in referencedSymbols.SelectMany(r => r.Definition.Locations).Where(l => l.IsInSource))
        {
            var lineSpan = defLocation.GetLineSpan();
            var key = (lineSpan.Path, lineSpan.StartLinePosition.Line, lineSpan.StartLinePosition.Character);
            if (!InScope(Context.Solution.GetDocument(defLocation.SourceTree), lineSpan.Path)) continue;
            if (!seen.Add(key)) continue;
            definitions.Add(new FoundLocation(lineSpan.Path, lineSpan.StartLinePosition, defLocation, IsDefinition: true));
        }

        var usages = new List<FoundLocation>();
        foreach (var refLocation in referencedSymbols.SelectMany(r => r.Locations))
        {
            if (refLocation.Document == null) continue;
            var span = refLocation.Location.GetLineSpan();
            var file = refLocation.Document.FilePath ?? span.Path;
            var key = (file, span.StartLinePosition.Line, span.StartLinePosition.Character);
            if (!InScope(refLocation.Document, file)) continue;
            if (!seen.Add(key)) continue;
            usages.Add(new FoundLocation(file, span.StartLinePosition, refLocation.Location, IsDefinition: false));
        }

        // Declarations first, then usages, each by file, line and column. Without
        // declarations their positions stay seen, so the result is exactly the usages a
        // full search reports (isDefinition: false).
        var found = (includeDeclaration ? SortByLocation(definitions) : [])
            .Concat(SortByLocation(usages))
            .ToList();

        var locations = new List<ReferenceLocationInfo>();
        foreach (var location in found.Take(maxResults))
        {
            locations.Add(new ReferenceLocationInfo
            {
                File = location.File,
                Line = location.Start.Line + 1,
                Column = location.Start.Character + 1,
                ContextSnippet = await GetContextSnippetAsync(location.Location, cancellationToken),
                IsWriteAccess = false,
                IsDefinition = location.IsDefinition
            });
        }

        var totalCount = found.Count;

        var candidateFqns = resolved.IsCandidate
            ? resolved.AllCandidates.Select(s => s.ToDisplayString()).ToList()
            : [];

        var result = new FindReferencesResult
        {
            SymbolName = symbol.Name,
            FullyQualifiedName = symbol.ToDisplayString(),
            References = locations,
            TotalCount = totalCount,
            Truncated = totalCount > locations.Count,
            SymbolIsCandidate = resolved.IsCandidate,
            CandidateFullyQualifiedNames = candidateFqns,
            LocationOverride = resolved.LocationOverride
        };

        return QueryResult<FindReferencesResult>.Succeeded(operationId, result);
    }

    private static List<FoundLocation> SortByLocation(IEnumerable<FoundLocation> locations) =>
        ResultOrder.ByLocation(locations, l => l.File, l => l.Start.Line, l => l.Start.Character, _ => null);

    /// <summary>A reference location before it is reported.</summary>
    private sealed record FoundLocation(string File, LinePosition Start, Location Location, bool IsDefinition);

    private static async Task<string?> GetContextSnippetAsync(Location location, CancellationToken cancellationToken)
    {
        if (!location.IsInSource) return null;

        var tree = location.SourceTree;
        if (tree == null) return null;

        var text = await tree.GetTextAsync(cancellationToken);
        var lineSpan = location.GetLineSpan();
        var lineIndex = lineSpan.StartLinePosition.Line;

        if (lineIndex < 0 || lineIndex >= text.Lines.Count) return null;

        return text.Lines[lineIndex].ToString().Trim();
    }
}
