using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Rename;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Rename;

/// <summary>
/// Renames any symbol with automatic reference updates across the solution.
/// </summary>
public sealed class RenameSymbolOperation : RefactoringOperationBase<RenameSymbolParams>
{
    private static readonly Regex IdentifierPattern = new(
        @"^@?[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.Compiled);

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

        if (!IdentifierPattern.IsMatch(@params.NewName))
            throw new RefactoringException(ErrorCodes.InvalidNewName, $"'{@params.NewName}' is not a valid C# identifier.");

        if (SyntaxFacts.GetKeywordKind(@params.NewName) != SyntaxKind.None)
            throw new RefactoringException(ErrorCodes.ReservedKeyword, $"'{@params.NewName}' is a C# reserved keyword.");

        if (@params.Line.HasValue && @params.Line.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "Line number must be >= 1.");

        if (@params.Column.HasValue && @params.Column.Value < 1)
            throw new RefactoringException(ErrorCodes.InvalidColumnNumber, "Column number must be >= 1.");

        if (@params.SymbolName == @params.NewName)
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

        // Validate rename is allowed
        ValidateRename(symbol, @params);

        // Find all references before rename
        var references = await ReferenceTracker.FindAllReferencesAsync(symbol, cancellationToken);

        // Compute rename options
        var options = new SymbolRenameOptions(
            RenameOverloads: @params.RenameOverloads,
            RenameInStrings: false,
            RenameInComments: false,
            RenameFile: false // We handle file rename separately
        );

        // Perform the rename
        var newSolution = await Renamer.RenameSymbolAsync(
            Context.Solution,
            symbol,
            options,
            @params.NewName,
            cancellationToken);

        // Handle file rename for types
        string? renamedFile = null;
        if (@params.RenameFile && symbol is INamedTypeSymbol namedType && document.FilePath != null)
        {
            var fileName = Path.GetFileNameWithoutExtension(document.FilePath);
            if (fileName == symbol.Name)
            {
                var newFileName = @params.NewName + ".cs";
                var newFilePath = Path.Combine(Path.GetDirectoryName(document.FilePath)!, newFileName);

                // Rename the document in the solution
                var doc = newSolution.GetDocument(document.Id);
                if (doc != null)
                {
                    newSolution = newSolution.WithDocumentFilePath(document.Id, newFilePath);
                    renamedFile = newFilePath;
                }
            }
        }

        // If preview mode, return without applying
        if (@params.Preview)
        {
            return CreatePreviewResult(operationId, symbol, @params, references.TotalReferenceCount, renamedFile);
        }

        // Commit changes
        var commitResult = await CommitChangesAsync(newSolution, cancellationToken);

        // Handle physical file rename
        string? fileRenameWarning = null;
        bool fileRenameSucceeded = false;
        if (renamedFile != null && document.FilePath != null)
        {
            try
            {
                if (File.Exists(document.FilePath) && !File.Exists(renamedFile))
                {
                    File.Move(document.FilePath, renamedFile);
                    fileRenameSucceeded = true;
                }
                else if (File.Exists(renamedFile))
                {
                    fileRenameWarning = $"File rename skipped: target file '{renamedFile}' already exists.";
                }
            }
            catch (IOException ex)
            {
                fileRenameWarning = $"File rename failed: {ex.Message}. Code references were updated but file was not renamed.";
                renamedFile = null; // Clear to indicate file was not actually renamed
            }
        }

        var changes = new FileChanges
        {
            FilesModified = commitResult.FilesModified,
            FilesCreated = fileRenameSucceeded ? commitResult.FilesCreated.Concat(new[] { renamedFile! }).ToList() : commitResult.FilesCreated,
            FilesDeleted = fileRenameSucceeded ? commitResult.FilesDeleted.Concat(new[] { document.FilePath! }).ToList() : commitResult.FilesDeleted
        };

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
        string? renamedFile)
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

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }
}
