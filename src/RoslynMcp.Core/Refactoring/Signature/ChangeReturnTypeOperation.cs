using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Signature;

/// <summary>
/// Changes the return type of a method, or the type of a property or indexer, on the whole
/// override / implementation family (see <see cref="MemberFamily"/>). Only the declarations
/// change: return statements, bodies and callers are left alone, and the compiler errors that
/// this causes in the affected documents are reported as warnings.
/// </summary>
/// <remarks>
/// <c>void</c> is allowed in both directions for methods (a method that now returns a value
/// but has no <c>return</c> is one of the reported errors); a property or indexer cannot be
/// <c>void</c>. A <c>ref</c> / <c>ref readonly</c> return stays one.
/// </remarks>
public sealed class ChangeReturnTypeOperation : RefactoringOperationBase<ChangeReturnTypeParams>
{
    private const string TypeAnnotationKind = "RoslynMcp.ChangeReturnType.Type";

    private static readonly SymbolDisplayFormat ComparisonFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>
    /// Creates a new change return type operation.
    /// </summary>
    public ChangeReturnTypeOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ChangeReturnTypeParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.MemberName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "memberName is required.");

        if (string.IsNullOrWhiteSpace(@params.NewType))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "newType is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, PathResolver.GetCSharpFileRejectionReason(@params.SourceFile));

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (@params.Line is < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        ChangeReturnTypeParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var name = @params.MemberName.Trim();
        var isIndexer = name is "this" or "this[]";
        var declaration = SignatureSyntax.SelectDeclaration(
            root.DescendantNodes().OfType<MemberDeclarationSyntax>()
                .Where(m => m switch
                {
                    MethodDeclarationSyntax method => method.Identifier.Text == name,
                    PropertyDeclarationSyntax property => property.Identifier.Text == name,
                    IndexerDeclarationSyntax => isIndexer,
                    _ => false
                })
                .ToList(),
            "member",
            name,
            @params.Line,
            Identifier,
            ErrorCodes.MemberNotFound);

        var symbol = semanticModel.GetDeclaredSymbol(declaration, cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve the member.");

        var position = declaration is MethodDeclarationSyntax methodDeclaration
            ? methodDeclaration.ParameterList.OpenParenToken.Span.End
            : TypeOf(declaration)!.SpanStart;
        var newType = SignatureSyntax.BindType(semanticModel, position, @params.NewType) ?? throw new RefactoringException(
            ErrorCodes.InvalidReturnType,
            $"'{@params.NewType}' is not a type that resolves where '{symbol.ToDisplayString()}' is declared.");

        var (currentType, returnsByRef) = symbol switch
        {
            IMethodSymbol method => (method.ReturnType, method.ReturnsByRef || method.ReturnsByRefReadonly),
            IPropertySymbol property => (property.Type, property.ReturnsByRef || property.ReturnsByRefReadonly),
            _ => throw new RefactoringException(ErrorCodes.InvalidSymbolKind, $"'{symbol.ToDisplayString()}' is not a method or property.")
        };

        if (newType.SpecialType == SpecialType.System_Void && (symbol is IPropertySymbol || returnsByRef))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidReturnType,
                $"'{symbol.ToDisplayString()}' cannot be void: it is a {(symbol is IPropertySymbol ? "property" : "ref return")}.");
        }

        if (currentType.ToDisplayString(ComparisonFormat) == newType.ToDisplayString(ComparisonFormat))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidReturnType,
                $"'{symbol.ToDisplayString()}' already has type '{currentType.ToDisplayString()}'.");
        }

        var solution = Context.Solution;
        var family = await MemberFamily.FindAsync(symbol, semanticModel.Compilation, solution, cancellationToken);
        var edits = new SyntaxEdits();
        var declarations = new List<(Document Document, SyntaxNode Declaration, TypeSyntax OldType, SyntaxAnnotation Annotation)>();

        foreach (var member in family.Members)
        {
            foreach (var reference in member.Declarations)
            {
                var node = await reference.GetSyntaxAsync(cancellationToken);
                var declaringDocument = solution.GetDocument(reference.SyntaxTree)!;
                var oldType = TypeOf(node) ?? throw new RefactoringException(
                    ErrorCodes.InvalidSymbolKind,
                    $"'{member.Symbol.ToDisplayString()}' is declared by a record parameter; change that parameter's type instead.");
                if (oldType is RefTypeSyntax refType)
                    oldType = refType.Type;

                var model = await declaringDocument.GetSemanticModelAsync(cancellationToken)
                    ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not analyze file.");
                var annotation = new SyntaxAnnotation(TypeAnnotationKind);
                var written = SignatureSyntax.WriteType(newType, member, model, node.SpanStart)
                    .WithTriviaFrom(oldType)
                    .WithAdditionalAnnotations(annotation);

                edits.Add(declaringDocument.Id, oldType, _ => written);
                declarations.Add((declaringDocument, node, oldType, annotation));
            }
        }

        var affected = family.AffectedDocuments(solution, await family.FindReferencesAsync(solution, cancellationToken));
        var newSolution = await edits.ApplyAsync(solution, cancellationToken);
        var warnings = await IntroducedErrors.FindAsync(solution, newSolution, affected, cancellationToken);

        if (@params.Preview)
        {
            await PreviewCommitAsync(newSolution, cancellationToken);
            return new RefactoringResult
            {
                Success = true,
                OperationId = operationId,
                Preview = true,
                PendingChanges = await CreatePendingChangesAsync(newSolution, declarations, cancellationToken),
                Warnings = warnings
            };
        }

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
                Name = symbol.Name,
                FullyQualifiedName = symbol.ToDisplayString(),
                Kind = symbol is IMethodSymbol ? Contracts.Enums.SymbolKind.Method : Contracts.Enums.SymbolKind.Property
            },
            Warnings = warnings
        };
    }

    private static SyntaxToken Identifier(MemberDeclarationSyntax declaration) => declaration switch
    {
        MethodDeclarationSyntax method => method.Identifier,
        PropertyDeclarationSyntax property => property.Identifier,
        IndexerDeclarationSyntax indexer => indexer.ThisKeyword,
        _ => declaration.GetFirstToken()
    };

    private static TypeSyntax? TypeOf(SyntaxNode declaration) => declaration switch
    {
        MethodDeclarationSyntax method => method.ReturnType,
        BasePropertyDeclarationSyntax property => property.Type,
        _ => null
    };

    private static async Task<List<PendingChange>> CreatePendingChangesAsync(
        Solution newSolution,
        List<(Document Document, SyntaxNode Declaration, TypeSyntax OldType, SyntaxAnnotation Annotation)> declarations,
        CancellationToken cancellationToken)
    {
        var changes = new List<PendingChange>();
        foreach (var (document, declaration, oldType, annotation) in declarations)
        {
            var newRoot = await newSolution.GetDocument(document.Id)!.GetSyntaxRootAsync(cancellationToken);
            var newType = newRoot?.GetAnnotatedNodes(annotation).FirstOrDefault();
            var name = Identifier((MemberDeclarationSyntax)declaration).Text;
            var line = oldType.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

            changes.Add(new PendingChange
            {
                File = document.FilePath ?? document.Name,
                ChangeType = ChangeKind.Modify,
                Description = $"Change the type of '{name}' from '{oldType}' to '{newType}'",
                BeforeSnippet = $"{oldType} {name}",
                AfterSnippet = $"{newType} {name}",
                StartLine = line,
                EndLine = line
            });
        }

        return changes;
    }
}
