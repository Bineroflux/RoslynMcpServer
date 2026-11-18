using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Simplification;
using RoslynMcp.Core.Refactoring.Utilities;

namespace RoslynMcp.Core.Refactoring.Inline;

/// <summary>
/// Decides how one call of the inlined method is replaced: which arguments are substituted
/// where the parameter is used and which are first stored in a local (keeping their order and
/// number of evaluations), where those locals can go, and which names must change. A call that
/// can't be inlined without changing what the code does is refused with a reason.
/// </summary>
internal sealed class InlineCallPlanner
{
    private readonly InlinedBody _body;
    private readonly Dictionary<SyntaxNode, HashSet<string>> _introducedNames = [];

    public InlineCallPlanner(InlinedBody body) => _body = body;

    /// <summary>
    /// The plan for the call <paramref name="invocation"/> in <paramref name="document"/>, or
    /// null with <see cref="Refusal"/> saying why it can't be inlined.
    /// </summary>
    public async Task<CallPlan?> PlanAsync(
        int site,
        Document document,
        InvocationExpressionSyntax invocation,
        CancellationToken cancellationToken)
    {
        Refusal = null;
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (model?.GetOperation(invocation, cancellationToken) is not IInvocationOperation operation)
            return Refuse("the call does not compile");

        if (invocation.Expression is MemberBindingExpressionSyntax)
            return Refuse("it is a conditional access (?.)");

        if (InExpressionTree(invocation, model, cancellationToken))
            return Refuse("it is inside an expression tree");

        if (IsChecked(invocation, model.Compilation) != _body.Model.Compilation.Options.CheckOverflow)
            return Refuse("it runs in another overflow-checking context (checked/unchecked) than the method body");

        var method = _body.Method;
        var within = EnclosingType(model, invocation.SpanStart);
        var sameType = within != null && SymbolEqualityComparer.Default.Equals(within.OriginalDefinition, method.ContainingType.OriginalDefinition);

        // The receiver of an instance call, when it is not (implicitly) this or base.
        ExpressionSyntax? receiver = null;
        if (!method.IsStatic && invocation.Expression is MemberAccessExpressionSyntax { Expression: not (ThisExpressionSyntax or BaseExpressionSyntax) } access)
            receiver = access.Expression;

        if (_body.UsesBase && (receiver != null || !sameType))
            return Refuse("the body uses 'base', which means another type here");

        if (_body.UsesEventAsValue && !sameType)
            return Refuse("the body raises or reads an event, which only its declaring type can do");

        var receiverType = receiver != null ? model.GetTypeInfo(receiver, cancellationToken).Type : within;
        if (Inaccessible(model.Compilation, within, receiverType, cancellationToken) is { } inaccessible)
            return Refuse($"the body uses '{inaccessible}', which is not accessible here");

        var typeMap = TypeMap(operation.TargetMethod);
        var captured = CapturedSymbols(invocation, model, cancellationToken);
        var inputs = new List<CallInput>();

        if (receiver != null)
        {
            if (model.GetOperation(receiver, cancellationToken) is not { } receiverOperation || receiverType == null)
                return Refuse("its receiver does not compile");
            inputs.Add(new CallInput(null, InputSource.ForReceiver(), receiverType, needsCast: false,
                Classify(receiverOperation, captured), _body.This, Flow(model, [receiver], written: false), Flow(model, [receiver], written: true)));
        }

        foreach (var argument in operation.Arguments)
        {
            if (argument.Parameter is not { } parameter)
                return Refuse("an argument matches no parameter");

            InputSource source;
            var value = argument.Value;
            switch (argument.ArgumentKind)
            {
                case ArgumentKind.Explicit when argument.Syntax is ArgumentSyntax syntax:
                    source = InputSource.ForArgument(invocation.ArgumentList.Arguments.IndexOf(syntax));
                    break;
                case ArgumentKind.Explicit when invocation.Expression is MemberAccessExpressionSyntax extensionAccess && argument.Value.Syntax == extensionAccess.Expression:
                    source = InputSource.ForReceiver();
                    break;
                case ArgumentKind.ParamArray when value is IArrayCreationOperation array:
                    var elements = array.Initializer?.ElementValues ?? [];
                    var indexes = elements.Select(e => invocation.ArgumentList.Arguments.IndexOf(a => a.Expression == e.Syntax)).ToList();
                    if (indexes.Any(i => i < 0))
                        return Refuse("its params arguments could not be matched");
                    source = InputSource.ForParams(indexes, ((IArrayTypeSymbol)parameter.Type).ElementType);
                    break;
                case ArgumentKind.DefaultValue:
                    source = InputSource.ForDefault(DefaultValue(argument.Value, parameter.Type, document, model, invocation.SpanStart));
                    break;
                default:
                    return Refuse("it passes a params collection in expanded form");
            }

            var needsCast = argument.ArgumentKind == ArgumentKind.Explicit &&
                value is IConversionOperation { IsImplicit: true, Conversion.IsIdentity: false };
            var classOperand = value is IConversionOperation { IsImplicit: true, OperatorMethod: null } conversion ? conversion.Operand : value;
            var uses = _body.Parameters[parameter.Ordinal];
            var argumentClass = argument.ArgumentKind switch
            {
                ArgumentKind.DefaultValue => ArgumentClass.Constant,
                ArgumentKind.ParamArray => ArgumentClass.Impure,
                _ => Classify(classOperand, captured)
            };
            IReadOnlyList<SyntaxNode> expressions = argument.ArgumentKind switch
            {
                ArgumentKind.Explicit => [value.Syntax],
                ArgumentKind.ParamArray => ((IArrayCreationOperation)value).Initializer?.ElementValues.Select(e => e.Syntax).ToList() ?? [],
                _ => []
            };
            inputs.Add(new CallInput(parameter, source, parameter.Type, needsCast, argumentClass, uses,
                Flow(model, expressions, written: false), Flow(model, expressions, written: true)));
        }

        Decide(inputs);

        if (receiver != null && inputs[0].Mode == InputMode.Temp && IsMutableStruct(method))
            return Refuse("its receiver is a struct that would have to be copied into a local first");

        // Where the replacement goes.
        var statement = invocation.Parent is ExpressionStatementSyntax expressionStatement && expressionStatement.Expression == invocation
            ? expressionStatement
            : null;
        CallForm form;
        StatementSyntax? hoistBefore = null;
        var temps = inputs.Where(i => i.Mode == InputMode.Temp).ToList();
        if (statement != null)
        {
            form = method.ReturnsVoid ? CallForm.Statement : CallForm.Discard;
            if (form == CallForm.Discard && !IsStatementExpression(_body.Root) && model.LookupSymbols(invocation.SpanStart, name: "_").Length > 0)
                return Refuse("its result would have to be discarded with '_', which is a variable here");
        }
        else if (_body.IsBlock)
        {
            return Refuse("the body is a block of statements, which can only replace a call that is a statement of its own");
        }
        else
        {
            form = CallForm.Expression;
            if (temps.Count > 0)
            {
                var reason = FindHoistPosition(invocation, model, temps, out hoistBefore);
                if (reason != null)
                    return Refuse($"it needs locals for {Describe(temps)}, but {reason}");
            }
        }

        // Names: the body's declarations and the new locals must not clash with anything in scope.
        var member = ContainingMember(invocation);
        if (!_introducedNames.TryGetValue(member, out var introduced))
            _introducedNames[member] = introduced = [];
        var taken = new HashSet<string>(StringComparer.Ordinal);
        taken.UnionWith(model.LookupSymbols(invocation.SpanStart).Select(s => s.Name));
        taken.UnionWith(DeclaredNames(member));
        taken.UnionWith(introduced);

        var newNames = new List<string>();
        var renames = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
        foreach (var declared in _body.Declared)
        {
            var name = Unique(declared.Name, taken);
            newNames.Add(name);
            if (name != declared.Name)
                renames[declared] = name;
        }

        foreach (var temp in temps)
        {
            temp.TempName = Unique(temp.Parameter?.Name ?? ReceiverName(temp.Type), taken);
            newNames.Add(temp.TempName);
        }

        var plan = new CallPlan
        {
            Site = site,
            Document = document,
            Invocation = invocation,
            Form = form,
            Statement = statement,
            HoistBefore = hoistBefore,
            Inputs = inputs,
            HasReceiver = receiver != null,
            Renames = renames,
            TypeMap = typeMap,
            NullableAnnotations = model.GetNullableContext(invocation.SpanStart).AnnotationsEnabled(),
            ReturnType = _body.NeedsReturnCast ? operation.TargetMethod.ReturnType : null,
            Namespaces = MissingNamespaces(model, invocation.SpanStart),
            Body = _body
        };

        // A dry run finds types that can't be written here.
        plan.BuildBody(invocation, out var failure);
        if (failure != null)
            return Refuse(failure);

        introduced.UnionWith(newNames);
        return plan;
    }

