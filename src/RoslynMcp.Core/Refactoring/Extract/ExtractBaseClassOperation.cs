using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Delete;
using RoslynMcp.Core.Refactoring.Hierarchy;
using RoslynMcp.Core.Refactoring.Signature;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Extract;

/// <summary>
/// Extracts members to a new base class.
/// </summary>
/// <remarks>
/// Members are picked by name like pull_members_up picks them (every overload of a method,
/// field-like events, indexers as <c>this</c>, one variable of <c>int a, b;</c>); a private member
/// becomes protected and a <c>private protected</c> one stays. A member that uses members of the
/// type staying behind is refused with the members it uses. The members leave the type's text with
/// their comments, as in safe_delete; compiler errors the move leaves (e.g. a readonly field still
/// assigned in the type's constructor) are returned as warnings.
/// </remarks>
public sealed class ExtractBaseClassOperation : RefactoringOperationBase<ExtractBaseClassParams>
{
    private const string RemoveAnnotationKind = "RoslynMcp.ExtractBaseClass.Remove";
    private const string TypeAnnotationKind = "RoslynMcp.ExtractBaseClass.Type";

    /// <summary>
    /// Creates a new extract base class operation.
    /// </summary>
    public ExtractBaseClassOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ExtractBaseClassParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.TypeName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "typeName is required.");

        if (string.IsNullOrWhiteSpace(@params.BaseClassName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "baseClassName is required.");

        if (@params.Members == null || @params.Members.Count == 0)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "members is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, PathResolver.GetCSharpFileRejectionReason(@params.SourceFile));

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (!IsValidIdentifier(@params.BaseClassName))
            throw new RefactoringException(ErrorCodes.InvalidSymbolName, $"Invalid base class name: {@params.BaseClassName}");

        if (@params.TargetFile != null)
        {
            if (!PathResolver.IsAbsolutePath(@params.TargetFile))
                throw new RefactoringException(ErrorCodes.InvalidTargetPath, "targetFile must be an absolute path.");

            if (!PathResolver.IsValidCSharpFilePath(@params.TargetFile))
                throw new RefactoringException(ErrorCodes.InvalidTargetPath, PathResolver.GetCSharpFileRejectionReason(@params.TargetFile, "targetFile"));
        }
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ExtractBaseClassParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (root == null || semanticModel == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");
        }

        // Find the type declaration
        var typeDeclaration = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.Text == @params.TypeName);

        if (typeDeclaration == null)
        {
            throw new RefactoringException(
                ErrorCodes.TypeNotFound,
                $"Class '{@params.TypeName}' not found in file.");
        }

        // Get the type symbol
        var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) as INamedTypeSymbol;
        if (typeSymbol == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve type symbol.");
        }

        // Check if type already has a base class other than Object
        if (typeSymbol.BaseType != null &&
            typeSymbol.BaseType.SpecialType != SpecialType.System_Object)
        {
            throw new RefactoringException(
                ErrorCodes.TypeAlreadyHasBase,
                $"Type '{@params.TypeName}' already has base class '{typeSymbol.BaseType.Name}'.");
        }

        // Check if base class name already exists
        var existingType = await TypeResolver.FindTypeByNameAsync(
            $"{typeSymbol.ContainingNamespace}.{@params.BaseClassName}",
            cancellationToken);

        if (existingType != null)
        {
            throw new RefactoringException(
                ErrorCodes.NameCollision,
                $"Type '{@params.BaseClassName}' already exists.");
        }

        // Find members to extract; what they use has to come along.
        var selected = MovedMembers.Select(typeSymbol, @params.Members, cancellationToken);
        if (selected.FirstOrDefault(s => !typeDeclaration.Span.Contains(s.Declaration.Span) || s.Declaration.SyntaxTree != typeDeclaration.SyntaxTree) is { } elsewhere)
        {
            throw new RefactoringException(
                ErrorCodes.MemberNotMoveable,
                $"'{elsewhere.Symbols[0].ToDisplayString()}' is declared in another part of '{@params.TypeName}'.");
        }

        var moved = selected.SelectMany(s => s.Symbols).ToList();
        var dependencies = MovedMembers.Dependencies(
            selected.SelectMany(s => s.IsWhole ? [s.Declaration] : s.Variables.Cast<SyntaxNode>()), typeSymbol, moved, semanticModel, cancellationToken);
        if (dependencies.Count > 0)
        {
            throw new RefactoringException(
                ErrorCodes.MemberDependsOnDerived,
                $"The members use {string.Join(", ", dependencies.Select(d => $"'{d.ToDisplayString()}'"))}, which would stay in '{@params.TypeName}'. Extract them too.",
                new Dictionary<string, object> { ["dependencies"] = dependencies.Select(d => d.Name).Distinct().ToList() });
        }

        var membersToExtract = selected
            .Select(s => s.Chosen(s.Declaration))
            .Select((member, i) => member.WithModifiers(
                MovedMembers.MovedModifiers(member.Modifiers, selected[i].Symbols[0], @params.BaseClassName, sameAssembly: true).Modifiers))
            .ToList();

        // Generate base class
        var baseClass = GenerateBaseClass(
            @params.BaseClassName,
            membersToExtract,
            @params.MakeAbstract);

        // Get namespace
        var namespaceName = typeSymbol.ContainingNamespace?.ToDisplayString();

        // If preview mode, return without applying
        if (@params.Preview)
        {
            return CreatePreviewResult(operationId, @params, moved, baseClass, namespaceName);
        }

        // Mark what leaves the type; it is removed from the text once the rest is done.
        var originalSolution = document.Project.Solution;
        var markedRoot = root.ReplaceNodes(
            selected.SelectMany(s => s.Removed).Append(typeDeclaration),
            (original, rewritten) => rewritten.WithAdditionalAnnotations(
                new SyntaxAnnotation(original == typeDeclaration ? TypeAnnotationKind : RemoveAnnotationKind)));
        document = document.WithSyntaxRoot(markedRoot);
        root = (await document.GetSyntaxRootAsync(cancellationToken))!;
        typeDeclaration = (ClassDeclarationSyntax)root.GetAnnotatedNodes(TypeAnnotationKind).Single();

        // Apply changes
        Solution newSolution;
        if (@params.TargetFile != null && !PathResolver.PathsEqual(@params.TargetFile, @params.SourceFile))
        {
            // Create new file with base class
            newSolution = await CreateBaseClassInNewFileAsync(
                document.Project.Solution,
                document.Project,
                @params.TargetFile,
                baseClass,
                namespaceName,
                root,
                cancellationToken);
        }
        else
        {
            // Add base class to same file
            newSolution = AddBaseClassToSameFile(
                document,
                root,
                typeDeclaration,
                baseClass);
        }

        // Update derived class: remove extracted members and add base class
        var updatedDoc = newSolution.GetDocument(document.Id);
        if (updatedDoc != null)
        {
            var updatedRoot = await updatedDoc.GetSyntaxRootAsync(cancellationToken);
            if (updatedRoot != null)
            {
                var updatedTypeDecl = (ClassDeclarationSyntax)updatedRoot.GetAnnotatedNodes(TypeAnnotationKind).Single();

                // Add base class to type, before any interfaces. SyntaxGenerator moves the
                // trivia that ended the type's header (e.g. the line break before `{`) behind
                // the new base list, which is then laid out by the formatter.
                var newTypeDecl = (ClassDeclarationSyntax)SyntaxGenerator.GetGenerator(updatedDoc)
                    .AddBaseType(updatedTypeDecl, SyntaxFactory.ParseTypeName(@params.BaseClassName));
                newTypeDecl = newTypeDecl.WithBaseList(GeneratedCodeFormatter.Mark(newTypeDecl.BaseList!));

                updatedRoot = updatedRoot.ReplaceNode(updatedTypeDecl, newTypeDecl);
                newSolution = updatedDoc.WithSyntaxRoot(updatedRoot).Project.Solution;
            }
        }

        newSolution = await GeneratedCodeFormatter.FormatAsync(newSolution, document, cancellationToken);

        // Remove the extracted declarations (or variables) from the text, with their comments.
        var formatted = newSolution.GetDocument(document.Id)!;
        var formattedRoot = await formatted.GetSyntaxRootAsync(cancellationToken);
        var formattedText = await formatted.GetTextAsync(cancellationToken);
        newSolution = newSolution.WithDocumentText(document.Id, DeclarationRemoval.Remove(
            formattedText, DeclarationRemoval.Spans(formattedText, formattedRoot!.GetAnnotatedNodes(RemoveAnnotationKind))));

        var warnings = await FindWarningsAsync(originalSolution, newSolution, document.Id, @params.TargetFile, moved, cancellationToken);

        // Commit changes
        var commitResult = await CommitChangesAsync(newSolution, cancellationToken);

        return new RefactoringResult
        {
            Success = true,
            OperationId = operationId,
            Changes = new FileChanges
            {
                FilesModified = commitResult.FilesModified,
                FilesCreated = commitResult.FilesCreated,
                FilesDeleted = commitResult.FilesDeleted
            },
            Symbol = new Contracts.Models.SymbolInfo
            {
                Name = @params.BaseClassName,
                FullyQualifiedName = string.IsNullOrEmpty(namespaceName)
                    ? @params.BaseClassName
                    : $"{namespaceName}.{@params.BaseClassName}",
                Kind = Contracts.Enums.SymbolKind.Class
            },
            Warnings = warnings
        };
    }

    /// <summary>
    /// The compiler errors the extraction introduces in the type's file, the new base class's file
    /// and the files referencing the moved members.
    /// </summary>
    private static async Task<IReadOnlyList<string>?> FindWarningsAsync(
        Solution before,
        Solution after,
        DocumentId typeDocument,
        string? targetFile,
        List<ISymbol> moved,
        CancellationToken cancellationToken)
    {
        var documents = new HashSet<DocumentId> { typeDocument };
        if (targetFile != null)
            documents.UnionWith(after.GetDocumentIdsWithFilePath(targetFile));
        foreach (var symbol in moved)
        {
            foreach (var reference in await SymbolFinder.FindReferencesAsync(symbol, before, cancellationToken))
                documents.UnionWith(reference.Locations.Select(l => l.Document.Id));
        }

        return await IntroducedErrors.FindAsync(before, after, documents, cancellationToken);
    }

    private static ClassDeclarationSyntax GenerateBaseClass(
        string className,
        List<MemberDeclarationSyntax> members,
        bool makeAbstract)
    {
        var modifiers = new List<SyntaxToken> { SyntaxFactory.Token(SyntaxKind.PublicKeyword) };
        if (makeAbstract)
        {
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.AbstractKeyword));
        }

        return SeparateMembers(SyntaxFactory.ClassDeclaration(className)
            .WithModifiers(SyntaxFactory.TokenList(modifiers))
            .WithMembers(SyntaxFactory.List(members))
            .NormalizeWhitespace(), "\r\n");
    }

    /// <summary>
    /// Puts a blank line between the members of a class laid out by <c>NormalizeWhitespace</c>,
    /// which writes them on consecutive lines.
    /// </summary>
    private static ClassDeclarationSyntax SeparateMembers(ClassDeclarationSyntax type, string lineEnding) =>
        type.WithMembers(SyntaxFactory.List(type.Members.Select((member, i) => i == 0
            ? member
            : member.WithLeadingTrivia(member.GetLeadingTrivia().Insert(0, SyntaxFactory.EndOfLine(lineEnding))))));

    private static async Task<Solution> CreateBaseClassInNewFileAsync(
        Solution solution,
        Project project,
        string targetFile,
        ClassDeclarationSyntax baseClass,
        string? namespaceName,
        SyntaxNode sourceRoot,
        CancellationToken cancellationToken)
    {
        // Build compilation unit with usings from source
        var usings = sourceRoot.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .ToList();

        MemberDeclarationSyntax wrappedClass;
        if (!string.IsNullOrEmpty(namespaceName))
        {
            wrappedClass = SyntaxFactory.FileScopedNamespaceDeclaration(
                    SyntaxFactory.ParseName(namespaceName))
                .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(baseClass));
        }
        else
        {
            wrappedClass = baseClass;
        }

        // Create the new document empty first: its line ending can only come from its
        // .editorconfig (or the platform default) since there is no text to go by.
        var newDoc = project.AddDocument(
            Path.GetFileName(targetFile),
            SourceText.From(string.Empty),
            filePath: targetFile);
        var lineEnding = await LineEndings.GetAsync(newDoc, cancellationToken);

        var compilationUnit = SyntaxFactory.CompilationUnit()
            .WithUsings(SyntaxFactory.List(usings))
            .WithMembers(SyntaxFactory.SingletonList(wrappedClass))
            .NormalizeWhitespace(eol: lineEnding);
        var normalizedClass = compilationUnit.DescendantNodes().OfType<ClassDeclarationSyntax>().First();
        compilationUnit = compilationUnit.ReplaceNode(normalizedClass, SeparateMembers(normalizedClass, lineEnding));

        return newDoc.WithSyntaxRoot(compilationUnit).Project.Solution;
    }

    private static Solution AddBaseClassToSameFile(
        Document document,
        SyntaxNode root,
        ClassDeclarationSyntax derivedClass,
        ClassDeclarationSyntax baseClass)
    {
        // Insert base class before derived class
        var newBaseClass = GeneratedCodeFormatter.MarkInserted(baseClass);

        var newRoot = root.InsertNodesBefore(derivedClass, new[] { newBaseClass });
        var newDoc = document.WithSyntaxRoot(newRoot);

        return newDoc.Project.Solution;
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        ExtractBaseClassParams @params,
        List<ISymbol> members,
        ClassDeclarationSyntax baseClass,
        string? namespaceName)
    {
        var memberNames = string.Join(", ", members.Select(m => m is IPropertySymbol { IsIndexer: true } ? "this" : m.Name).Distinct());
        var baseClassCode = baseClass.NormalizeWhitespace().ToFullString();

        var targetFile = @params.TargetFile ?? @params.SourceFile;
        var isNewFile = @params.TargetFile != null && !PathResolver.PathsEqual(@params.TargetFile, @params.SourceFile);

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = targetFile,
                ChangeType = isNewFile ? ChangeKind.Create : ChangeKind.Modify,
                Description = $"Extract base class {@params.BaseClassName} with members: {memberNames}",
                BeforeSnippet = isNewFile ? "// (new file)" : $"// Before class '{@params.TypeName}'",
                AfterSnippet = baseClassCode
            },
            new()
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Update {@params.TypeName} to inherit from {@params.BaseClassName}",
                BeforeSnippet = $"class {@params.TypeName}",
                AfterSnippet = $"class {@params.TypeName} : {@params.BaseClassName}"
            }
        };

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }

    private static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (!char.IsLetter(name[0]) && name[0] != '_') return false;
        return name.All(c => char.IsLetterOrDigit(c) || c == '_');
    }
}
