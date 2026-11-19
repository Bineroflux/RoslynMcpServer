using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Convert;

/// <summary>
/// Converts properties between auto-property and full-property forms.
/// </summary>
public sealed class ConvertPropertyOperation : RefactoringOperationBase<ConvertPropertyParams>
{
    /// <inheritdoc />
    public ConvertPropertyOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ConvertPropertyParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.Direction))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "direction is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, PathResolver.GetCSharpFileRejectionReason(@params.SourceFile));

        if (!@params.Line.HasValue && string.IsNullOrWhiteSpace(@params.PropertyName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "Either propertyName or line must be provided.");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (!Enum.TryParse<ConversionDirection>(@params.Direction, ignoreCase: true, out var dir) ||
            (dir != ConversionDirection.ToAutoProperty && dir != ConversionDirection.ToFullProperty))
        {
            throw new RefactoringException(ErrorCodes.CannotConvert,
                "direction must be 'ToAutoProperty' or 'ToFullProperty'.");
        }

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ConvertPropertyParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var direction = Enum.Parse<ConversionDirection>(@params.Direction, ignoreCase: true);

        // Find the property
        var properties = root.DescendantNodes().OfType<PropertyDeclarationSyntax>();

        if (!string.IsNullOrWhiteSpace(@params.PropertyName))
            properties = properties.Where(p => p.Identifier.Text == @params.PropertyName);

        if (@params.Line.HasValue)
            properties = properties.Where(p =>
                p.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == @params.Line.Value);

        var property = properties.FirstOrDefault();
        if (property == null)
            throw new RefactoringException(ErrorCodes.SymbolNotFound,
                $"Property '{@params.PropertyName ?? $"at line {@params.Line}"}' not found.");

        SyntaxNode newRoot;
        string beforeSnippet;
        string afterSnippet;

        if (direction == ConversionDirection.ToFullProperty)
        {
            (newRoot, beforeSnippet, afterSnippet) = ConvertToFullProperty(root, property);
        }
        else
        {
            (newRoot, beforeSnippet, afterSnippet) = ConvertToAutoProperty(root, property);
        }

        if (@params.Preview)
        {
            var pendingChanges = new List<PendingChange>
            {
                new()
                {
                    File = @params.SourceFile,
                    ChangeType = ChangeKind.Modify,
                    Description = $"Convert property to {direction}",
                    BeforeSnippet = beforeSnippet,
                    AfterSnippet = afterSnippet
                }
            };
            return RefactoringResult.PreviewResult(operationId, pendingChanges);
        }

        // The generated property and field are laid out like their surroundings, with the
        // document's indentation and line ending.
        var newDocument = document.WithSyntaxRoot(newRoot);
        var newSolution = await GeneratedCodeFormatter.FormatAsync(newDocument.Project.Solution, document, cancellationToken);
        var commitResult = await CommitChangesAsync(newSolution, cancellationToken);

        return RefactoringResult.Succeeded(operationId,
            new FileChanges { FilesModified = commitResult.FilesModified, FilesCreated = commitResult.FilesCreated, FilesDeleted = commitResult.FilesDeleted },
            new Contracts.Models.SymbolInfo { Name = property.Identifier.Text, FullyQualifiedName = property.Identifier.Text, Kind = Contracts.Enums.SymbolKind.Property },
            0, 0);
    }

    private static (SyntaxNode newRoot, string before, string after) ConvertToFullProperty(
        SyntaxNode root, PropertyDeclarationSyntax property)
    {
        // Must be an auto-property
        if (property.AccessorList == null)
            throw new RefactoringException(ErrorCodes.CannotConvert, "Property does not have accessors.");

        var isAutoProperty = property.AccessorList.Accessors.All(a => a.Body == null && a.ExpressionBody == null);
        if (!isAutoProperty)
            throw new RefactoringException(ErrorCodes.CannotConvert, "Property is already a full property.");

        // Generate backing field name
        var fieldName = "_" + char.ToLower(property.Identifier.Text[0]) + property.Identifier.Text.Substring(1);

        // Create backing field; it takes over the property's initializer, as written.
        var declarator = SyntaxFactory.VariableDeclarator(fieldName);
        if (property.Initializer?.Value is { } initialValue)
        {
            declarator = declarator.WithInitializer(SyntaxFactory.EqualsValueClause(
                GeneratedCodeFormatter.KeepLayout(initialValue.WithoutTrivia(), initialValue.WithoutTrivia(), initialValue)));
        }

        var backingField = SyntaxFactory.FieldDeclaration(
            SyntaxFactory.VariableDeclaration(property.Type.WithoutTrivia())
                .WithVariables(SyntaxFactory.SingletonSeparatedList(declarator)))
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)))
            .NormalizeWhitespace();

        // Create full property with getter and setter
        var accessors = new List<AccessorDeclarationSyntax>();

        var hasGetter = property.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        var hasSetter = property.AccessorList.Accessors.Any(a =>
            a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration));

        if (hasGetter)
        {
            var getter = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithBody(SyntaxFactory.Block(
                    SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName(fieldName))));
            accessors.Add(getter);
        }

        if (hasSetter)
        {
            var originalSetter = property.AccessorList.Accessors.First(a =>
                a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration));

            var setterKind = originalSetter.IsKind(SyntaxKind.InitAccessorDeclaration)
                ? SyntaxKind.InitAccessorDeclaration
                : SyntaxKind.SetAccessorDeclaration;

            var setter = SyntaxFactory.AccessorDeclaration(setterKind)
                .WithModifiers(originalSetter.Modifiers)
                .WithBody(SyntaxFactory.Block(
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.IdentifierName(fieldName),
                            SyntaxFactory.IdentifierName("value")))));
            accessors.Add(setter);
        }

        // Only the accessors are generated (and laid out by the formatter); the property's
        // attributes, modifiers, type and name stay as written.
        var generated = SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)).NormalizeWhitespace();
        var newProperty = WithoutTrailingWhitespace(property, property.AccessorList.OpenBraceToken.GetPreviousToken())
            .WithAccessorList(GeneratedCodeFormatter.Mark(generated
                .WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)
                .WithTrailingTrivia(property.GetTrailingTrivia())))
            .WithInitializer(null)
            .WithSemicolonToken(default);

        var before = property.NormalizeWhitespace().ToFullString();
        var after = backingField.NormalizeWhitespace().ToFullString() + "\n" + newProperty.NormalizeWhitespace().ToFullString();

        // Insert backing field before the property
        var parent = property.Parent;
        if (parent is TypeDeclarationSyntax typeDecl)
        {
            var propertyIndex = typeDecl.Members.IndexOf(property);
            var newMembers = typeDecl.Members
                .Insert(propertyIndex, GeneratedCodeFormatter.MarkInserted(backingField));
            var newTypeDecl = typeDecl.WithMembers(newMembers);

            // Now replace the original property (which shifted by 1) with the new full property
            var shiftedProperty = newTypeDecl.Members[propertyIndex + 1] as PropertyDeclarationSyntax;
            if (shiftedProperty != null)
            {
                newTypeDecl = (TypeDeclarationSyntax)newTypeDecl.ReplaceNode(shiftedProperty, newProperty);
            }

            return (root.ReplaceNode(typeDecl, newTypeDecl), before, after);
        }

        // Fallback: just replace the property
        return (root.ReplaceNode(property, newProperty), before, after);
    }

    private static (SyntaxNode newRoot, string before, string after) ConvertToAutoProperty(
        SyntaxNode root, PropertyDeclarationSyntax property)
    {
        if (property.AccessorList == null)
            throw new RefactoringException(ErrorCodes.CannotConvert, "Property does not have accessors.");

        var isAutoProperty = property.AccessorList.Accessors.All(a => a.Body == null && a.ExpressionBody == null);
        if (isAutoProperty)
            throw new RefactoringException(ErrorCodes.CannotConvert, "Property is already an auto-property.");

        // Create auto-property accessors
        var accessors = new List<AccessorDeclarationSyntax>();

        foreach (var accessor in property.AccessorList.Accessors)
        {
            var autoAccessor = SyntaxFactory.AccessorDeclaration(accessor.Kind())
                .WithModifiers(accessor.Modifiers)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
            accessors.Add(autoAccessor);
        }

        // `{ get; set; }` on the line of the name; the rest of the property stays as written.
        var space = SyntaxFactory.Space;
        var autoList = SyntaxFactory.AccessorList(
            SyntaxFactory.Token(SyntaxKind.OpenBraceToken).WithLeadingTrivia(space).WithTrailingTrivia(space),
            SyntaxFactory.List(accessors.Select(a => a
                .WithModifiers(SyntaxFactory.TokenList(a.Modifiers.Select(m => m.WithoutTrivia().WithTrailingTrivia(space))))
                .WithKeyword(a.Keyword.WithoutTrivia())
                .WithSemicolonToken(a.SemicolonToken.WithoutTrivia().WithTrailingTrivia(space)))),
            SyntaxFactory.Token(SyntaxKind.CloseBraceToken).WithTrailingTrivia(property.AccessorList.CloseBraceToken.TrailingTrivia));
        var newProperty = WithoutTrailingWhitespace(property, property.AccessorList.OpenBraceToken.GetPreviousToken())
            .WithAccessorList(autoList)
            .WithExpressionBody(null);

        var before = property.NormalizeWhitespace().ToFullString();
        var after = newProperty.NormalizeWhitespace().ToFullString();

        return (root.ReplaceNode(property, newProperty), before, after);
    }

    /// <summary>The property with the whitespace (and line break) after <paramref name="token"/> removed.</summary>
    private static PropertyDeclarationSyntax WithoutTrailingWhitespace(PropertyDeclarationSyntax property, SyntaxToken token) =>
        property.ReplaceToken(token, token.WithTrailingTrivia(
            token.TrailingTrivia.Where(t => !t.IsKind(SyntaxKind.WhitespaceTrivia) && !t.IsKind(SyntaxKind.EndOfLineTrivia))));
}
