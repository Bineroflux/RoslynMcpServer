using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Query.Base;
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
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be a .cs file.");

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

        var locations = new List<ReferenceLocationInfo>();
        var seen = new HashSet<(string file, int line, int col)>();
        var totalCount = 0;
        var maxResults = @params.MaxResults ?? int.MaxValue;

        foreach (var searchSymbol in symbolsToSearch)
        {
            var referencedSymbols = await SymbolFinder.FindReferencesAsync(
                searchSymbol, Context.Solution, cancellationToken);

            foreach (var referencedSymbol in referencedSymbols)
            {
                // Add the definition itself
                foreach (var defLocation in referencedSymbol.Definition.Locations.Where(l => l.IsInSource))
                {
                    var lineSpan = defLocation.GetLineSpan();
                    var key = (lineSpan.Path, lineSpan.StartLinePosition.Line, lineSpan.StartLinePosition.Character);
                    if (!seen.Add(key)) continue;
                    totalCount++;
                    if (locations.Count < maxResults)
                    {
                        var snippet = await GetContextSnippetAsync(defLocation, cancellationToken);
                        locations.Add(new ReferenceLocationInfo
                        {
                            File = lineSpan.Path,
                            Line = lineSpan.StartLinePosition.Line + 1,
                            Column = lineSpan.StartLinePosition.Character + 1,
                            ContextSnippet = snippet,
                            IsWriteAccess = false,
                            IsDefinition = true
                        });
                    }
                }

                // Add each reference
                foreach (var refLocation in referencedSymbol.Locations)
                {
                    if (refLocation.Document == null) continue;
                    var span = refLocation.Location.GetLineSpan();
                    var file = refLocation.Document.FilePath ?? span.Path;
                    var key = (file, span.StartLinePosition.Line, span.StartLinePosition.Character);
                    if (!seen.Add(key)) continue;
                    totalCount++;

                    if (locations.Count < maxResults)
                    {
                        var snippet = await GetContextSnippetAsync(refLocation.Location, cancellationToken);
                        locations.Add(new ReferenceLocationInfo
                        {
                            File = file,
                            Line = span.StartLinePosition.Line + 1,
                            Column = span.StartLinePosition.Character + 1,
                            ContextSnippet = snippet,
                            IsWriteAccess = false,
                            IsDefinition = false
                        });
                    }
                }
            }
        }

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
            CandidateFullyQualifiedNames = candidateFqns
        };

        return QueryResult<FindReferencesResult>.Succeeded(operationId, result);
    }

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
