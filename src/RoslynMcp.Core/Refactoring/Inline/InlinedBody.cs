using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Simplification;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Refactoring.Signature;
using RoslynMcp.Core.Refactoring.Utilities;

namespace RoslynMcp.Core.Refactoring.Inline;

/// <summary>
/// The body of a method to inline, analyzed once in its own document: what each name in it
/// refers to, how often and where each parameter (and <c>this</c>) is evaluated, which side
/// effects run before that, and what it declares. <see cref="Rewrite"/> turns a copy of it into
/// code that means the same at a call site.
/// </summary>
internal sealed class InlinedBody
{
    /// <summary>Annotation kind on names that must still bind to the same member at the call site.</summary>
    public const string SymbolAnnotationKind = "RoslynMcp.InlineMethod.Symbol";

    private readonly Dictionary<SyntaxNode, (NameKind Kind, ISymbol Symbol)> _names = [];
    private readonly Dictionary<SyntaxToken, ISymbol> _declaredTokens = [];
    private readonly Dictionary<SyntaxNode, string> _nameOfs = [];

    private InlinedBody(IMethodSymbol method, SyntaxNode root, SemanticModel model)
    {
        Method = method;
        Root = root;
        Model = model;
    }

    /// <summary>The method.</summary>
    public IMethodSymbol Method { get; }

    /// <summary>The returned expression, or the block of a void method.</summary>
    public SyntaxNode Root { get; }

    /// <summary>The semantic model of the declaring document.</summary>
    public SemanticModel Model { get; }

    /// <summary>Whether the body is a block of statements (a void method).</summary>
    public bool IsBlock => Root is BlockSyntax;

    /// <summary>Whether the returned expression's type differs from the return type, so the call site needs a cast.</summary>
    public bool NeedsReturnCast { get; private set; }

    /// <summary>How each parameter is used, by ordinal.</summary>
    public IReadOnlyList<InputUses> Parameters { get; private set; } = [];

    /// <summary>How <c>this</c> is used, explicitly or through an instance member.</summary>
    public InputUses This { get; private set; } = InputUses.None;

    /// <summary>Whether anything in the body has a side effect.</summary>
    public bool HasEffects { get; private set; }

    /// <summary>Whether the body uses <c>base</c>.</summary>
    public bool UsesBase { get; private set; }

    /// <summary>Whether the body reads or raises a field-like event of its type (only possible inside the type).</summary>
    public bool UsesEventAsValue { get; private set; }

    /// <summary>The locals, local functions, lambda parameters and range variables the body declares.</summary>
    public IReadOnlyList<ISymbol> Declared { get; private set; } = [];

    /// <summary>
    /// The members, constructors and types the body refers to, each with whether it is reached
    /// through <c>this</c> (whose type changes with the receiver).
    /// </summary>
    public IReadOnlyList<(ISymbol Symbol, bool ThroughThis)> Referenced { get; private set; } = [];

    /// <summary>The extension methods the body calls in extension form, which need their namespace imported.</summary>
    public IReadOnlyList<IMethodSymbol> ReducedExtensions { get; private set; } = [];

    /// <summary>
    /// Analyzes <paramref name="declaration"/>'s body. Fails with
    /// <see cref="ErrorCodes.MethodNotInlinable"/> when its shape or content can't be inlined.
    /// </summary>
    public static InlinedBody Analyze(
        IMethodSymbol method,
        MethodDeclarationSyntax declaration,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        var name = method.ToDisplayString();
        if (declaration.Body?.DescendantNodes().OfType<YieldStatementSyntax>().Any() == true)
            throw Refuse(name, "it is an iterator");

        SyntaxNode root;
        if (declaration.ExpressionBody is { } arrow)
        {
            root = arrow.Expression;
        }
        else if (declaration.Body is { } block)
        {
            if (method.ReturnsVoid)
            {
                root = block;
            }
            else if (block.Statements is [ReturnStatementSyntax { Expression: { } returned }])
            {
                root = returned;
            }
            else
            {
                throw Refuse(name, "its body must be an expression or a single return statement");
            }
        }
        else
        {
            throw Refuse(name, "it has no body");
        }

        if (root is ThrowExpressionSyntax)
            throw Refuse(name, "its body is a throw expression");

        CheckSyntax(name, root, method.ReturnsVoid);

        var body = new InlinedBody(method, root, model);
        body.Collect(cancellationToken);
        return body;
    }

