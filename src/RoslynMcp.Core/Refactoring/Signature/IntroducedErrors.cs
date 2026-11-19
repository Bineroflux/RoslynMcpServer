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
    /// The errors in the source-generated documents of <paramref name="projects"/> that
    /// <paramref name="after"/> has and <paramref name="before"/> had not, located where the
    /// generated code's <c>#line</c> directives map them (a Razor component's errors in its
    /// <c>.razor</c> file), for the files <paramref name="mappedTo"/> accepts; null when there are
    /// none. Written and compared as <see cref="FindAsync"/> does, lines included.
    /// </summary>
    /// <param name="before">The solution before the change.</param>
    /// <param name="after">The solution after the change.</param>
    /// <param name="projects">The projects whose generated documents to check.</param>
    /// <param name="mappedTo">Whether errors located in a file count.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<IReadOnlyList<string>?> FindInGeneratedAsync(
        Solution before,
        Solution after,
        IReadOnlyCollection<ProjectId> projects,
        Func<string, bool> mappedTo,
        CancellationToken cancellationToken)
    {
        var remaining = (await GetGeneratedErrorsAsync(before, projects, mappedTo, cancellationToken))
            .GroupBy(e => e.Key)
            .ToDictionary(g => g.Key, g => g.Count());

        var added = new List<string>();
        foreach (var (key, text) in await GetGeneratedErrorsAsync(after, projects, mappedTo, cancellationToken))
        {
            if (remaining.TryGetValue(key, out var count) && count > 0)
                remaining[key] = count - 1;
            else
                added.Add(text);
        }

        return added.Count == 0 ? null : added;
    }

    private static async Task<List<(string Key, string Text)>> GetGeneratedErrorsAsync(
        Solution solution,
        IEnumerable<ProjectId> projects,
        Func<string, bool> mappedTo,
        CancellationToken cancellationToken)
    {
        var errors = new List<(string Path, int Line, int Column, string Key, string Text)>();
        foreach (var project in projects.Select(solution.GetProject).OfType<Project>())
        {
            foreach (var document in await project.GetSourceGeneratedDocumentsAsync(cancellationToken))
            {
                if (await document.GetSemanticModelAsync(cancellationToken) is not { } model)
                    continue;

                foreach (var diagnostic in model.GetDiagnostics(cancellationToken: cancellationToken)
                    .Where(d => d.Severity == DiagnosticSeverity.Error))
                {
                    var mapped = diagnostic.Location.GetMappedLineSpan();
                    var span = mapped.IsValid && mapped.HasMappedPath ? mapped : diagnostic.Location.GetLineSpan();
                    if (!mappedTo(span.Path))
                        continue;

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
        }

        return errors
            .DistinctBy(e => e.Text)
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Line)
            .ThenBy(e => e.Column)
            .Select(e => (e.Key, e.Text))
            .ToList();
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
