using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Picks the line ending for code written into a document, so generated lines match the
/// file instead of always using CRLF (the <c>NormalizeWhitespace</c> default), and gives code
/// moved from another file the line ending of the file it moves to.
/// </summary>
internal static class LineEndings
{
    private const string MovedKind = "RoslynMcp.MovedCode";

    /// <summary>
    /// The document's line ending: its first line break. A document without one (empty or
    /// new) uses the <c>end_of_line</c> of its <c>.editorconfig</c>, else the line ending most
    /// of its project's files use, else <see cref="Environment.NewLine"/>.
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

        return await ProjectFileConventions.GetEditorConfigLineEndingAsync(document, cancellationToken)
            ?? await ProjectFileConventions.GetPrevailingLineEndingAsync(document.Project, cancellationToken)
            ?? Environment.NewLine;
    }

    /// <summary>
    /// Marks code an operation copies from one file into another without laying it out anew,
    /// so <see cref="GeneratedCodeFormatter.FormatAsync"/> gives its line breaks the line ending
    /// of the file it goes into (see <see cref="Convert"/>). Code it formats or keeps the
    /// layout of is converted anyway.
    /// </summary>
    public static TNode MarkMoved<TNode>(TNode node)
        where TNode : SyntaxNode =>
        node.WithAdditionalAnnotations(new SyntaxAnnotation(MovedKind));

    /// <summary>The nodes of <paramref name="root"/> marked with <see cref="MarkMoved"/>.</summary>
    public static IEnumerable<SyntaxNode> MovedNodes(SyntaxNode root) => root.GetAnnotatedNodes(MovedKind);

    /// <summary>
    /// <paramref name="node"/> with every line break written as <paramref name="lineEnding"/>:
    /// those that end lines of code and directives, and those inside comments, documentation
    /// comments and disabled text. A line break inside a string literal (verbatim, raw, or an
    /// interpolated verbatim or raw string) is part of its value, so it stays as it is; the
    /// literals that keep a different one are counted in <paramref name="keptLiterals"/>. The
    /// same instance when nothing changes.
    /// </summary>
    public static TNode Convert<TNode>(TNode node, string lineEnding, out int keptLiterals)
        where TNode : SyntaxNode
    {
        var rewriter = new LineBreakRewriter(lineEnding);
        var converted = (TNode)rewriter.Visit(node);
        keptLiterals = rewriter.KeptLiterals.Count;
        return converted;
    }

    /// <summary>
    /// Gives the whole of a document (a file an operation creates) the one line ending
    /// <paramref name="lineEnding"/> (see <see cref="Convert"/>); a string literal that keeps
    /// another is reported in <paramref name="warnings"/>.
    /// </summary>
    public static async Task<Solution> ConvertDocumentAsync(
        Solution solution,
        DocumentId documentId,
        string lineEnding,
        ICollection<string>? warnings,
        CancellationToken cancellationToken)
    {
        var document = solution.GetDocument(documentId);
        if (document == null || await document.GetSyntaxRootAsync(cancellationToken) is not { } root)
            return solution;

        var converted = Convert(root, lineEnding, out var kept);
        if (kept > 0)
            warnings?.Add(KeptLiteralsWarning(document.FilePath ?? document.Name, kept, lineEnding));

        return ReferenceEquals(converted, root) ? solution : solution.WithDocumentSyntaxRoot(documentId, converted);
    }

    /// <summary>
    /// What to report when <paramref name="count"/> string literals moved into
    /// <paramref name="filePath"/> keep line breaks other than its <paramref name="lineEnding"/>.
    /// </summary>
    public static string KeptLiteralsWarning(string filePath, int count, string lineEnding) =>
        $"'{filePath}': {(count == 1 ? "a string literal" : $"{count} string literals")} of the code moved there " +
        $"{(count == 1 ? "keeps its" : "keep their")} line breaks, which are part of {(count == 1 ? "its" : "their")} value; " +
        $"the file's other lines end in {Name(lineEnding)}.";

    /// <summary>"LF", "CRLF" or "CR".</summary>
    public static string Name(string lineEnding) => lineEnding switch
    {
        "\r\n" => "CRLF",
        "\r" => "CR",
        _ => "LF",
    };

    /// <summary>Whether <paramref name="text"/> has a line break other than <paramref name="lineEnding"/>.</summary>
    private static bool HasOtherLineBreak(string text, string lineEnding)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (LineBreakAt(text, i) is not { } lineBreak)
                continue;
            if (lineBreak != lineEnding)
                return true;
            i += lineBreak.Length - 1;
        }

        return false;
    }

    /// <summary><paramref name="text"/> with every line break written as <paramref name="lineEnding"/>.</summary>
    private static string WithLineBreaks(string text, string lineEnding)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (LineBreakAt(text, i) is { } lineBreak)
            {
                builder.Append(lineEnding);
                i += lineBreak.Length - 1;
            }
            else
            {
                builder.Append(text[i]);
            }
        }

        return builder.ToString();
    }

    private static string? LineBreakAt(string text, int i) => text[i] switch
    {
        '\r' => i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : "\r",
        '\n' => "\n",
        _ => null,
    };

    /// <summary>
    /// Writes line breaks in one line ending, into structured trivia (directives, documentation
    /// comments) too. Tokens keep their text, except the line breaks of documentation comments;
    /// a token that holds another line break is part of a string literal.
    /// </summary>
    private sealed class LineBreakRewriter(string lineEnding) : CSharpSyntaxRewriter(visitIntoStructuredTrivia: true)
    {
        public HashSet<SyntaxNode> KeptLiterals { get; } = [];

        public override SyntaxTrivia VisitTrivia(SyntaxTrivia trivia)
        {
            switch (trivia.Kind())
            {
                case SyntaxKind.EndOfLineTrivia:
                    return trivia.ToString() == lineEnding ? trivia : SyntaxFactory.EndOfLine(lineEnding);

                case SyntaxKind.MultiLineCommentTrivia when HasOtherLineBreak(trivia.ToString(), lineEnding):
                    return SyntaxFactory.Comment(WithLineBreaks(trivia.ToString(), lineEnding));

                case SyntaxKind.DisabledTextTrivia when HasOtherLineBreak(trivia.ToString(), lineEnding):
                    return SyntaxFactory.DisabledText(WithLineBreaks(trivia.ToString(), lineEnding));

                default:
                    return base.VisitTrivia(trivia);
            }
        }

        public override SyntaxToken VisitToken(SyntaxToken token)
        {
            var isXmlLineBreak = token.IsKind(SyntaxKind.XmlTextLiteralNewLineToken);
            if (!isXmlLineBreak && HasOtherLineBreak(token.Text, lineEnding))
            {
                // A verbatim or raw string, or a part of an interpolated one: its line breaks are its value.
                if ((token.Parent is InterpolatedStringTextSyntax text ? text.Parent : token.Parent) is { } literal)
                    KeptLiterals.Add(literal);
            }

            var visited = base.VisitToken(token);
            return isXmlLineBreak && token.Text != lineEnding
                ? SyntaxFactory.Token(visited.LeadingTrivia, SyntaxKind.XmlTextLiteralNewLineToken, lineEnding, lineEnding, visited.TrailingTrivia)
                : visited;
        }
    }
}
