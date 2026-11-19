using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Delete;
using RoslynMcp.Core.Refactoring.Signature;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Hierarchy;

/// <summary>
/// Moves members of a type into its base class, or declares them in an interface it implements.
/// </summary>
/// <remarks>
/// <para>
/// Into a base class, each declaration moves with its documentation comment and attributes; a
/// private member becomes protected (a <c>private protected</c> one stays, or becomes protected
/// when the base class is in another assembly). With <c>makeAbstract</c>, methods, properties,
/// indexers and events are declared abstract in the base class and stay in the type as overrides
/// (fields still move); a base class that is never instantiated (<c>new</c>, a type argument
/// for a <c>new()</c>-constrained type parameter, <c>Activator.CreateInstance</c>; reflection by
/// name isn't seen) is made abstract. Refused: a member
/// that uses members of the type staying behind (listed), a name or signature the target already
/// declares, an override of a member the base class declares, <c>base.</c> calls whose meaning
/// would change, and types that can't be written in the base class's terms.
/// </para>
/// <para>
/// Into an interface, the signature of each public instance method, property, indexer or event is
/// added (public accessors only); the type keeps its implementation, which stays an implicit
/// implementation. Non-public members are refused rather than made public or explicit, which
/// would change who can call them.
/// </para>
/// <para>
/// The members go into the target's part in <c>targetFile</c>, or else its first part in a file
/// that isn't generated. Usings the moved code needs are added to that file: namespaces, a
/// <c>using static</c> for static members named without their type, and the aliases it uses; a
/// name the target uses for something else is written qualified instead. Compiler errors introduced in the
/// files of the target, the type, the target's other derived types or implementers (members they
/// now hide or lack) are returned as warnings, with notes on changed accessibility.
/// </para>
/// </remarks>
public sealed class PullMembersUpOperation : RefactoringOperationBase<PullMembersUpParams>
{
    private const string RemoveAnnotationKind = "RoslynMcp.PullMembersUp.Remove";
    private const string InsertedAnnotationKind = "RoslynMcp.PullMembersUp.Inserted";

    /// <summary>
    /// Creates a new pull members up operation.
    /// </summary>
    public PullMembersUpOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(PullMembersUpParams @params)
    {
        if (string.IsNullOrWhiteSpace(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "sourceFile is required.");

        if (string.IsNullOrWhiteSpace(@params.TypeName))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "typeName is required.");

        if (@params.Members == null || @params.Members.Count == 0 || @params.Members.Any(string.IsNullOrWhiteSpace))
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "members is required (names of the members to move).");

        if (!PathResolver.IsAbsolutePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "sourceFile must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, PathResolver.GetCSharpFileRejectionReason(@params.SourceFile));

        if (!File.Exists(@params.SourceFile))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Source file not found: {@params.SourceFile}");

        if (@params.Line is < 1)
            throw new RefactoringException(ErrorCodes.InvalidLineNumber, "line must be >= 1.");

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
        PullMembersUpParams @params,
        CancellationToken cancellationToken)
    {
        var document = GetDocumentOrThrow(@params.SourceFile);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || model == null)
            throw new RefactoringException(ErrorCodes.RoslynError, "Could not parse file.");

