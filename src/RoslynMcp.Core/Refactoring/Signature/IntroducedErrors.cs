using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
    /// <param name="before">The solution before the change.</param>
    /// <param name="after">The solution after the change.</param>
    /// <param name="documents">The documents to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="matchLines">
    /// Whether an error must also stay on its line to count as the same. A change that adds or
    /// removes lines (moving code into a new method, inserting a property) passes false, so the
    /// errors below it, which only moved, are not reported.
    /// </param>
    public static async Task<IReadOnlyList<string>?> FindAsync(
        Solution before,
        Solution after,
        IReadOnlyCollection<DocumentId> documents,
        CancellationToken cancellationToken,
        bool matchLines = true)
    {
        var remaining = (await GetErrorsAsync(before, documents, matchLines, cancellationToken))
            .GroupBy(e => e.Key)
            .ToDictionary(g => g.Key, g => g.Count());

        var added = new List<string>();
        foreach (var (key, text) in await GetErrorsAsync(after, documents, matchLines, cancellationToken))
        {
            if (remaining.TryGetValue(key, out var count) && count > 0)
                remaining[key] = count - 1;
            else
                added.Add(text);
        }

        return added.Count == 0 ? null : added;
    }

    /// <summary>
    /// The semantic model whose diagnostics are <paramref name="document"/>'s errors. A compilation
    /// binds the global usings of all its files once, and reports their errors only to the semantic
    /// model that triggered that binding: once another file has been bound, an unresolvable
    /// <c>global using</c> (e.g. in the SDK's <c>GlobalUsings.g.cs</c> after a namespace rename)
    /// reports nothing. A file declaring global usings is therefore checked on a fresh copy of
    /// the compilation.
    /// </summary>
    private static async Task<SemanticModel?> GetSemanticModelAsync(Document document, CancellationToken cancellationToken)
    {
        if (await document.GetSyntaxRootAsync(cancellationToken) is CompilationUnitSyntax root &&
            root.Usings.Any(u => u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)) &&
            await document.Project.GetCompilationAsync(cancellationToken) is { } compilation)
        {
            return compilation.Clone().GetSemanticModel(root.SyntaxTree);
        }

        return await document.GetSemanticModelAsync(cancellationToken);
    }

    private static async Task<List<(string Key, string Text)>> GetErrorsAsync(
        Solution solution,
        IEnumerable<DocumentId> documents,
        bool matchLines,
        CancellationToken cancellationToken)
    {
        var errors = new List<(string Path, int Line, int Column, string Key, string Text)>();
        foreach (var id in documents)
        {
            if (solution.GetDocument(id) is not { } document ||
                await GetSemanticModelAsync(document, cancellationToken) is not { } model)
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
                    matchLines ? $"{span.Path}|{line}|{diagnostic.Id}|{message}" : $"{span.Path}|{diagnostic.Id}|{message}",
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
