using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;
using RoslynMcp.Core.Refactoring.Utilities;

namespace RoslynMcp.Core.Refactoring.Inline;

/// <summary>
/// How one call is inlined (see <see cref="InlineCallPlanner"/>), and the syntax that replaces
/// it. The syntax is built from the call as other edits left it, so calls nested in its
/// arguments are inlined too.
/// </summary>
internal sealed class CallPlan
{
    /// <summary>Annotation kind on the code that replaces a call; its data is <see cref="Site"/>.</summary>
    public const string SiteAnnotationKind = "RoslynMcp.InlineMethod.Site";

    /// <summary>Annotation kind on a block whose statements belong in the enclosing statement list.</summary>
    public const string SpliceAnnotationKind = "RoslynMcp.InlineMethod.Splice";

    public required int Site { get; init; }
    public required Document Document { get; init; }
    public required InvocationExpressionSyntax Invocation { get; init; }
    public required CallForm Form { get; init; }

    /// <summary>The call's statement, for <see cref="CallForm.Statement"/> and <see cref="CallForm.Discard"/>.</summary>
    public ExpressionStatementSyntax? Statement { get; init; }

    /// <summary>The statement the locals of an expression call go before, if it needs any.</summary>
    public StatementSyntax? HoistBefore { get; init; }

    /// <summary>The receiver (if explicit) and the arguments, in evaluation order.</summary>
    public required IReadOnlyList<CallInput> Inputs { get; init; }

    public required bool HasReceiver { get; init; }
    public required IReadOnlyDictionary<ISymbol, string> Renames { get; init; }
    public required IReadOnlyDictionary<ITypeParameterSymbol, ITypeSymbol> TypeMap { get; init; }
    public required bool NullableAnnotations { get; init; }

    /// <summary>The return type at this call, when the returned expression needs a cast to it.</summary>
    public ITypeSymbol? ReturnType { get; init; }

    /// <summary>Namespaces the call's file has to import for the body's extension method calls.</summary>
    public required IReadOnlyList<string> Namespaces { get; init; }

    public required InlinedBody Body { get; init; }

    /// <summary>What the inlined call does differently, for <c>warnings</c>.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>The local declarations (and evaluations of unused inputs) the last <see cref="BuildBody"/> produced.</summary>
    public IReadOnlyList<StatementSyntax> Temps { get; private set; } = [];

    private SyntaxAnnotation SiteAnnotation => new(SiteAnnotationKind, Site.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// The body rewritten for <paramref name="invocation"/> (the call, possibly with nested calls
    /// already inlined), and the locals it needs in <see cref="Temps"/>.
    /// </summary>
    public SyntaxNode? BuildBody(InvocationExpressionSyntax invocation, out string? failure)
    {
        var arguments = new ExpressionSyntax?[Body.Method.Parameters.Length];
        ExpressionSyntax? receiver = null;
        var temps = new List<StatementSyntax>();
        foreach (var input in Inputs)
        {
            if (input.Mode == InputMode.Drop)
                continue;

            var expression = Source(input, invocation).WithoutTrivia();
            if (input.Mode == InputMode.Evaluate)
            {
                temps.Add(Mark(Evaluation(expression)));
                continue;
            }

            ExpressionSyntax value;
            if (input.Mode == InputMode.Temp)
            {
                temps.Add(Mark(TempDeclaration(input, expression)));
                value = SyntaxFactory.IdentifierName(input.TempName!);
            }
            else
            {
                value = Parenthesize(input.NeedsCast ? Cast(input.Type, expression) : expression);
            }

            if (input.Parameter == null)
                receiver = value;
            else
                arguments[input.Parameter.Ordinal] = value;
        }

        Temps = temps;
        return Body.Rewrite(arguments, HasReceiver ? receiver : null, Renames, TypeMap, NullableAnnotations, out failure);
    }

    /// <summary>The expression replacing the call, from <paramref name="invocation"/> as other edits left it.</summary>
    public ExpressionSyntax BuildExpression(InvocationExpressionSyntax invocation)
    {
        var body = ((ExpressionSyntax)BuildBody(invocation, out _)!).WithoutTrivia().WithAdditionalAnnotations(SiteAnnotation);
        if (body.GetText().Lines.Count > 1)
            body = GeneratedCodeFormatter.Mark(KeepBodyLayout(body, Body.Root));

        if (Body.Method.ReturnsVoid)
            return body.WithTriviaFrom(invocation);

        return Parenthesize(ReturnType != null ? Cast(ReturnType, body) : body).WithTriviaFrom(invocation);
    }

    /// <summary>
    /// The statements replacing the call's statement (as other edits left it): its locals, then
    /// the body's statements, or the returned expression as a statement.
    /// </summary>
    public List<StatementSyntax> BuildStatements(ExpressionStatementSyntax statement)
    {
        var body = BuildBody((InvocationExpressionSyntax)statement.Expression, out _)!;
        var statements = new List<StatementSyntax>(Temps);
        if (body is BlockSyntax block)
        {
            var originals = ((BlockSyntax)Body.Root).Statements;
            statements.AddRange(block.Statements.Select((s, i) => Mark(
                (block.Statements.Count == originals.Count ? KeepBodyLayout(s, originals[i]) : s).WithAdditionalAnnotations(SiteAnnotation))));
        }
        else
        {
            var expression = (ExpressionSyntax)body;
            var kept = KeepBodyLayout(expression.WithoutTrivia(), Body.Root);
            if (!IsStatementExpression(expression))
            {
                kept = SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.ParseExpression("_"), kept);
            }

            statements.Add(Mark(SyntaxFactory.ExpressionStatement(kept)));
        }

        return statements;
    }