    /// <summary>Why the last call given to <see cref="PlanAsync"/> was refused.</summary>
    public string? Refusal { get; private set; }

    private CallPlan? Refuse(string reason)
    {
        Refusal = reason;
        return null;
    }

    /// <summary>
    /// Decides for each input whether it is substituted (inline), stored in a local first
    /// (temp), or dropped (unused, without side effects). Constants are always substituted, and
    /// so are reads of the caller's own locals unless another argument writes them. Of the
    /// rest, reads of other state are substituted when the body runs no side effect before
    /// using them; a single argument with side effects is substituted when the body evaluates
    /// it exactly once before anything else happens. Otherwise all of them get locals, in
    /// their order of evaluation.
    /// </summary>
    private void Decide(List<CallInput> inputs)
    {
        var undecided = new List<CallInput>();
        foreach (var input in inputs)
        {
            if (input.Uses.Written)
            {
                input.Mode = InputMode.Temp;
            }
            else if (input.Class == ArgumentClass.Constant)
            {
                input.Mode = input.Uses.Count == 0 ? InputMode.Drop : InputMode.Inline;
            }
            else if (input.Class == ArgumentClass.LocalRead)
            {
                var writtenByOthers = inputs.Any(other => other != input && other.Writes.Overlaps(input.Reads));
                input.Mode = writtenByOthers ? InputMode.Temp : input.Uses.Count == 0 ? InputMode.Drop : InputMode.Inline;
            }
            else
            {
                undecided.Add(input);
            }
        }

        if (undecided.Count == 0)
            return;

        if (undecided.All(i => i.Class == ArgumentClass.StateRead && i.Uses.BeforeEffects(_body.HasEffects)))
        {
            foreach (var input in undecided)
                input.Mode = input.Uses.Count == 0 ? InputMode.Drop : InputMode.Inline;
        }
        else if (undecided is [{ Class: ArgumentClass.Impure } single] && single.Uses.OnceFirst &&
            inputs.Where(i => i.Mode == InputMode.Inline && i.Class != ArgumentClass.Constant).All(i => !single.Writes.Overlaps(i.Reads)))
        {
            single.Mode = InputMode.Inline;
        }
        else
        {
            foreach (var input in undecided)
                input.Mode = InputMode.Temp;
        }
    }

