using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
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
        var affected = new HashSet<DocumentId>();
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
                affected.Add(declaringDocument.Id);
            }

            // Where an implementing or derived type may stop compiling, and where the member is used.
            foreach (var typeDeclaration in member.Symbol.ContainingType.DeclaringSyntaxReferences)
            {
                if (solution.GetDocument(typeDeclaration.SyntaxTree) is { } typeDocument)
                    affected.Add(typeDocument.Id);
            }

            foreach (var referenced in await SymbolFinder.FindReferencesAsync(member.Symbol, solution, cancellationToken))
                affected.UnionWith(referenced.Locations.Select(l => l.Document.Id));
        }

        var errorsBefore = await GetErrorsAsync(solution, affected, cancellationToken);
        var newSolution = await edits.ApplyAsync(solution, cancellationToken);
        var warnings = NewErrors(errorsBefore, await GetErrorsAsync(newSolution, affected, cancellationToken));

        if (@params.Preview)
        {
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

    /// <summary>
    /// The compiler errors in <paramref name="documents"/>, each once, as the compiler writes
    /// them: <c>path(line,column): error id: message</c>.
    /// </summary>
    private static async Task<List<(string Key, string Text)>> GetErrorsAsync(
        Solution solution,
        IEnumerable<DocumentId> documents,
        CancellationToken cancellationToken)
    {
        var errors = new List<(string Path, int Line, int Column, string Key, string Text)>();
        foreach (var id in documents)
        {
            if (solution.GetDocument(id) is not { } document ||
                await document.GetSemanticModelAsync(cancellationToken) is not { } model)
            {
                continue;
            }

            foreach (var diagnostic in model.GetDiagnostics(cancellationToken: cancellationToken)
                .Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                var span = diagnostic.Location.GetLineSpan();
                var (line, column) = (span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
                var message = diagnostic.GetMessage(CultureInfo.InvariantCulture);
                errors.Add((
                    span.Path,
                    line,
                    column,
                    $"{span.Path}|{line}|{diagnostic.Id}|{message}",
                    $"{span.Path}({line},{column}): error {diagnostic.Id}: {message}"));
            }
        }

        // A file shared by several projects (multi-targeting) reports its errors once per project.
        return errors
            .DistinctBy(e => e.Text)
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Line)
            .ThenBy(e => e.Column)
            .Select(e => (e.Key, e.Text))
            .ToList();
    }

    /// <summary>
    /// The errors in <paramref name="after"/> that were not there before. Errors are compared
    /// without their column, which a longer or shorter type name shifts.
    /// </summary>
    private static IReadOnlyList<string>? NewErrors(List<(string Key, string Text)> before, List<(string Key, string Text)> after)
    {
        var remaining = before.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.Count());
        var added = new List<string>();
        foreach (var (key, text) in after)
        {
            if (remaining.TryGetValue(key, out var count) && count > 0)
                remaining[key] = count - 1;
            else
                added.Add(text);
        }

        return added.Count == 0 ? null : added;
    }

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
