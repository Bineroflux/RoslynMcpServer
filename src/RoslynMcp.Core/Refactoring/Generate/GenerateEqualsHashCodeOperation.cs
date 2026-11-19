using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Generate;

/// <summary>
/// Generates Equals() and GetHashCode() overrides for a type.
/// </summary>
public sealed class GenerateEqualsHashCodeOperation : RefactoringOperationBase<GenerateEqualsHashCodeParams>
{
    private const string ComparisonAnnotationKind = "RoslynMcp.GenerateEqualsHashCode.Comparison";

    /// <inheritdoc />
    public GenerateEqualsHashCodeOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(GenerateEqualsHashCodeParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.TypeName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "typeName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, PathResolver.GetCSharpFileRejectionReason(@params.SourceFile));

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        GenerateEqualsHashCodeParams @params,
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

        // Check for existing overrides
        if (typeSymbol.GetMembers("Equals").Any(m => m is IMethodSymbol method && !method.IsImplicitlyDeclared && method.Parameters.Length == 1))
            throw new RefactoringException(ErrorCodes.AlreadyHasOverride, "Type already has an Equals override.");

        var members = EqualityMemberCollector.CollectMembers(typeSymbol, @params.Fields);
        if (members.Count == 0)
            throw new RefactoringException(ErrorCodes.NoMembersToGenerate, "No fields or properties available for equality generation.");

        // Generate Equals method. Framework types are written fully qualified and simplified
        // to what the file's usings allow, so the code compiles without implicit usings too.
        var nullable = semanticModel.GetNullableContext(typeDecl.CloseBraceToken.SpanStart).AnnotationsEnabled();
        var equalsMethod = GenerateEquals(typeSymbol, members, semanticModel.Compilation, nullable);
        var hashCodeMethod = GenerateGetHashCode(members, semanticModel.Compilation);

        if (@params.Preview)
        {
            var code = equalsMethod.NormalizeWhitespace().ToFullString() + "\n\n" +
                       hashCodeMethod.NormalizeWhitespace().ToFullString();
            var pendingChanges = new List<PendingChange>
            {
                new()
                {
                    File = @params.SourceFile,
                    ChangeType = Contracts.Enums.ChangeKind.Modify,
                    Description = $"Generate Equals and GetHashCode for {@params.TypeName}",
                    BeforeSnippet = $"// Type '{@params.TypeName}' (no Equals/GetHashCode)",
                    AfterSnippet = code
                }
            };
            return RefactoringResult.PreviewResult(operationId, pendingChanges);
        }

        var newTypeDecl = typeDecl.AddMembers(
            GeneratedCodeFormatter.MarkInserted(equalsMethod),
            GeneratedCodeFormatter.MarkInserted(hashCodeMethod));

        var newRoot = root.ReplaceNode(typeDecl, newTypeDecl);
        var newDocument = document.WithSyntaxRoot(newRoot);
        var newSolution = await GeneratedCodeFormatter.FormatAsync(
            newDocument.Project.Solution, document, cancellationToken);
        newSolution = await WrapComparisonsAsync(newSolution, document, cancellationToken);
        var commitResult = await CommitChangesAsync(newSolution, cancellationToken);

        return RefactoringResult.Succeeded(operationId,
            new FileChanges { FilesModified = commitResult.FilesModified, FilesCreated = commitResult.FilesCreated, FilesDeleted = commitResult.FilesDeleted },
            new Contracts.Models.SymbolInfo { Name = @params.TypeName, FullyQualifiedName = @params.TypeName, Kind = Contracts.Enums.SymbolKind.Class },
            0, 0);
    }

