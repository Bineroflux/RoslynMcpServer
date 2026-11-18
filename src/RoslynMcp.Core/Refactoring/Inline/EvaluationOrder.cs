using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RoslynMcp.Core.Refactoring.Inline;

/// <summary>
/// The order in which operations run and which of them have side effects, as far as inlining
/// needs it: whether moving an expression to another point of evaluation can change what it
/// does or what others see.
/// </summary>
internal static class EvaluationOrder
{
    /// <summary>
    /// Visits <paramref name="root"/>'s operations in evaluation order (operands before the
    /// operation that uses them, siblings in source order). <paramref name="visit"/> gets each
    /// operation when its evaluation starts, with whether an operation with a side effect has
    /// completed by then; it returns false to stop the walk.
    /// </summary>
    public static void Walk(IOperation root, Func<IOperation, bool, bool> visit, Func<IOperation, bool> isEffect)
    {
        var effectSeen = false;
        Visit(root);

        bool Visit(IOperation operation)
        {
            if (!visit(operation, effectSeen))
                return false;

            foreach (var child in Children(operation))
            {
                if (!Visit(child))
                    return false;
            }

            if (isEffect(operation))
                effectSeen = true;
            return true;
        }
    }

    /// <summary>
    /// The operations that complete before <paramref name="target"/> starts, within
    /// <paramref name="root"/>. The target of a simple assignment is not itself evaluated
    /// before the value (only its receiver and indexes are), so it is left out.
    /// </summary>
    public static List<IOperation> CompletedBefore(IOperation root, IOperation target)
    {
        var completed = new List<IOperation>();
        Visit(root);
        return completed;

        bool Visit(IOperation operation)
        {
            if (operation == target)
                return false;

            foreach (var child in Children(operation))
            {
                if (!Visit(child))
                    return false;
            }

            if (operation.Parent is not ISimpleAssignmentOperation assignment || assignment.Target != operation)
                completed.Add(operation);
            return true;
        }
    }

    private static IEnumerable<IOperation> Children(IOperation operation) =>
        operation.ChildOperations.OrderBy(child => child.Syntax.SpanStart);

    /// <summary>
    /// Whether completing <paramref name="operation"/> may change state others can observe, or
    /// leaves the expression early: calls (property and indexer accesses included), object
    /// creation, assignments and increments (except of <paramref name="isOwnLocal"/> locals),
    /// await, throw, user-defined operators and conversions, dynamic operations.
    /// </summary>
    public static bool IsEffect(IOperation operation, Func<ISymbol, bool> isOwnLocal) => operation switch
    {
        IInvocationOperation or IObjectCreationOperation or IDynamicObjectCreationOperation or IWithOperation => true,
        IPropertyReferenceOperation or IEventAssignmentOperation => true,
        IAssignmentOperation assignment => !TargetsOwnLocals(assignment.Target, isOwnLocal),
        IIncrementOrDecrementOperation increment => !TargetsOwnLocals(increment.Target, isOwnLocal),
        IAwaitOperation or IThrowOperation => true,
        IUnaryOperation { OperatorMethod: not null } or IBinaryOperation { OperatorMethod: not null } => true,
        IConversionOperation { OperatorMethod: not null } => true,
        IDynamicInvocationOperation or IDynamicMemberReferenceOperation or IDynamicIndexerAccessOperation => true,
        _ => false
    };

    private static bool TargetsOwnLocals(IOperation target, Func<ISymbol, bool> isOwnLocal) => target switch
    {
        ILocalReferenceOperation local => isOwnLocal(local.Local),
        IDeclarationExpressionOperation => true,
        ITupleOperation tuple => tuple.Elements.All(e => TargetsOwnLocals(e, isOwnLocal)),
        IDiscardOperation => true,
        _ => false
    };

    /// <summary>
    /// Where <paramref name="node"/> runs relative to <paramref name="root"/>, an ancestor:
    /// conditionally (in a branch, the right operand of <c>&amp;&amp;</c>, <c>||</c> or
    /// <c>??</c>, after <c>?.</c>, in a catch or finally block), and repeatedly (in a loop
    /// other than a foreach collection or a for initializer, in a lambda or local function, in
    /// a query).
    /// </summary>
    public static (bool Conditional, bool Repeated) Context(SyntaxNode node, SyntaxNode root)
    {
        var (conditional, repeated) = (false, false);
        for (SyntaxNode child = node, parent = node.Parent!; child != root && parent != null; child = parent, parent = parent.Parent!)
        {
            switch (parent)
            {
                case AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or QueryExpressionSyntax or QueryBodySyntax:
                case WhileStatementSyntax or DoStatementSyntax:
                case ForStatementSyntax @for when @for.Declaration != child && !@for.Initializers.Contains(child):
                case CommonForEachStatementSyntax forEach when forEach.Expression != child:
                    (conditional, repeated) = (true, true);
                    break;
                case IfStatementSyntax @if when @if.Condition != child:
                case ElseClauseSyntax or SwitchSectionSyntax or SwitchExpressionArmSyntax or WhenClauseSyntax:
                case CatchClauseSyntax or FinallyClauseSyntax:
                case ConditionalExpressionSyntax conditionalExpression when conditionalExpression.Condition != child:
                case BinaryExpressionSyntax binary when binary.Right == child &&
                    binary.Kind() is SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression or SyntaxKind.CoalesceExpression:
                case AssignmentExpressionSyntax assignment when assignment.Right == child && assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression):
                case ConditionalAccessExpressionSyntax access when access.WhenNotNull == child:
                    conditional = true;
                    break;
            }
        }

        return (conditional, repeated);
    }
}
