using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Simplification;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Generate;

/// <summary>
/// Generates a ToString() override for a type, as an interpolated string (the default) or
/// built with a <see cref="System.Text.StringBuilder"/>. Both produce
/// <c>TypeName { Member1 = value1, Member2 = value2 }</c>.
/// </summary>
public sealed class GenerateToStringOperation : RefactoringOperationBase<GenerateToStringParams>
{
    private const string InterpolatedFormat = "interpolated";
    private const string StringBuilderFormat = "stringbuilder";

    /// <inheritdoc />
    public GenerateToStringOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(GenerateToStringParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.TypeName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "typeName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, PathResolver.GetCSharpFileRejectionReason(@params.SourceFile));

        if (@params.Format != null
            && !@params.Format.Equals(InterpolatedFormat, StringComparison.OrdinalIgnoreCase)
            && !@params.Format.Equals(StringBuilderFormat, StringComparison.OrdinalIgnoreCase))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidParameterValue,
                $"Invalid format '{@params.Format}'. Valid values: {InterpolatedFormat}, {StringBuilderFormat}.");
        }

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        GenerateToStringParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var typeDecl = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.Text == @params.TypeName);

        if (typeDecl == null)
            throw new RefactoringException(ErrorCodes.TypeNotFound, $"Type '{@params.TypeName}' not found.");

        var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl, cancellationToken) as INamedTypeSymbol;
        if (typeSymbol == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve type symbol.");

        // Check for existing ToString override
        if (typeSymbol.GetMembers("ToString").Any(m => m is IMethodSymbol method && !method.IsImplicitlyDeclared && method.Parameters.Length == 0))
            throw new RefactoringException(ErrorCodes.AlreadyHasOverride, "Type already has a ToString override.");

        var members = EqualityMemberCollector.CollectMembers(typeSymbol, @params.Fields);
        if (members.Count == 0)
            throw new RefactoringException(ErrorCodes.NoMembersToGenerate, "No fields or properties available for ToString generation.");

        var toStringMethod = StringBuilderFormat.Equals(@params.Format, StringComparison.OrdinalIgnoreCase)
            ? GenerateStringBuilderToString(@params.TypeName, members)
            : GenerateInterpolatedToString(@params.TypeName, members);

        var newTypeDecl = typeDecl.AddMembers(GeneratedCodeFormatter.MarkInserted(toStringMethod));

        var newRoot = root.ReplaceNode(typeDecl, newTypeDecl);
        var newDocument = document.WithSyntaxRoot(newRoot);
        var newSolution = await GeneratedCodeFormatter.FormatAsync(
            newDocument.Project.Solution, document, cancellationToken);

        if (@params.Preview)
        {
            await PreviewCommitAsync(newSolution, cancellationToken);
            var code = toStringMethod.NormalizeWhitespace().ToFullString();
            var pendingChanges = new List<PendingChange>
            {
                new()
                {
                    File = @params.SourceFile,
                    ChangeType = Contracts.Enums.ChangeKind.Modify,
                    Description = $"Generate ToString for {@params.TypeName}",
                    BeforeSnippet = $"// Type '{@params.TypeName}' (no ToString)",
                    AfterSnippet = code
                }
            };
            return RefactoringResult.PreviewResult(operationId, pendingChanges);
        }

        var commitResult = await CommitChangesAsync(newSolution, cancellationToken);

        return RefactoringResult.Succeeded(operationId,
            new FileChanges { FilesModified = commitResult.FilesModified, FilesCreated = commitResult.FilesCreated, FilesDeleted = commitResult.FilesDeleted },
            new Contracts.Models.SymbolInfo { Name = @params.TypeName, FullyQualifiedName = @params.TypeName, Kind = Contracts.Enums.SymbolKind.Class },
            0, 0);
    }

    private static MethodDeclarationSyntax GenerateInterpolatedToString(string typeName, List<ISymbol> members)
    {
        // $"TypeName {{ Field1 = {Field1}, Field2 = {Field2} }}"
        // Each run of literal text goes into a single text token: adjacent text tokens get
        // separated by whitespace when laid out, which would end up inside the string.
        var parts = new List<InterpolatedStringContentSyntax>();
        var text = typeName + " {{ ";

        for (int i = 0; i < members.Count; i++)
        {
            text += (i == 0 ? "" : ", ") + members[i].Name + " = ";
            parts.Add(InterpolatedText(text));
            parts.Add(SyntaxFactory.Interpolation(IdentifierNames.Name(members[i].Name)));
            text = "";
        }

        parts.Add(InterpolatedText(text + " }}"));

        var interpolatedString = SyntaxFactory.InterpolatedStringExpression(
            SyntaxFactory.Token(SyntaxKind.InterpolatedStringStartToken),
            SyntaxFactory.List(parts));

        return CreateToStringMethod(SyntaxFactory.Block(SyntaxFactory.ReturnStatement(interpolatedString)));
    }

    /// <summary>
    /// Builds the same text as <see cref="GenerateInterpolatedToString"/> with a
    /// <c>StringBuilder</c>: one <c>Append</c> per run of literal text and per member. The
    /// local is named so that it cannot shadow a member it appends.
    /// </summary>
    private static MethodDeclarationSyntax GenerateStringBuilderToString(string typeName, List<ISymbol> members)
    {
        var builder = "builder";
        for (var suffix = 1; members.Any(m => m.Name == builder); suffix++)
            builder = "builder" + suffix;

        // global:: so a namespace or type named System in the user's code cannot capture
        // the name; the simplifier shortens it to what the file's usings allow.
        var builderType = SyntaxFactory.ParseTypeName("global::System.Text.StringBuilder")
            .WithAdditionalAnnotations(Simplifier.Annotation);

        var statements = new List<StatementSyntax>
        {
            SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                    .WithVariables(SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(builder)
                            .WithInitializer(SyntaxFactory.EqualsValueClause(
                                SyntaxFactory.ObjectCreationExpression(builderType)
                                    .WithArgumentList(SyntaxFactory.ArgumentList()))))))
        };

        var text = typeName + " { ";
        for (int i = 0; i < members.Count; i++)
        {
            text += (i == 0 ? "" : ", ") + members[i].Name + " = ";
            statements.Add(Append(builder, SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(text))));
            statements.Add(Append(builder, IdentifierNames.Name(members[i].Name)));
            text = "";
        }

        statements.Add(Append(builder, SyntaxFactory.LiteralExpression(
            SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(" }"))));
        statements.Add(SyntaxFactory.ReturnStatement(Invoke(builder, "ToString")));

        return CreateToStringMethod(SyntaxFactory.Block(statements));
    }

    private static ExpressionStatementSyntax Append(string builder, ExpressionSyntax argument) =>
        SyntaxFactory.ExpressionStatement(Invoke(builder, "Append", argument));

    private static InvocationExpressionSyntax Invoke(string target, string method, params ExpressionSyntax[] arguments) =>
        SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(target),
                    SyntaxFactory.IdentifierName(method)))
            .WithArgumentList(SyntaxFactory.ArgumentList(
                SyntaxFactory.SeparatedList(arguments.Select(SyntaxFactory.Argument))));

    private static MethodDeclarationSyntax CreateToStringMethod(BlockSyntax body) =>
        SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)),
                "ToString")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
            .WithBody(body)
            .NormalizeWhitespace();

    /// <summary>
    /// Literal text of an interpolated string. <paramref name="text"/> is source text, so
    /// literal braces must already be escaped as <c>{{</c> / <c>}}</c>.
    /// </summary>
    private static InterpolatedStringTextSyntax InterpolatedText(string text) =>
        SyntaxFactory.InterpolatedStringText(
            SyntaxFactory.Token(
                SyntaxFactory.TriviaList(),
                SyntaxKind.InterpolatedStringTextToken,
                text,
                text.Replace("{{", "{").Replace("}}", "}"),
                SyntaxFactory.TriviaList()));
}
