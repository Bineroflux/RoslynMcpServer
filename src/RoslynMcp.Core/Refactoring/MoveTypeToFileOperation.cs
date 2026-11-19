using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring;

/// <summary>
/// Moves a type declaration to a different file.
/// Handles file creation, using directive updates, and reference preservation.
/// </summary>
public sealed class MoveTypeToFileOperation : RefactoringOperationBase<MoveTypeToFileParams>
{
    /// <summary>
    /// Creates a new move type to file operation.
    /// </summary>
    /// <param name="context">Workspace context.</param>
    public MoveTypeToFileOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        MoveTypeToFileParams @params,
        CancellationToken cancellationToken)
    {
        // Resolve symbol
        var resolution = await TypeResolver.FindTypeInFileAsync(
            @params.SourceFile,
            @params.SymbolName,
            @params.Line,
            cancellationToken);

        // Validate target
        await ValidateTargetAsync(@params, resolution, cancellationToken);

        // Find all references
        var references = await ReferenceTracker.FindAllReferencesAsync(
            resolution.Symbol,
            cancellationToken);

        // Compute changes
        var original = Context.Solution;
        var (newSolution, changeInfo) = await ComputeChangesAsync(
            @params,
            resolution,
            references,
            cancellationToken);

        // Usings only the moved type used go from the file it left; those unused before stay.
        // Every target framework's document of the edited files gets their new text first.
        newSolution = await LinkedDocuments.SyncAsync(
            original, newSolution, original.GetDocumentIdsWithFilePath(@params.TargetFile).Append(resolution.Document.Id), cancellationToken);
        var (cleaned, usingsRemoved) = await NewlyReportedUsings.RemoveLeftUnusedAsync(original, newSolution, [resolution.Document.Id], cancellationToken);
        newSolution = cleaned;

        // If preview mode, return without applying
        if (@params.Preview)
        {
            await PreviewCommitAsync(newSolution, resolution.Document.FilePath, cancellationToken);
            return CreatePreviewResult(operationId, @params, changeInfo, resolution, usingsRemoved);
        }

        // Commit changes; a new target file takes the encoding of the file the type comes from,
        // unless its .editorconfig names one.
        var commitResult = await CommitChangesAsync(newSolution, resolution.Document.FilePath, cancellationToken);

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
            Symbol = CreateSymbolInfo(resolution, @params.TargetFile),
            ReferencesUpdated = references.TotalReferenceCount,
            UsingDirectivesRemoved = usingsRemoved.Count
        };
    }

    /// <inheritdoc />
    protected override void ValidateParams(MoveTypeToFileParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.SymbolName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "symbolName is required.");

        if (string.IsNullOrWhiteSpace(@params.TargetFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "targetFile is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsAbsolutePath(@params.TargetFile))
            throw new RefactoringException(ErrorCodes.InvalidTargetPath, "targetFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, PathResolver.GetCSharpFileRejectionReason(@params.SourceFile));

        if (!PathResolver.IsValidCSharpFilePath(@params.TargetFile))
            throw new RefactoringException(ErrorCodes.InvalidTargetPath, PathResolver.GetCSharpFileRejectionReason(@params.TargetFile, "targetFile"));

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        // Check source != target
        if (PathResolver.PathsEqual(@params.SourceFile, @params.TargetFile))
            throw new RefactoringException(ErrorCodes.SameLocation, "Source and target files are the same.");
    }

    private async Task ValidateTargetAsync(
        MoveTypeToFileParams @params,
        SymbolResolutionResult resolution,
        CancellationToken cancellationToken)
    {
        var targetDoc = Context.GetDocumentByPath(@params.TargetFile);

        if (targetDoc == null && !@params.CreateTargetFile)
        {
            throw new RefactoringException(
                ErrorCodes.SourceFileNotFound,
                $"Target file does not exist: {@params.TargetFile}. Set createTargetFile=true to create it.");
        }

        // If target exists, check for name collision
        if (targetDoc != null)
        {
            var targetRoot = await targetDoc.GetSyntaxRootAsync(cancellationToken);
            if (targetRoot != null)
            {
                var existingTypes = targetRoot.DescendantNodes()
                    .OfType<TypeDeclarationSyntax>()
                    .Where(t => t.Identifier.Text == resolution.Symbol.Name)
                    .ToList();

                if (existingTypes.Count > 0)
                {
                    throw new RefactoringException(
                        ErrorCodes.NameCollision,
                        $"Target file already contains a type named '{resolution.Symbol.Name}'.",
                        suggestions: ["Rename the type before moving", "Choose a different target file"]);
                }
            }
        }
    }

    private async Task<(Solution, ChangeInfo)> ComputeChangesAsync(
        MoveTypeToFileParams @params,
        SymbolResolutionResult resolution,
        ReferenceSearchResult references,
        CancellationToken cancellationToken)
    {
        var solution = Context.Solution;
        var sourceDoc = resolution.Document;
        var sourceRoot = (await sourceDoc.GetSyntaxRootAsync(cancellationToken))!;

        // Extract the type declaration with its trivia
        var typeNode = resolution.Declaration;

        // Get the namespace
        var namespaceDecl = typeNode.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        var namespaceName = namespaceDecl?.Name.ToString() ?? resolution.Symbol.ContainingNamespace.ToDisplayString();

        // Check if target document exists
        var targetDoc = Context.GetDocumentByPath(@params.TargetFile);

        // The type takes the line ending of the file it goes into (its string literals excepted).
        int keptLiterals;
        string lineEnding;
        DocumentId? newDocumentId = null;
        if (targetDoc != null)
        {
            // Add to the existing file, with the usings the type needs that the file lacks.
            lineEnding = await LineEndings.GetAsync(targetDoc, cancellationToken);
            var targetRoot = (CompilationUnitSyntax)(await targetDoc.GetSyntaxRootAsync(cancellationToken))!;
            var targetModel = (await targetDoc.GetSemanticModelAsync(cancellationToken))!;
            var sourceModel = (await sourceDoc.GetSemanticModelAsync(cancellationToken))!;
            var position = TargetContainer(targetRoot, namespaceName) switch
            {
                NamespaceDeclarationSyntax block => block.OpenBraceToken.Span.End,
                FileScopedNamespaceDeclarationSyntax fileScoped => fileScoped.SemicolonToken.Span.End,
                _ => targetRoot.Members.FirstOrDefault()?.SpanStart ?? targetRoot.EndOfFileToken.SpanStart
            };
            var qualified = new Dictionary<SyntaxNode, SyntaxNode>();
            var needed = MovedCodeUsings.Needed(
                [typeNode],
                resolution.Symbol,
                sourceMoves: true,
                sourceModel,
                targetModel,
                position,
                targetModel.GetNullableContext(position).AnnotationsEnabled(),
                qualified,
                cancellationToken);
            var moved = qualified.Count == 0 ? typeNode : typeNode.ReplaceNodes(qualified.Keys, (original, _) => qualified[original]);
            var newTargetRoot = AddTypeToExistingFile(
                targetRoot, LineEndings.Convert(moved, lineEnding, out keptLiterals), GeneratedCodeFormatter.IndentationOf(typeNode), namespaceName, lineEnding);
            solution = solution.WithDocumentSyntaxRoot(targetDoc.Id, UsingDirectives.Add(newTargetRoot, needed, lineEnding));
            if (qualified.Count > 0)
                solution = (await Simplifier.ReduceAsync(solution.GetDocument(targetDoc.Id)!, Simplifier.Annotation, cancellationToken: cancellationToken)).Project.Solution;
        }
        else
        {
            // Create new document, with the end_of_line of its .editorconfig, else the line
            // ending of the file the type comes from. It gets the source's usings; those it doesn't
            // need go once the type has left the source.
            var project = sourceDoc.Project;
            var probe = project.AddDocument(
                Path.GetFileNameWithoutExtension(@params.TargetFile), SourceText.From(string.Empty), filePath: @params.TargetFile);
            lineEnding = await ProjectFileConventions.GetEditorConfigLineEndingAsync(probe, cancellationToken)
                ?? await LineEndings.GetAsync(sourceDoc, cancellationToken);
            var targetContent = CreateTargetFileContent(
                LineEndings.Convert(typeNode, lineEnding, out keptLiterals),
                GeneratedCodeFormatter.IndentationOf(typeNode),
                namespaceName,
                NewFileUsings.FromSource(sourceRoot).Select(u => LineEndings.Convert(u, lineEnding, out _)).ToList(),
                lineEnding);
            var newDoc = project.AddDocument(
                Path.GetFileNameWithoutExtension(@params.TargetFile),
                targetContent,
                filePath: @params.TargetFile);
            solution = newDoc.Project.Solution;
            newDocumentId = newDoc.Id;
        }

        if (keptLiterals > 0)
            AddWarnings([LineEndings.KeptLiteralsWarning(@params.TargetFile, keptLiterals, lineEnding)]);

        // Remove type from source file
        var newSourceRoot = sourceRoot.RemoveNode(typeNode, SyntaxRemoveOptions.KeepNoTrivia);

        // If source file is now empty (no types), we'll delete it
        var remainingTypes = newSourceRoot?.DescendantNodes().OfType<TypeDeclarationSyntax>().Any() ?? false;

        if (!remainingTypes && newSourceRoot != null)
        {
            // Remove the document entirely
            solution = solution.RemoveDocument(sourceDoc.Id);
        }
        else if (newSourceRoot != null)
        {
            solution = solution.WithDocumentSyntaxRoot(sourceDoc.Id, newSourceRoot);
        }

        if (newDocumentId != null)
            solution = await NewFileUsings.RemoveUnnecessaryAsync(solution, newDocumentId, cancellationToken);

        return (solution, new ChangeInfo
        {
            SourceFileEmptied = !remainingTypes,
            TargetFileCreated = targetDoc == null,
            TypeNode = typeNode
        });
    }

    private static string CreateTargetFileContent(
        TypeDeclarationSyntax typeNode,
        string writtenAt,
        string namespaceName,
        List<UsingDirectiveSyntax> usings,
        string lineEnding)
    {
        // Build using directives
        var usingStatements = string.Join(lineEnding, usings.Select(u => u.ToFullString().TrimEnd()));

        // Use file-scoped namespace (modern C# style)
        var namespaceDecl = $"namespace {namespaceName};";

        // The type declaration as written, at the namespace's top level (the namespace line is
        // followed by one blank line)
        var typeDecl = Placed(typeNode, writtenAt, indentation: "").ToFullString();

        var blankLine = lineEnding + lineEnding;
        var content = string.IsNullOrEmpty(usingStatements)
            ? $"{namespaceDecl}{blankLine}{typeDecl}"
            : $"{usingStatements}{blankLine}{namespaceDecl}{blankLine}{typeDecl}";

        return content.TrimStart();
    }

    /// <summary>
    /// Where <see cref="AddTypeToExistingFile"/> puts the type: the namespace declaration of
    /// <paramref name="namespaceName"/>, else the first file-scoped namespace, else (null) the file.
    /// </summary>
    private static BaseNamespaceDeclarationSyntax? TargetContainer(SyntaxNode root, string namespaceName) =>
        root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault(n => n.Name.ToString() == namespaceName) ??
        root.DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();

    /// <summary>
    /// <paramref name="root"/> with the type added after the members of its
    /// <see cref="TargetContainer"/>: indented like those members (in an empty namespace block,
    /// one level deeper than the block), after a blank line.
    /// </summary>
    private static CompilationUnitSyntax AddTypeToExistingFile(
        CompilationUnitSyntax root,
        TypeDeclarationSyntax typeNode,
        string writtenAt,
        string namespaceName,
        string lineEnding)
    {
        var container = TargetContainer(root, namespaceName);
        var members = container?.Members ?? root.Members;
        var indentation = members.FirstOrDefault() is { } first
            ? GeneratedCodeFormatter.IndentationOf(first)
            : container is NamespaceDeclarationSyntax block ? GeneratedCodeFormatter.IndentationOf(block) + "    " : "";
        var placed = Placed(typeNode, writtenAt, indentation);
        if (members.Count > 0)
            placed = placed.WithLeadingTrivia(placed.GetLeadingTrivia().Insert(0, SyntaxFactory.EndOfLine(lineEnding)));

        return container != null
            ? root.ReplaceNode(container, container.AddMembers(placed))
            : root.AddMembers(placed);
    }

    /// <summary>
    /// The type for a place whose members start at <paramref name="indentation"/>: without the
    /// blank lines before it, and its lines re-indented from <paramref name="writtenAt"/>, the
    /// indentation it was written at (the text of its tokens, string literals included, stays).
    /// </summary>
    private static TypeDeclarationSyntax Placed(TypeDeclarationSyntax type, string writtenAt, string indentation)
    {
        var trimmed = type.WithLeadingTrivia(type.GetLeadingTrivia()
            .SkipWhile(t => t.IsKind(SyntaxKind.WhitespaceTrivia) || t.IsKind(SyntaxKind.EndOfLineTrivia)));
        var reindented = GeneratedCodeFormatter.Reindent(trimmed, writtenAt, indentation, startsLine: false);
        return indentation.Length == 0
            ? reindented
            : reindented.WithLeadingTrivia(reindented.GetLeadingTrivia().Insert(0, SyntaxFactory.Whitespace(indentation)));
    }

    private Contracts.Models.SymbolInfo CreateSymbolInfo(SymbolResolutionResult resolution, string targetFile)
    {
        var location = resolution.Declaration.GetLocation().GetLineSpan();

        return new Contracts.Models.SymbolInfo
        {
            Name = resolution.Symbol.Name,
            FullyQualifiedName = resolution.Symbol.ToDisplayString(),
            Kind = MapSymbolKind(resolution.Symbol),
            PreviousLocation = new SymbolLocation
            {
                File = resolution.Document.FilePath!,
                Line = location.StartLinePosition.Line + 1,
                Column = location.StartLinePosition.Character + 1
            },
            NewLocation = new SymbolLocation
            {
                File = targetFile,
                Line = 1, // Will be accurate after file is written
                Column = 1
            }
        };
    }

    private static Contracts.Enums.SymbolKind MapSymbolKind(INamedTypeSymbol symbol)
    {
        return symbol.TypeKind switch
        {
            TypeKind.Class => Contracts.Enums.SymbolKind.Class,
            TypeKind.Struct => Contracts.Enums.SymbolKind.Struct,
            TypeKind.Interface => Contracts.Enums.SymbolKind.Interface,
            TypeKind.Enum => Contracts.Enums.SymbolKind.Enum,
            TypeKind.Delegate => Contracts.Enums.SymbolKind.Delegate,
            _ when symbol.IsRecord => Contracts.Enums.SymbolKind.Record,
            _ => Contracts.Enums.SymbolKind.Class
        };
    }

    private RefactoringResult CreatePreviewResult(
        Guid operationId,
        MoveTypeToFileParams @params,
        ChangeInfo changeInfo,
        SymbolResolutionResult resolution,
        List<NewlyReportedUsings.Removed> usingsRemoved)
    {
        var pendingChanges = new List<PendingChange>();

        // Source file change
        pendingChanges.Add(new PendingChange
        {
            File = resolution.Document.FilePath!,
            ChangeType = changeInfo.SourceFileEmptied ? ChangeKind.Delete : ChangeKind.Modify,
            Description = changeInfo.SourceFileEmptied
                ? $"Delete file (emptied after removing {resolution.Symbol.Name})"
                : $"Remove {resolution.Symbol.Name} declaration"
        });

        // Target file change
        pendingChanges.Add(new PendingChange
        {
            File = @params.TargetFile,
            ChangeType = changeInfo.TargetFileCreated ? ChangeKind.Create : ChangeKind.Modify,
            Description = changeInfo.TargetFileCreated
                ? $"Create file with {resolution.Symbol.Name}"
                : $"Add {resolution.Symbol.Name} declaration"
        });
        pendingChanges.AddRange(usingsRemoved.Select(r => r.ToPendingChange("the moved type")));

        return new RefactoringResult
        {
            Success = true,
            OperationId = operationId,
            Preview = true,
            PendingChanges = pendingChanges,
            UsingDirectivesRemoved = usingsRemoved.Count
        };
    }

    private sealed class ChangeInfo
    {
        public bool SourceFileEmptied { get; init; }
        public bool TargetFileCreated { get; init; }
        public required TypeDeclarationSyntax TypeNode { get; init; }
    }
}
