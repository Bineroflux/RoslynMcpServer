using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Lays out code that operations generate. Generated syntax is built at column 0, so it is
/// marked with <see cref="Formatter.Annotation"/> and only the marked nodes (plus the
/// whitespace right around them) are formatted before the commit: they get the
/// indentation and line breaks of their surroundings while the rest of the file stays
/// exactly as it was.
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
    /// Formats the marked nodes of one document of <paramref name="solution"/>.
    /// </summary>
    public static async Task<Solution> FormatAsync(
        Solution solution,
        DocumentId documentId,
        CancellationToken cancellationToken)
    {
        var document = solution.GetDocument(documentId);
        if (document == null)
        {
            return solution;
        }

        var formatted = await Formatter.FormatAsync(
            document, Formatter.Annotation, cancellationToken: cancellationToken);
        return formatted.Project.Solution;
    }
}