        var typeDeclaration = SignatureSyntax.SelectDeclaration(
            root.DescendantNodes().OfType<TypeDeclarationSyntax>().Where(t => t.Identifier.ValueText == @params.TypeName && t is not InterfaceDeclarationSyntax).ToList(),
            "type",
            @params.TypeName,
            @params.Line,
            t => t.Identifier,
            ErrorCodes.TypeNotFound);
        var type = model.GetDeclaredSymbol(typeDeclaration, cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not resolve the type.");

        var solution = Context.Solution;
        var (target, view) = ResolveTarget(type, @params.TargetType);
        var (targetDocument, targetDeclaration) = TargetDeclaration(target, solution, @params.TargetFile, cancellationToken);
        var targetModel = await targetDocument.GetSemanticModelAsync(cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not analyze the target's file.");
        var isInterface = target.TypeKind == TypeKind.Interface;
        var makeAbstract = @params.MakeAbstract && !isInterface;
        var sameAssembly = type.ContainingAssembly.Name == target.ContainingAssembly.Name;
        var nullable = targetModel.GetNullableContext(targetDeclaration.OpenBraceToken.Span.End).AnnotationsEnabled();
        var typeMap = TypeMap(type, view);

        var selected = MovedMembers.Select(type, @params.Members, cancellationToken);
        var moved = selected.SelectMany(s => s.Symbols).ToList();
        CheckMembers(selected, target, isInterface, makeAbstract);
        CheckConflicts(moved, target, typeMap, model.Compilation);

        var notes = new List<string>();
        var newMembers = new List<MemberDeclarationSyntax>();
        var usings = new HashSet<UsingDirectives.NewUsing>();
        var edits = new SyntaxEdits();
        var dependencies = new List<ISymbol>();

        foreach (var declaration in selected)
        {
            var declaringDocument = solution.GetDocument(declaration.Declaration.SyntaxTree)
                ?? throw new RefactoringException(ErrorCodes.MemberNotMoveable, $"'{declaration.Symbols[0].ToDisplayString()}' is declared in generated code.");
            var declaringModel = declaringDocument.Id == document.Id ? model : (await declaringDocument.GetSemanticModelAsync(cancellationToken))!;
            var abstractMember = makeAbstract && declaration.Declaration is not FieldDeclarationSyntax;
            var signatureOnly = isInterface || abstractMember;
            var movedNodes = signatureOnly ? Signature(declaration.Declaration) : MovedNodes(declaration);

            dependencies.AddRange(MovedMembers.Dependencies(movedNodes, type, moved, declaringModel, cancellationToken)
                .Where(d => !dependencies.Contains(d, SymbolEqualityComparer.Default)));
            if (!signatureOnly)
                CheckBaseAccess(movedNodes, target, declaringModel, cancellationToken);
            var qualified = new Dictionary<SyntaxNode, SyntaxNode>();
            usings.UnionWith(NeededUsings(movedNodes, type, declaringModel, targetModel, targetDeclaration, nullable, qualified, cancellationToken));

            var translated = Translate(declaration.Declaration, type, typeMap, qualified, declaringModel, nullable);
            var member = declaration.Chosen(translated);
            MemberDeclarationSyntax forTarget;
            if (isInterface)
            {
                forTarget = InterfaceMember(member, declaration.Symbols[0]);
            }
            else
            {
                var (modifiers, note) = MovedMembers.MovedModifiers(Modifiers(member), declaration.Symbols[0], target.Name, sameAssembly);
                if (note != null)
                    notes.Add(note);
                forTarget = abstractMember
                    ? AbstractMember(member, declaration.Symbols[0], AccessibilityOnly(modifiers))
                    : WithModifiers(member, WithoutNew(modifiers, declaration.Symbols[0], target));

                if (abstractMember)
                {
                    var overrideModifiers = OverrideModifiers(modifiers, declaration.Symbols[0]);
                    edits.Add(declaringDocument.Id, declaration.Declaration, rewritten => MakeOverride((MemberDeclarationSyntax)rewritten, overrideModifiers));
                }
                else
                {
                    foreach (var node in declaration.Removed)
                        edits.Add(declaringDocument.Id, node, rewritten => rewritten.WithAdditionalAnnotations(new SyntaxAnnotation(RemoveAnnotationKind)));
                }
            }

            var inserted = forTarget
                .WithLeadingTrivia(MovedMembers.Comments(declaration.Declaration).Insert(0, SyntaxFactory.ElasticCarriageReturnLineFeed))
                .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)
                .WithAdditionalAnnotations(Formatter.Annotation, new SyntaxAnnotation(InsertedAnnotationKind, declaration.Symbols[0].Name));

            // A member moved whole keeps the layout it is written with (only re-indented); a
            // signature made for an interface or an abstract member is laid out like generated code.
            newMembers.Add(signatureOnly
                ? inserted
                : GeneratedCodeFormatter.KeepMemberLayout(
                    inserted, forTarget.WithLeadingTrivia(MovedMembers.CommentLines(declaration.Declaration)), declaration.Declaration));
        }

        if (dependencies.Count > 0)
            throw DependsOnDerived(type, dependencies);

        var makesTargetAbstract = makeAbstract && !target.IsAbstract;
        if (makesTargetAbstract)
        {
            await CheckNotInstantiatedAsync(target, solution, cancellationToken);
            notes.Add($"'{target.ToDisplayString()}' was not abstract and is never instantiated; it is abstract now.");
        }

        edits.Add(targetDocument.Id, targetDeclaration, rewritten =>
            Insert((TypeDeclarationSyntax)rewritten, newMembers, makesTargetAbstract));

        var lineEnding = await LineEndings.GetAsync(targetDocument, cancellationToken);
        var newSolution = await edits.ApplyAsync(
            solution,
            cancellationToken,
            (id, newRoot) =>
                id == targetDocument.Id && newRoot is CompilationUnitSyntax unit && usings.Count > 0
                    ? UsingDirectives.Add(unit, usings, lineEnding)
                    : newRoot,
            notes);
        var annotated = newSolution;
        var removals = await RemoveMarkedAsync(newSolution, edits.Documents, cancellationToken);
        newSolution = await LinkedDocuments.SyncAsync(solution, removals.Solution, edits.Documents, cancellationToken);

        var (siblingNotes, related) = await RelatedTypesAsync(type, target, moved, makeAbstract, solution, cancellationToken);
        notes.AddRange(siblingNotes);
        var affected = edits.Documents.Concat(related).Distinct().ToList();
        var warnings = notes.Concat(await IntroducedErrors.FindAsync(solution, newSolution, affected, cancellationToken) ?? []).ToList();

        if (@params.Preview)
        {
            await PreviewCommitAsync(newSolution, cancellationToken);
            return new RefactoringResult
            {
                Success = true,
                OperationId = operationId,
                Preview = true,
                PendingChanges = await CreatePendingChangesAsync(annotated, targetDocument.Id, target, makesTargetAbstract, removals.Changes, selected, makeAbstract, isInterface, cancellationToken),
                Warnings = warnings.Count == 0 ? null : warnings
            };
        }

        var commit = await CommitChangesAsync(newSolution, cancellationToken);
        return new RefactoringResult
        {
            Success = true,
            OperationId = operationId,
            Changes = new FileChanges
            {
                FilesModified = commit.FilesModified.Distinct(PathResolver.PathComparer).ToList(),
                FilesCreated = commit.FilesCreated,
                FilesDeleted = commit.FilesDeleted
            },
            Symbol = new Contracts.Models.SymbolInfo
            {
                Name = target.Name,
                FullyQualifiedName = target.ToDisplayString(),
                Kind = isInterface ? Contracts.Enums.SymbolKind.Interface : Contracts.Enums.SymbolKind.Class
            },
            ReferencesUpdated = moved.Count,
            Warnings = warnings.Count == 0 ? null : warnings
        };
    }

    /// <summary>The target type (definition) and how <paramref name="type"/> sees it (constructed).</summary>
    private static (INamedTypeSymbol Target, INamedTypeSymbol View) ResolveTarget(INamedTypeSymbol type, string? targetName)
    {
        var baseType = type.TypeKind == TypeKind.Class && type.BaseType is { SpecialType: not SpecialType.System_Object } b ? b : null;
        if (string.IsNullOrWhiteSpace(targetName))
        {
            return baseType == null
                ? throw new RefactoringException(
                    ErrorCodes.BaseClassNotFound,
                    $"'{type.ToDisplayString()}' has no base class to pull members up to; name an interface it implements as targetType.")
                : (baseType.OriginalDefinition, baseType);
        }

        var name = targetName.Trim();
        bool Matches(INamedTypeSymbol candidate) =>
            candidate.Name == name ||
            candidate.OriginalDefinition.ToDisplayString() == name ||
            candidate.ToDisplayString() == name ||
            candidate.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == name;

        if (baseType != null && Matches(baseType))
            return (baseType.OriginalDefinition, baseType);

        var interfaces = type.AllInterfaces.Where(Matches).ToList();
        return interfaces.Count switch
        {
            1 => (interfaces[0].OriginalDefinition, interfaces[0]),
            0 => throw new RefactoringException(
                ErrorCodes.InterfaceNotFound,
                $"'{name}' is neither the base class of '{type.ToDisplayString()}' nor an interface it implements."),
            _ => throw new RefactoringException(
                ErrorCodes.SymbolAmbiguous,
                $"'{type.ToDisplayString()}' implements several interfaces named '{name}': {string.Join(", ", interfaces.Select(i => i.ToDisplayString()))}. Give the full name.")
        };
    }

    /// <summary>
    /// The target's declaration to add the members to: its part in <paramref name="targetFile"/>
    /// when given (refused when the file has none, or is generated), else its first part in a
    /// source file that isn't generated.
    /// </summary>
    private static (Document Document, TypeDeclarationSyntax Declaration) TargetDeclaration(
        INamedTypeSymbol target,
        Solution solution,
        string? targetFile,
        CancellationToken cancellationToken)
    {
        if (target.DeclaringSyntaxReferences.Length == 0)
        {
            throw new RefactoringException(
                ErrorCodes.BaseClassNotFound,
                $"'{target.ToDisplayString()}' is declared in {target.ContainingAssembly?.Name ?? "metadata"}, not in source.");
        }

        if (targetFile != null)
        {
            foreach (var reference in target.DeclaringSyntaxReferences.Where(r => PathResolver.PathsEqual(r.SyntaxTree.FilePath, targetFile)))
            {
                if (GeneratedCode.IsGenerated(reference.SyntaxTree, cancellationToken))
                    throw new RefactoringException(ErrorCodes.InvalidTargetPath, $"targetFile '{targetFile}' is generated code, which the build overwrites.");

                if (solution.GetDocument(reference.SyntaxTree) is { } document && reference.GetSyntax(cancellationToken) is TypeDeclarationSyntax declaration)
                    return (document, declaration);
            }

            var files = target.DeclaringSyntaxReferences.Select(r => r.SyntaxTree.FilePath).Distinct(PathResolver.PathComparer);
            throw new RefactoringException(
                ErrorCodes.InvalidTargetPath,
                $"targetFile '{targetFile}' declares no part of '{target.ToDisplayString()}', which is declared in {string.Join(", ", files)}.");
        }

        foreach (var reference in target.DeclaringSyntaxReferences)
        {
            if (solution.GetDocument(reference.SyntaxTree) is { } document &&
                !GeneratedCode.IsGenerated(reference.SyntaxTree, cancellationToken) &&
                reference.GetSyntax(cancellationToken) is TypeDeclarationSyntax declaration)
            {
                return (document, declaration);
            }
        }

        throw new RefactoringException(ErrorCodes.BaseClassNotFound, $"'{target.ToDisplayString()}' is declared only in generated code.");
    }

