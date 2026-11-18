using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Refactoring;

namespace RoslynMcp.Core.Query.Utilities;

/// <summary>
/// Maps a line range onto the region shape Roslyn's flow analysis accepts.
/// </summary>
/// <remarks>
/// <c>AnalyzeDataFlow</c>/<c>AnalyzeControlFlow(first, last)</c> require both statements
/// to be siblings in one statement list. Taking the first and last of <em>all</em>
/// statements inside the range breaks as soon as the range ends inside a nested block
/// (e.g. a method body ending in <c>if (x) { return; }</c>): the last one is the nested
/// <c>return</c>, Roslyn throws, and the tool failed with an internal error.
/// </remarks>
internal static class FlowAnalysisRegion
{
    /// <summary>
    /// Returns the first and last statement of the region: the outermost statements fully
    /// inside <paramref name="span"/>, which must all belong to the same statement list. A
    /// single selected block (e.g. a whole method body) is analyzed through its statements.
    /// Returns null when the span contains no complete statement.
    /// </summary>
    /// <exception cref="RefactoringException">The outermost statements belong to different statement lists.</exception>
    public static (StatementSyntax First, StatementSyntax Last)? FindStatementRange(SyntaxNode root, TextSpan span)
    {
        var outermost = root.DescendantNodes(span)
            .OfType<StatementSyntax>()
            .Where(s => span.Contains(s.Span)
                && !s.Ancestors().OfType<StatementSyntax>().Any(a => span.Contains(a.Span)))
            .ToList();

        if (outermost.Count == 0)
            return null;

        if (outermost is [BlockSyntax { Statements.Count: > 0 } block])
            return (block.Statements.First(), block.Statements.Last());

        var list = StatementList(outermost[0]);
        if (outermost.Any(s => StatementList(s) != list))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidRegion,
                "The selected lines span statements from different blocks. Select complete statements within a single block.");
        }

        return (outermost[0], outermost[^1]);
    }

    /// <summary>
    /// Returns the single outermost expression-bodied member body (<c>=&gt; expr</c>) or
    /// initializer value (<c>= expr</c> of a field, property or variable) fully inside
    /// <paramref name="span"/>, for regions that contain no statements. Parameter default
    /// values are not candidates. Returns null when there is not exactly one candidate.
    /// </summary>
    public static ExpressionSyntax? FindExpression(SyntaxNode root, TextSpan span)
    {
        var candidates = root.DescendantNodes(span)
            .Select(n => n switch
            {
                ArrowExpressionClauseSyntax arrow => arrow.Expression,
                EqualsValueClauseSyntax { Parent: not ParameterSyntax } initializer => initializer.Value,
                _ => null
            })
            .OfType<ExpressionSyntax>()
            .Where(e => span.Contains(e.Span))
            .ToList();

        var outermost = candidates
            .Where(e => !candidates.Any(other => other != e && other.Span.Contains(e.Span)))
            .ToList();

        return outermost.Count == 1 ? outermost[0] : null;
    }

    /// <summary>
    /// Top-level statements each sit in their own <see cref="GlobalStatementSyntax"/>; their
    /// shared list is the compilation unit.
    /// </summary>
    private static SyntaxNode? StatementList(StatementSyntax statement) =>
        statement.Parent is GlobalStatementSyntax global ? global.Parent : statement.Parent;
}