    private static RefactoringException Refuse(string method, string reason) =>
        new(ErrorCodes.MethodNotInlinable, $"'{method}' cannot be inlined: {reason}.");

    private static void CheckSyntax(string method, SyntaxNode root, bool returnsVoid)
    {
        foreach (var node in root.DescendantNodesAndSelf())
        {
            var reason = node switch
            {
                YieldStatementSyntax => "it is an iterator",
                StackAllocArrayCreationExpressionSyntax or ImplicitStackAllocArrayCreationExpressionSyntax => "it uses stackalloc",
                UnsafeStatementSyntax or FixedStatementSyntax or PointerTypeSyntax or FunctionPointerTypeSyntax => "it uses unsafe code",
                PrefixUnaryExpressionSyntax unary when unary.Kind() is SyntaxKind.AddressOfExpression or SyntaxKind.PointerIndirectionExpression => "it uses unsafe code",
                MemberAccessExpressionSyntax access when access.IsKind(SyntaxKind.PointerMemberAccessExpression) => "it uses unsafe code",
                LabeledStatementSyntax or GotoStatementSyntax => "it uses labels or goto",
                MakeRefExpressionSyntax or RefValueExpressionSyntax or RefTypeExpressionSyntax => "it uses __makeref",
                _ when node.IsKind(SyntaxKind.ArgListExpression) => "it uses __arglist",
                ReturnStatementSyntax when returnsVoid && !InsideFunction(node, root) => "its void body returns early",
                _ => null
            };

            if (reason != null)
                throw Refuse(method, reason);
        }
    }

    /// <summary>Whether <paramref name="node"/> is inside a lambda or local function within <paramref name="root"/>.</summary>
    private static bool InsideFunction(SyntaxNode node, SyntaxNode root) =>
        node.Ancestors().TakeWhile(a => a != root).Any(a => a is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);

