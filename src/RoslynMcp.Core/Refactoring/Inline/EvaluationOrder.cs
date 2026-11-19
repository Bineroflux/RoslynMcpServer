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
    /// operation when its evaluation starts, with the hazards of the operations completed by
    /// then (see <paramref name="hazards"/>); it returns false to stop the walk.
    /// </summary>
    public static void Walk(IOperation root, Func<IOperation, Hazards, bool> visit, Func<IOperation, Hazards> hazards)
    {
        var seen = Hazards.None;
        Visit(root);

        bool Visit(IOperation operation)
        {
            if (!visit(operation, seen))
                return false;

            foreach (var child in Children(operation))
            {
                if (!Visit(child))
                    return false;
            }

            seen |= hazards(operation);
            return true;
        }
    }

    /// <summary>
    /// What completing <paramref name="operation"/> may do that code moved past it could notice:
    /// a side effect (<see cref="IsEffect"/>), an exception (<see cref="MayThrow"/>), or an access
    /// to a member of <c>this</c>, which throws once <c>this</c> is replaced by a receiver that is null.
    /// </summary>
    public static Hazards HazardsOf(IOperation operation, Func<ISymbol, bool> isOwnLocal) =>
        (IsEffect(operation, isOwnLocal) ? Hazards.Effect : Hazards.None) |
        (MayThrow(operation) ? Hazards.Throw : Hazards.None) |
        (Dereferences(operation) is IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance } ? Hazards.ThisAccess : Hazards.None);

    /// <summary>
    /// Whether evaluating <paramref name="operation"/> itself may throw (besides the calls
    /// <see cref="IsEffect"/> counts): division and remainder, decimal arithmetic, arithmetic and
    /// conversions in a checked context (which includes a project built with
    /// <c>CheckForOverflowUnderflow</c>), unboxing and explicit reference conversions, element
    /// access, and member access on a receiver that may be null (<see cref="MayBeNull"/>; for
    /// <c>this</c>, see <see cref="Hazards.ThisAccess"/>).
    /// </summary>
    public static bool MayThrow(IOperation operation) => operation switch
    {
        IBinaryOperation { OperatorMethod: null } binary =>
            binary.IsChecked ||
            binary.OperatorKind is BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder ||
            (binary.Type?.SpecialType == SpecialType.System_Decimal &&
                binary.OperatorKind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract or BinaryOperatorKind.Multiply),
        IUnaryOperation { OperatorMethod: null, IsChecked: true } => true,
        ICompoundAssignmentOperation { OperatorMethod: null, IsChecked: true } => true,
        IIncrementOrDecrementOperation { OperatorMethod: null, IsChecked: true } => true,
        IConversionOperation { OperatorMethod: null } conversion =>
            (conversion.IsChecked && !conversion.Conversion.IsIdentity) ||
            conversion.GetConversion() is { IsUnboxing: true } or { IsExplicit: true, IsReference: true },
        IArrayElementReferenceOperation or IInlineArrayAccessOperation => true,
        _ => Dereferences(operation) is { } instance && instance is not IInstanceReferenceOperation && MayBeNull(instance)
    };

    /// <summary>
    /// The receiver <paramref name="operation"/> uses a member of, so that it throws when the
    /// receiver is null: of a member access or an instance call (not an extension method).
    /// </summary>
    public static IOperation? Dereferences(IOperation operation) => operation switch
    {
        IMemberReferenceOperation { Instance: { } instance } => instance,
        IInvocationOperation { Instance: { } instance } => instance,
        _ => null
    };

    /// <summary>
    /// Whether the value of <paramref name="operation"/> may be null: it is neither <c>this</c>
    /// or <c>base</c>, nor of a value type, nor a creation or a literal, nor known to be non-null
    /// by the compiler's nullable analysis in a context with nullable warnings enabled.
    /// </summary>
    public static bool MayBeNull(IOperation operation)
    {
        while (true)
        {
            if (operation is IParenthesizedOperation parenthesized)
                operation = parenthesized.Operand;
            else if (operation is IConversionOperation { IsImplicit: true, OperatorMethod: null, Conversion.IsReference: true } conversion)
                operation = conversion.Operand;
            else
                break;
        }

        if (operation is IInstanceReferenceOperation or IObjectCreationOperation or IAnonymousObjectCreationOperation or IArrayCreationOperation
                or IDelegateCreationOperation or IInterpolatedStringOperation or ITypeOfOperation or INameOfOperation or IAnonymousFunctionOperation ||
            operation.Type is { IsValueType: true } ||
            operation.ConstantValue is { HasValue: true, Value: not null })
        {
            return false;
        }

        if (operation.SemanticModel is not { } model || operation.Syntax is not ExpressionSyntax expression ||
            !model.GetNullableContext(expression.SpanStart).WarningsEnabled())
        {
            return true;
        }

        return model.GetTypeInfo(expression).Nullability.FlowState != NullableFlowState.NotNull;
    }

    /// <summary>
    /// Whether <paramref name="property"/> is an auto-property declared in source that no other
    /// code can stand in for: not virtual, abstract, an unsealed override, an interface member,
    /// an indexer, extern or partial, and its accessors have no bodies (or it is a record's
    /// positional property). Reading it reads a field.
    /// </summary>
    public static bool IsAutoProperty(IPropertySymbol property)
    {
        property = property.OriginalDefinition;
        if (property.IsVirtual || property.IsAbstract || (property.IsOverride && !property.IsSealed) || property.IsIndexer ||
            property.IsExtern || property.ContainingType.TypeKind == TypeKind.Interface ||
            property.PartialDefinitionPart != null || property.PartialImplementationPart != null)
        {
            return false;
        }

        return property.DeclaringSyntaxReferences is [var reference] && reference.GetSyntax() switch
        {
            PropertyDeclarationSyntax { ExpressionBody: null, AccessorList.Accessors: var accessors } =>
                accessors.All(a => a.Body == null && a.ExpressionBody == null),
            ParameterSyntax => property.ContainingType.IsRecord,
            _ => false
        };
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
    /// leaves the expression early: calls (property and indexer accesses included, except reads
    /// of auto-properties declared in source, see <see cref="IsAutoProperty"/>), object creation,
    /// assignments and increments (except of <paramref name="isOwnLocal"/> locals), await,
    /// throw, user-defined operators and conversions, dynamic operations.
    /// </summary>
    public static bool IsEffect(IOperation operation, Func<ISymbol, bool> isOwnLocal) => operation switch
    {
        IInvocationOperation or IObjectCreationOperation or IDynamicObjectCreationOperation or IWithOperation => true,
        IPropertyReferenceOperation property => !IsAutoProperty(property.Property),
        IEventAssignmentOperation => true,
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

/// <summary>What an operation that completed may have done, as far as moving code past it matters.</summary>
[Flags]
internal enum Hazards
{
    None = 0,

    /// <summary>A side effect (see <see cref="EvaluationOrder.IsEffect"/>).</summary>
    Effect = 1,

    /// <summary>A possible exception (see <see cref="EvaluationOrder.MayThrow"/>).</summary>
    Throw = 2,

    /// <summary>
    /// An access to a member of <c>this</c>: harmless in the method, but at a call site where
    /// <c>this</c> becomes a receiver that may be null, it may throw.
    /// </summary>
    ThisAccess = 4
}
