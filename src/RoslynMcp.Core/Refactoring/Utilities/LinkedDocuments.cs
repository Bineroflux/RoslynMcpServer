using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.FileSystem;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Keeps the documents of one file in step when a project compiles it more than once (one
/// project per target framework, linked files): an operation edits one of them, the others get
/// its text.
/// </summary>
internal static class LinkedDocuments
{
    /// <summary>
    /// <paramref name="changed"/> with the other documents of each file in
    /// <paramref name="edited"/> given that document's new text; documents whose text did not
    /// change stay as they were.
    /// </summary>
    public static async Task<Solution> SyncAsync(
        Solution original,
        Solution changed,
        IEnumerable<DocumentId> edited,
        CancellationToken cancellationToken)
    {
        var result = changed;
        foreach (var id in edited.Distinct())
        {
            if (changed.GetDocument(id) is not { FilePath: { } path } document)
                continue;

            var text = await document.GetTextAsync(cancellationToken);
            if (original.GetDocument(id) is { } before && (await before.GetTextAsync(cancellationToken)).ContentEquals(text))
                continue;

            foreach (var linked in changed.GetDocumentIdsWithFilePath(path).Where(l => l != id))
                result = result.WithDocumentText(linked, text);
        }

        return result;
    }

    /// <summary>
    /// Gives the documents of a file that several projects compile (one per target framework)
    /// one text with the renames of all of them. Roslyn's renamer renames each document on its
    /// own, so a reference in an <c>#if</c> branch that only one of them compiles is renamed in
    /// that document only, and the commit would write one of the versions. A line takes the
    /// first changed version among the documents, <paramref name="preferred"/> first; a file
    /// whose versions differ in their number of lines (a rename changes no line breaks) is left
    /// as the renamer made it.
    /// </summary>
    /// <param name="original">The solution before the rename.</param>
    /// <param name="renamed">The solution the renamer returned.</param>
    /// <param name="preferred">The document whose version wins a line first, e.g. the declaring one.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<Solution> MergeRenamesAsync(
        Solution original,
        Solution renamed,
        DocumentId? preferred,
        CancellationToken cancellationToken)
    {
        var result = renamed;
        var paths = new HashSet<string>(PathResolver.PathComparer);
        var changed = renamed.GetChanges(original).GetProjectChanges()
            .SelectMany(p => p.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true));
        foreach (var id in changed)
        {
            if (renamed.GetDocument(id)?.FilePath is not { } path || !paths.Add(path))
                continue;

            var ids = renamed.GetDocumentIdsWithFilePath(path).OrderBy(d => d == preferred ? 0 : 1).ToList();
            if (ids.Count < 2 || original.GetDocument(ids[0]) is not { } originalDocument)
                continue;

            var originalText = await originalDocument.GetTextAsync(cancellationToken);
            var versions = new List<SourceText>();
            foreach (var linked in ids)
                versions.Add(await renamed.GetDocument(linked)!.GetTextAsync(cancellationToken));

            if (versions.All(v => v.ContentEquals(versions[0])) || versions.Any(v => v.Lines.Count != originalText.Lines.Count))
                continue;

            var merged = new StringBuilder();
            foreach (var line in originalText.Lines)
            {
                var before = originalText.ToString(line.SpanIncludingLineBreak);
                merged.Append(versions
                    .Select(v => v.ToString(v.Lines[line.LineNumber].SpanIncludingLineBreak))
                    .FirstOrDefault(after => after != before) ?? before);
            }

            var text = SourceText.From(merged.ToString(), originalText.Encoding, originalText.ChecksumAlgorithm);
            foreach (var linked in ids)
                result = result.WithDocumentText(linked, text);
        }

        return result;
    }
}
