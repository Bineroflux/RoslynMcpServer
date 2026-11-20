using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Rename;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Rename;

/// <summary>
/// Renames any symbol with automatic reference updates across the solution.
/// </summary>
public sealed class RenameSymbolOperation : RefactoringOperationBase<RenameSymbolParams>
{
    private readonly SymbolResolver _symbolResolver;

    /// <summary>
    /// Creates a new rename symbol operation.
    /// </summary>
    /// <param name="context">Workspace context.</param>
    public RenameSymbolOperation(WorkspaceContext context) : base(context)
    {
        _symbolResolver = context.CreateGeneralSymbolResolver();
    }

    /// <inheritdoc />
    protected override void ValidateParams(RenameSymbolParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.SymbolName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "symbolName is required.");

        if (string.IsNullOrWhiteSpace(@params.NewName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "newName is required.");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, PathResolver.GetCSharpFileRejectionReason(@params.SourceFile));

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        // The kind of symbol is known only once it is found; ValidateRename checks type names.
        var newName = IdentifierNames.Validate(@params.NewName, NameKind.Member, "newName", ErrorCodes.InvalidNewName);

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Column number must be >= 1.");

        if (IdentifierNames.ValueText(@params.SymbolName) == newName)
            throw new RefactoringException(ErrorCodes.SameLocation, "New name is the same as current name.");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        RenameSymbolParams @params,
        CancellationToken cancellationToken)
    {
        // Find the symbol
        var (symbol, document) = await FindSymbolAsync(@params, cancellationToken);

        // renameFile is about the file that declares the type, which is not the file the
        // symbol was found in when the caller pointed at a usage elsewhere.
        document = GetFileNamedAfterType(symbol) ?? document;

        // Validate rename is allowed
        ValidateRename(symbol, @params);

        // Find all references before rename
        var references = await ReferenceTracker.FindAllReferencesAsync(symbol, cancellationToken);

        // The renamer does not rename references in generated code (a Razor component's C#).
        AddWarnings(GeneratedReferences.Warnings(
            references.ReferencesByDocument.Values.SelectMany(r => r),
            $"the reference to '{symbol.ToDisplayString()}'",
            $"renamed to '{@params.NewName}'",
            cancellationToken));

        // Compute rename options
        var options = new SymbolRenameOptions(
            RenameOverloads: @params.RenameOverloads,
            RenameInStrings: false,
            RenameInComments: false,
            RenameFile: false // We handle file rename separately
        );

        // Perform the rename. The renamer renames each target framework's document of a
        // multi-targeted file on its own; merging gives the file the renames of every #if branch.
        var newSolution = await Renamer.RenameSymbolAsync(
            Context.Solution,
            symbol,
            options,
            @params.NewName,
            cancellationToken);
        newSolution = await LinkedDocuments.MergeAsync(Context.Solution, newSolution, document.Id, cancellationToken);

        // Handle file rename for types. The document is repointed at its new path
        // *before* the commit, so the commit writes the renamed source straight to the
        // new file name. That makes a pre-existing target file indistinguishable from
        // our own output afterwards, so a collision has to be detected here — while the
        // document still points at its original path — and not after the commit.
        string? renamedFile = null;
        string? fileRenameWarning = null;
        var caseOnlyFileRename = false;
        if (@params.RenameFile && symbol is INamedTypeSymbol && document.FilePath != null)
        {
            var fileName = Path.GetFileNameWithoutExtension(document.FilePath);
            if (fileName == symbol.Name)
            {
                var newFileName = IdentifierNames.ValueText(@params.NewName) + ".cs";
                var newFilePath = Path.Combine(Path.GetDirectoryName(document.FilePath)!, newFileName);

                // A rename that only changes casing (Foo -> foo) targets the very same
                // file on a case-insensitive file system; that is not a collision, and
                // the atomic write performed by the commit applies the new spelling.
                caseOnlyFileRename = PathResolver.PathsEqual(document.FilePath, newFilePath);

                if (!caseOnlyFileRename && File.Exists(newFilePath))
                {
                    // Leave the document at its original path so the commit updates the
                    // source in place instead of overwriting the unrelated target file.
                    fileRenameWarning = $"File rename skipped: target file '{newFilePath}' already exists.";
                }
                else if (newSolution.GetDocument(document.Id) != null)
                {
                    // Every target framework's document of the file moves with it.
                    foreach (var id in newSolution.GetDocumentIdsWithFilePath(document.FilePath))
                        newSolution = newSolution.WithDocumentFilePath(id, newFilePath);
                    renamedFile = newFilePath;
                }
            }
        }

        // If preview mode, return without applying
        if (@params.Preview)
        {
            await PreviewCommitAsync(newSolution, cancellationToken);
            return CreatePreviewResult(
                operationId,
                symbol,
                @params,
                references.TotalReferenceCount,
                renamedFile,
                fileRenameWarning);
        }

        // Commit changes
        var commitResult = await CommitChangesAsync(newSolution, cancellationToken);

        // The commit has completed the physical file rename: it wrote the renamed source under
        // the new path (the document points there) and then deleted the original, both under
        // the lock all RoslynMcp processes share. A case-only rename resolves to the same file,
        // which the commit rewrote under its new spelling. All that is left is reporting an
        // original the commit could not delete.
        var fileRenameSucceeded = renamedFile != null;
        if (renamedFile != null && document.FilePath != null && !caseOnlyFileRename &&
            commitResult.OldFilesNotDeleted.TryGetValue(document.FilePath, out var notDeleted))
        {
            fileRenameSucceeded = false;
            fileRenameWarning =
                $"Original file '{document.FilePath}' could not be deleted: {notDeleted}. " +
                $"The renamed source was written to '{renamedFile}'; delete the original manually.";
        }

        var renameMovedFile = fileRenameSucceeded && !caseOnlyFileRename;
        var changes = BuildFileChanges(
            commitResult,
            renameMovedFile ? document.FilePath : null,
            renameMovedFile ? renamedFile : null);

        var result = RefactoringResult.Succeeded(
            operationId,
            changes,
            CreateSymbolInfo(symbol, @params.NewName, document.FilePath, fileRenameSucceeded ? renamedFile : null),
            references.TotalReferenceCount,
            0);

        // Include warning in result if file rename failed
        if (fileRenameWarning != null)
        {
            return new RefactoringResult
            {
                Success = true,
                OperationId = result.OperationId,
                Preview = result.Preview,
                Changes = result.Changes,
                Symbol = result.Symbol,
                ReferencesUpdated = result.ReferencesUpdated,
                UsingDirectivesAdded = result.UsingDirectivesAdded,
                UsingDirectivesRemoved = result.UsingDirectivesRemoved,
                ExecutionTimeMs = result.ExecutionTimeMs,
                Error = RefactoringError.Create("PARTIAL_SUCCESS", fileRenameWarning),
                PendingChanges = result.PendingChanges
            };
        }

        return result;
    }

    /// <summary>
    /// For a type, the document among its declarations whose file is named after it (the
    /// one <c>renameFile</c> renames); otherwise null.
    /// </summary>
    private Document? GetFileNamedAfterType(ISymbol symbol) =>
        symbol is INamedTypeSymbol
            ? symbol.Locations
                .Where(location => location.IsInSource)
                .Select(location => Context.Solution.GetDocument(location.SourceTree))
                .FirstOrDefault(d => d?.FilePath != null && Path.GetFileNameWithoutExtension(d.FilePath) == symbol.Name)
            : null;

    /// <summary>
    /// Merges the commit's file lists with the outcome of the file rename. The commit
    /// writes the renamed document under its new path, so that write is reported as a
    /// creation plus the deletion of the original rather than as a modification of a
    /// path that no longer exists.
    /// </summary>
    /// <param name="commitResult">Result of writing the solution changes.</param>
    /// <param name="deletedFile">Original file path, or null when no file was renamed.</param>
    /// <param name="createdFile">New file path, or null when no file was renamed.</param>
    private static FileChanges BuildFileChanges(
        CommitResult commitResult,
        string? deletedFile,
        string? createdFile)
    {
        // A multi-targeted file is written once per target framework's document.
        var modified = commitResult.FilesModified.Distinct(PathResolver.PathComparer);
        if (deletedFile == null || createdFile == null)
        {
            return new FileChanges
            {
                FilesModified = modified.ToList(),
                FilesCreated = commitResult.FilesCreated,
                FilesDeleted = commitResult.FilesDeleted
            };
        }

        return new FileChanges
        {
            FilesModified = modified
                .Where(f => !PathResolver.PathsEqual(f, createdFile))
                .ToList(),
            FilesCreated = commitResult.FilesCreated
                .Where(f => !PathResolver.PathsEqual(f, createdFile))
                .Append(createdFile)
                .ToList(),
            FilesDeleted = commitResult.FilesDeleted
                .Append(deletedFile)
                .ToList()
        };
    }

    private async Task<(ISymbol Symbol, Document Document)> FindSymbolAsync(
        RenameSymbolParams @params,
        CancellationToken cancellationToken)
    {
        // Delegate to the shared resolver so rename benefits from the same
        // position-then-name resolution and line-scan column recovery as the query
        // tools. When a line is supplied without a column, the column defaults to 1,
        // which for declarator-based symbols (fields, events, locals) lands on the
        // leading modifier/type token rather than the identifier. Walking up the
        // ancestors of that token never reaches the VariableDeclaratorSyntax, so the
        // resolver's line-scan fallback recovers the unique identifier on the line.
        var resolution = await _symbolResolver.ResolveSymbolAsync(
            @params.SourceFile,
            @params.SymbolName,
            @params.Line,
            @params.Column,
            cancellationToken);

        return (resolution.Symbol, resolution.Document);
    }

    private static void ValidateRename(ISymbol symbol, RenameSymbolParams @params)
    {
        // Roslyn's renamer writes a name verbatim only when it is a reserved keyword, so
        // `@required` would come out as `required`, which cannot name a type (CS9029).
        if (symbol is INamedTypeSymbol && IdentifierNames.IsReservedTypeName(IdentifierNames.ValueText(@params.NewName)))
        {
            throw new RefactoringException(
                ErrorCodes.ReservedKeyword,
                $"newName '{@params.NewName}' cannot name a type here: rename_symbol writes it without '@', where it is a keyword.");
        }

        // Cannot rename constructors directly
        if (symbol is IMethodSymbol method)
        {
            if (method.MethodKind == MethodKind.Constructor)
            {
                throw new RefactoringException(
                    ErrorCodes.CannotRenameConstructor,
                    "Cannot rename constructor directly. Rename the containing type instead.");
            }

            if (method.MethodKind == MethodKind.Destructor)
            {
                throw new RefactoringException(
                    ErrorCodes.CannotRenameDestructor,
                    "Cannot rename destructor directly. Rename the containing type instead.");
            }

            if (method.MethodKind == MethodKind.UserDefinedOperator ||
                method.MethodKind == MethodKind.Conversion)
            {
                throw new RefactoringException(
                    ErrorCodes.CannotRenameOperator,
                    "Cannot rename operators.");
            }
        }

        // Cannot rename symbols from external assemblies
        if (symbol.ContainingAssembly != null &&
            !symbol.Locations.Any(l => l.IsInSource))
        {
            throw new RefactoringException(
                ErrorCodes.CannotRenameExternal,
                "Cannot rename symbols from external assemblies.");
        }
    }

    /// <summary>
    /// Creates symbol information for the result, with safe null handling for locations.
    /// </summary>
    private static Contracts.Models.SymbolInfo CreateSymbolInfo(
        ISymbol symbol,
        string newName,
        string? previousFile,
        string? newFile)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        FileLinePositionSpan? lineSpan = null;

        // Safely get line span only if location is valid and in source
        if (location != null && location.IsInSource)
        {
            try
            {
                var span = location.GetLineSpan();
                // Validate the span has meaningful data
                if (span.Path != null || span.StartLinePosition.Line >= 0)
                {
                    lineSpan = span;
                }
            }
            catch (InvalidOperationException)
            {
                // GetLineSpan can throw if location is invalid - treat as no location
            }
        }

        SymbolLocation? prevLocation = null;
        SymbolLocation? newLocation = null;

        if (previousFile != null && lineSpan.HasValue)
        {
            prevLocation = new SymbolLocation
            {
                File = previousFile,
                Line = lineSpan.Value.StartLinePosition.Line + 1,
                Column = lineSpan.Value.StartLinePosition.Character + 1
            };
        }

        if (newFile != null)
        {
            // Use line span if available, otherwise default to line 1, column 1
            newLocation = new SymbolLocation
            {
                File = newFile,
                Line = lineSpan?.StartLinePosition.Line + 1 ?? 1,
                Column = lineSpan?.StartLinePosition.Character + 1 ?? 1
            };
        }

        return new Contracts.Models.SymbolInfo
        {
            Name = newName,
            FullyQualifiedName = symbol.ToDisplayString().Replace(symbol.Name, newName),
            Kind = MapSymbolKind(symbol),
            PreviousLocation = prevLocation,
            NewLocation = newLocation
        };
    }

    private static Contracts.Enums.SymbolKind MapSymbolKind(ISymbol symbol)
    {
        return symbol switch
        {
            INamedTypeSymbol namedType => namedType.TypeKind switch
            {
                TypeKind.Class => Contracts.Enums.SymbolKind.Class,
                TypeKind.Struct => Contracts.Enums.SymbolKind.Struct,
                TypeKind.Interface => Contracts.Enums.SymbolKind.Interface,
                TypeKind.Enum => Contracts.Enums.SymbolKind.Enum,
                TypeKind.Delegate => Contracts.Enums.SymbolKind.Delegate,
                _ when namedType.IsRecord => Contracts.Enums.SymbolKind.Record,
                _ => Contracts.Enums.SymbolKind.Class
            },
            IMethodSymbol => Contracts.Enums.SymbolKind.Method,
            IPropertySymbol => Contracts.Enums.SymbolKind.Property,
            IFieldSymbol => Contracts.Enums.SymbolKind.Field,
            IEventSymbol => Contracts.Enums.SymbolKind.Event,
            ILocalSymbol => Contracts.Enums.SymbolKind.Local,
            IParameterSymbol => Contracts.Enums.SymbolKind.Parameter,
            INamespaceSymbol => Contracts.Enums.SymbolKind.Namespace,
            _ => Contracts.Enums.SymbolKind.Class
        };
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        ISymbol symbol,
        RenameSymbolParams @params,
        int referenceCount,
        string? renamedFile,
        string? fileRenameWarning)
    {
        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Rename '{symbol.Name}' to '{@params.NewName}'"
            }
        };

        if (referenceCount > 0)
        {
            pendingChanges.Add(new PendingChange
            {
                File = "(multiple files)",
                ChangeType = ChangeKind.Modify,
                Description = $"Update {referenceCount} reference(s)"
            });
        }

        if (renamedFile != null)
        {
            pendingChanges.Add(new PendingChange
            {
                File = renamedFile,
                ChangeType = ChangeKind.Create,
                Description = "Rename file to match type name"
            });
        }

        var preview = RefactoringResult.PreviewResult(operationId, pendingChanges);
        if (fileRenameWarning == null)
            return preview;

        // Surface a target-file collision in preview too, so the caller learns that the
        // file rename will not happen before applying the operation.
        return new RefactoringResult
        {
            Success = true,
            OperationId = preview.OperationId,
            Preview = true,
            PendingChanges = preview.PendingChanges,
            Error = RefactoringError.Create("PARTIAL_SUCCESS", fileRenameWarning)
        };
    }
}