    private static MethodDeclarationSyntax GenerateEquals(
        INamedTypeSymbol type,
        List<ISymbol> members,
        Compilation compilation,
        bool nullableAnnotations)
    {
        var comparerType = compilation.GetTypeByMetadataName("System.Collections.Generic.EqualityComparer`1");

        // Build equality comparisons: field1 == other.field1 && field2 == other.field2
        ExpressionSyntax? comparison = null;
        foreach (var member in members)
        {
            var left = IdentifierNames.Name(member.Name);
            var right = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName("other"),
                IdentifierNames.Name(member.Name));

            var memberType = EqualityMemberCollector.GetMemberType(member);
            ExpressionSyntax eq;

            if (!HasBuiltInEquality(memberType))
            {
                // EqualityComparer<T>.Default.Equals(field, other.field)
                var comparer = comparerType == null
                    ? SyntaxFactory.QualifiedName(
                            SyntaxFactory.ParseName("global::System.Collections.Generic"),
                            SyntaxFactory.GenericName(SyntaxFactory.Identifier("EqualityComparer"), SyntaxFactory.TypeArgumentList(
                                SyntaxFactory.SingletonSeparatedList(SyntaxGenerationHelper.CreateTypeSyntax(memberType, nullableAnnotations)))))
                        .WithAdditionalAnnotations(Simplifier.Annotation)
                    : SyntaxGenerationHelper.CreateTypeSyntax(comparerType.Construct(memberType), nullableAnnotations);
                eq = SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            comparer,
                            SyntaxFactory.IdentifierName("Default")),
                        SyntaxFactory.IdentifierName("Equals")))
                    .WithArgumentList(SyntaxFactory.ArgumentList(
                        SyntaxFactory.SeparatedList(new[]
                        {
                            SyntaxFactory.Argument(left),
                            SyntaxFactory.Argument(right)
                        })));
            }
            else
            {
                eq = SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression, left, right);
            }

            comparison = comparison == null
                ? eq
                : SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression, comparison, eq);
        }

        // public override bool Equals(object? obj)
        // {
        //     return obj is TypeName other && field1 == other.field1 && ...;
        // }
        var returnExpr = SyntaxFactory.BinaryExpression(
            SyntaxKind.LogicalAndExpression,
            SyntaxFactory.IsPatternExpression(
                SyntaxFactory.IdentifierName("obj"),
                SyntaxFactory.DeclarationPattern(
                    SyntaxGenerationHelper.CreateTypeSyntax(type, nullableAnnotations: false),
                    SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier("other")))),
            comparison!);

        TypeSyntax objectType = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));
        if (nullableAnnotations)
            objectType = SyntaxFactory.NullableType(objectType);

        return SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)),
                "Equals")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("obj")).WithType(objectType))))
            .WithBody(SyntaxFactory.Block(SyntaxFactory.ReturnStatement(returnExpr)
                .WithAdditionalAnnotations(new SyntaxAnnotation(ComparisonAnnotationKind))))
            .NormalizeWhitespace();
    }

    /// <summary>
    /// Puts each operand of the generated <c>&amp;&amp;</c> chain in <c>Equals</c> on a line of
    /// its own, aligned under the first one like Visual Studio's generator
    /// (<c>return obj is Pair&lt;T&gt; other &amp;&amp;</c>, then <c>       First == other.First &amp;&amp;</c>
    /// ...), with the document's line ending. Runs after formatting, which decides where the
    /// <c>return</c> starts. A chain with one comparison stays on one line.
    /// </summary>
    private static async Task<Solution> WrapComparisonsAsync(Solution solution, Document original, CancellationToken cancellationToken)
    {
        var document = solution.GetDocument(original.Id);
        var root = document == null ? null : await document.GetSyntaxRootAsync(cancellationToken);
        if (root?.GetAnnotatedNodes(ComparisonAnnotationKind).OfType<ReturnStatementSyntax>().FirstOrDefault()
            is not { Expression: { } chain } statement)
        {
            return solution;
        }

        // `obj is T other && (a == other.a && b == other.b ...)`: every && of the chain.
        var operators = chain
            .DescendantNodesAndSelf(node => node.IsKind(SyntaxKind.LogicalAndExpression))
            .Where(node => node.IsKind(SyntaxKind.LogicalAndExpression))
            .Select(node => ((BinaryExpressionSyntax)node).OperatorToken)
            .ToList();

        if (operators.Count < 2)
            return solution;

        var text = await document!.GetTextAsync(cancellationToken);
        var line = text.Lines.GetLineFromPosition(statement.SpanStart);
        var indentation = text.ToString(TextSpan.FromBounds(line.Start, statement.SpanStart)) +
            new string(' ', statement.ReturnKeyword.Text.Length + 1);
        var lineEnding = await LineEndings.GetAsync(original, cancellationToken);

        root = root.ReplaceTokens(operators, (token, _) => token.WithTrailingTrivia(
            SyntaxFactory.EndOfLine(lineEnding), SyntaxFactory.Whitespace(indentation)));
        return document.WithSyntaxRoot(root).Project.Solution;
    }

    /// <summary>
    /// Whether <c>==</c> compares values of <paramref name="type"/> (also lifted, for
    /// <c>int?</c>): numbers, <c>bool</c>, <c>char</c> and enums. Everything else, reference
    /// types, structs and type parameters, is compared with <c>EqualityComparer&lt;T&gt;</c>,
    /// where <c>==</c> compares references or does not compile (CS0019).
    /// </summary>
    private static bool HasBuiltInEquality(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];

        return type.TypeKind == TypeKind.Enum || type.SpecialType is
            SpecialType.System_Boolean or SpecialType.System_Char or
            SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16 or SpecialType.System_UInt16 or
            SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64 or
            SpecialType.System_Decimal or SpecialType.System_Single or SpecialType.System_Double or
            SpecialType.System_IntPtr or SpecialType.System_UIntPtr;
    }

    private static MethodDeclarationSyntax GenerateGetHashCode(List<ISymbol> members, Compilation compilation)
    {
        // System.HashCode, simplified to HashCode where `using System;` is in scope.
        TypeSyntax HashCodeType() => compilation.GetTypeByMetadataName("System.HashCode") is { } hashCode
            ? SyntaxGenerationHelper.CreateTypeSyntax(hashCode, nullableAnnotations: false)
            : SyntaxFactory.ParseTypeName("global::System.HashCode").WithAdditionalAnnotations(Simplifier.Annotation);

        // Use HashCode.Combine(field1, field2, ...)
        var arguments = members.Select(m =>
            SyntaxFactory.Argument(IdentifierNames.Name(m.Name)))
            .ToArray();

        ExpressionSyntax hashExpr;
        if (arguments.Length <= 8) // HashCode.Combine supports up to 8 args
        {
            hashExpr = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    HashCodeType(),
                    SyntaxFactory.IdentifierName("Combine")))
                .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)));
        }
        else
        {
            // For > 8 fields, use HashCode builder: var hash = new HashCode(); hash.Add(f); ... return hash.ToHashCode();
            var statements = new List<StatementSyntax>();

            // var hash = new HashCode();
            statements.Add(SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                    .WithVariables(SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator("hash")
                            .WithInitializer(SyntaxFactory.EqualsValueClause(
                                SyntaxFactory.ObjectCreationExpression(HashCodeType())
                                    .WithArgumentList(SyntaxFactory.ArgumentList())))))));

            // hash.Add(fieldN); for each member
            foreach (var member in members)
            {
                statements.Add(SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("hash"),
                            SyntaxFactory.IdentifierName("Add")))
                        .WithArgumentList(SyntaxFactory.ArgumentList(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.Argument(IdentifierNames.Name(member.Name)))))));
            }

            // return hash.ToHashCode();
            statements.Add(SyntaxFactory.ReturnStatement(
                SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("hash"),
                        SyntaxFactory.IdentifierName("ToHashCode")))));

            return SyntaxFactory.MethodDeclaration(
                    SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)),
                    "GetHashCode")
                .WithModifiers(SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                    SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
                .WithBody(SyntaxFactory.Block(statements))
                .NormalizeWhitespace();
        }

        return SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)),
                "GetHashCode")
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword)))
            .WithBody(SyntaxFactory.Block(SyntaxFactory.ReturnStatement(hashExpr)))
            .NormalizeWhitespace();
    }
}
