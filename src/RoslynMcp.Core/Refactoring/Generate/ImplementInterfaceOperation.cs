using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Generate;

/// <summary>
/// Generates interface member implementations for a type.
/// </summary>
public sealed class ImplementInterfaceOperation : RefactoringOperationBase<ImplementInterfaceParams>
{
    /// <summary>
    /// Creates a new implement interface operation.
    /// </summary>
    public ImplementInterfaceOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(ImplementInterfaceParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.TypeName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "typeName is required.");

        if (string.IsNullOrWhiteSpace(@params.InterfaceName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "interfaceName is required.");

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
        ImplementInterfaceParams @params,
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
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.Text == @params.TypeName);

        if (typeDeclaration == null)
        {
            throw new RefactoringException(
                ErrorCodes.TypeNotFound,
                $"Type '{@params.TypeName}' not found in file.");
        }

        // Get the type symbol
        var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) as INamedTypeSymbol;
        if (typeSymbol == null)
        {
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve type symbol.");
        }

        // Find the interface
        var interfaceSymbol = await FindInterfaceAsync(
            typeSymbol,
            @params.InterfaceName,
            cancellationToken);

        if (interfaceSymbol == null)
        {
            throw new RefactoringException(
                ErrorCodes.InterfaceNotFound,
                $"Interface '{@params.InterfaceName}' not found.");
        }

        if (interfaceSymbol.TypeKind != TypeKind.Interface)
        {
            throw new RefactoringException(
                ErrorCodes.InterfaceNotFound,
                $"'{interfaceSymbol.ToDisplayString()}' is not an interface.");
        }

        // A type that does not list the interface yet gets it added to its base list first:
        // otherwise implicit stubs implement nothing and explicit ones do not compile
        // (CS0540). The members are then determined against the updated type, so members
        // it already has that now implement the interface are not generated again.
        var addsInterface = !typeSymbol.AllInterfaces.Contains(interfaceSymbol, SymbolEqualityComparer.Default);
        if (addsInterface)
        {
            if (interfaceSymbol.IsGenericType && interfaceSymbol.Equals(interfaceSymbol.OriginalDefinition, SymbolEqualityComparer.Default))
            {
                throw new RefactoringException(
                    ErrorCodes.InterfaceNotFound,
                    $"'{interfaceSymbol.ToDisplayString()}' is generic. Add it with its type arguments to the base list of '{@params.TypeName}' first.");
            }

            var interfaceName = interfaceSymbol.ToDisplayString();
            var tracker = new SyntaxAnnotation();
            var withInterface = (TypeDeclarationSyntax)SyntaxGenerator.GetGenerator(document).AddInterfaceType(
                typeDeclaration,
                SyntaxFactory.ParseTypeName(interfaceSymbol.ToMinimalDisplayString(semanticModel, typeDeclaration.SpanStart)));
            withInterface = withInterface
                .WithBaseList(GeneratedCodeFormatter.Mark(withInterface.BaseList!))
                .WithAdditionalAnnotations(tracker);

            var updatedDocument = document.WithSyntaxRoot(root.ReplaceNode(typeDeclaration, withInterface));
            root = await updatedDocument.GetSyntaxRootAsync(cancellationToken);
            semanticModel = await updatedDocument.GetSemanticModelAsync(cancellationToken);
            if (root == null || semanticModel == null)
            {
                throw new RefactoringException(ErrorCodes.RoslynError, "Could not analyze the updated type.");
            }

            typeDeclaration = root.GetAnnotatedNodes(tracker).OfType<TypeDeclarationSyntax>().Single();
            typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve type symbol.");
            interfaceSymbol = typeSymbol.AllInterfaces.FirstOrDefault(i => i.ToDisplayString() == interfaceName)
                ?? throw new RefactoringException(
                    ErrorCodes.InterfaceNotFound,
                    $"Interface '{interfaceName}' is not accessible from '{@params.TypeName}'.");
        }

        // Get unimplemented members
        var unimplementedMembers = MemberAnalyzer.GetUnimplementedMembers(typeSymbol, interfaceSymbol).ToList();

        // Filter to requested members if specified
        if (@params.Members != null && @params.Members.Count > 0)
        {
            var requestedSet = new HashSet<string>(@params.Members);
            unimplementedMembers = unimplementedMembers.Where(m => requestedSet.Contains(m.Name)).ToList();
        }

        if (unimplementedMembers.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.MemberAlreadyImplemented,
                "All interface members are already implemented.");
        }

        // Generate implementations (appended at the end of the type)
        var nullableAnnotations = semanticModel
            .GetNullableContext(typeDeclaration.CloseBraceToken.SpanStart)
            .AnnotationsEnabled();
        var implementations = GenerateImplementations(
            unimplementedMembers,
            @params.ExplicitImplementation,
            @params.ThrowNotImplemented,
            nullableAnnotations);

        // If preview mode, return without applying
        if (@params.Preview)
        {
            return CreatePreviewResult(operationId, @params, unimplementedMembers, implementations, addsInterface);
        }

        // Add implementations to type
        var newTypeDeclaration = AddMembers(typeDeclaration, implementations);
        var newRoot = root.ReplaceNode(typeDeclaration, newTypeDeclaration);

        var newDocument = document.WithSyntaxRoot(newRoot);
        var newSolution = await GeneratedCodeFormatter.FormatAsync(
            newDocument.Project.Solution, document, cancellationToken);

        // Commit changes
        var commitResult = await CommitChangesAsync(newSolution, cancellationToken);

        return RefactoringResult.Succeeded(
            operationId,
            new FileChanges
            {
                FilesModified = commitResult.FilesModified,
                FilesCreated = commitResult.FilesCreated,
                FilesDeleted = commitResult.FilesDeleted
            },
            new Contracts.Models.SymbolInfo
            {
                Name = @params.TypeName,
                FullyQualifiedName = typeSymbol.ToDisplayString(),
                Kind = Contracts.Enums.SymbolKind.Class
            },
            0,
            0);
    }

    private async Task<INamedTypeSymbol?> FindInterfaceAsync(
        INamedTypeSymbol typeSymbol,
        string interfaceName,
        CancellationToken cancellationToken)
    {
        // First check if type already implements/references the interface
        var directInterface = typeSymbol.AllInterfaces
            .FirstOrDefault(i =>
                i.Name == interfaceName ||
                i.ToDisplayString() == interfaceName);

        if (directInterface != null)
        {
            return directInterface;
        }

        // Try to find interface in workspace
        return await TypeResolver.FindTypeByNameAsync(interfaceName, cancellationToken);
    }

    private static List<MemberDeclarationSyntax> GenerateImplementations(
        List<ISymbol> members,
        bool explicitImplementation,
        bool throwNotImplemented,
        bool nullableAnnotations)
    {
        var implementations = new List<MemberDeclarationSyntax>();

        foreach (var member in members)
        {
            MemberDeclarationSyntax? impl = member switch
            {
                IMethodSymbol method => SyntaxGenerationHelper.CreateMethodStub(
                    method,
                    explicitImplementation,
                    callBase: false,
                    throwNotImplemented,
                    nullableAnnotations: nullableAnnotations),
                IPropertySymbol { IsIndexer: true } indexer => SyntaxGenerationHelper.CreateIndexerStub(
                    indexer,
                    explicitImplementation,
                    throwNotImplemented,
                    nullableAnnotations: nullableAnnotations),
                IPropertySymbol property => SyntaxGenerationHelper.CreatePropertyStub(
                    property,
                    explicitImplementation,
                    throwNotImplemented,
                    nullableAnnotations: nullableAnnotations),
                IEventSymbol evt => SyntaxGenerationHelper.CreateEventStub(
                    evt,
                    explicitImplementation,
                    nullableAnnotations),
                _ => null
            };

            if (impl != null)
            {
                implementations.Add(impl);
            }
        }

        return implementations;
    }

    private static TypeDeclarationSyntax AddMembers(
        TypeDeclarationSyntax typeDeclaration,
        List<MemberDeclarationSyntax> newMembers)
    {
        return typeDeclaration.AddMembers(newMembers.Select(GeneratedCodeFormatter.MarkInserted).ToArray());
    }

    private static RefactoringResult CreatePreviewResult(
        Guid operationId,
        ImplementInterfaceParams @params,
        List<ISymbol> members,
        List<MemberDeclarationSyntax> implementations,
        bool addsInterface)
    {
        var memberNames = string.Join(", ", members.Select(m => m.Name));
        var baseList = addsInterface ? $" (adds {@params.InterfaceName} to the base list of {@params.TypeName})" : "";
        var implCode = string.Join("\n\n",
            implementations.Select(i => i.NormalizeWhitespace().ToFullString()));

        var pendingChanges = new List<PendingChange>
        {
            new()
            {
                File = @params.SourceFile,
                ChangeType = ChangeKind.Modify,
                Description = $"Implement {@params.InterfaceName} members: {memberNames}{baseList}",
                BeforeSnippet = $"// End of type '{@params.TypeName}'",
                AfterSnippet = implCode
            }
        };

        return RefactoringResult.PreviewResult(operationId, pendingChanges);
    }
}