    /// <summary>
    /// How the type's own type parameters are written in the target: those the type passes on as
    /// type arguments become the target's type parameters (<c>Derived&lt;U&gt; : Base&lt;U&gt;</c>).
    /// </summary>
    private static Dictionary<ITypeParameterSymbol, ITypeSymbol> TypeMap(INamedTypeSymbol type, INamedTypeSymbol view)
    {
        var map = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var (parameter, argument) in view.OriginalDefinition.TypeParameters.Zip(view.TypeArguments))
        {
            if (argument is ITypeParameterSymbol own && type.TypeParameters.Contains(own, SymbolEqualityComparer.Default))
                map.TryAdd(own, parameter);
        }

        return map;
    }

    private static void CheckMembers(List<SelectedDeclaration> selected, INamedTypeSymbol target, bool isInterface, bool makeAbstract)
    {
        foreach (var declaration in selected)
        {
            foreach (var symbol in declaration.Symbols)
            {
                var name = symbol.ToDisplayString();
                if (isInterface)
                {
                    if (symbol is IFieldSymbol)
                        throw new RefactoringException(ErrorCodes.MemberNotMoveable, $"'{name}' is a field; an interface can't declare it.");
                    if (symbol.IsStatic)
                        throw new RefactoringException(ErrorCodes.MemberNotMoveable, $"'{name}' is static; only instance members are added to an interface.");
                    if (symbol.DeclaredAccessibility != Accessibility.Public)
                    {
                        throw new RefactoringException(
                            ErrorCodes.MemberNotPublic,
                            $"'{name}' is not public, so it can't implement a member of '{target.ToDisplayString()}'. " +
                            "Make it public first (an explicit implementation would hide it from its type's own callers).");
                    }

                    continue;
                }

                if (symbol.IsOverride && Overridden(symbol) is { } overridden &&
                    SymbolEqualityComparer.Default.Equals(overridden.ContainingType.OriginalDefinition, target))
                {
                    throw new RefactoringException(
                        ErrorCodes.MemberConflict,
                        $"'{name}' overrides '{overridden.ToDisplayString()}', which '{target.Name}' declares itself.");
                }

                if (!makeAbstract || symbol is IFieldSymbol)
                    continue;

                if (symbol.IsStatic)
                    throw new RefactoringException(ErrorCodes.MemberNotMoveable, $"'{name}' is static and can't be made abstract.");
                if (symbol is IPropertySymbol property &&
                    (property.GetMethod?.DeclaredAccessibility == Accessibility.Private || property.SetMethod?.DeclaredAccessibility == Accessibility.Private))
                {
                    throw new RefactoringException(
                        ErrorCodes.MemberNotMoveable,
                        $"'{name}' has a private accessor, which an abstract property can't declare nor an override add.");
                }

                if (!declaration.IsWhole)
                {
                    throw new RefactoringException(
                        ErrorCodes.MemberNotMoveable,
                        $"'{name}' is declared together with other events; select all of them or declare it on its own.");
                }
            }
        }
    }

    /// <summary>Refuses members whose name (and, for methods and indexers, signature) the target already declares.</summary>
    private static void CheckConflicts(List<ISymbol> moved, INamedTypeSymbol target, Dictionary<ITypeParameterSymbol, ITypeSymbol> typeMap, Compilation compilation)
    {
        foreach (var symbol in moved)
        {
            var existing = symbol is IPropertySymbol { IsIndexer: true }
                ? target.GetMembers().Where(m => m is IPropertySymbol { IsIndexer: true })
                : target.GetMembers(symbol.Name).Where(m => !m.IsImplicitlyDeclared);

            foreach (var member in existing)
            {
                var parameters = Parameters(symbol);
                var conflicts = parameters == null || Parameters(member) is not { } other
                    ? true
                    : SameSignature(parameters, other, symbol, member, typeMap, compilation);
                if (conflicts)
                {
                    throw new RefactoringException(
                        ErrorCodes.MemberConflict,
                        $"'{target.ToDisplayString()}' already declares '{member.ToDisplayString()}'.");
                }
            }
        }

        static IReadOnlyList<IParameterSymbol>? Parameters(ISymbol symbol) => symbol switch
        {
            IMethodSymbol method => method.Parameters,
            IPropertySymbol { IsIndexer: true } indexer => indexer.Parameters,
            _ => null
        };

        static bool SameSignature(
            IReadOnlyList<IParameterSymbol> moved,
            IReadOnlyList<IParameterSymbol> existing,
            ISymbol movedSymbol,
            ISymbol existingSymbol,
            Dictionary<ITypeParameterSymbol, ITypeSymbol> typeMap,
            Compilation compilation)
        {
            if (moved.Count != existing.Count ||
                (movedSymbol as IMethodSymbol)?.TypeParameters.Length != (existingSymbol as IMethodSymbol)?.TypeParameters.Length)
            {
                return false;
            }

            var map = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(typeMap, SymbolEqualityComparer.Default);
            if (movedSymbol is IMethodSymbol movedMethod && existingSymbol is IMethodSymbol existingMethod)
            {
                foreach (var (from, to) in movedMethod.TypeParameters.Zip(existingMethod.TypeParameters))
                    map[from] = to;
            }

            return moved.Zip(existing).All(pair =>
                pair.First.RefKind == pair.Second.RefKind &&
                MemberFamily.Substitute(pair.First.Type, map, compilation) is { } translated &&
                SymbolEqualityComparer.Default.Equals(translated, pair.Second.Type));
        }
    }

    private static ISymbol? Overridden(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.OverriddenMethod,
        IPropertySymbol property => property.OverriddenProperty,
        IEventSymbol @event => @event.OverriddenEvent,
        _ => null
    };

    /// <summary>The syntax that moves with a whole declaration, or with the chosen variables of a field.</summary>
    private static IEnumerable<SyntaxNode> MovedNodes(SelectedDeclaration declaration) =>
        declaration.IsWhole
            ? [declaration.Declaration]
            : declaration.Variables.Cast<SyntaxNode>().Append(((BaseFieldDeclarationSyntax)declaration.Declaration).Declaration.Type);

    /// <summary>The parts of a declaration an abstract or interface member repeats: types, parameters, constraints.</summary>
    private static IEnumerable<SyntaxNode> Signature(MemberDeclarationSyntax declaration) => declaration switch
    {
        MethodDeclarationSyntax method => new SyntaxNode?[] { method.ReturnType, method.TypeParameterList, method.ParameterList }.OfType<SyntaxNode>().Concat(method.ConstraintClauses),
        PropertyDeclarationSyntax property => [property.Type],
        IndexerDeclarationSyntax indexer => [indexer.Type, indexer.ParameterList],
        EventDeclarationSyntax @event => [@event.Type],
        EventFieldDeclarationSyntax eventField => [eventField.Declaration.Type],
        _ => [declaration]
    };

    /// <summary>
    /// Refuses <c>base.X</c> in moved code when <c>X</c> is the target's own member (or one it
    /// overrides): in the target, <c>base</c> means the target's base class.
    /// </summary>
    private static void CheckBaseAccess(IEnumerable<SyntaxNode> nodes, INamedTypeSymbol target, SemanticModel model, CancellationToken cancellationToken)
    {
        foreach (var access in nodes.SelectMany(n => n.DescendantNodesAndSelf()).OfType<MemberAccessExpressionSyntax>().Where(a => a.Expression is BaseExpressionSyntax))
        {
            if (model.GetSymbolInfo(access, cancellationToken).Symbol is not { } symbol)
                continue;

            var declaredByTarget = SymbolEqualityComparer.Default.Equals(symbol.ContainingType.OriginalDefinition, target) ||
                target.GetMembers(symbol.Name).Any(m => Overridden(m) is { } overridden && SymbolEqualityComparer.Default.Equals(overridden.OriginalDefinition, symbol.OriginalDefinition));
            if (declaredByTarget)
            {
                throw new RefactoringException(
                    ErrorCodes.MemberNotMoveable,
                    $"'{access}' calls '{target.Name}'s own member; in '{target.Name}', base would mean its base class instead.");
            }
        }
    }

    private static RefactoringException DependsOnDerived(INamedTypeSymbol type, List<ISymbol> dependencies)
    {
        var names = dependencies.Select(d => d.Name).Distinct().ToList();
        return new RefactoringException(
            ErrorCodes.MemberDependsOnDerived,
            $"The members use {string.Join(", ", dependencies.Select(d => $"'{d.ToDisplayString()}'"))}, which stay in '{type.Name}'. " +
            "Pull them up too, or move these members on their own first.",
            new Dictionary<string, object> { ["dependencies"] = names });
    }

    /// <summary>
    /// The usings the target's file needs for the names the moved code uses: the namespaces of
    /// the types and extension methods it names; a <c>using static</c> of the type of a static
    /// member (or nested type) it names without its type, the way a <c>using static</c> or an
    /// enclosing type made it visible; and the aliases it uses. Where such a name would bind to
    /// something else in the target (a member of the target with that name, an alias of that name
    /// for something else), the name is written qualified instead: <paramref name="qualified"/>
    /// receives the replacement of each such name. Fails when the target's project can't see a
    /// type the moved code uses.
    /// </summary>
    private static List<UsingDirectives.NewUsing> NeededUsings(
        IEnumerable<SyntaxNode> nodes,
        INamedTypeSymbol source,
        SemanticModel model,
        SemanticModel targetModel,
        TypeDeclarationSyntax targetDeclaration,
        bool nullable,
        Dictionary<SyntaxNode, SyntaxNode> qualified,
        CancellationToken cancellationToken)
    {
        var position = targetDeclaration.OpenBraceToken.Span.End;
        var needed = new List<UsingDirectives.NewUsing>();
        foreach (var name in nodes.SelectMany(n => n.DescendantNodesAndSelf()).OfType<SimpleNameSyntax>())
        {
            if (name is IdentifierNameSyntax identifier && model.GetAliasInfo(identifier, cancellationToken) is { } alias)
            {
                AliasUsing(identifier, alias);
                continue;
            }

            var info = model.GetSymbolInfo(name, cancellationToken);
            var symbol = info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);
            if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor && name.Parent is AttributeSyntax)
                symbol = constructor.ContainingType; // an attribute's name binds to its constructor

            switch (symbol)
            {
                case INamedTypeSymbol named when !IsQualified(name) && WrittenFor(name, named):
                    TypeUsing(name, named);
                    break;
                case IMethodSymbol { ReducedFrom: { } extension } when name.Parent is MemberAccessExpressionSyntax access && access.Name == name:
                    ExtensionUsing(extension);
                    break;
                case IFieldSymbol or IPropertySymbol or IEventSymbol or IMethodSymbol { MethodKind: MethodKind.Ordinary }
                    when !IsQualified(name) && (symbol.IsStatic || symbol is IFieldSymbol { IsConst: true }) && symbol.ContainingType is { } containing &&
                        !SymbolEqualityComparer.Default.Equals(containing.OriginalDefinition, source.OriginalDefinition):
                    StaticMemberUsing(name, symbol, containing);
                    break;
            }
        }

        return needed;

        // A type named by its simple name: its namespace, or for a nested type a static import of
        // the type it is nested in; qualified where the name means something else in the target.
        void TypeUsing(SimpleNameSyntax name, INamedTypeSymbol named)
        {
            var bound = targetModel.GetSpeculativeSymbolInfo(position, name, SpeculativeBindingOption.BindAsTypeOrNamespace).Symbol;
            if (bound != null && SameSymbol(bound, named))
                return;

            CheckVisible(named);
            if (named.ContainingType is { } outer)
            {
                if (SymbolEqualityComparer.Default.Equals(outer.OriginalDefinition, source.OriginalDefinition))
                    return; // a dependency left behind, refused on its own

                if (bound == null)
                    needed.Add(new UsingDirectives.NewUsing(TypeName(outer), Static: true));
                else
                    qualified[name] = Qualify(name, SyntaxGenerationHelper.CreateTypeSyntax(named, nullable));
                return;
            }

            if (named.ContainingNamespace is { IsGlobalNamespace: false } ns)
                needed.Add(new UsingDirectives.NewUsing(ns.ToDisplayString()));
        }

        // An extension method called in extension form: the namespace of its class.
        void ExtensionUsing(IMethodSymbol extension)
        {
            if (targetModel.LookupNamespacesAndTypes(position, name: extension.ContainingType.Name)
                .Any(s => SymbolEqualityComparer.Default.Equals(s.OriginalDefinition, extension.ContainingType.OriginalDefinition)))
            {
                return;
            }

            CheckVisible(extension.ContainingType);
            var outer = extension.ContainingType;
            while (outer.ContainingType != null)
                outer = outer.ContainingType;
            if (outer.ContainingNamespace is { IsGlobalNamespace: false } ns)
                needed.Add(new UsingDirectives.NewUsing(ns.ToDisplayString()));
        }

        // A static member named without its type (through a using static, or an enclosing type):
        // a using static of its type, or its type written in front of it.
        void StaticMemberUsing(SimpleNameSyntax name, ISymbol member, INamedTypeSymbol containing)
        {
            var bound = targetModel.GetSpeculativeSymbolInfo(position, SyntaxFactory.IdentifierName(name.Identifier), SpeculativeBindingOption.BindAsExpression);
            if (bound.Symbol is { } symbol ? SameSymbol(symbol, member) : bound.CandidateSymbols.Any(c => SameSymbol(c, member)))
                return;

            CheckVisible(containing);
            if (bound.Symbol == null && bound.CandidateSymbols.Length == 0)
            {
                needed.Add(new UsingDirectives.NewUsing(TypeName(containing), Static: true));
                return;
            }

            ExpressionSyntax type = SyntaxGenerationHelper.CreateTypeSyntax(containing, nullable);
            qualified[name] = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.ParseExpression(type.ToString()).WithAdditionalAnnotations(Simplifier.Annotation),
                    name.WithoutTrivia())
                .WithTriviaFrom(name)
                .WithAdditionalAnnotations(Simplifier.Annotation);
        }

        // An alias: nothing when the target has the same one, else the same alias, or what it
        // stands for where its name means something else in the target.
        void AliasUsing(IdentifierNameSyntax name, IAliasSymbol alias)
        {
            var targetAlias = targetModel.GetSpeculativeAliasInfo(position, name, SpeculativeBindingOption.BindAsTypeOrNamespace);
            if (targetAlias != null && SameSymbol(targetAlias.Target, alias.Target))
                return;

            if (alias.Target is ITypeSymbol aliasedType)
                CheckVisible(aliasedType);

            var option = SyntaxFacts.IsInNamespaceOrTypeContext(name) ? SpeculativeBindingOption.BindAsTypeOrNamespace : SpeculativeBindingOption.BindAsExpression;
            var bound = targetModel.GetSpeculativeSymbolInfo(position, name, option);
            if ((targetAlias == null && bound.Symbol == null && bound.CandidateSymbols.Length == 0) || name.Parent is AliasQualifiedNameSyntax)
            {
                var target = alias.Target is ITypeSymbol type ? TypeName(type) : alias.Target.ToDisplayString();
                needed.Add(new UsingDirectives.NewUsing(target, Alias: alias.Name));
                return;
            }

            qualified[name] = Qualify(name, alias.Target is ITypeSymbol typeTarget
                ? SyntaxGenerationHelper.CreateTypeSyntax(typeTarget, nullable)
                : SyntaxFactory.ParseName("global::" + alias.Target.ToDisplayString()).WithAdditionalAnnotations(Simplifier.Annotation));
        }

        void CheckVisible(ITypeSymbol type)
        {
            if (type.ContainingAssembly != null && !SymbolFinder.FindSimilarSymbols(type.OriginalDefinition, targetModel.Compilation, cancellationToken).Any())
            {
                throw new RefactoringException(
                    ErrorCodes.MemberNotMoveable,
                    $"The moved code uses '{type.ToDisplayString()}' from {type.ContainingAssembly.Name}, which '{targetModel.Compilation.AssemblyName}' does not reference.");
            }
        }

        static bool IsQualified(SimpleNameSyntax name) =>
            name.Parent is QualifiedNameSyntax qualified && qualified.Right == name ||
            name.Parent is MemberAccessExpressionSyntax access && access.Name == name ||
            name.Parent is MemberBindingExpressionSyntax ||
            name.Parent is AliasQualifiedNameSyntax;

        // The name of the type itself, or of an attribute class without its Attribute suffix.
        static bool WrittenFor(SimpleNameSyntax name, INamedTypeSymbol type) =>
            name.Identifier.ValueText == type.Name ||
            (name.Identifier.ValueText + "Attribute" == type.Name && name.Ancestors().OfType<AttributeSyntax>().Any());

        // The same symbol, also across compilations (the target may be in another project).
        static bool SameSymbol(ISymbol left, ISymbol right) =>
            SymbolEqualityComparer.Default.Equals(left.OriginalDefinition, right.OriginalDefinition) ||
            (left.Kind == right.Kind &&
                left.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == right.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) &&
                left.OriginalDefinition.GetDocumentationCommentId() == right.OriginalDefinition.GetDocumentationCommentId());

        // A type as a using directive names it: fully qualified, without global::.
        static string TypeName(ITypeSymbol type) =>
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted));

        // What replaces a name: in an expression, member accesses; elsewhere the qualified name.
        static SyntaxNode Qualify(SimpleNameSyntax name, TypeSyntax qualifiedName) =>
            (SyntaxFacts.IsInNamespaceOrTypeContext(name)
                ? (SyntaxNode)SyntaxFactory.ParseTypeName(qualifiedName.ToString())
                : SyntaxFactory.ParseExpression(qualifiedName.ToString()))
            .WithTriviaFrom(name)
            .WithAdditionalAnnotations(Simplifier.Annotation);
    }

    /// <summary>
    /// The declaration with <paramref name="qualified"/>'s names replaced and the types that
    /// mention the type's own type parameters written in the target's terms.
    /// </summary>
    private static MemberDeclarationSyntax Translate(
        MemberDeclarationSyntax declaration,
        INamedTypeSymbol type,
        Dictionary<ITypeParameterSymbol, ITypeSymbol> typeMap,
        Dictionary<SyntaxNode, SyntaxNode> qualified,
        SemanticModel model,
        bool nullable)
    {
        if (type.TypeParameters.Length == 0 && qualified.Count == 0)
            return declaration;

        var translator = new TypeTranslator(model, type, typeMap, qualified, nullable);
        var translated = (MemberDeclarationSyntax)translator.Visit(declaration)!;
        if (translator.Failure != null)
        {
            throw new RefactoringException(
                ErrorCodes.TypeNotExpressible,
                $"'{translator.Failure}' cannot be written in the target: it uses a type parameter of '{type.Name}' that the target has no counterpart for.");
        }

        return translated;
    }

    private static SyntaxTokenList Modifiers(MemberDeclarationSyntax member) => member.Modifiers;

    private static MemberDeclarationSyntax WithModifiers(MemberDeclarationSyntax member, SyntaxTokenList modifiers) =>
        member.WithModifiers(modifiers);

    /// <summary>Drops <c>new</c> when nothing above the target has a member of that name to hide.</summary>
    private static SyntaxTokenList WithoutNew(SyntaxTokenList modifiers, ISymbol symbol, INamedTypeSymbol target)
    {
        if (!modifiers.Any(SyntaxKind.NewKeyword))
            return modifiers;

        for (var type = target.BaseType; type != null; type = type.BaseType)
        {
            if (type.GetMembers(symbol.Name).Length > 0)
                return modifiers;
        }

        return Remove(modifiers, SyntaxKind.NewKeyword);
    }

    private static SyntaxTokenList AccessibilityOnly(SyntaxTokenList modifiers) =>
        SyntaxFactory.TokenList(modifiers.Where(m => SyntaxFacts.IsAccessibilityModifier(m.Kind())).Select(m => m.WithoutTrivia().WithTrailingTrivia(SyntaxFactory.Space)));

    private static SyntaxTokenList Remove(SyntaxTokenList modifiers, SyntaxKind kind)
    {
        var index = modifiers.IndexOf(kind);
        if (index < 0)
            return modifiers;

        var removed = modifiers.RemoveAt(index);
        return index == 0 && removed.Count > 0
            ? removed.Replace(removed[0], removed[0].WithLeadingTrivia(modifiers[0].LeadingTrivia))
            : removed;
    }

    /// <summary>The abstract declaration of a member: its signature, the accessors it has, no body.</summary>
    private static MemberDeclarationSyntax AbstractMember(MemberDeclarationSyntax member, ISymbol symbol, SyntaxTokenList accessibility)
    {
        var modifiers = accessibility.Add(SyntaxFactory.Token(SyntaxKind.AbstractKeyword).WithTrailingTrivia(SyntaxFactory.Space));
        if (symbol.IsOverride)
            modifiers = modifiers.Add(SyntaxFactory.Token(SyntaxKind.OverrideKeyword).WithTrailingTrivia(SyntaxFactory.Space));
        return Bodiless(member, symbol, modifiers, forInterface: false);
    }

    /// <summary>The interface declaration of a member: its signature and public accessors.</summary>
    private static MemberDeclarationSyntax InterfaceMember(MemberDeclarationSyntax member, ISymbol symbol) =>
        Bodiless(member, symbol, default, forInterface: true);

    private static MemberDeclarationSyntax Bodiless(MemberDeclarationSyntax member, ISymbol symbol, SyntaxTokenList modifiers, bool forInterface)
    {
        var semicolon = SyntaxFactory.Token(SyntaxKind.SemicolonToken);
        switch (member)
        {
            case MethodDeclarationSyntax method:
                return method
                    .WithAttributeLists(default)
                    .WithModifiers(modifiers)
                    .WithBody(null)
                    .WithExpressionBody(null)
                    .WithSemicolonToken(semicolon);
            case PropertyDeclarationSyntax property:
                return property
                    .WithAttributeLists(default)
                    .WithModifiers(modifiers)
                    .WithExpressionBody(null)
                    .WithInitializer(null)
                    .WithSemicolonToken(default)
                    .WithAccessorList(Accessors((IPropertySymbol)symbol, forInterface));
            case IndexerDeclarationSyntax indexer:
                return indexer
                    .WithAttributeLists(default)
                    .WithModifiers(modifiers)
                    .WithExpressionBody(null)
                    .WithSemicolonToken(default)
                    .WithAccessorList(Accessors((IPropertySymbol)symbol, forInterface));
            case EventFieldDeclarationSyntax eventField:
                return eventField
                    .WithAttributeLists(default)
                    .WithModifiers(modifiers)
                    .WithDeclaration(eventField.Declaration.WithVariables(SyntaxFactory.SeparatedList(
                        eventField.Declaration.Variables.Select(v => v.WithInitializer(null)),
                        eventField.Declaration.Variables.GetSeparators())));
            case EventDeclarationSyntax @event:
                return SyntaxFactory.EventFieldDeclaration(
                        default,
                        modifiers,
                        SyntaxFactory.Token(SyntaxKind.EventKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                        SyntaxFactory.VariableDeclaration(@event.Type, SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(@event.Identifier.WithoutTrivia()))),
                        semicolon);
            default:
                return member;
        }
    }

    /// <summary>
    /// The accessors an abstract or interface property declares: those that aren't private (for an
    /// interface, public ones), with their own accessibility where it differs (abstract only).
    /// </summary>
    private static AccessorListSyntax Accessors(IPropertySymbol property, bool forInterface)
    {
        var accessors = new List<AccessorDeclarationSyntax>();
        foreach (var accessor in new[] { property.GetMethod, property.SetMethod })
        {
            if (accessor == null || accessor.DeclaredAccessibility == Accessibility.Private ||
                (forInterface && accessor.DeclaredAccessibility != Accessibility.Public))
            {
                continue;
            }

            var kind = accessor.MethodKind == MethodKind.PropertyGet
                ? SyntaxKind.GetAccessorDeclaration
                : accessor.IsInitOnly ? SyntaxKind.InitAccessorDeclaration : SyntaxKind.SetAccessorDeclaration;
            var declaration = SyntaxFactory.AccessorDeclaration(kind).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
            if (!forInterface && accessor.DeclaredAccessibility != property.DeclaredAccessibility)
            {
                declaration = declaration.WithModifiers(SyntaxFactory.TokenList(
                    SyntaxFacts.GetText(accessor.DeclaredAccessibility).Split(' ').Select(t => SyntaxFactory.ParseToken(t).WithTrailingTrivia(SyntaxFactory.Space))));
            }

            accessors.Add(declaration);
        }

        return SyntaxFactory.AccessorList(SyntaxFactory.List(accessors));
    }

    /// <summary>
    /// The modifiers of the implementation that stays as an override: the base's accessibility, no
    /// <c>virtual</c> or <c>new</c>, and <c>override</c> after the accessibility.
    /// </summary>
    private static SyntaxTokenList OverrideModifiers(SyntaxTokenList modifiers, ISymbol symbol)
    {
        var result = Remove(Remove(modifiers, SyntaxKind.VirtualKeyword), SyntaxKind.NewKeyword);
        if (symbol.IsOverride)
            return result;

        var index = 0;
        while (index < result.Count && SyntaxFacts.IsAccessibilityModifier(result[index].Kind()))
            index++;
        return result.Insert(index, SyntaxFactory.Token(SyntaxKind.OverrideKeyword).WithTrailingTrivia(SyntaxFactory.Space));
    }

    /// <summary>The implementation as an override: new modifiers, and no constraint clauses (an override inherits them).</summary>
    private static MemberDeclarationSyntax MakeOverride(MemberDeclarationSyntax member, SyntaxTokenList modifiers)
    {
        var leading = member.GetLeadingTrivia();
        var result = member.WithModifiers(modifiers.Count > 0 ? modifiers.Replace(modifiers[0], modifiers[0].WithLeadingTrivia()) : modifiers);
        if (result is MethodDeclarationSyntax { ConstraintClauses.Count: > 0 } method)
        {
            var close = method.ParameterList.CloseParenToken;
            result = method
                .WithConstraintClauses(default)
                .WithParameterList(method.ParameterList.WithCloseParenToken(close.WithTrailingTrivia(method.ConstraintClauses.Last().GetTrailingTrivia())));
        }

        return result.WithLeadingTrivia(leading);
    }

    /// <summary>
    /// The target with the new members (in a class, fields after its fields; the rest at the end)
    /// and, if needed, <c>abstract</c>. A target written on one line is laid out anew.
    /// </summary>
    private static TypeDeclarationSyntax Insert(TypeDeclarationSyntax target, List<MemberDeclarationSyntax> members, bool makeAbstract)
    {
        var list = target.Members;
        foreach (var member in members)
        {
            if (member is BaseFieldDeclarationSyntax && target is not InterfaceDeclarationSyntax)
            {
                var lastField = list.LastIndexOf(m => m is BaseFieldDeclarationSyntax);
                list = list.Insert(lastField + 1, member);
            }
            else
            {
                list = list.Add(member);
            }
        }

        var result = target.WithMembers(list);
        var text = target.SyntaxTree.GetText();
        if (text.Lines.GetLineFromPosition(target.OpenBraceToken.SpanStart).LineNumber == text.Lines.GetLineFromPosition(target.CloseBraceToken.SpanStart).LineNumber)
            result = result.WithAdditionalAnnotations(Formatter.Annotation);
        if (!makeAbstract)
            return result;

        var @abstract = SyntaxFactory.Token(SyntaxKind.AbstractKeyword).WithTrailingTrivia(SyntaxFactory.Space);
        if (result.Modifiers.Count == 0)
        {
            var keyword = result.Keyword;
            return result
                .WithKeyword(keyword.WithLeadingTrivia())
                .WithModifiers(SyntaxFactory.TokenList(@abstract.WithLeadingTrivia(keyword.LeadingTrivia)));
        }

        var index = 0;
        while (index < result.Modifiers.Count && SyntaxFacts.IsAccessibilityModifier(result.Modifiers[index].Kind()))
            index++;
        return result.WithModifiers(result.Modifiers.Insert(index, @abstract));
    }

    /// <summary>
    /// Refuses to make the base class abstract when something creates an instance of it: a
    /// <c>new</c> expression; the class as the type argument of a type parameter with a
    /// <c>new()</c> constraint, written or (in a file that names the class) inferred; or
    /// <c>Activator.CreateInstance</c> with the class as type argument or <c>typeof</c> argument.
    /// Creation through reflection by name is not seen.
    /// </summary>
    private static async Task CheckNotInstantiatedAsync(INamedTypeSymbol target, Solution solution, CancellationToken cancellationToken)
    {
        var creations = new List<(string Location, string How)>();
        void Add(Location location, string how)
        {
            var span = location.GetLineSpan();
            creations.Add(($"{span.Path}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1})", how));
        }

        foreach (var constructor in target.InstanceConstructors)
        {
            foreach (var location in (await SymbolFinder.FindReferencesAsync(constructor, solution, cancellationToken)).SelectMany(r => r.Locations))
            {
                if (location.IsImplicit || await location.Document.GetSyntaxRootAsync(cancellationToken) is not { } root)
                    continue;

                var node = root.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                if (node.AncestorsAndSelf().FirstOrDefault(n => n is BaseObjectCreationExpressionSyntax or ConstructorInitializerSyntax) is BaseObjectCreationExpressionSyntax)
                    Add(location.Location, "new");
            }
        }

        // Type arguments are looked for in the files that name the class: a type argument is
        // written there, or inferred from something declared with the class's name.
        var documents = (await SymbolFinder.FindReferencesAsync(target, solution, cancellationToken))
            .SelectMany(r => r.Locations)
            .Where(l => !l.IsImplicit)
            .Select(l => l.Document.Id)
            .Distinct()
            .ToList();
        foreach (var id in documents)
        {
            var document = solution.GetDocument(id)!;
            if (await document.GetSyntaxRootAsync(cancellationToken) is not { } root || await document.GetSemanticModelAsync(cancellationToken) is not { } model)
                continue;

            foreach (var node in root.DescendantNodes())
            {
                var (symbol, name) = node switch
                {
                    GenericNameSyntax generic => (model.GetSymbolInfo(generic, cancellationToken).Symbol, generic.Identifier),
                    InvocationExpressionSyntax { Expression: not GenericNameSyntax and not MemberAccessExpressionSyntax { Name: GenericNameSyntax } } invocation =>
                        (model.GetSymbolInfo(invocation, cancellationToken).Symbol, CalledName(invocation)),
                    _ => (null, default)
                };

                if (symbol is IMethodSymbol { Name: "CreateInstance", ContainingType: { Name: "Activator", ContainingNamespace.Name: "System" } } activator)
                {
                    var created = activator.TypeArguments.FirstOrDefault() ??
                        (node is InvocationExpressionSyntax { ArgumentList.Arguments: [var first, ..] } &&
                            model.GetOperation(first.Expression, cancellationToken) is ITypeOfOperation typeOf ? typeOf.TypeOperand : null);
                    if (created != null && IsTarget(created))
                        Add(name.GetLocation(), "Activator.CreateInstance");
                    continue;
                }

                var (parameters, arguments) = symbol switch
                {
                    IMethodSymbol method => (method.TypeParameters, method.TypeArguments),
                    INamedTypeSymbol type => (type.TypeParameters, type.TypeArguments),
                    _ => ([], [])
                };
                foreach (var (parameter, argument) in parameters.Zip(arguments))
                {
                    if (parameter.HasConstructorConstraint && IsTarget(argument))
                        Add(name.GetLocation(), $"the new() constraint of '{parameter.Name}' in '{symbol!.OriginalDefinition.ToDisplayString()}'");
                }
            }
        }

        var distinct = creations.DistinctBy(c => c.Location).ToList();
        if (distinct.Count > 0)
        {
            throw new RefactoringException(
                ErrorCodes.BaseClassInstantiated,
                $"makeAbstract needs '{target.ToDisplayString()}' to be abstract, but it is instantiated at " +
                string.Join(", ", distinct.Take(5).Select(c => $"{c.Location} ({c.How})")) +
                (distinct.Count > 5 ? $" and {distinct.Count - 5} more" : "") + ".",
                new Dictionary<string, object> { ["instantiations"] = distinct.Select(c => c.Location).ToList() });
        }

        // The class, also as another project sees it.
        bool IsTarget(ITypeSymbol type) =>
            SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, target) ||
            (type.ContainingAssembly?.Name == target.ContainingAssembly.Name &&
                type.OriginalDefinition.GetDocumentationCommentId() == target.GetDocumentationCommentId());

        static SyntaxToken CalledName(InvocationExpressionSyntax invocation) => invocation.Expression switch
        {
            MemberAccessExpressionSyntax access => access.Name.Identifier,
            SimpleNameSyntax simple => simple.Identifier,
            _ => invocation.Expression.GetFirstToken()
        };
    }

    /// <summary>Removes the marked declarations and variables from the text, with their comments.</summary>
    private static async Task<(Solution Solution, List<PendingChange> Changes)> RemoveMarkedAsync(
        Solution solution,
        IEnumerable<DocumentId> documents,
        CancellationToken cancellationToken)
    {
        var changes = new List<PendingChange>();
        foreach (var id in documents)
        {
            var document = solution.GetDocument(id)!;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var marked = root?.GetAnnotatedNodes(RemoveAnnotationKind).ToList() ?? [];
            if (marked.Count == 0)
                continue;

            var text = await document.GetTextAsync(cancellationToken);
            foreach (var node in marked)
            {
                var lines = text.Lines.GetLinePositionSpan(DeclarationRemoval.Span(text, node));
                changes.Add(new PendingChange
                {
                    File = document.FilePath ?? document.Name,
                    ChangeType = ChangeKind.Modify,
                    Description = "Remove the moved declaration",
                    BeforeSnippet = text.ToString(TextSpan.FromBounds(node.SpanStart, Math.Min(node.Span.End, text.Lines.GetLineFromPosition(node.SpanStart).End))).Trim(),
                    StartLine = lines.Start.Line + 1,
                    EndLine = lines.End.Character == 0 ? lines.End.Line : lines.End.Line + 1
                });
            }

            solution = solution.WithDocumentText(id, DeclarationRemoval.Remove(text, DeclarationRemoval.Spans(text, marked)));
        }

        return (solution, changes);
    }

    /// <summary>
    /// The documents of the target's other derived types (or implementers) and of the type's own
    /// derived types, whose errors are reported; and notes on derived types whose members would now
    /// hide a moved one.
    /// </summary>
    private static async Task<(List<string> Notes, List<DocumentId> Documents)> RelatedTypesAsync(
        INamedTypeSymbol type,
        INamedTypeSymbol target,
        List<ISymbol> moved,
        bool makeAbstract,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var related = new List<INamedTypeSymbol>();
        if (target.TypeKind == TypeKind.Interface)
            related.AddRange(await SymbolFinder.FindImplementationsAsync(target, solution, transitive: true, cancellationToken: cancellationToken));
        else
            related.AddRange(await SymbolFinder.FindDerivedClassesAsync(target, solution, transitive: true, cancellationToken: cancellationToken));

        var notes = new List<string>();
        foreach (var other in related.Where(r => !SymbolEqualityComparer.Default.Equals(r.OriginalDefinition, type.OriginalDefinition)))
        {
            if (target.TypeKind == TypeKind.Interface || makeAbstract)
                continue;

            foreach (var symbol in moved)
            {
                var same = other.GetMembers(symbol.Name).FirstOrDefault(m => !m.IsImplicitlyDeclared && m.Kind == symbol.Kind && !m.IsOverride);
                if (same != null)
                {
                    notes.Add($"'{same.ToDisplayString()}' has the name of the pulled-up '{symbol.Name}' and now hides it (CS0108); " +
                        "make it 'new', an override, or pull it up as well.");
                }
            }
        }

        var documents = related.Append(type)
            .SelectMany(t => t.DeclaringSyntaxReferences)
            .Select(r => solution.GetDocument(r.SyntaxTree)?.Id)
            .OfType<DocumentId>()
            .Distinct()
            .ToList();
        return (notes, documents);
    }

    private static async Task<List<PendingChange>> CreatePendingChangesAsync(
        Solution annotated,
        DocumentId targetDocument,
        INamedTypeSymbol target,
        bool madeAbstract,
        List<PendingChange> removals,
        List<SelectedDeclaration> selected,
        bool makeAbstract,
        bool isInterface,
        CancellationToken cancellationToken)
    {
        var changes = new List<PendingChange>();
        var document = annotated.GetDocument(targetDocument)!;
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        foreach (var node in root?.GetAnnotatedNodes(InsertedAnnotationKind) ?? [])
        {
            var line = node.GetLocation().GetLineSpan();
            changes.Add(new PendingChange
            {
                File = document.FilePath ?? document.Name,
                ChangeType = ChangeKind.Modify,
                Description = $"Add '{node.GetAnnotations(InsertedAnnotationKind).First().Data}' to '{target.Name}'" +
                    (isInterface ? "" : makeAbstract && node is not FieldDeclarationSyntax ? " as abstract" : ""),
                AfterSnippet = node.ToString().Trim(),
                StartLine = line.StartLinePosition.Line + 1,
                EndLine = line.EndLinePosition.Line + 1
            });
        }

        if (madeAbstract)
        {
            changes.Add(new PendingChange
            {
                File = document.FilePath ?? document.Name,
                ChangeType = ChangeKind.Modify,
                Description = $"Make '{target.Name}' abstract"
            });
        }

        if (makeAbstract)
        {
            foreach (var declaration in selected.Where(s => s.Declaration is not FieldDeclarationSyntax))
            {
                var line = declaration.Declaration.GetLocation().GetLineSpan();
                changes.Add(new PendingChange
                {
                    File = line.Path,
                    ChangeType = ChangeKind.Modify,
                    Description = $"Make '{declaration.Symbols[0].Name}' an override",
                    StartLine = line.StartLinePosition.Line + 1,
                    EndLine = line.StartLinePosition.Line + 1
                });
            }
        }

        changes.AddRange(removals);
        return changes;
    }

    /// <summary>
    /// Rewrites the types in a declaration that mention its type's type parameters into the
    /// target's terms, and replaces the names in <paramref name="qualified"/>; <see cref="Failure"/>
    /// names a type that can't be translated.
    /// </summary>
    private sealed class TypeTranslator(
        SemanticModel model,
        INamedTypeSymbol type,
        Dictionary<ITypeParameterSymbol, ITypeSymbol> map,
        Dictionary<SyntaxNode, SyntaxNode> qualified,
        bool nullable) : CSharpSyntaxRewriter
    {
        public string? Failure { get; private set; }

        public override SyntaxNode? Visit(SyntaxNode? node)
        {
            if (node != null && qualified.TryGetValue(node, out var replacement))
                return replacement;

            // Names only: a nullable, array or tuple type keeps its shape around the translated names.
            if (node is NameSyntax syntax && !syntax.IsVar && IsTypePosition(syntax) &&
                model.GetTypeInfo(syntax).Type is { } bound && Mentions(bound))
            {
                var substituted = MemberFamily.Substitute(bound, map, model.Compilation);
                if (substituted == null)
                {
                    Failure ??= bound.ToDisplayString();
                    return node;
                }

                return SyntaxGenerationHelper.CreateTypeSyntax(substituted, nullable).WithTriviaFrom(node);
            }

            return base.Visit(node);
        }

        /// <summary>Whether a name names a type (rather than a value).</summary>
        private bool IsTypePosition(NameSyntax syntax) => model.GetSymbolInfo(syntax).Symbol is ITypeSymbol;

        private bool Mentions(ITypeSymbol symbol) => symbol switch
        {
            ITypeParameterSymbol parameter => type.TypeParameters.Contains(parameter, SymbolEqualityComparer.Default),
            IArrayTypeSymbol array => Mentions(array.ElementType),
            INamedTypeSymbol named => named.TypeArguments.Any(Mentions) || (named.ContainingType != null && Mentions(named.ContainingType)),
            _ => false
        };
    }
}
