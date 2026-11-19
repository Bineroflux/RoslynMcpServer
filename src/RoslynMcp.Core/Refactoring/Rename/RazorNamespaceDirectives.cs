using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.FileSystem;

namespace RoslynMcp.Core.Refactoring.Rename;

/// <summary>
/// The Razor side of <c>rename_namespace</c>: the directives of the project's <c>.razor</c> and
/// <c>.cshtml</c> files (<c>_Imports.razor</c> and <c>_ViewImports.cshtml</c> among them), which
/// the workspace holds as additional documents for the Razor generator. They are rewritten as
/// text, line by line, segment by segment: a <c>@using</c> or <c>@namespace</c> whose name is the
/// old namespace or one under it (<c>@using Company.Old.Pages</c> becomes
/// <c>@using Company.New.Pages</c>, <c>@using Company.OldStuff</c> stays), and in the directives
/// that take a type (<c>@inherits</c>, <c>@inject</c>, <c>@model</c>, <c>@implements</c>,
/// <c>@attribute</c>, <c>@typeparam … where …</c>, <c>@layout</c>) the old namespace where it
/// starts a qualified name (<c>@inherits Company.Old.BasePage</c>), outside string literals.
/// Any other mention of the old name in a Razor file (C# in <c>@code</c> blocks, markup, a
/// name relative to a <c>@using</c>) is left and reported by line.
/// </summary>
internal static partial class RazorNamespaceDirectives
{
    /// <summary>
    /// A directive at the start of a line: <c>@using</c> (also <c>static</c>, an alias or
    /// <c>global::</c>) or <c>@namespace</c>, and the dotted name it holds.
    /// </summary>
    [GeneratedRegex(@"^\s*@(?<kind>using|namespace)\s+(?:static\s+)?(?:@?[A-Za-z_]\w*\s*=\s*)?(?:global::\s*)?(?<name>@?[A-Za-z_]\w*(?:\s*\.\s*@?[A-Za-z_]\w*)*)", RegexOptions.CultureInvariant)]
    private static partial Regex Directive();

    [GeneratedRegex(@"\s+|@", RegexOptions.CultureInvariant)]
    private static partial Regex NameNoise();

