using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Convert;

/// <summary>
/// Converts members between expression body and block body forms.
/// </summary>
/// <remarks>
/// Only the body changes: the member's attributes, comments and signature stay as written, and
/// so does the expression, whose other lines move by as much as its first. To an expression
/// body, <c>=&gt; expression;</c> goes on the line the signature ends on; to a block body, the
/// block is laid out like the rest of the file. A body with comments that would have no place is
/// refused.
/// </remarks>
public sealed class ConvertExpressionBodyOperation : RefactoringOperationBase<ConvertExpressionBodyParams>
{
    /// <inheritdoc />
    public ConvertExpressionBodyOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ConvertExpressionBodyParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.Direction))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "direction is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, PathResolver.GetCSharpFileRejectionReason(@params.SourceFile));

        if (!@params.Line.HasValue && string.IsNullOrWhiteSpace(@params.MemberName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "Either memberName or line must be provided.");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (!Enum.TryParse<ConversionDirection>(@params.Direction, ignoreCase: true, out var dir) ||
            (dir != ConversionDirection.ToExpressionBody && dir != ConversionDirection.ToBlockBody))
        {
            throw new RefactoringException(ErrorCodes.CannotConvert,
                "direction must be 'ToExpressionBody' or 'ToBlockBody'.");
        }

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ConvertExpressionBodyParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || model == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var direction = Enum.Parse<ConversionDirection>(@params.Direction, ignoreCase: true);

        // Find the target member
        var member = FindMember(root, @params.MemberName, @params.Line);
        if (member == null)
            throw new RefactoringException(ErrorCodes.MethodNotFound,
                $"Member '{@params.MemberName ?? $"at line {@params.Line}"}' not found.");

        SyntaxNode newMember;
        string beforeSnippet;
        string afterSnippet;

        if (direction == ConversionDirection.ToExpressionBody)
        {
            (newMember, beforeSnippet, afterSnippet) = ConvertToExpressionBody(member);
        }
        else
        {
            // A void method, or an async one returning Task / ValueTask, gets no return.
            var returnsNothing = model.GetDeclaredSymbol(member, cancellationToken) is IMethodSymbol method &&
                (method.ReturnsVoid || method.IsAsync && method.ReturnType is INamedTypeSymbol { IsGenericType: false, Name: "Task" or "ValueTask" });
            (newMember, beforeSnippet, afterSnippet) = ConvertToBlockBody(member, returnsNothing);
        }

        var newRoot = root.ReplaceNode(member, newMember);
        var newSolution = await GeneratedCodeFormatter.FormatAsync(
            document.WithSyntaxRoot(newRoot).Project.Solution, document, cancellationToken);

        if (@params.Preview)
        {
            await PreviewCommitAsync(newSolution, cancellationToken);
            var pendingChanges = new List<PendingChange>
            {
                new()
                {
                    File = @params.SourceFile,
                    ChangeType = ChangeKind.Modify,
                    Description = $"Convert member to {direction}",
                    BeforeSnippet = beforeSnippet,
                    AfterSnippet = afterSnippet
                }
            };
            return RefactoringResult.PreviewResult(operationId, pendingChanges);
        }

        var commitResult = await CommitChangesAsync(newSolution, cancellationToken);

        return RefactoringResult.Succeeded(operationId,
            new FileChanges { FilesModified = commitResult.FilesModified, FilesCreated = commitResult.FilesCreated, FilesDeleted = commitResult.FilesDeleted },
            null, 0, 0);
    }

    private static MemberDeclarationSyntax? FindMember(SyntaxNode root, string? memberName, int? line)
    {
        var members = root.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .Where(m => m is MethodDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax
                        or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax);

        if (!string.IsNullOrWhiteSpace(memberName))
        {
            members = members.Where(m => GetMemberName(m) == memberName);
        }

        if (line.HasValue)
        {
            members = members.Where(m =>
                m.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line.Value);
        }

        return members.FirstOrDefault();
    }

    private static string? GetMemberName(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        PropertyDeclarationSyntax p => p.Identifier.Text,
        IndexerDeclarationSyntax => "this[]",
        OperatorDeclarationSyntax o => $"operator {o.OperatorToken.Text}",
        ConversionOperatorDeclarationSyntax c => $"implicit/explicit operator",
        _ => null
    };

    private static (SyntaxNode newNode, string before, string after) ConvertToExpressionBody(MemberDeclarationSyntax member)
    {
        switch (member)
        {
            case MethodDeclarationSyntax method:
                if (method.ExpressionBody != null)
                    throw new RefactoringException(ErrorCodes.CannotConvert, "Method already has expression body.");
                if (method.Body == null || method.Body.Statements.Count != 1)
                    throw new RefactoringException(ErrorCodes.CannotConvert, "Method body must contain exactly one statement to convert.");

                var statement = method.Body.Statements[0];
                var expr = ExtractExpression(statement)
                    ?? throw new RefactoringException(ErrorCodes.CannotConvert, "Cannot extract expression from statement.");

                var before = method.Body.ToString().Trim();
                var (arrow, semicolon) = ToArrow(method.Body.OpenBraceToken, method.Body.CloseBraceToken, method.Body, statement, expr.Value, expr.Kept);
                var newMethod = WithoutTrailingWhitespace(method, method.Body.OpenBraceToken.GetPreviousToken())
                    .WithBody(null)
                    .WithExpressionBody(arrow)
                    .WithSemicolonToken(semicolon);
                return (newMethod, before, $"=> {arrow.Expression};");

            case PropertyDeclarationSyntax prop:
                if (prop.ExpressionBody != null)
                    throw new RefactoringException(ErrorCodes.CannotConvert, "Property already has expression body.");
                if (prop.AccessorList?.Accessors.Count != 1 || prop.AccessorList.Accessors[0].Keyword.IsKind(SyntaxKind.SetKeyword))
                    throw new RefactoringException(ErrorCodes.CannotConvert, "Only single get-only properties can be converted to expression body.");

                var getter = prop.AccessorList!.Accessors[0];
                if (getter.AttributeLists.Count > 0 || getter.Modifiers.Count > 0)
                    throw new RefactoringException(ErrorCodes.CannotConvert, "The getter has attributes or modifiers, which an expression-bodied property has no place for.");

                (ExpressionSyntax Value, TextSpan Kept)? propExpr;
                StatementSyntax? getterStatement = null;

                if (getter.ExpressionBody != null)
                {
                    propExpr = (getter.ExpressionBody.Expression, getter.ExpressionBody.Expression.Span);
                }
                else if (getter.Body?.Statements.Count == 1)
                {
                    getterStatement = getter.Body.Statements[0];
                    propExpr = ExtractExpression(getterStatement);
                }
                else
                {
                    throw new RefactoringException(ErrorCodes.CannotConvert, "Getter must contain exactly one return statement.");
                }

                if (propExpr == null)
                    throw new RefactoringException(ErrorCodes.CannotConvert, "Cannot extract expression from getter.");

                var propBefore = prop.AccessorList.ToString().Trim();
                var (propArrow, propSemicolon) = ToArrow(
                    prop.AccessorList.OpenBraceToken, prop.AccessorList.CloseBraceToken, prop.AccessorList, getterStatement, propExpr.Value.Value, propExpr.Value.Kept);
                var newProp = WithoutTrailingWhitespace(prop, prop.AccessorList.OpenBraceToken.GetPreviousToken())
                    .WithAccessorList(null)
                    .WithExpressionBody(propArrow)
                    .WithSemicolonToken(propSemicolon);
                return (newProp, propBefore, $"=> {propArrow.Expression};");

            default:
                throw new RefactoringException(ErrorCodes.CannotConvert, "Member type does not support expression body conversion.");
        }
    }

    /// <summary>
    /// The arrow clause and semicolon that replace a body (a block, or an accessor list with one
    /// getter): <c> =&gt; expression;</c> on the line the member's signature ends on. The
    /// expression stays as written, its other lines moved by as much as its first line; a comment
    /// after the statement and the line break after the body's closing brace follow the semicolon.
    /// Refuses a body with comments or directives that would have no place.
    /// </summary>
    /// <param name="open">The body's opening brace.</param>
    /// <param name="close">The body's closing brace.</param>
    /// <param name="body">The body.</param>
    /// <param name="statement">The statement holding the expression, if any (not for <c>get =&gt; x;</c>).</param>
    /// <param name="value">The expression the arrow returns.</param>
    /// <param name="kept">Where the code of <paramref name="value"/> is in the body.</param>
    private static (ArrowExpressionClauseSyntax Arrow, SyntaxToken Semicolon) ToArrow(
        SyntaxToken open,
        SyntaxToken close,
        SyntaxNode body,
        StatementSyntax? statement,
        ExpressionSyntax value,
        TextSpan kept)
    {
        var signatureEnd = open.GetPreviousToken();
        var lost = new List<SyntaxTrivia>(signatureEnd.TrailingTrivia);
        var keptTokens = body.DescendantTokens().Where(t => kept.Contains(t.Span)).ToList();
        foreach (var token in body.DescendantTokens())
        {
            if (keptTokens.Contains(token))
            {
                if (token == keptTokens[0])
                    lost.AddRange(token.LeadingTrivia);
                if (token == keptTokens[^1])
                    lost.AddRange(token.TrailingTrivia);
                continue;
            }

            lost.AddRange(token.LeadingTrivia);
            if (token != close && (statement == null || token != statement.GetLastToken()))
                lost.AddRange(token.TrailingTrivia);
        }

        if (lost.Any(t => !t.IsKind(SyntaxKind.WhitespaceTrivia) && !t.IsKind(SyntaxKind.EndOfLineTrivia)))
        {
            throw new RefactoringException(
                ErrorCodes.CannotConvert,
                "The body has comments or directives outside its expression, which an expression body has no place for; move them first.");
        }

        // The expression keeps its layout; its other lines move by as much as its first line does.
        var lines = open.SyntaxTree!.GetText().Lines;
        var from = GeneratedCodeFormatter.LeadingWhitespace(lines.GetLineFromPosition(kept.Start).ToString());
        var to = GeneratedCodeFormatter.LeadingWhitespace(lines.GetLineFromPosition(signatureEnd.SpanStart).ToString());
        var arrow = SyntaxFactory.ArrowExpressionClause(
            SyntaxFactory.Token(SyntaxKind.EqualsGreaterThanToken).WithLeadingTrivia(SyntaxFactory.Space).WithTrailingTrivia(SyntaxFactory.Space),
            GeneratedCodeFormatter.Reindent(value.WithoutTrivia(), from, to, startsLine: false));

        // `return x; // why` keeps its comment after the new semicolon.
        var comment = statement == null
            ? []
            : statement.GetTrailingTrivia().TakeWhile(t => !t.IsKind(SyntaxKind.EndOfLineTrivia)).ToList();
        var semicolon = SyntaxFactory.Token(SyntaxKind.SemicolonToken).WithTrailingTrivia(
            comment.Any(t => !t.IsKind(SyntaxKind.WhitespaceTrivia)) ? comment.Concat(close.TrailingTrivia) : close.TrailingTrivia);
        return (arrow, semicolon);
    }

    /// <summary>The member with the whitespace (and line break) after <paramref name="token"/> removed.</summary>
    private static TMember WithoutTrailingWhitespace<TMember>(TMember member, SyntaxToken token)
        where TMember : SyntaxNode =>
        member.ReplaceToken(token, token.WithTrailingTrivia(
            token.TrailingTrivia.Where(t => !t.IsKind(SyntaxKind.WhitespaceTrivia) && !t.IsKind(SyntaxKind.EndOfLineTrivia))));

    private static (SyntaxNode newNode, string before, string after) ConvertToBlockBody(MemberDeclarationSyntax member, bool returnsNothing)
    {
        switch (member)
        {
            case MethodDeclarationSyntax method:
                if (method.ExpressionBody == null)
                    throw new RefactoringException(ErrorCodes.CannotConvert, "Method does not have an expression body.");

                var methodBefore = $"=> {method.ExpressionBody.Expression};";
                var block = ToBlock(method.ExpressionBody, method.SemicolonToken, returnsNothing);
                var newMethod = WithoutTrailingWhitespace(method, method.ExpressionBody.ArrowToken.GetPreviousToken())
                    .WithExpressionBody(null)
                    .WithSemicolonToken(default)
                    .WithBody(block);
                return (newMethod, methodBefore, block.ToString().Trim());

            case PropertyDeclarationSyntax prop:
                if (prop.ExpressionBody == null)
                    throw new RefactoringException(ErrorCodes.CannotConvert, "Property does not have an expression body.");

                var propBefore = $"=> {prop.ExpressionBody.Expression};";
                var getterBody = ToBlock(prop.ExpressionBody, prop.SemicolonToken, returnsNothing: false);
                var accessors = SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(
                        SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithBody(getterBody.WithoutTrivia())))
                    .NormalizeWhitespace();
                var newProp = WithoutTrailingWhitespace(prop, prop.ExpressionBody.ArrowToken.GetPreviousToken())
                    .WithExpressionBody(null)
                    .WithSemicolonToken(default)
                    .WithAccessorList(GeneratedCodeFormatter.Mark(accessors
                        .WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)
                        .WithTrailingTrivia(prop.SemicolonToken.TrailingTrivia)));
                return (newProp, propBefore, accessors.ToString().Trim());

            default:
                throw new RefactoringException(ErrorCodes.CannotConvert, "Member type does not support block body conversion.");
        }
    }

    /// <summary>
    /// The block for an expression body: the expression returned (a statement of its own when the
    /// member returns nothing, thrown when it is a throw expression). The formatter lays out the
    /// block; the expression stays as written.
    /// </summary>
    private static BlockSyntax ToBlock(ArrowExpressionClauseSyntax arrow, SyntaxToken semicolon, bool returnsNothing)
    {
        if (arrow.ArrowToken.GetPreviousToken().TrailingTrivia.Concat(arrow.ArrowToken.LeadingTrivia).Concat(arrow.ArrowToken.TrailingTrivia)
                .Concat(arrow.Expression.GetLeadingTrivia()).Concat(arrow.Expression.GetTrailingTrivia()).Concat(semicolon.LeadingTrivia)
                .Any(t => !t.IsKind(SyntaxKind.WhitespaceTrivia) && !t.IsKind(SyntaxKind.EndOfLineTrivia)))
        {
            throw new RefactoringException(
                ErrorCodes.CannotConvert,
                "The expression body has comments around its expression, which the conversion would lose; move them first.");
        }

        var expression = arrow.Expression;
        StatementSyntax statement = expression switch
        {
            ThrowExpressionSyntax thrown => SyntaxFactory.ThrowStatement(Kept(thrown.Expression)),
            _ when returnsNothing => SyntaxFactory.ExpressionStatement(Kept(expression)),
            _ => SyntaxFactory.ReturnStatement(Kept(expression))
        };

        return GeneratedCodeFormatter.Mark(SyntaxFactory.Block(statement).NormalizeWhitespace()
            .WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)
            .WithTrailingTrivia(semicolon.TrailingTrivia));
    }

    /// <summary>An expression marked to keep the layout it is written with.</summary>
    private static ExpressionSyntax Kept(ExpressionSyntax expression) =>
        GeneratedCodeFormatter.KeepLayout(expression.WithoutTrivia(), expression.WithoutTrivia(), expression);

    /// <summary>The expression a statement can become (with where its code is), or null.</summary>
    private static (ExpressionSyntax Value, TextSpan Kept)? ExtractExpression(StatementSyntax statement) => statement switch
    {
        ReturnStatementSyntax { Expression: { } returned } => (returned, returned.Span),
        ExpressionStatementSyntax exprStmt => (exprStmt.Expression, exprStmt.Expression.Span),
        ThrowStatementSyntax { Expression: { } thrown } throwStmt =>
            (SyntaxFactory.ThrowExpression(throwStmt.ThrowKeyword, thrown), TextSpan.FromBounds(throwStmt.ThrowKeyword.SpanStart, thrown.Span.End)),
        _ => null
    };
}
