using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Lays out code that operations generate. Generated syntax is built at column 0, so it is
/// marked with <see cref="Formatter.Annotation"/> and only the marked nodes (plus the
/// whitespace right around them) are formatted before the commit: they get the
/// indentation, line breaks and line ending of their surroundings while the rest of the
/// file stays exactly as it was. Generated type names are written fully qualified and
/// marked with <see cref="Simplifier.Annotation"/>; only those are simplified.
/// </summary>
internal static class GeneratedCodeFormatter
{
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

        return document.Project.Solution;
    }
}