    /// <summary>
    /// The replacement of <paramref name="original"/> by <paramref name="statements"/>: a block
    /// marked for splicing into the enclosing statement list (a block, a switch section, or the
    /// top-level statements), the statement itself in an embedded position, or a real block there.
    /// </summary>
    public static StatementSyntax Replace(StatementSyntax original, IReadOnlyList<StatementSyntax> statements)
    {
        var list = statements.ToList();
        if (list.Count > 0)
        {
            list[0] = list[0].WithLeadingTrivia(original.GetLeadingTrivia().AddRange(
                list[0].GetLeadingTrivia().SkipWhile(t => t.IsKind(SyntaxKind.WhitespaceTrivia) || t.IsKind(SyntaxKind.EndOfLineTrivia))));

            // A comment after the call moves to the last statement.
            if (original.GetTrailingTrivia().Any(t => !t.IsKind(SyntaxKind.WhitespaceTrivia) && !t.IsKind(SyntaxKind.EndOfLineTrivia)))
                list[^1] = list[^1].WithTrailingTrivia(original.GetTrailingTrivia());
        }

        if (original.Parent is BlockSyntax or SwitchSectionSyntax or GlobalStatementSyntax)
            return SyntaxFactory.Block(list).WithAdditionalAnnotations(new SyntaxAnnotation(SpliceAnnotationKind));

        if (list is [var single] && single is not LocalDeclarationStatementSyntax)
            return single;

        return GeneratedCodeFormatter.Mark(SyntaxFactory.Block(list.Select(s => s.WithoutLeadingTrivia().WithLeadingTrivia(SyntaxFactory.ElasticMarker))))
            .WithTriviaFrom(original);
    }

    /// <summary>
    /// Moves the statements of blocks marked for splicing into their enclosing statement lists (a
    /// top-level statement becomes several). A block written on one line that gains statements is
    /// laid out anew, with its braces on lines of their own.
    /// </summary>
    public static SyntaxNode Splice(SyntaxNode root) => new Splicer().Visit(root);

