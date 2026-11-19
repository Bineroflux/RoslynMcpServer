using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Delete;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Removes the using directives a change made redundant: those the compiler reports a given
/// diagnostic for after the change but did not report it for before (a duplicate the change
/// created, CS0105; a directive the change left unused, CS8019). Directives that already had the
/// diagnostic stay as they are, so a change never cleans up more than it caused.
/// </summary>
internal static class NewlyReportedUsings
{
    /// <summary>CS0105: the using directive appeared previously in this namespace (a warning).</summary>
    public const string DuplicateUsing = "CS0105";

    /// <summary>CS8933: the using directive appeared previously as a global using (hidden).</summary>
    public const string DuplicateOfGlobalUsing = "CS8933";

    /// <summary>CS8019: unnecessary using directive (hidden; reported where doc comments are parsed).</summary>
    public const string UnnecessaryUsing = "CS8019";

    /// <summary>A removed directive: its file, its 1-based line before the removal, and its text.</summary>
    public sealed record Removed(string File, int Line, string Directive)
    {
        /// <summary>The removal as a preview lists it: "Remove 'using X;', which only <paramref name="usedBy"/> used".</summary>
        public PendingChange ToPendingChange(string usedBy) => new()
        {
            File = File,
            ChangeType = ChangeKind.Modify,
            Description = $"Remove '{Directive}', which only {usedBy} used",
            BeforeSnippet = Directive,
            StartLine = Line,
            EndLine = Line
        };
    }

    /// <summary>
    /// <see cref="RemoveAsync"/> for the usings a move of code out of <paramref name="documents"/>
    /// left unused (CS8019 after the move, not before); global usings stay.
    /// </summary>
    public static Task<(Solution Solution, List<Removed> Removed)> RemoveLeftUnusedAsync(
        Solution before,
        Solution after,
        IEnumerable<DocumentId> documents,
        CancellationToken cancellationToken) =>
        RemoveAsync(before, after, documents, [UnnecessaryUsing], includeGlobal: false, cancellationToken);

