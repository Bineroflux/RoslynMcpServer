using Microsoft.CodeAnalysis;

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
}