    private ExpressionSyntax Source(CallInput input, InvocationExpressionSyntax invocation)
    {
        var source = input.Source;
        switch (source.Kind)
        {
            case InputSourceKind.Receiver:
                return ((MemberAccessExpressionSyntax)invocation.Expression).Expression;
            case InputSourceKind.Argument:
                return invocation.ArgumentList.Arguments[source.Index].Expression;
            case InputSourceKind.Default:
                return source.Default!;
            default:
                var elementType = SyntaxGenerationHelper.CreateTypeSyntax(source.ElementType!, NullableAnnotations);
                if (source.ParamsIndexes!.Count == 0)
                {
                    return SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.ParseName("System.Array").WithAdditionalAnnotations(Simplifier.Annotation),
                            SyntaxFactory.GenericName(SyntaxFactory.Identifier("Empty"), SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList(elementType))))
                        .WithAdditionalAnnotations(Simplifier.Annotation));
                }

                var space = SyntaxFactory.Space;
                var elements = source.ParamsIndexes.Select(i => invocation.ArgumentList.Arguments[i].Expression.WithoutTrivia());
                return SyntaxFactory.ArrayCreationExpression(
                    SyntaxFactory.Token(SyntaxKind.NewKeyword).WithTrailingTrivia(space),
                    SyntaxFactory.ArrayType(elementType, SyntaxFactory.SingletonList(SyntaxFactory.ArrayRankSpecifier(
                        SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(SyntaxFactory.OmittedArraySizeExpression())))),
                    SyntaxFactory.InitializerExpression(
                        SyntaxKind.ArrayInitializerExpression,
                        SyntaxFactory.Token(SyntaxKind.OpenBraceToken).WithLeadingTrivia(space).WithTrailingTrivia(space),
                        SyntaxFactory.SeparatedList(elements, elements.Skip(1).Select(_ => SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(space))),
                        SyntaxFactory.Token(SyntaxKind.CloseBraceToken).WithLeadingTrivia(space)));
        }
    }

    private LocalDeclarationStatementSyntax TempDeclaration(CallInput input, ExpressionSyntax value)
    {
        TypeSyntax type = InlinedBody.IsWritable(input.Type)
            ? SyntaxGenerationHelper.CreateTypeSyntax(input.Type, NullableAnnotations)
            : SyntaxFactory.IdentifierName("var");
        return SyntaxFactory.LocalDeclarationStatement(SyntaxFactory.VariableDeclaration(
            type,
            SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(input.TempName!))
                .WithInitializer(SyntaxFactory.EqualsValueClause(value)))));
    }

    /// <summary>An unused input evaluated as a statement: <c>GetObj();</c>, or <c>_ = obj.Value;</c>.</summary>
    private static ExpressionStatementSyntax Evaluation(ExpressionSyntax value) =>
        SyntaxFactory.ExpressionStatement(IsStatementExpression(value)
            ? value
            : SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.IdentifierName("_"), value));

    private ExpressionSyntax Cast(ITypeSymbol type, ExpressionSyntax expression) =>
        SyntaxFactory.CastExpression(SyntaxGenerationHelper.CreateTypeSyntax(type, NullableAnnotations), Parenthesize(expression))
            .WithAdditionalAnnotations(Simplifier.Annotation);

    private static ExpressionSyntax Parenthesize(ExpressionSyntax expression) =>
        SyntaxFactory.ParenthesizedExpression(expression.WithoutTrivia()).WithAdditionalAnnotations(Simplifier.Annotation);

    /// <summary>
    /// The method's code, rewritten for the call, marked to keep the layout the method's code is
    /// written with: the formatter places it, but only its indentation changes; what was
    /// substituted (arguments, qualified names) is simplified as usual.
    /// </summary>
    private static TNode KeepBodyLayout<TNode>(TNode rewritten, SyntaxNode original)
        where TNode : SyntaxNode =>
        GeneratedCodeFormatter.KeepLayout(rewritten, rewritten, original);

    private TStatement Mark<TStatement>(TStatement statement)
        where TStatement : StatementSyntax
    {
        // A generated statement ends its line with a plain line break: an elastic one lets the
        // formatter separate top-level statements by a blank line, like members.
        var marked = statement.WithAdditionalAnnotations(SiteAnnotation, Formatter.Annotation);
        return marked.GetTrailingTrivia().Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia))
            ? marked
            : marked.WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);
    }

    private static bool IsStatementExpression(ExpressionSyntax expression) =>
        expression is InvocationExpressionSyntax or AwaitExpressionSyntax or ObjectCreationExpressionSyntax or AssignmentExpressionSyntax ||
        expression.Kind() is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression
            or SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression;

    /// <summary>The method name of a call: what a line and column point at.</summary>
    public static SyntaxToken NameToken(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax access => access.Name.Identifier,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier,
        SimpleNameSyntax name => name.Identifier,
        _ => invocation.Expression.GetFirstToken()
    };

    private sealed class Splicer : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitBlock(BlockSyntax node)
        {
            var visited = (BlockSyntax)base.VisitBlock(node)!;
            if (!visited.Statements.Any(IsSplice))
                return visited;

            var block = visited.WithStatements(Flatten(visited.Statements));
            if (block.Statements.Count == 0 || (EndsLine(block.OpenBraceToken) && EndsLine(block.CloseBraceToken.GetPreviousToken())))
                return block;

            var statements = block.Statements;
            var first = statements[0].WithLeadingTrivia(statements[0].GetLeadingTrivia().SkipWhile(t => t.IsKind(SyntaxKind.WhitespaceTrivia)));
            statements = statements.Replace(statements[0], first);
            var last = statements[^1];
            if (!EndsLine(last.GetLastToken()))
            {
                statements = statements.Replace(last, last.WithTrailingTrivia(
                    last.GetTrailingTrivia().Where(t => !t.IsKind(SyntaxKind.WhitespaceTrivia)).Append(SyntaxFactory.ElasticCarriageReturnLineFeed)));
            }

            return GeneratedCodeFormatter.Mark(block
                .WithOpenBraceToken(block.OpenBraceToken.WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed))
                .WithStatements(statements)
                .WithCloseBraceToken(block.CloseBraceToken.WithLeadingTrivia(SyntaxFactory.ElasticMarker)));
        }

        public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
        {
            var visited = (CompilationUnitSyntax)base.VisitCompilationUnit(node)!;
            return visited.Members.Any(m => m is GlobalStatementSyntax { Statement: var statement } && IsSplice(statement))
                ? visited.WithMembers(SyntaxFactory.List(visited.Members.SelectMany(member =>
                    member is GlobalStatementSyntax { Statement: BlockSyntax block } && IsSplice(block)
                        ? block.Statements.Select(s => (MemberDeclarationSyntax)SyntaxFactory.GlobalStatement(s))
                        : [member])))
                : visited;
        }

        private static bool IsSplice(StatementSyntax statement) => statement.HasAnnotations(SpliceAnnotationKind);

        private static bool EndsLine(SyntaxToken token) => token.TrailingTrivia.Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia));

        public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
        {
            var visited = (SwitchSectionSyntax)base.VisitSwitchSection(node)!;
            return visited.WithStatements(Flatten(visited.Statements));
        }

        private static SyntaxList<StatementSyntax> Flatten(SyntaxList<StatementSyntax> statements) =>
            statements.Any(s => s.HasAnnotations(SpliceAnnotationKind))
                ? SyntaxFactory.List(statements.SelectMany(s =>
                    s is BlockSyntax block && s.HasAnnotations(SpliceAnnotationKind) ? block.Statements : [s]))
                : statements;
    }
}