    /// <summary>
    /// How an argument's value behaves when moved: a constant, a read of the caller's own
    /// (uncaptured, non-ref) locals and parameters, a read of other state (fields, captured
    /// locals), or anything with side effects or a possible exception.
    /// </summary>
    private static ArgumentClass Classify(IOperation operation, HashSet<ISymbol> captured)
    {
        if (operation.ConstantValue.HasValue)
            return ArgumentClass.Constant;

        switch (operation)
        {
            case ILiteralOperation or IDefaultValueOperation or ITypeOfOperation or ISizeOfOperation or INameOfOperation:
            case IAnonymousFunctionOperation or IInstanceReferenceOperation:
            case IDelegateCreationOperation { Target: IAnonymousFunctionOperation or IMethodReferenceOperation { Instance: null or IInstanceReferenceOperation } }:
                return ArgumentClass.Constant;
            case IParenthesizedOperation parenthesized:
                return Classify(parenthesized.Operand, captured);
            case IConversionOperation { OperatorMethod: null, IsImplicit: true } conversion:
                return Classify(conversion.Operand, captured);
            case ILocalReferenceOperation local:
                return local.Local.IsRef || captured.Contains(local.Local) ? ArgumentClass.StateRead : ArgumentClass.LocalRead;
            case IParameterReferenceOperation parameter:
                return parameter.Parameter.RefKind != RefKind.None || captured.Contains(parameter.Parameter)
                    ? ArgumentClass.StateRead
                    : ArgumentClass.LocalRead;
            case IFieldReferenceOperation field:
                return field.Instance == null ? ArgumentClass.StateRead : Max(ArgumentClass.StateRead, Classify(field.Instance, captured));
            case IUnaryOperation { OperatorMethod: null, IsChecked: false } unary:
                return Classify(unary.Operand, captured);
            case IBinaryOperation { OperatorMethod: null, IsChecked: false } binary
                when binary.OperatorKind is not (BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder):
                return Max(Classify(binary.LeftOperand, captured), Classify(binary.RightOperand, captured));
            default:
                return ArgumentClass.Impure;
        }
    }

