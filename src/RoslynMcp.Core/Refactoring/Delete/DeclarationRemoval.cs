using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp.Core.Refactoring.Delete;

/// <summary>
/// Removes declarations from source text. Working on the text, not the tree, leaves every other
/// character (line endings, indentation, comments elsewhere) exactly as it was.
/// </summary>
internal static class DeclarationRemoval
{
    /// <summary>
    /// The text to remove to delete <paramref name="node"/>, a declaration as
    /// <see cref="SymbolDeclarations.Nodes"/> returns it.
    /// </summary>
    /// <remarks>
    /// A declaration on lines of its own takes those lines, together with the comment and
    /// documentation-comment lines right above it (up to a blank line or a preprocessor
    /// directive) and a comment at the end of its last line. One blank line next to it goes too
    /// when the declaration sat between two blank lines, or between a blank line and a closing
    /// brace or the end of the file, or between an opening brace (or the start of the file) and a
    /// blank line, so blank lines don't pile up. One variable of several (<c>int a, b;</c>) or an
    /// enum member sharing its line takes its comma with it. A declaration sharing a line with
    /// other code takes only itself and the spaces after it.
    /// </remarks>
    public static TextSpan Span(SourceText text, SyntaxNode node)
    {
        switch (node)
        {
            case VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration }:
                return ElementSpan(declaration.Variables, node);
            case EnumMemberDeclarationSyntax { Parent: EnumDeclarationSyntax @enum } member when !OwnsItsLines(text, member, EndWithComma(member)):
                return ElementSpan(@enum.Members, node);
        }

        var end = node is EnumMemberDeclarationSyntax enumMember ? EndWithComma(enumMember) : node.Span.End;
        if (!OwnsItsLines(text, node, end))
        {
            // The spaces after it, or before it when it ends the line.
            var before = node.SpanStart;
            var after = end;
            while (after < text.Length && text[after] is ' ' or '\t')
                after++;
            if (after == text.Length || text[after] is '\r' or '\n')
            {
                after = end;
                while (before > 0 && text[before - 1] is ' ' or '\t')
                    before--;
            }

            return TextSpan.FromBounds(before, after);
        }

        var root = node.SyntaxTree.GetRoot();
        var first = text.Lines.GetLineFromPosition(node.SpanStart).LineNumber;
        while (first > 0 && text.Lines[first - 1].Start >= node.FullSpan.Start && IsCommentLine(text, root, text.Lines[first - 1]))
            first--;

        var last = text.Lines.GetLineFromPosition(end).LineNumber;
        var start = text.Lines[first].Start;
        var stop = text.Lines[last].EndIncludingLineBreak;

        var previous = first - 1;
        var next = last + 1;
        var hasNext = next < text.Lines.Count && !(next == text.Lines.Count - 1 && text.Lines[next].Span.IsEmpty);
        var previousBlank = previous >= 0 && IsBlank(text.Lines[previous]);
        var nextBlank = hasNext && IsBlank(text.Lines[next]);

        if (previousBlank && (nextBlank || !hasNext || FirstChar(text.Lines[next]) == '}'))
            start = text.Lines[previous].Start;
        else if ((previous < 0 || LastChar(text.Lines[previous]) == '{') && nextBlank)
            stop = text.Lines[next].EndIncludingLineBreak;

        return TextSpan.FromBounds(start, stop);
    }

    /// <summary>
    /// <paramref name="text"/> without <paramref name="spans"/>; overlapping spans are merged.
    /// </summary>
    public static SourceText Remove(SourceText text, IEnumerable<TextSpan> spans)
    {
        var merged = new List<TextSpan>();
        foreach (var span in spans.OrderBy(s => s.Start))
        {
            if (merged.Count > 0 && span.Start <= merged[^1].End)
                merged[^1] = TextSpan.FromBounds(merged[^1].Start, Math.Max(merged[^1].End, span.End));
            else
                merged.Add(span);
        }

        return text.WithChanges(merged.Select(span => new TextChange(span, "")));
    }

    /// <summary>
    /// Whether a file declares nothing: no types, top-level statements, assembly attributes or
    /// global usings, only (empty) namespaces, usings and comments.
    /// </summary>
    public static bool DeclaresNothing(CompilationUnitSyntax root) =>
        root.AttributeLists.Count == 0 &&
        root.Usings.All(u => u.GlobalKeyword.IsKind(SyntaxKind.None)) &&
        root.Members.All(IsEmptyNamespace);

    private static bool IsEmptyNamespace(MemberDeclarationSyntax member) =>
        member is BaseNamespaceDeclarationSyntax ns && ns.Members.All(IsEmptyNamespace);

    /// <summary>
    /// The span of one element of a separated list with one comma: the comma before it, or the
    /// one after it when it is the first.
    /// </summary>
    private static TextSpan ElementSpan<TNode>(SeparatedSyntaxList<TNode> list, SyntaxNode element)
        where TNode : SyntaxNode
    {
        var index = list.IndexOf((TNode)element);
        if (index > 0)
            return TextSpan.FromBounds(list.GetSeparator(index - 1).SpanStart, element.Span.End);
        if (list.Count > 1)
            return TextSpan.FromBounds(element.SpanStart, list[1].SpanStart);
        return list.SeparatorCount > 0
            ? TextSpan.FromBounds(element.SpanStart, list.GetSeparator(0).Span.End)
            : element.Span;
    }

    private static int EndWithComma(EnumMemberDeclarationSyntax member)
    {
        var next = member.GetLastToken().GetNextToken();
        return next.IsKind(SyntaxKind.CommaToken) ? next.Span.End : member.Span.End;
    }

    /// <summary>
    /// Whether the declaration starts its line and only trivia follows <paramref name="end"/> on
    /// its last line.
    /// </summary>
    private static bool OwnsItsLines(SourceText text, SyntaxNode node, int end)
    {
        var startLine = text.Lines.GetLineFromPosition(node.SpanStart);
        for (var i = startLine.Start; i < node.SpanStart; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
                return false;
        }

        var nextToken = node.SyntaxTree.GetRoot().FindToken(end - 1).GetNextToken(includeZeroWidth: true);
        return nextToken.IsKind(SyntaxKind.None) ||
            nextToken.IsKind(SyntaxKind.EndOfFileToken) ||
            text.Lines.GetLineFromPosition(nextToken.SpanStart).LineNumber > text.Lines.GetLineFromPosition(end).LineNumber;
    }

    /// <summary>Whether a line holds only a comment or documentation comment (no directive or disabled code).</summary>
    private static bool IsCommentLine(SourceText text, SyntaxNode root, TextLine line)
    {
        var position = line.Start;
        while (position < line.End && char.IsWhiteSpace(text[position]))
            position++;
        if (position == line.End)
            return false;

        var trivia = root.FindTrivia(position);
        return trivia.Kind() is SyntaxKind.SingleLineCommentTrivia or SyntaxKind.MultiLineCommentTrivia
            or SyntaxKind.SingleLineDocumentationCommentTrivia or SyntaxKind.MultiLineDocumentationCommentTrivia;
    }

    private static bool IsBlank(TextLine line) => string.IsNullOrWhiteSpace(line.ToString());

    private static char FirstChar(TextLine line) => line.ToString().TrimStart().FirstOrDefault();

    private static char LastChar(TextLine line) => line.ToString().TrimEnd().LastOrDefault();
}