    private void Collect(CancellationToken cancellationToken)
    {
        // What the body declares, so the call site can rename it.
        var declared = new List<ISymbol>();
        foreach (var node in Root.DescendantNodesAndSelf())
        {
            var (symbol, token) = node switch
            {
                VariableDeclaratorSyntax v => (Model.GetDeclaredSymbol(v, cancellationToken), v.Identifier),
                SingleVariableDesignationSyntax d => (Model.GetDeclaredSymbol(d, cancellationToken), d.Identifier),
                ForEachStatementSyntax f => (Model.GetDeclaredSymbol(f, cancellationToken), f.Identifier),
                CatchDeclarationSyntax c when !c.Identifier.IsKind(SyntaxKind.None) => (Model.GetDeclaredSymbol(c, cancellationToken), c.Identifier),
                LocalFunctionStatementSyntax l => (Model.GetDeclaredSymbol(l, cancellationToken), l.Identifier),
                TypeParameterSyntax t => (Model.GetDeclaredSymbol(t, cancellationToken), t.Identifier),
                ParameterSyntax p => (Model.GetDeclaredSymbol(p, cancellationToken), p.Identifier),
                QueryClauseSyntax or QueryContinuationSyntax => (Model.GetDeclaredSymbol(node, cancellationToken), QueryIdentifier(node)),
                _ => (null, default)
            };

            if (symbol != null && !token.IsKind(SyntaxKind.None))
            {
                declared.Add(symbol);
                _declaredTokens[token] = symbol;
            }
        }

        Declared = declared;
        var declaredSet = new HashSet<ISymbol>(declared, SymbolEqualityComparer.Default);

        var referenced = new List<(ISymbol, bool)>();
        var extensions = new List<IMethodSymbol>();
        CollectNames(Root, declaredSet, referenced, extensions, cancellationToken);

        // Operations: parameter and this uses, effects, members referenced.
        var operation = Model.GetOperation(Root, cancellationToken)
            ?? throw Refuse(Method.ToDisplayString(), "its body could not be analyzed");
        var parameterUses = Method.Parameters.Select(_ => new List<Use>()).ToList();
        var thisUses = new List<Use>();
        bool IsOwn(ISymbol symbol) => declaredSet.Contains(symbol);

        EvaluationOrder.Walk(operation, (op, effectBefore) =>
        {
            for (var current = op; current != null; current = current.Parent)
            {
                if (current is INameOfOperation)
                    return true;
            }

            switch (op)
            {
                case IParameterReferenceOperation { Parameter: var parameter } when SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, Method):
                    parameterUses[parameter.Ordinal].Add(Use(op, effectBefore));
                    break;
                case IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance }:
                    thisUses.Add(Use(op, effectBefore));
                    break;
                case IMemberReferenceOperation member:
                    referenced.Add((member.Member, member.Instance is IInstanceReferenceOperation));
                    if (member is IEventReferenceOperation && op.Parent is not IEventAssignmentOperation)
                        UsesEventAsValue = true;
                    break;
                case IInvocationOperation invocation:
                    referenced.Add((invocation.TargetMethod, invocation.Instance is IInstanceReferenceOperation));
                    break;
                case IObjectCreationOperation { Constructor: { } constructor }:
                    referenced.Add((constructor, false));
                    break;
            }

            if (EvaluationOrder.IsEffect(op, IsOwn))
                HasEffects = true;
            return true;
        }, op => EvaluationOrder.IsEffect(op, IsOwn));

        var flow = Root is ExpressionSyntax rootExpression
            ? Model.AnalyzeDataFlow(rootExpression)
            : Model.AnalyzeDataFlow((StatementSyntax)Root);
        var written = flow?.WrittenInside ?? [];
        Parameters = Method.Parameters
            .Select(p => new InputUses(parameterUses[p.Ordinal], written.Contains(p, SymbolEqualityComparer.Default)))
            .ToList();
        This = new InputUses(thisUses, Written: false);
        UsesBase = Root.DescendantNodesAndSelf().OfType<BaseExpressionSyntax>().Any();
        Referenced = referenced;
        ReducedExtensions = extensions;

        if (!Method.ReturnsVoid && Root is ExpressionSyntax expression)
        {
            var type = Model.GetTypeInfo(expression, cancellationToken).Type;
            NeedsReturnCast = type == null || !SymbolEqualityComparer.Default.Equals(type, Method.ReturnType);
        }
    }

    private Use Use(IOperation operation, bool effectBefore)
    {
        var (conditional, repeated) = EvaluationOrder.Context(operation.Syntax, Root);
        return new Use(effectBefore, conditional, repeated);
    }

    private static SyntaxToken QueryIdentifier(SyntaxNode node) => node switch
    {
        FromClauseSyntax from => from.Identifier,
        LetClauseSyntax let => let.Identifier,
        JoinClauseSyntax join => join.Identifier,
        JoinIntoClauseSyntax into => into.Identifier,
        QueryContinuationSyntax continuation => continuation.Identifier,
        _ => default
    };

    /// <summary>
    /// Classifies the names of the body top-down; a name bound to a type is replaced as a whole,
    /// so its parts are not looked at.
    /// </summary>
    private void CollectNames(
        SyntaxNode node,
        HashSet<ISymbol> declared,
        List<(ISymbol, bool)> referenced,
        List<IMethodSymbol> extensions,
        CancellationToken cancellationToken)
    {
        if (node is InvocationExpressionSyntax invocation &&
            Model.GetOperation(invocation, cancellationToken) is INameOfOperation { ConstantValue: { HasValue: true, Value: string value } })
        {
            _nameOfs[invocation] = value;
            return;
        }

        if (node is NameSyntax or MemberAccessExpressionSyntax && Classify(node, declared, cancellationToken) is { } classified)
        {
            _names[node] = classified;
            if (classified.Kind == NameKind.Type)
            {
                referenced.Add((classified.Symbol, false));
                return;
            }
        }

        if (node is SimpleNameSyntax simple && Model.GetSymbolInfo(simple, cancellationToken).Symbol is IMethodSymbol { ReducedFrom: { } reducedFrom } &&
            simple.Parent is MemberAccessExpressionSyntax access && access.Name == simple)
        {
            extensions.Add(reducedFrom);
        }

        foreach (var child in node.ChildNodes())
            CollectNames(child, declared, referenced, extensions, cancellationToken);
    }

    private (NameKind Kind, ISymbol Symbol)? Classify(SyntaxNode node, HashSet<ISymbol> declared, CancellationToken cancellationToken)
    {
        var symbol = Model.GetSymbolInfo(node, cancellationToken).Symbol;
        if (symbol == null)
            return null;

        // What the body declares itself (a generic local function is used constructed).
        if (declared.Contains(symbol.OriginalDefinition))
        {
            if (node is SimpleNameSyntax own)
                _declaredTokens[own.Identifier] = symbol.OriginalDefinition;
            return null;
        }

        if (symbol is ITypeSymbol type)
        {
            if (type is ITypeParameterSymbol typeParameter)
            {
                var own = SymbolEqualityComparer.Default.Equals(typeParameter.DeclaringMethod, Method) ||
                    (typeParameter.DeclaringType is { } declaringType && IsContainingType(declaringType));
                return own && node is IdentifierNameSyntax ? (NameKind.TypeParameter, typeParameter) : null;
            }

            // A name written for the type (not var, dynamic, nint); an alias counts.
            var written = node switch
            {
                SimpleNameSyntax simpleName => simpleName.Identifier.ValueText,
                QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
                AliasQualifiedNameSyntax aliasQualified => aliasQualified.Name.Identifier.ValueText,
                MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
                _ => null
            };
            var isAlias = node is IdentifierNameSyntax identifier && Model.GetAliasInfo(identifier, cancellationToken) != null;
            return type is INamedTypeSymbol && (isAlias || written == type.Name) ? (NameKind.Type, type) : null;
        }

        if (node is not SimpleNameSyntax name)
            return null;

        if (symbol is IParameterSymbol parameter && SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, Method))
            return (NameKind.Parameter, parameter);

        if (symbol is not (IFieldSymbol or IPropertySymbol or IMethodSymbol or IEventSymbol) || !IsMemberAccessByName(name))
            return null;

        return symbol.IsStatic || symbol is IFieldSymbol { IsConst: true }
            ? (NameKind.StaticMember, symbol)
            : (NameKind.ThisMember, symbol);
    }

    private bool IsContainingType(INamedTypeSymbol type)
    {
        for (var containing = Method.ContainingType; containing != null; containing = containing.ContainingType)
        {
            if (SymbolEqualityComparer.Default.Equals(containing.OriginalDefinition, type.OriginalDefinition))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a simple name accesses a member by itself (implicitly through <c>this</c> or its
    /// type), rather than after a dot, as an initializer target or as a named argument.
    /// </summary>
    private static bool IsMemberAccessByName(SimpleNameSyntax name) => name.Parent switch
    {
        MemberAccessExpressionSyntax access => access.Expression == name,
        MemberBindingExpressionSyntax or QualifiedNameSyntax or AliasQualifiedNameSyntax => false,
        NameColonSyntax or NameEqualsSyntax or ExpressionColonSyntax => false,
        AssignmentExpressionSyntax assignment => !(assignment.Left == name && assignment.Parent is InitializerExpressionSyntax
        {
            RawKind: (int)SyntaxKind.ObjectInitializerExpression or (int)SyntaxKind.WithInitializerExpression
        }),
        _ => true
    };

    /// <summary>
    /// A copy of the body for one call site: parameters replaced by <paramref name="arguments"/>,
    /// <c>this</c> by <paramref name="receiver"/> (null keeps <c>this</c>), declared names
    /// renamed, type parameters replaced through <paramref name="typeMap"/>, and the types and
    /// static members it names written so that they bind wherever the copy goes (fully qualified
    /// and marked for the simplifier). Names of members carry the member's identity so the result
    /// can be checked. Null when a type can't be written at the call site.
    /// </summary>
    public SyntaxNode? Rewrite(
        IReadOnlyList<ExpressionSyntax?> arguments,
        ExpressionSyntax? receiver,
        IReadOnlyDictionary<ISymbol, string> renames,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeMap,
        bool nullableAnnotations,
        out string? failure)
    {
        var rewriter = new Rewriter(this, arguments, receiver, renames, typeMap, nullableAnnotations);
        var result = rewriter.Visit(Root);
        failure = rewriter.Failure;
        return failure == null ? result : null;
    }

    private enum NameKind
    {
        Parameter,
        TypeParameter,
        Type,
        StaticMember,
        ThisMember
    }

    private sealed class Rewriter(
        InlinedBody body,
        IReadOnlyList<ExpressionSyntax?> arguments,
        ExpressionSyntax? receiver,
        IReadOnlyDictionary<ISymbol, string> renames,
        IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> typeMap,
        bool nullableAnnotations) : CSharpSyntaxRewriter
    {
        public string? Failure { get; private set; }

        public override SyntaxNode? Visit(SyntaxNode? node)
        {
            if (node == null || Failure != null)
                return node;

            if (node is InvocationExpressionSyntax && body._nameOfs.TryGetValue(node, out var nameOf))
            {
                return SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(nameOf))
                    .WithTriviaFrom(node);
            }

            if (!body._names.TryGetValue(node, out var name))
                return Annotate(node, base.Visit(node));

            switch (name.Kind)
            {
                case NameKind.Parameter:
                    var argument = arguments[((IParameterSymbol)name.Symbol).Ordinal]
                        ?? throw new InvalidOperationException($"No value for parameter '{name.Symbol.Name}'.");
                    return argument.WithTriviaFrom(node);

                case NameKind.TypeParameter or NameKind.Type:
                    return WriteType((ITypeSymbol)name.Symbol, node, asExpression: node.Parent is MemberAccessExpressionSyntax access && access.Expression == node) ?? node;

                case NameKind.StaticMember:
                    var containing = WriteType(name.Symbol.ContainingType, node, asExpression: true);
                    if (containing == null)
                        return node;
                    var staticName = (SimpleNameSyntax)Annotate(node, base.Visit(node))!;
                    return SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, containing.WithoutTrivia(), staticName.WithoutTrivia())
                        .WithTriviaFrom(node)
                        .WithAdditionalAnnotations(Simplifier.Annotation);

                default:
                    var memberName = (SimpleNameSyntax)Annotate(node, base.Visit(node))!;
                    return SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            receiver ?? SyntaxFactory.ThisExpression(),
                            memberName.WithoutTrivia())
                        .WithTriviaFrom(node)
                        .WithAdditionalAnnotations(Simplifier.Annotation);
            }
        }

        public override SyntaxNode? VisitThisExpression(ThisExpressionSyntax node) =>
            receiver == null ? node : receiver.WithTriviaFrom(node);

        public override SyntaxNode? VisitNullableType(NullableTypeSyntax node)
        {
            // T? of an unconstrained T is T itself when T is a value type.
            if (body._names.TryGetValue(node.ElementType, out var name) && name.Kind == NameKind.TypeParameter &&
                name.Symbol is ITypeParameterSymbol { HasValueTypeConstraint: false } parameter &&
                typeMap.TryGetValue(parameter, out var type) && type.IsValueType)
            {
                return Visit(node.ElementType)?.WithTriviaFrom(node);
            }

            return base.VisitNullableType(node);
        }

        public override SyntaxToken VisitToken(SyntaxToken token)
        {
            if (body._declaredTokens.TryGetValue(token, out var symbol) && renames.TryGetValue(symbol, out var newName))
                return SyntaxFactory.Identifier(token.LeadingTrivia, newName, token.TrailingTrivia);
            return base.VisitToken(token);
        }

        /// <summary>
        /// <paramref name="type"/> after type parameter substitution, written for the call site;
        /// null (with <see cref="Failure"/> set) when it can't be written there.
        /// </summary>
        /// <param name="type">The type as the body names it.</param>
        /// <param name="original">The name it replaces.</param>
        /// <param name="asExpression">
        /// Whether it is the left side of a member access, where a qualified name has to be written
        /// as member accesses.
        /// </param>
        private ExpressionSyntax? WriteType(ITypeSymbol type, SyntaxNode original, bool asExpression)
        {
            var substituted = MemberFamily.Substitute(type, typeMap, body.Model.Compilation);
            if (substituted == null || !IsWritable(substituted))
            {
                Failure = $"the type '{type.ToDisplayString()}' cannot be written at the call site";
                return null;
            }

            ExpressionSyntax syntax = SyntaxGenerationHelper.CreateTypeSyntax(substituted, nullableAnnotations);
            if (asExpression && syntax is not PredefinedTypeSyntax)
                syntax = SyntaxFactory.ParseExpression(syntax.ToString()).WithAdditionalAnnotations(Simplifier.Annotation);
            return syntax.WithTriviaFrom(original);
        }

        /// <summary>Marks a member name with the member it must bind to.</summary>
        private SyntaxNode? Annotate(SyntaxNode original, SyntaxNode? rewritten)
        {
            if (rewritten is not SimpleNameSyntax ||
                body.Model.GetSymbolInfo(original).Symbol is not (IFieldSymbol or IPropertySymbol or IMethodSymbol or IEventSymbol) ||
                body.Model.GetSymbolInfo(original).Symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction })
            {
                return rewritten;
            }

            var symbol = body.Model.GetSymbolInfo(original).Symbol!;
            var id = Identity(symbol);
            return id == null ? rewritten : rewritten.WithAdditionalAnnotations(new SyntaxAnnotation(SymbolAnnotationKind, id));
        }
    }

    /// <summary>
    /// A member's identity across compilations: the documentation ID of its definition (of the
    /// extension method itself for a call in extension form).
    /// </summary>
    public static string? Identity(ISymbol symbol)
    {
        var definition = symbol is IMethodSymbol { ReducedFrom: { } reducedFrom } ? reducedFrom : symbol;
        return definition.OriginalDefinition.GetDocumentationCommentId();
    }

    /// <summary>Whether a type can be written in source: no anonymous, error or pointer types.</summary>
    public static bool IsWritable(ITypeSymbol type) => type switch
    {
        { IsAnonymousType: true } or { TypeKind: TypeKind.Error } or IPointerTypeSymbol or IFunctionPointerTypeSymbol => false,
        IArrayTypeSymbol array => IsWritable(array.ElementType),
        INamedTypeSymbol named => named.TypeArguments.All(IsWritable) && (named.ContainingType == null || IsWritable(named.ContainingType)),
        _ => true
    };
}