    /// <summary>A directive at the start of a line that takes a type, and the rest of the line.</summary>
    [GeneratedRegex(@"^\s*@(?:inherits|inject|model|implements|attribute|typeparam|layout)\b(?<rest>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex TypeDirective();

    /// <summary>The rewrite of one Razor file, the same for every project that has it.</summary>
    /// <param name="Path">The file.</param>
    /// <param name="Documents">Its additional documents, one per project (target framework).</param>
    /// <param name="Text">Its text before the rename.</param>
    /// <param name="NewText">Its text after it.</param>
    /// <param name="Directives">How many directives were rewritten.</param>
    /// <param name="NamespaceDirectives">How many of them were <c>@namespace</c> directives.</param>
    public sealed record FileRewrite(
        string Path,
        IReadOnlyList<TextDocument> Documents,
        SourceText Text,
        SourceText NewText,
        int Directives,
        int NamespaceDirectives);

    /// <summary>
    /// The places where the old namespace starts a qualified name: a whole-segment match (also
    /// after <c>global::</c>) that a dot and a name follow; the <c>name</c> group is the old name.
    /// </summary>
    private static Regex QualifiedPrefix(IReadOnlyList<string> oldSegments) => new(
        @"(?<![\w.@])(?:global::\s*)?(?<name>" + string.Join(@"\s*\.\s*", oldSegments.Select(s => "@?" + Regex.Escape(s))) +
        @")(?=\s*\.\s*@?[A-Za-z_])",
        RegexOptions.CultureInvariant);

    /// <summary>Whether <paramref name="index"/> of <paramref name="line"/> lies inside a string literal.</summary>
    private static bool InString(string line, int index) => line[..index].Count(c => c == '"') % 2 == 1;

    /// <summary>What the rename changes in Razor files, and what it leaves.</summary>
    /// <param name="Files">The files to rewrite.</param>
    /// <param name="Mentions">
    /// Each mention of the old namespace outside such a directive, as <c>path(line)</c>.
    /// </param>
    /// <param name="Projects">The projects with Razor files that mention the old namespace's last segment.</param>
    public sealed record Plan(List<FileRewrite> Files, List<string> Mentions, HashSet<ProjectId> Projects);

    /// <summary>Whether <paramref name="path"/> is a Razor file (<c>.razor</c>, <c>.cshtml</c>).</summary>
    public static bool IsRazorFile(string? path) =>
        path != null &&
        (path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Plans the rewrite of the Razor files of <paramref name="solution"/>.
    /// </summary>
    /// <param name="solution">The solution before the rename.</param>
    /// <param name="oldSegments">The old namespace's segments.</param>
    /// <param name="covers">Whether a namespace is the old one or under it.</param>
    /// <param name="map">The new name of a namespace <paramref name="covers"/> accepts.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<Plan> PlanAsync(
        Solution solution,
        IReadOnlyList<string> oldSegments,
        Func<string, bool> covers,
        Func<string, string> map,
        CancellationToken cancellationToken)
    {
        var plan = new Plan([], [], []);
        var lastSegment = oldSegments[^1];
        var newName = map(string.Join('.', oldSegments));
        var qualified = QualifiedPrefix(oldSegments);
        var mention = new Regex(
            @"(?<![\w.@])(?:global::\s*)?" + string.Join(@"\s*\.\s*", oldSegments.Select(s => "@?" + Regex.Escape(s))) + @"(?!\w)",
            RegexOptions.CultureInvariant);

        var files = solution.Projects
            .SelectMany(p => p.AdditionalDocuments)
            .Where(d => IsRazorFile(d.FilePath))
            .GroupBy(d => PathResolver.NormalizePath(d.FilePath!), PathResolver.PathComparer);
        foreach (var file in files)
        {
            var documents = file.ToList();
            var text = await documents[0].GetTextAsync(cancellationToken);
            if (text.ToString().IndexOf(lastSegment, StringComparison.Ordinal) < 0)
                continue;

            plan.Projects.UnionWith(documents.Select(d => d.Project.Id));
            var changes = new List<TextChange>();
            var namespaces = 0;
            foreach (var line in text.Lines)
            {
                var lineText = line.ToString();
                if (TypeDirective().Match(lineText) is { Success: true } typeDirective)
                {
                    var rest = typeDirective.Groups["rest"];
                    var rewritten = new List<TextChange>();
                    foreach (Match match in qualified.Matches(lineText, rest.Index))
                    {
                        var old = match.Groups["name"];
                        if (!InString(lineText, old.Index))
                            rewritten.Add(new TextChange(new TextSpan(old.Index, old.Length), newName));
                    }

                    changes.AddRange(rewritten.Select(c => new TextChange(new TextSpan(line.Start + c.Span.Start, c.Span.Length), c.NewText!)));
                    if (mention.IsMatch(SourceText.From(lineText).WithChanges(rewritten).ToString()))
                        plan.Mentions.Add($"{documents[0].FilePath}({line.LineNumber + 1})");
                    continue;
                }

                var directive = Directive().Match(lineText);
                if (directive.Success)
                {
                    var name = directive.Groups["name"];
                    var written = NameNoise().Replace(name.Value, "");
                    if (covers(written))
                    {
                        changes.Add(new TextChange(new TextSpan(line.Start + name.Index, name.Length), map(written)));
                        if (directive.Groups["kind"].Value == "namespace")
                            namespaces++;

                        // The rest of the line (a comment) is not looked at.
                        continue;
                    }
                }

                if (mention.IsMatch(lineText))
                    plan.Mentions.Add($"{documents[0].FilePath}({line.LineNumber + 1})");
            }

            if (changes.Count > 0)
                plan.Files.Add(new FileRewrite(documents[0].FilePath!, documents, text, text.WithChanges(changes), changes.Count, namespaces));
        }

        return plan;
    }

    /// <summary><paramref name="solution"/> with the Razor files' additional documents rewritten.</summary>
    public static Solution Apply(Solution solution, Plan plan)
    {
        foreach (var file in plan.Files)
        {
            foreach (var document in file.Documents)
                solution = solution.WithAdditionalDocumentText(document.Id, file.NewText, PreservationMode.PreserveIdentity);
        }

        return solution;
    }
}
