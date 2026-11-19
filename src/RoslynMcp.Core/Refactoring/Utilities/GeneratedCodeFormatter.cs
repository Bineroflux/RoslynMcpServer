using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Lays out code that operations generate. Generated syntax is built at column 0, so it is
/// marked with <see cref="Formatter.Annotation"/> and only the marked nodes (plus the
/// whitespace right around them) are formatted before the commit: they get the
/// indentation, line breaks and line ending of their surroundings while the rest of the
/// file stays exactly as it was. Generated type names are written fully qualified and
/// marked with <see cref="Simplifier.Annotation"/>; only those are simplified. Code an
/// operation moves into generated code (<see cref="KeepLayout{TNode}(TNode, StatementSyntax, bool, bool)"/>)
/// keeps the layout it was written with.
/// </summary>
internal static class GeneratedCodeFormatter
{
    private const string KeepLayoutKind = "RoslynMcp.KeepLayout";

    /// <summary>The code as written, for each node marked to keep its layout.</summary>
    private static readonly ConditionalWeakTable<SyntaxAnnotation, KeptLayout> KeptLayouts = new();

    /// <summary>Moved code and where it sits: one of a run of whole statements, or an expression inside a line.</summary>
    /// <param name="Original">The code as it was written.</param>
    /// <param name="Indentation">The indentation of the line it started on.</param>
    /// <param name="FirstStatement">The first of a run of statements: it starts a line, without the blank lines before it.</param>
    /// <param name="LastStatement">The last of a run of statements: it ends its line.</param>
    /// <param name="Inline">An expression: its first line stays where the formatter put it, its other lines move along.</param>
    private sealed record KeptLayout(SyntaxNode Original, string Indentation, bool FirstStatement, bool LastStatement, bool Inline);

    /// <summary>
    /// Marks a statement an operation moves into generated code (a node <see cref="FormatAsync"/>
    /// lays out): after formatting, the statement is put back as it was written (single-line
    /// statements and blocks, blank lines, comments, spacing, wrapped arguments), with only the
    /// indentation of its lines changed by as much as its first line moved. Lines inside a
    /// string, comment or directive are not touched.
    /// </summary>
    /// <param name="node">The statement as it goes into the new tree (it may be laid out anew there).</param>
    /// <param name="original">The statement as written, in its original tree.</param>
    /// <param name="first">Whether it is the first of the moved statements (blank lines before it are dropped).</param>
    /// <param name="last">Whether it is the last of the moved statements (it gets a line break if it shared its line).</param>
    public static TNode KeepLayout<TNode>(TNode node, StatementSyntax original, bool first, bool last)
        where TNode : SyntaxNode =>
        KeepLayout(node, new KeptLayout(original, IndentationOf(original), first, last, Inline: false));

    /// <summary>
    /// Marks an expression an operation moves into generated code: after formatting, it is put
    /// back as written; its first line stays where the formatter put it, its other lines move
    /// with it.
    /// </summary>
    /// <param name="node">The expression as it goes into the new tree.</param>
    /// <param name="written">The expression with the layout to keep (its content may differ from the original's, e.g. a rewritten declaration).</param>
    /// <param name="original">The expression in its original tree, which tells the indentation it was written at.</param>
    public static TNode KeepLayout<TNode>(TNode node, ExpressionSyntax written, ExpressionSyntax original)
        where TNode : SyntaxNode =>
        KeepLayout(node, new KeptLayout(written, IndentationOf(original), FirstStatement: false, LastStatement: false, Inline: true));

    private static TNode KeepLayout<TNode>(TNode node, KeptLayout layout)
        where TNode : SyntaxNode
    {
        var annotation = new SyntaxAnnotation(KeepLayoutKind);
        KeptLayouts.AddOrUpdate(annotation, layout);
        return node.WithAdditionalAnnotations(annotation);
    }

    /// <summary>
    /// Marks a generated or rewritten node for <see cref="FormatAsync"/>.
    /// </summary>
    public static TNode Mark<TNode>(TNode node)
        where TNode : SyntaxNode =>
        node.WithAdditionalAnnotations(Formatter.Annotation);