    private static ArgumentClass Max(ArgumentClass left, ArgumentClass right) => left > right ? left : right;

    /// <summary>The variables <paramref name="expressions"/> read, or (<paramref name="written"/>) assign or pass by reference.</summary>
    private static HashSet<ISymbol> Flow(SemanticModel model, IEnumerable<SyntaxNode> expressions, bool written)
    {
        var symbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var expression in expressions.OfType<ExpressionSyntax>())
        {
            if (model.AnalyzeDataFlow(expression) is { Succeeded: true } flow)
                symbols.UnionWith(written ? flow.WrittenInside : flow.ReadInside);
        }

        return symbols;
    }

    /// <summary>Locals and parameters captured by a lambda or local function in the call's member: other code may change them.</summary>
    private static HashSet<ISymbol> CapturedSymbols(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        var captured = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var function in ContainingMember(node).DescendantNodes().Where(n => n is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
        {
            foreach (var name in function.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (model.GetSymbolInfo(name, cancellationToken).Symbol is (ILocalSymbol or IParameterSymbol) and var symbol)
                    captured.Add(symbol);
            }
        }

        return captured;
    }

    /// <summary>The member (or the top-level statements) containing a call: the scope its names share.</summary>
    private static SyntaxNode ContainingMember(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case GlobalStatementSyntax global:
                    return global.Parent!;
                case MemberDeclarationSyntax and not BaseTypeDeclarationSyntax and not BaseNamespaceDeclarationSyntax:
                    return ancestor;
            }
        }

        return node.SyntaxTree.GetRoot();
    }

    private static IEnumerable<string> DeclaredNames(SyntaxNode member) =>
        member.DescendantNodes().Select(node => node switch
        {
            VariableDeclaratorSyntax v => v.Identifier,
            SingleVariableDesignationSyntax d => d.Identifier,
            ForEachStatementSyntax f => f.Identifier,
            CatchDeclarationSyntax c => c.Identifier,
            LocalFunctionStatementSyntax l => l.Identifier,
            ParameterSyntax p => p.Identifier,
            TypeParameterSyntax t => t.Identifier,
            LabeledStatementSyntax label => label.Identifier,
            FromClauseSyntax from => from.Identifier,
            LetClauseSyntax let => let.Identifier,
            JoinClauseSyntax join => join.Identifier,
            JoinIntoClauseSyntax into => into.Identifier,
            QueryContinuationSyntax continuation => continuation.Identifier,
            _ => default
        }).Where(t => !t.IsKind(SyntaxKind.None)).Select(t => t.ValueText);

    private static string Unique(string name, HashSet<string> taken)
    {
        var candidate = name;
        for (var i = 1; taken.Contains(candidate) || SyntaxFacts.GetKeywordKind(candidate) != SyntaxKind.None; i++)
            candidate = name + i;
        taken.Add(candidate);
        return candidate;
    }

    private static string ReceiverName(ITypeSymbol type)
    {
        var name = type.Name;
        return string.IsNullOrEmpty(name) || !SyntaxFacts.IsValidIdentifier(name)
            ? "receiver"
            : char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static string Describe(List<CallInput> temps) =>
        string.Join(", ", temps.Select(t => t.Parameter == null ? "the receiver" : $"'{t.Parameter.Name}'"));

    /// <summary>
    /// The statement before which the locals of an expression call can go, or why there is
    /// none: the call must be evaluated exactly once, unconditionally, by a statement that can
    /// be preceded by others, and nothing the statement evaluates before the call may change
    /// meaning when the arguments are evaluated first.
    /// </summary>
    private string? FindHoistPosition(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        List<CallInput> temps,
        out StatementSyntax? statement)
    {
        statement = null;
        SyntaxNode child = invocation;
        foreach (var ancestor in invocation.Ancestors())
        {
            switch (ancestor)
            {
                case StatementSyntax found:
                    statement = found;
                    break;
                case AnonymousFunctionExpressionSyntax:
                    return "it is in a lambda expression, which can't take statements";
                case ArrowExpressionClauseSyntax:
                    return "it is in an expression-bodied member, which can't take statements";
                case EqualsValueClauseSyntax when ancestor.Parent is VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax } or PropertyDeclarationSyntax or ParameterSyntax:
                    return "it is in an initializer, which can't take statements";
                case ConstructorInitializerSyntax:
                    return "it is in a constructor initializer, which can't take statements";
                case MemberDeclarationSyntax or AttributeSyntax:
                    return "it is not inside a statement";
            }

            if (statement != null)
                break;
            child = ancestor;
        }

        if (statement == null)
            return "it is not inside a statement";

        if (statement is not (ExpressionStatementSyntax or LocalDeclarationStatementSyntax or ReturnStatementSyntax or ThrowStatementSyntax
            or YieldStatementSyntax or IfStatementSyntax or SwitchStatementSyntax or CommonForEachStatementSyntax or UsingStatementSyntax or LockStatementSyntax))
        {
            return "its statement (a loop condition or another construct) would evaluate the locals only once";
        }

        if (statement.Parent is LabeledStatementSyntax ||
            (statement.Parent is GlobalStatementSyntax && statement is LocalDeclarationStatementSyntax))
        {
            return "its statement can't be preceded by others";
        }

        var (conditional, repeated) = EvaluationOrder.Context(invocation, statement);
        if (conditional || repeated)
            return "it runs conditionally or repeatedly within its statement";

        if (model.GetOperation(statement) is not { } statementOperation || model.GetOperation(invocation) is not { } callOperation)
            return "its statement could not be analyzed";

        var tempWrites = temps.SelectMany(t => t.Writes).ToHashSet(SymbolEqualityComparer.Default);
        foreach (var completed in EvaluationOrder.CompletedBefore(statementOperation, callOperation))
        {
            if (!IsInert(completed, tempWrites))
                return "its statement evaluates something before the call that would then run after the arguments";
        }

        return null;
    }

    /// <summary>Whether evaluating <paramref name="operation"/> earlier or later than the temporaries makes no difference.</summary>
    private static bool IsInert(IOperation operation, HashSet<ISymbol> tempWrites) => operation switch
    {
        _ when operation.ConstantValue.HasValue => true,
        ILiteralOperation or IInstanceReferenceOperation or IVariableDeclaratorOperation or IDeclarationExpressionOperation => true,
        // Structure only: what they evaluate was checked on its own.
        IArgumentOperation or IVariableInitializerOperation or IVariableDeclarationOperation or IVariableDeclarationGroupOperation => true,
        IParenthesizedOperation or IConversionOperation { OperatorMethod: null } => true,
        ILocalReferenceOperation local => !tempWrites.Contains(local.Local),
        IParameterReferenceOperation parameter => !tempWrites.Contains(parameter.Parameter),
        IFieldReferenceOperation field => field.Parent is ISimpleAssignmentOperation assignment && assignment.Target == field,
        _ => false
    };

    private static bool IsStatementExpression(SyntaxNode node) =>
        node is InvocationExpressionSyntax or AwaitExpressionSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax ||
        node is AssignmentExpressionSyntax ||
        node.Kind() is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression
            or SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression;

    private static bool IsMutableStruct(IMethodSymbol method) =>
        method.ContainingType.IsValueType && !method.ContainingType.IsReadOnly && !method.IsReadOnly;

    private Dictionary<ITypeParameterSymbol, ITypeSymbol> TypeMap(IMethodSymbol target)
    {
        var map = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var (parameter, argument) in _body.Method.TypeParameters.Zip(target.TypeArguments))
            map[parameter] = argument;

        for (INamedTypeSymbol? type = _body.Method.ContainingType, constructed = target.ContainingType;
             type != null && constructed != null;
             type = type.ContainingType, constructed = constructed.ContainingType)
        {
            foreach (var (parameter, argument) in type.TypeParameters.Zip(constructed.TypeArguments))
                map[parameter] = argument;
        }

        return map;
    }

    /// <summary>The first member, constructor or type the body uses that isn't accessible at the call site.</summary>
    private string? Inaccessible(Compilation compilation, INamedTypeSymbol? within, ITypeSymbol? receiverType, CancellationToken cancellationToken)
    {
        ISymbol accessor = within ?? (ISymbol)compilation.Assembly;
        foreach (var (symbol, throughThis) in _body.Referenced.Distinct())
        {
            if (symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction } ||
                symbol.ContainingAssembly == null)
            {
                continue;
            }

            var mapped = SymbolFinder.FindSimilarSymbols(symbol.OriginalDefinition, compilation, cancellationToken).FirstOrDefault();
            if (mapped == null || !compilation.IsSymbolAccessibleWithin(mapped, accessor, throughThis ? receiverType : null))
                return symbol.ToDisplayString();
        }

        return null;
    }

    private static INamedTypeSymbol? EnclosingType(SemanticModel model, int position)
    {
        for (var symbol = model.GetEnclosingSymbol(position); symbol != null; symbol = symbol.ContainingSymbol)
        {
            if (symbol is INamedTypeSymbol type)
                return type;
        }

        return null;
    }

    private static bool InExpressionTree(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken) =>
        node.Ancestors().OfType<AnonymousFunctionExpressionSyntax>().Any(lambda =>
            model.GetTypeInfo(lambda, cancellationToken).ConvertedType?.OriginalDefinition.ToDisplayString() == "System.Linq.Expressions.Expression<TDelegate>");

    private static bool IsChecked(SyntaxNode node, Compilation compilation)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case CheckedExpressionSyntax expression:
                    return expression.IsKind(SyntaxKind.CheckedExpression);
                case CheckedStatementSyntax statement:
                    return statement.IsKind(SyntaxKind.CheckedStatement);
            }
        }

        return compilation.Options.CheckOverflow;
    }

    /// <summary>
    /// The namespaces of the extension methods the body calls in extension form that aren't
    /// imported at <paramref name="position"/>.
    /// </summary>
    private IReadOnlyList<string> MissingNamespaces(SemanticModel model, int position)
    {
        var missing = new List<string>();
        foreach (var extension in _body.ReducedExtensions)
        {
            var type = extension.ContainingType;
            if (type.ContainingNamespace is not { IsGlobalNamespace: false } ns)
                continue;

            var visible = model.LookupNamespacesAndTypes(position, name: type.Name)
                .Any(s => s.OriginalDefinition.GetDocumentationCommentId() == type.OriginalDefinition.GetDocumentationCommentId());
            if (!visible)
                missing.Add(ns.ToDisplayString());
        }

        return missing.Distinct().ToList();
    }

    /// <summary>The value of an optional argument the call leaves out, written for the call site.</summary>
    private static ExpressionSyntax DefaultValue(IOperation value, ITypeSymbol type, Document document, SemanticModel model, int position)
    {
        var nullable = model.GetNullableContext(position).AnnotationsEnabled();
        if (!value.ConstantValue.HasValue)
            return SyntaxFactory.DefaultExpression(SyntaxGenerationHelper.CreateTypeSyntax(type, nullable));

        var constant = value.ConstantValue.Value;
        if (constant == null)
            return SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);

        var enumType = type is INamedTypeSymbol { TypeKind: TypeKind.Enum } named
            ? named
            : type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableType &&
              nullableType.TypeArguments[0] is INamedTypeSymbol { TypeKind: TypeKind.Enum } underlying
                ? underlying
                : null;
        if (enumType?.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => f.HasConstantValue && Equals(f.ConstantValue, constant)) is { } member)
        {
            return SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxGenerationHelper.CreateTypeSyntax(enumType, nullable),
                    SyntaxFactory.IdentifierName(member.Name))
                .WithAdditionalAnnotations(Simplifier.Annotation);
        }

        var literal = (ExpressionSyntax)SyntaxGenerator.GetGenerator(document).LiteralExpression(constant);
        return SyntaxFactory.CastExpression(
                SyntaxGenerationHelper.CreateTypeSyntax(enumType ?? type, nullable),
                SyntaxFactory.ParenthesizedExpression(literal).WithAdditionalAnnotations(Simplifier.Annotation))
            .WithAdditionalAnnotations(Simplifier.Annotation);
    }
}

