using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.FileSystem;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Adds a <c>@using</c> directive to a Razor file (<c>.razor</c>, <c>.cshtml</c>), which the
/// workspace holds as additional documents (one per project or target framework) for the Razor
/// generator. The line goes after the file's leading <c>@using</c> directives, else after a
/// leading <c>@page</c> or <c>@namespace</c> (a page's <c>@page</c> comes first), else at the
/// top, in the file's line ending.
/// </summary>
internal static partial class RazorUsings
{
    [GeneratedRegex(@"^\s*@(?<kind>page|namespace|using|inherits|inject|attribute|implements|layout|typeparam|rendermode|model)\b", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingDirective();

    [GeneratedRegex(@"^\s*@using\s+(?:global::\s*)?(?<name>[\w.\s]+?)\s*;?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex UsingDirective();

    /// <summary>
    /// <paramref name="solution"/> with <c>@using <paramref name="ns"/></c> added to the Razor file
    /// <paramref name="path"/>, and the 1-based line it was added as; null when it was not added:
    /// the file has no additional document, or the file or an <c>_Imports.razor</c> /
    /// <c>_ViewImports.cshtml</c> of its folder or a folder above (in the same project) already
    /// imports the namespace.
    /// </summary>
    public static async Task<(Solution Solution, int? Line)> AddAsync(
        Solution solution,
        string path,
        string ns,
        CancellationToken cancellationToken)
    {
        var documents = solution.Projects
            .SelectMany(p => p.AdditionalDocuments)
            .Where(d => d.FilePath != null && PathResolver.PathsEqual(d.FilePath, path))
            .ToList();
        if (documents.Count == 0)
            return (solution, null);

        var text = await documents[0].GetTextAsync(cancellationToken);
        if (Imports(text, ns) || await ImportedAboveAsync(documents[0], ns, cancellationToken))
            return (solution, null);

        var insertion = Insertion(text, ns);
        var newText = text.WithChanges(insertion);
        foreach (var document in documents)
            solution = solution.WithAdditionalDocumentText(document.Id, newText, PreservationMode.PreserveIdentity);
        return (solution, newText.Lines.GetLineFromPosition(insertion.Span.Start + (insertion.NewText![0] == '@' ? 0 : insertion.NewText.Length - 1)).LineNumber + 1);
    }

    private static bool Imports(SourceText text, string ns) =>
        text.Lines.Any(line => UsingDirective().Match(line.ToString()) is { Success: true } match &&
            Regex.Replace(match.Groups["name"].Value, @"\s+", "") == ns);

    /// <summary>Whether an imports file of the document's folder or a folder above imports <paramref name="ns"/>.</summary>
    private static async Task<bool> ImportedAboveAsync(TextDocument document, string ns, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(document.FilePath);
        foreach (var imports in document.Project.AdditionalDocuments)
        {
            var name = Path.GetFileName(imports.FilePath);
            var importsDirectory = Path.GetDirectoryName(imports.FilePath);
            if (name is not ("_Imports.razor" or "_ViewImports.cshtml") || directory == null || importsDirectory == null ||
                !(PathResolver.NormalizePath(directory) + Path.DirectorySeparatorChar).StartsWith(
                    PathResolver.NormalizePath(importsDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Imports(await imports.GetTextAsync(cancellationToken), ns))
                return true;
        }

        return false;
    }

    /// <summary>The change that inserts the directive (see <see cref="RazorUsings"/>).</summary>
    private static TextChange Insertion(SourceText text, string ns)
    {
        var lineEnding = text.Lines.Select(l => text.ToString(TextSpan.FromBounds(l.End, l.EndIncludingLineBreak))).FirstOrDefault(b => b.Length > 0) ?? "\n";
        int? lastUsing = null;
        int? lastHeader = null;
        foreach (var line in text.Lines)
        {
            var lineText = line.ToString();
            if (string.IsNullOrWhiteSpace(lineText))
                continue;

            if (LeadingDirective().Match(lineText) is not { Success: true } directive)
                break;

            switch (directive.Groups["kind"].Value)
            {
                case "using":
                    lastUsing = line.LineNumber;
                    break;
                case "page" or "namespace":
                    lastHeader = line.LineNumber;
                    break;
            }
        }

        if ((lastUsing ?? lastHeader) is not { } after)
            return new TextChange(new TextSpan(0, 0), $"@using {ns}{lineEnding}");

        var anchor = text.Lines[after];
        return anchor.EndIncludingLineBreak > anchor.End
            ? new TextChange(new TextSpan(anchor.EndIncludingLineBreak, 0), $"@using {ns}{lineEnding}")
            : new TextChange(new TextSpan(anchor.End, 0), $"{lineEnding}@using {ns}");
    }
}
