using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Picks the line ending for code written into a document, so generated lines match the
/// file instead of always using CRLF (the <c>NormalizeWhitespace</c> default).
/// </summary>
internal static class LineEndings
{
    /// <summary>
    /// The document's line ending: its first line break. A document without one (empty or
    /// new) uses the <c>end_of_line</c> of its <c>.editorconfig</c>, else
    /// <see cref="Environment.NewLine"/>.
    /// </summary>
    public static async Task<string> GetAsync(Document document, CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken);
        foreach (var line in text.Lines)
        {
            if (line.EndIncludingLineBreak > line.End)
            {
                return text.ToString(TextSpan.FromBounds(line.End, line.EndIncludingLineBreak));
            }
        }

        var tree = await document.GetSyntaxTreeAsync(cancellationToken);
        if (tree != null &&
            document.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GetOptions(tree)
                .TryGetValue("end_of_line", out var endOfLine))
        {
            switch (endOfLine.Trim().ToLowerInvariant())
            {
                case "lf":
                    return "\n";
                case "crlf":
                    return "\r\n";
                case "cr":
                    return "\r";
            }
        }

        return Environment.NewLine;
    }
}