/// <summary>Where a call's replacement goes.</summary>
internal enum CallForm
{
    /// <summary>The call is an expression; the returned expression replaces it.</summary>
    Expression,

    /// <summary>The call of a void method is a statement; the body's statements replace it.</summary>
    Statement,

    /// <summary>The call's result is unused; the returned expression becomes a statement.</summary>
    Discard
}

/// <summary>What happens to an argument (or the receiver).</summary>
internal enum InputMode
{
    /// <summary>Substituted where the parameter is used.</summary>
    Inline,

    /// <summary>Stored in a local first, which the body then uses.</summary>
    Temp,

    /// <summary>Unused and without side effects.</summary>
    Drop
}

/// <summary>How an argument's value behaves when evaluated elsewhere; ordered from harmless to not.</summary>
internal enum ArgumentClass
{
    Constant,
    LocalRead,
    StateRead,
    Impure
}

/// <summary>Where an input's value comes from in the call.</summary>
internal sealed record InputSource(InputSourceKind Kind, int Index = -1, IReadOnlyList<int>? ParamsIndexes = null, ITypeSymbol? ElementType = null, ExpressionSyntax? Default = null)
{
    public static InputSource ForReceiver() => new(InputSourceKind.Receiver);
    public static InputSource ForArgument(int index) => new(InputSourceKind.Argument, index);
    public static InputSource ForParams(IReadOnlyList<int> indexes, ITypeSymbol elementType) => new(InputSourceKind.Params, ParamsIndexes: indexes, ElementType: elementType);
    public static InputSource ForDefault(ExpressionSyntax value) => new(InputSourceKind.Default, Default: value);
}

internal enum InputSourceKind
{
    Receiver,
    Argument,
    Params,
    Default
}

/// <summary>A value the body uses: the receiver (no parameter) or an argument.</summary>
internal sealed class CallInput(
    IParameterSymbol? parameter,
    InputSource source,
    ITypeSymbol type,
    bool needsCast,
    ArgumentClass @class,
    InputUses uses,
    HashSet<ISymbol> reads,
    HashSet<ISymbol> writes)
{
    public IParameterSymbol? Parameter { get; } = parameter;
    public InputSource Source { get; } = source;

    /// <summary>The parameter's type at this call (or the receiver's).</summary>
    public ITypeSymbol Type { get; } = type;

    /// <summary>Whether the argument is converted implicitly to the parameter's type.</summary>
    public bool NeedsCast { get; } = needsCast;

    public ArgumentClass Class { get; } = @class;
    public InputUses Uses { get; } = uses;

    /// <summary>The caller's locals and parameters the value's expression reads.</summary>
    public HashSet<ISymbol> Reads { get; } = reads;

    /// <summary>The caller's locals and parameters the value's expression writes.</summary>
    public HashSet<ISymbol> Writes { get; } = writes;

    public InputMode Mode { get; set; }
    public string? TempName { get; set; }
}