    /// <summary>
    /// Marks a node inserted among existing members or statements for
    /// <see cref="FormatAsync"/>. Its outer line breaks become elastic, so the formatter puts
    /// it on its own line(s) with the usual blank lines around it instead of keeping
    /// hard-coded ones.
    /// </summary>
    public static TNode MarkInserted<TNode>(TNode node)
        where TNode : SyntaxNode =>
        Mark(node
            .WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)
            .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed));

    /// <summary>
    /// Simplifies the marked type names and formats the marked nodes of
    /// <paramref name="originalDocument"/>'s counterpart in <paramref name="solution"/>,
    /// using the line ending of the original document. Simplification only shortens names
    /// to what the usings in scope allow; it never adds usings.
    /// </summary>
    public static async Task<Solution> FormatAsync(
        Solution solution,
        Document originalDocument,
        CancellationToken cancellationToken)
    {
        var document = solution.GetDocument(originalDocument.Id);
        if (document == null)
        {
            return solution;
        }

        var lineEnding = await LineEndings.GetAsync(originalDocument, cancellationToken);
        document = await Simplifier.ReduceAsync(document, Simplifier.Annotation, cancellationToken: cancellationToken);
        document = await Formatter.FormatAsync(document, Formatter.Annotation, cancellationToken: cancellationToken);
        document = await RestoreKeptLayoutAsync(document, cancellationToken);

        // Generated nodes carry NormalizeWhitespace's CRLFs and the formatter writes its own
        // NewLine; give every line break it may have touched (inside the marked nodes and
        // between them and their neighbouring tokens) the document's line ending.
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return document.Project.Solution;
        }

        var lineBreaks = root.GetAnnotatedNodes(Formatter.Annotation)
            .SelectMany(node => node.DescendantTrivia()
                .Concat(node.GetFirstToken().GetPreviousToken().TrailingTrivia)
                .Concat(node.GetLastToken().GetNextToken().LeadingTrivia))
            .Where(trivia => trivia.IsKind(SyntaxKind.EndOfLineTrivia) && trivia.ToString() != lineEnding)
            .Distinct()
            .ToList();

        if (lineBreaks.Count > 0)
        {
            root = root.ReplaceTrivia(lineBreaks, (_, _) => SyntaxFactory.EndOfLine(lineEnding));
            document = document.WithSyntaxRoot(root);
        }

        // A documentation comment carried over from another file ends its lines with XML new-line tokens.
        var xmlLineBreaks = root.GetAnnotatedNodes(Formatter.Annotation)
            .SelectMany(node => node.DescendantTokens(descendIntoTrivia: true))
            .Where(token => token.IsKind(SyntaxKind.XmlTextLiteralNewLineToken) && token.Text != lineEnding)
            .ToList();

        if (xmlLineBreaks.Count > 0)
        {
            root = root.ReplaceTokens(xmlLineBreaks, (original, _) => SyntaxFactory.Token(
                original.LeadingTrivia, SyntaxKind.XmlTextLiteralNewLineToken, lineEnding, lineEnding, original.TrailingTrivia));
            document = document.WithSyntaxRoot(root);
        }

        return document.Project.Solution;
    }

    /// <summary>Puts the code marked to keep its layout back as it was written, re-indented to where it is now.</summary>
    private static async Task<Document> RestoreKeptLayoutAsync(Document document, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var kept = root?.GetAnnotatedNodes(KeepLayoutKind).ToList();
        if (root == null || kept is not { Count: > 0 })
            return document;

        var text = await document.GetTextAsync(cancellationToken);
        var newRoot = root.ReplaceNodes(kept, (formatted, rewritten) =>
        {
            if (!KeptLayouts.TryGetValue(formatted.GetAnnotations(KeepLayoutKind).First(), out var layout))
                return rewritten;

            var indentation = LeadingWhitespace(text.Lines.GetLineFromPosition(formatted.SpanStart).ToString());
            var restored = Reindent(layout.Original, layout.Indentation, indentation, startsLine: !layout.Inline);

            if (layout.Inline)
            {
                restored = restored.WithLeadingTrivia(formatted.GetLeadingTrivia()).WithTrailingTrivia(formatted.GetTrailingTrivia());
            }
            else
            {
                if (layout.FirstStatement)
                    restored = restored.WithLeadingTrivia(FromFirstContentLine(restored.GetLeadingTrivia(), indentation));
                if (layout.LastStatement && !restored.GetTrailingTrivia().Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia)))
                    restored = restored.WithTrailingTrivia(formatted.GetTrailingTrivia());
            }

            return formatted.CopyAnnotationsTo(restored)!;
        });

        return document.WithSyntaxRoot(newRoot);
    }

    /// <summary>
    /// The node with the whitespace that starts each of its lines changed from
    /// <paramref name="from"/> to <paramref name="to"/> (lines indented less are left alone);
    /// the text of tokens and comments, so lines inside strings, is not touched.
    /// </summary>
    private static SyntaxNode Reindent(SyntaxNode node, string from, string to, bool startsLine)
    {
        if (from == to)
            return node;

        var lineStarts = new HashSet<SyntaxTrivia>();
        var atLineStart = startsLine;
        void Visit(SyntaxTrivia trivia)
        {
            if (atLineStart && trivia.IsKind(SyntaxKind.WhitespaceTrivia))
                lineStarts.Add(trivia);
            atLineStart = trivia.IsKind(SyntaxKind.EndOfLineTrivia) || trivia.IsDirective || trivia.ToFullString().EndsWith('\n');
        }

        foreach (var token in node.DescendantTokens())
        {
            foreach (var trivia in token.LeadingTrivia)
                Visit(trivia);
            atLineStart = false;
            foreach (var trivia in token.TrailingTrivia)
                Visit(trivia);
        }

        return node.ReplaceTrivia(lineStarts, (original, _) =>
        {
            var whitespace = original.ToString();
            return whitespace.StartsWith(from, StringComparison.Ordinal)
                ? SyntaxFactory.Whitespace(to + whitespace[from.Length..])
                : original;
        });
    }

    /// <summary>Leading trivia without the blank lines before the first comment or token, starting with <paramref name="indentation"/>.</summary>
    private static SyntaxTriviaList FromFirstContentLine(SyntaxTriviaList trivia, string indentation)
    {
        var content = 0;
        while (content < trivia.Count && (trivia[content].IsKind(SyntaxKind.WhitespaceTrivia) || trivia[content].IsKind(SyntaxKind.EndOfLineTrivia)))
            content++;

        var lineStart = content;
        while (lineStart > 0 && !trivia[lineStart - 1].IsKind(SyntaxKind.EndOfLineTrivia))
            lineStart--;

        var kept = trivia.Skip(lineStart).ToList();
        if (kept.Count == 0 || !kept[0].IsKind(SyntaxKind.WhitespaceTrivia))
            kept.Insert(0, SyntaxFactory.Whitespace(indentation));
        return SyntaxFactory.TriviaList(kept);
    }

    /// <summary>The indentation of the line a node starts on.</summary>
    private static string IndentationOf(SyntaxNode node) =>
        LeadingWhitespace(node.SyntaxTree.GetText().Lines.GetLineFromPosition(node.SpanStart).ToString());

    private static string LeadingWhitespace(string line) =>
        line[..(line.Length - line.TrimStart(' ', '\t').Length)];
}