/// <summary>One evaluation of a parameter or of <c>this</c> in the body.</summary>
/// <param name="EffectBefore">Whether a side effect completes before it.</param>
/// <param name="Conditional">Whether it may not run.</param>
/// <param name="Repeated">Whether it may run more than once or later (loop, lambda).</param>
internal sealed record Use(bool EffectBefore, bool Conditional, bool Repeated);

/// <summary>How the body uses one input (a parameter or <c>this</c>).</summary>
/// <param name="Uses">Its evaluations.</param>
/// <param name="Written">Whether the body assigns it.</param>
internal sealed record InputUses(IReadOnlyList<Use> Uses, bool Written)
{
    public static readonly InputUses None = new([], false);

    public int Count => Uses.Count;

    /// <summary>
    /// Whether an expression with side effects can take its place: it is evaluated exactly once,
    /// unconditionally, before any other side effect of the body.
    /// </summary>
    public bool OnceFirst => Uses is [{ EffectBefore: false, Conditional: false, Repeated: false }];

    /// <summary>
    /// Whether a read of state can take its place, however often: no side effect of the body
    /// can run before any of its evaluations.
    /// </summary>
    public bool BeforeEffects(bool bodyHasEffects) => Uses.All(u => !u.EffectBefore && !(u.Repeated && bodyHasEffects));
}
