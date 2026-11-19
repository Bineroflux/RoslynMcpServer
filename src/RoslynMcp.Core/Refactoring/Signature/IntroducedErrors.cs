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
    /// The errors <paramref name="after"/> has and <paramref name="before"/> had not in every
    /// document of the projects that compile one of <paramref name="affected"/> and of the
    /// projects that depend on them, written and compared as <see cref="FindAsync"/> does. A
    /// signature change can break code that never names the changed member: a <c>var</c> that
    /// carries its new type on, an overload that now wins or is ambiguous, a type that
    /// implements an interface through an inherited member.
    /// </summary>
    /// <remarks>
    /// Each project's new compilation is checked once as a whole (which binds its files in
    /// parallel); only the documents with errors in it are then compared with how they were
    /// before, since an error elsewhere cannot be a new one. Errors in generated code are
    /// located like <see cref="FindInGeneratedAsync"/> does; errors without a location (no
    /// entry point left) are written <c>project: error id: message</c>.
    /// </remarks>
    /// <param name="before">The solution before the change.</param>
    /// <param name="after">The solution after the change.</param>
    /// <param name="affected">The documents the change edits or that refer to what it changes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="matchLines">As for <see cref="FindAsync"/>.</param>
    public static async Task<IReadOnlyList<string>?> FindInProjectsAsync(
        Solution before,
        Solution after,
        IEnumerable<DocumentId> affected,
        CancellationToken cancellationToken,
        bool matchLines = true)
    {
        var withErrors = new HashSet<DocumentId>();
        var withGeneratedErrors = new HashSet<ProjectId>();
        var unlocated = new List<string>();
        foreach (var project in ProjectsToCheck(after, affected))
        {
            if (await project.GetCompilationAsync(cancellationToken) is not { } compilation)
                continue;

            var errors = compilation.GetDiagnostics(cancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            foreach (var tree in errors.Select(e => e.Location.SourceTree).OfType<SyntaxTree>().Distinct())
            {
                if (after.GetDocumentId(tree) is { } id && after.GetDocument(id) != null)
                    withErrors.Add(id);
                else
                    withGeneratedErrors.Add(project.Id);
            }

            if (errors.Any(e => e.Location.SourceTree == null))
                unlocated.AddRange(await UnlocatedErrorsAsync(before.GetProject(project.Id), project, errors, cancellationToken));
        }

        var added = new List<string>();
        if (withErrors.Count > 0)
            added.AddRange(await FindAsync(before, after, withErrors, cancellationToken, matchLines) ?? []);
        if (withGeneratedErrors.Count > 0)
            added.AddRange(await FindInGeneratedAsync(before, after, withGeneratedErrors, _ => true, cancellationToken) ?? []);
        added.AddRange(unlocated);

        return added.Count == 0 ? null : added;
    }

    /// <summary>
    /// The errors without a location among <paramref name="errors"/> (those of
    /// <paramref name="project"/>'s new compilation) that <paramref name="previous"/> had not.
    /// </summary>
    private static async Task<IEnumerable<string>> UnlocatedErrorsAsync(
        Project? previous,
        Project project,
        IReadOnlyList<Diagnostic> errors,
        CancellationToken cancellationToken)
    {
        static string Text(Project project, Diagnostic diagnostic) =>
            $"{project.FilePath ?? project.Name}: error {diagnostic.Id}: {diagnostic.GetMessage(CultureInfo.InvariantCulture)}";

        var remaining = previous != null && await previous.GetCompilationAsync(cancellationToken) is { } compilation
            ? compilation.GetDiagnostics(cancellationToken)
                .Where(d => d.Severity == DiagnosticSeverity.Error && d.Location.SourceTree == null)
                .Select(d => Text(project, d))
                .ToList()
            : [];

        var added = new List<string>();
        foreach (var text in errors.Where(e => e.Location.SourceTree == null).Select(e => Text(project, e)))
        {
            if (!remaining.Remove(text))
                added.Add(text);
        }

        return added;
    }

    /// <summary>
    /// The projects compiling one of <paramref name="documents"/> (or another document of its
    /// file, one per target framework) and those that depend on them.
    /// </summary>
    private static IEnumerable<Project> ProjectsToCheck(Solution solution, IEnumerable<DocumentId> documents)
    {
        var projects = new HashSet<ProjectId>();
        foreach (var id in documents)
        {
            projects.Add(id.ProjectId);
            if (solution.GetDocument(id)?.FilePath is { } path)
                projects.UnionWith(solution.GetDocumentIdsWithFilePath(path).Select(linked => linked.ProjectId));
        }

        var graph = solution.GetProjectDependencyGraph();
        foreach (var project in projects.ToList())
            projects.UnionWith(graph.GetProjectsThatTransitivelyDependOnThisProject(project));

        return projects.Select(solution.GetProject).OfType<Project>();
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