    /// <summary>
    /// In the files of <paramref name="documents"/>, removes from <paramref name="after"/> each
    /// using directive (at file level or in a namespace declaration) that has one of
    /// <paramref name="diagnosticIds"/> in every document compiling its file where it is
    /// active, and did not have it in <paramref name="before"/> in at least one of them. A
    /// directive is identified across the change by its text and the number of directives
    /// written the same before it. Comment lines right above a removed directive go with it
    /// (see <see cref="DeclarationRemoval.Spans"/>).
    /// </summary>
    /// <param name="before">The solution before the change.</param>
    /// <param name="after">The solution after it.</param>
    /// <param name="documents">Documents of the files to clean up (the other documents of each file are included).</param>
    /// <param name="diagnosticIds">The diagnostics that make a directive redundant.</param>
    /// <param name="includeGlobal">Whether <c>global using</c> directives may be removed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<(Solution Solution, List<Removed> Removed)> RemoveAsync(
        Solution before,
        Solution after,
        IEnumerable<DocumentId> documents,
        IReadOnlyCollection<string> diagnosticIds,
        bool includeGlobal,
        CancellationToken cancellationToken)
    {
        var result = after;
        var removed = new List<Removed>();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in documents)
        {
            if (after.GetDocument(id) is not { } document)
                continue;

            var ids = document.FilePath == null ? [id] : after.GetDocumentIdsWithFilePath(document.FilePath);
            if (document.FilePath != null && !files.Add(document.FilePath))
                continue;

            var present = new Dictionary<(string, int), int>();
            var reported = new Dictionary<(string, int), int>();
            var newlyReported = new HashSet<(string, int)>();
            var nodes = new Dictionary<(string, int), List<UsingDirectiveSyntax>>();
            foreach (var linked in ids)
            {
                var version = after.GetDocument(linked)!;
                if (await version.GetSyntaxRootAsync(cancellationToken) is not CompilationUnitSyntax root)
                    continue;

                var directives = Keyed(root, includeGlobal);
                foreach (var (key, directive) in directives)
                {
                    present[key] = present.GetValueOrDefault(key) + 1;
                    if (!nodes.TryGetValue(key, out var list))
                        nodes[key] = list = [];
                    list.Add(directive);
                }

                var now = await ReportedAsync(version, directives, diagnosticIds, cancellationToken);
                if (now.Count == 0)
                    continue;

                var earlier = before.GetDocument(linked) is { } original &&
                    await original.GetSyntaxRootAsync(cancellationToken) is CompilationUnitSyntax originalRoot
                        ? await ReportedAsync(original, Keyed(originalRoot, includeGlobal), diagnosticIds, cancellationToken)
                        : [];
                foreach (var key in now)
                {
                    reported[key] = reported.GetValueOrDefault(key) + 1;
                    if (!earlier.Contains(key))
                        newlyReported.Add(key);
                }
            }

            var keys = newlyReported.Where(key => reported[key] == present[key]).ToList();
            if (keys.Count == 0)
                continue;

            var text = await document.GetTextAsync(cancellationToken);
            var directivesToRemove = keys.SelectMany(key => nodes[key]).DistinctBy(d => d.Span).ToList();
            foreach (var directive in directivesToRemove.OrderBy(d => d.SpanStart))
            {
                removed.Add(new Removed(
                    document.FilePath ?? document.Name,
                    text.Lines.GetLineFromPosition(directive.SpanStart).LineNumber + 1,
                    directive.WithoutTrivia().ToString()));
            }

            var newText = DeclarationRemoval.Remove(text, DeclarationRemoval.Spans(text, directivesToRemove));
            foreach (var linked in ids)
                result = result.WithDocumentText(linked, newText);
        }

        return (result, removed);
    }

    /// <summary>
    /// The using directives of a file, at file level and inside namespace declarations, each
    /// with its text and the number of directives written the same before it.
    /// </summary>
    private static List<((string, int) Key, UsingDirectiveSyntax Directive)> Keyed(CompilationUnitSyntax root, bool includeGlobal)
    {
        var keyed = new List<((string, int), UsingDirectiveSyntax)>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var directive in root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax).OfType<UsingDirectiveSyntax>())
        {
            if (!includeGlobal && directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword))
                continue;

            var written = directive.WithoutTrivia().ToString();
            var occurrence = seen.GetValueOrDefault(written);
            seen[written] = occurrence + 1;
            keyed.Add(((written, occurrence), directive));
        }

        return keyed;
    }

    /// <summary>The directives of <paramref name="document"/> that have one of <paramref name="diagnosticIds"/>.</summary>
    private static async Task<HashSet<(string, int)>> ReportedAsync(
        Document document,
        List<((string, int) Key, UsingDirectiveSyntax Directive)> directives,
        IReadOnlyCollection<string> diagnosticIds,
        CancellationToken cancellationToken)
    {
        var reported = new HashSet<(string, int)>();
        if (directives.Count == 0 || await GetSemanticModelAsync(document, cancellationToken) is not { } model)
            return reported;

        // The compiler reports unused directives only for the whole file (a directive outside
        // a span may be used inside it); the others are found in each directive's span.
        var diagnostics = diagnosticIds.Contains(UnnecessaryUsing)
            ? model.GetDiagnostics(cancellationToken: cancellationToken)
            : [.. directives.SelectMany(d => model.GetDiagnostics(d.Directive.Span, cancellationToken))];
        var spans = diagnostics.Where(d => diagnosticIds.Contains(d.Id)).Select(d => d.Location.SourceSpan).ToList();
        foreach (var (key, directive) in directives)
        {
            if (spans.Any(span => directive.Span.Contains(span)))
                reported.Add(key);
        }

        return reported;
    }

    /// <summary>
    /// The semantic model whose diagnostics are those of <paramref name="document"/>'s usings. A
    /// compilation binds the global usings of all its files once and reports their diagnostics
    /// only to the semantic model that triggered that binding, so a file declaring global usings
    /// is read through a fresh copy of the compilation (as in <c>IntroducedErrors</c>).
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
}
