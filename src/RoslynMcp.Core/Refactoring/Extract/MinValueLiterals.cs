using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Extract;

/// <summary>
/// The literals <c>2147483648</c> and <c>9223372036854775808L</c>, which mean
/// <c>int.MinValue</c> and <c>long.MinValue</c> only as the operand of a unary minus.
/// </summary>
internal static class MinValueLiterals
{
    /// <summary>
    /// The negation around <paramref name="expression"/> when it is such a literal, otherwise
    /// <paramref name="expression"/>. On its own the literal is a <c>uint</c> / <c>ulong</c>:
    /// a variable or constant of that type, negated, is a <c>long</c> (<c>-2147483648</c>
    /// stops being an <c>int</c>) or does not compile (<c>-</c> on a <c>ulong</c>, CS0023).
    /// </summary>
    public static ExpressionSyntax WithNegation(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { Token.Value: 2147483648u or 9223372036854775808ul } literal &&
        literal.Parent is PrefixUnaryExpressionSyntax negation &&
        negation.IsKind(SyntaxKind.UnaryMinusExpression)
            ? negation
            : expression;
}
