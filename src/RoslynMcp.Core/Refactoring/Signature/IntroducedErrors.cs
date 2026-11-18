using System.Globalization;
using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Refactoring.Signature;

/// <summary>
/// The compiler errors a signature change introduces, for operations that report them as
/// warnings instead of failing: a declaration-only type change, a method group converted to a
/// delegate, a removed parameter the body still uses.
/// </summary>
internal static class IntroducedErrors
{
    /// <summary>
    /// The errors in <paramref name="documents"/> of <paramref name="after"/> that were not in
    /// <paramref name="before"/>, as the compiler writes them
    /// (<c>path(line,column): error id: message</c>), or null when there are none. Errors are
    /// compared without their column, which a longer or shorter name on the line shifts.
    /// </summary>
    public static async Task<IReadOnlyList<string>?> FindAsync(
        Solution before,
        Solution after,
        IReadOnlyCollection<DocumentId> documents,
        CancellationToken cancellationToken)
    {
        var remaining = (await GetErrorsAsync(before, documents, cancellationToken))
            .GroupBy(e => e.Key)
            .ToDictionary(g => g.Key, g => g.Count());

        var added = new List<string>();
        foreach (var (key, text) in await GetErrorsAsync(after, documents, cancellationToken))
        {
            if (remaining.TryGetValue(key, out var count) && count > 0)
                remaining[key] = count - 1;
            else
                added.Add(text);
        }

        return added.Count == 0 ? null : added;
    }

    private static async Task<List<(string Key, string Text)>> GetErrorsAsync(
        Solution solution,
        IEnumerable<DocumentId> documents,
        CancellationToken cancellationToken)
    {
        var errors = new List<(string Path, int Line, int Column, string Key, string Text)>();
        foreach (var id in documents)
        {
            if (solution.GetDocument(id) is not { } document ||
                await document.GetSemanticModelAsync(cancellationToken) is not { } model)
            {
                continue;
            }

            foreach (var diagnostic in model.GetDiagnostics(cancellationToken: cancellationToken)
                .Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                var span = diagnostic.Location.GetLineSpan();
                var (line, column) = (span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
                var message = diagnostic.GetMessage(CultureInfo.InvariantCulture);
                errors.Add((
                    span.Path,
                    line,
                    column,
                    $"{span.Path}|{line}|{diagnostic.Id}|{message}",
                    $"{span.Path}({line},{column}): error {diagnostic.Id}: {message}"));
            }
        }

        // A file shared by several projects (multi-targeting) reports its errors once per project.
        return errors
            .DistinctBy(e => e.Text)
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Line)
            .ThenBy(e => e.Column)
            .Select(e => (e.Key, e.Text))
            .ToList();
    }
}
