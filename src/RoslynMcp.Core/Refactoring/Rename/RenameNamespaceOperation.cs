using System.Xml;
using System.Xml.Linq;
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
using RoslynMcp.Core.Refactoring.Signature;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Rename;

/// <summary>
/// Renames a namespace declared in source, with its sub-namespaces, across the solution: the
/// namespace declarations and every name that refers to it in code (usings of every kind,
/// qualified names, <c>global::</c> names, documentation-comment crefs).
/// </summary>
/// <remarks>
/// <para>
/// One semantic rewrite handles every rename, whether only the last segment changes or the
/// depth does; Roslyn's Renamer can only do the former and would also edit generated files.
/// Every identifier spelled like the old namespace's last segment is bound; where it names the
/// namespace, the name up to it is rewritten. A fully qualified name gets the new name, a
/// <c>global::</c> one keeps its prefix, and a name that relied on an enclosing namespace or a
/// using outside the renamed one (<c>B.Foo</c> inside <c>namespace A</c> for <c>A.B</c>) is
/// written as <c>global::</c> plus the new name and simplified as far as it still binds.
/// Names relative to a renamed namespace (inside it, or through a using of it) stay as they are.
/// A name inside a renamed declaration that found a namespace or type through an enclosing
/// namespace the declaration leaves (<c>Sibling.Part</c> inside <c>namespace Company.Old</c>
/// for <c>Company.Sibling.Part</c>, renamed to <c>Other.New</c>), or that a type of the new
/// surroundings would shadow, is qualified as far as needed to bind as before. A using directive
/// the rename makes a duplicate of another in its scope (CS0105) or of a global using (CS8933)
/// is removed, and so is an import of an enclosing namespace of the old one that the rename
/// empties; duplicates that were there before stay.
/// </para>
/// <para>
/// A declaration whose enclosing declaration stays (<c>namespace A { namespace B }</c> with
/// <c>A.B</c> becoming <c>A.C</c>) only changes its own part. When the enclosing part would have
/// to change too, the enclosing block is rewritten if it declares nothing else, and the rename is
/// refused otherwise (<see cref="ErrorCodes.NamespaceBlockShared"/>). Also refused: a type that
/// would land next to an existing type of the same name and arity, a new name that is already a
/// type, and a namespace that also holds types from referenced assemblies.
/// </para>
/// <para>
/// In Razor files (<c>.razor</c>, <c>.cshtml</c>) the <c>@using</c> and <c>@namespace</c>
/// directives naming the old namespace or one under it are rewritten as text (see
/// <see cref="RazorNamespaceDirectives"/>); other mentions there are reported in <c>warnings</c>
/// by line, as are the errors the Razor generator's code gets.
/// </para>
/// <para>
/// Generated files (under <c>obj</c>, <c>*.g.cs</c>, an auto-generated header) are never edited.
/// When one still refers to the old namespace, or a project file has a matching
/// <c>&lt;Using Include&gt;</c> or <c>&lt;RootNamespace&gt;</c> (not rewritten either), the result
/// says so in <c>warnings</c>, and the compiler errors that leaves behind are reported too.
/// </para>
/// </remarks>
public sealed class RenameNamespaceOperation : RefactoringOperationBase<RenameNamespaceParams>
{
    /// <summary>
    /// Creates a new rename namespace operation.
    /// </summary>
    public RenameNamespaceOperation(WorkspaceContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void ValidateParams(RenameNamespaceParams @params)
    {
        var oldName = NormalizeName(@params.OldNamespace);
        var newName = NormalizeName(@params.NewNamespace);
        if (oldName.Length == 0)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "oldNamespace is required.");
        if (newName.Length == 0)
            throw new RefactoringException(ErrorCodes.MissingRequiredParam, "newNamespace is required.");

        IdentifierNames.ValidateNamespace(oldName, "oldNamespace");
        IdentifierNames.ValidateNamespace(newName, "newNamespace");

        if (oldName == newName)
            throw new RefactoringException(ErrorCodes.SameLocation, $"The namespace is already named '{newName}'.");
    }

    /// <inheritdoc />
    protected override async Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        RenameNamespaceParams @params,
        CancellationToken cancellationToken)
    {
        var rename = new NamespaceRename(NormalizeName(@params.OldNamespace), NormalizeName(@params.NewNamespace));
        var solution = Context.Solution;

        await CheckTypesAsync(rename, solution, cancellationToken);
        var scan = await ScanAsync(rename, solution, cancellationToken);
        var razor = await RazorNamespaceDirectives.PlanAsync(solution, rename.OldSegments, rename.Covers, rename.Map, cancellationToken);
        if (!scan.Files.Any(f => f.Declarations > 0) && !razor.Files.Any(f => f.NamespaceDirectives > 0))
        {
            throw new RefactoringException(
                ErrorCodes.SymbolNotFound,
                scan.Generated.Count > 0
                    ? $"Namespace '{rename.Old}' is declared only in generated files, which are not renamed."
                    : $"Namespace '{rename.Old}' is not declared in a source file of the loaded workspace.");
        }

        var newSolution = await ApplyAsync(solution, scan, cancellationToken);
        (newSolution, var usingsRemoved, var vanished) = await RemoveRedundantUsingsAsync(rename, solution, newSolution, scan, cancellationToken);
        newSolution = RazorNamespaceDirectives.Apply(newSolution, razor);

        var warnings = ProjectFileWarnings(rename, solution, out var projectsWithUsings);
        warnings.AddRange(scan.Generated.Select(path =>
            $"'{path}' is generated and was not changed; it still refers to namespace '{rename.Old}'."));
        warnings.AddRange(razor.Mentions.Select(line =>
            $"{line}: still names namespace '{rename.Old}'; in Razor files only @using and @namespace directives are rewritten."));

        var plans = new List<PlannedMove>();
        if (@params.UpdateFolders)
            warnings.AddRange(PlanMoves(scan, solution, plans));

        var affected = new HashSet<DocumentId>(scan.Files.SelectMany(f => DocumentIds(solution, f.Document)));
        affected.UnionWith(ChangedDocuments(solution, newSolution));
        affected.UnionWith(await DocumentsMentioningAsync(newSolution, vanished, cancellationToken));
        foreach (var projectId in scan.ProjectsWithGenerated.Concat(projectsWithUsings))
            affected.UnionWith(solution.GetProject(projectId)?.DocumentIds ?? []);
        warnings.AddRange(await IntroducedErrors.FindAsync(solution, newSolution, affected, cancellationToken) ?? []);
        warnings.AddRange(await IntroducedErrors.FindInGeneratedAsync(
            solution, newSolution, razor.Projects, RazorNamespaceDirectives.IsRazorFile, cancellationToken) ?? []);

        var references = scan.Files.Sum(f => f.References) + razor.Files.Sum(f => f.Directives);
        if (@params.Preview)
        {
            await PreviewCommitAsync(newSolution, cancellationToken);
            return new RefactoringResult
            {
                Success = true,
                OperationId = operationId,
                Preview = true,
                PendingChanges = await CreatePendingChangesAsync(rename, scan, razor, solution, newSolution, plans, cancellationToken),
                ReferencesUpdated = references,
                UsingDirectivesRemoved = usingsRemoved,
                Warnings = warnings.Count == 0 ? null : warnings
            };
        }

        var commit = await CommitChangesAsync(newSolution, cancellationToken);
        var filesModified = commit.FilesModified.Distinct(PathResolver.PathComparer).ToList();
        var filesCreated = new List<string>();
        var filesDeleted = new List<string>();
        RefactoringError? partial = null;

        if (plans.Count > 0)
        {
            var problems = new List<string>();
            var emptied = new List<(string Directory, string ProjectDirectory)>();
            var outcomes = await NamespaceFolderMove.MoveAsync(Context, plans, cancellationToken);
            for (var i = 0; i < outcomes.Count; i++)
            {
                var outcome = outcomes[i];
                if (outcome.Problem != null)
                    problems.Add(outcome.Problem);
                if (outcome.MovedTo == null)
                    continue;

                filesModified.RemoveAll(f => PathResolver.PathsEqual(f, outcome.SourcePath));
                filesCreated.Add(outcome.MovedTo);
                filesDeleted.Add(outcome.SourcePath);
                emptied.Add((Path.GetDirectoryName(outcome.SourcePath)!, Path.GetDirectoryName(plans[i].ProjectFile)!));

                // The compiler errors were found before the move; name the file where it is now.
                var movedFrom = outcome.SourcePath + "(";
                for (var w = 0; w < warnings.Count; w++)
                {
                    if (warnings[w].StartsWith(movedFrom, StringComparison.OrdinalIgnoreCase))
                        warnings[w] = outcome.MovedTo + warnings[w][outcome.SourcePath.Length..];
                }
            }

            DeleteEmptiedFolders(emptied);
            if (problems.Count > 0)
                partial = RefactoringError.Create("PARTIAL_SUCCESS", string.Join(" ", problems));
        }

        return new RefactoringResult
        {
            Success = true,
            OperationId = operationId,
            Changes = new FileChanges
            {
                FilesModified = filesModified,
                FilesCreated = filesCreated,
                FilesDeleted = filesDeleted
            },
            Symbol = new Contracts.Models.SymbolInfo
            {
                Name = rename.NewSegments[^1],
                FullyQualifiedName = rename.New,
                Kind = Contracts.Enums.SymbolKind.Namespace,
                PreviousNamespace = rename.Old,
                NewNamespace = rename.New
            },
            ReferencesUpdated = references,
            UsingDirectivesRemoved = usingsRemoved,
            Error = partial,
            Warnings = warnings.Count == 0 ? null : warnings
        };
    }

    private static string NormalizeName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        return trimmed.StartsWith("global::", StringComparison.Ordinal) ? trimmed["global::".Length..].Trim() : trimmed;
    }

    /// <summary>
    /// Refuses a rename that would put two types of one name and arity into a namespace, turn a
    /// type's name into a namespace's, or leave types of a referenced assembly behind.
    /// </summary>
    private static async Task CheckTypesAsync(NamespaceRename rename, Solution solution, CancellationToken cancellationToken)
    {
        var solutionAssemblies = solution.Projects.Select(p => p.AssemblyName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var collisions = new SortedSet<string>(StringComparer.Ordinal);
        var external = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var project in solution.Projects)
        {
            if (await project.GetCompilationAsync(cancellationToken) is not { } compilation)
                continue;

            if (FindNamespace(compilation.GlobalNamespace, rename.New) != null)
                rename.NewExists = true;

            if (FindNamespace(compilation.GlobalNamespace, rename.Old) is { } oldNamespace)
            {
                foreach (var ns in Namespaces(oldNamespace))
                {
                    var newName = rename.Map(ns.ToDisplayString());
                    var target = FindNamespace(compilation.GlobalNamespace, newName);
                    foreach (var type in ns.GetTypeMembers())
                    {
                        if (!solutionAssemblies.Contains(type.ContainingAssembly.Name))
                        {
                            external.Add($"'{type.ToDisplayString()}' ({type.ContainingAssembly.Name})");
                            continue;
                        }

                        foreach (var existing in target?.GetTypeMembers(type.Name, type.Arity) ?? [])
                        {
                            if (!rename.Covers(existing.ContainingNamespace.ToDisplayString()))
                                collisions.Add($"'{type.ToDisplayString()}' would collide with the existing '{existing.ToDisplayString()}'");
                        }
                    }

                    AddTypesNamed(compilation, newName);
                }
            }

            var segments = rename.NewSegments;
            for (var length = 1; length < segments.Length; length++)
                AddTypesNamed(compilation, string.Join('.', segments.Take(length)));
        }

        if (external.Count > 0)
        {
            throw new RefactoringException(
                ErrorCodes.NamespaceInMetadata,
                $"Namespace '{rename.Old}' also holds types of referenced assemblies, which cannot be renamed: " +
                string.Join(", ", external.Take(5)) + (external.Count > 5 ? $" and {external.Count - 5} more" : "") + ".");
        }

        if (collisions.Count > 0)
        {
            throw new RefactoringException(
                ErrorCodes.NameCollision,
                $"Renaming '{rename.Old}' to '{rename.New}' would cause name collisions: " +
                string.Join("; ", collisions.Take(5)) + (collisions.Count > 5 ? $"; and {collisions.Count - 5} more" : "") + ".",
                suggestions: ["Rename or move the colliding types first", "Choose a different namespace"]);
        }

        void AddTypesNamed(Compilation compilation, string fullName)
        {
            var dot = fullName.LastIndexOf('.');
            var parent = dot < 0 ? compilation.GlobalNamespace : FindNamespace(compilation.GlobalNamespace, fullName[..dot]);
            foreach (var type in parent?.GetTypeMembers(fullName[(dot + 1)..]) ?? [])
            {
                if (!rename.Covers(type.ContainingNamespace.ToDisplayString()))
                    collisions.Add($"namespace '{fullName}' would collide with the type '{type.ToDisplayString()}'");
            }
        }
    }

    private static INamespaceSymbol? FindNamespace(INamespaceSymbol global, string name)
    {
        var current = global;
        foreach (var segment in name.Split('.'))
        {
            current = current.GetNamespaceMembers().FirstOrDefault(n => n.Name == segment);
            if (current == null)
                return null;
        }

        return current;
    }

    private static IEnumerable<INamespaceSymbol> Namespaces(INamespaceSymbol root) =>
        new[] { root }.Concat(root.GetNamespaceMembers().SelectMany(Namespaces));

    /// <summary>
    /// Finds what to rewrite in every file that spells the old namespace's last segment. A file
    /// compiled by several projects (one per target framework) is read through each of its
    /// documents, so a name in an <c>#if</c> branch that only one of them compiles is found too.
    /// Generated files are only inspected.
    /// </summary>
    private static async Task<Scan> ScanAsync(NamespaceRename rename, Solution solution, CancellationToken cancellationToken)
    {
        var scan = new Scan();
        foreach (var documents in DocumentsByFile(solution))
        {
            var first = documents[0];
            var text = await first.GetTextAsync(cancellationToken);
            if (text.ToString().IndexOf(rename.LastSegment, StringComparison.Ordinal) < 0 ||
                await first.GetSyntaxRootAsync(cancellationToken) is not CompilationUnitSyntax firstRoot)
            {
                continue;
            }

            var generated = GeneratedCode.IsGenerated(firstRoot.SyntaxTree, cancellationToken);
            var file = new FileRewrite(first, firstRoot);
            foreach (var document in documents)
            {
                if (await document.GetSyntaxRootAsync(cancellationToken) is not CompilationUnitSyntax root)
                    continue;

                var version = new DocumentRewrite(document, root);
                CollectDeclarations(rename, file, version, editable: !generated);
                await CollectReferencesAsync(rename, file, version, cancellationToken);
                if (rename.ChangesLookup && version.RenamedBlocks.Count > 0)
                    await CollectEnclosedNamesAsync(rename, version, cancellationToken);
                file.Versions.Add(version);
            }

            if (file.Declarations + file.References == 0)
                continue;

            if (generated)
            {
                scan.Generated.Add(first.FilePath ?? first.Name);
                scan.ProjectsWithGenerated.UnionWith(documents.Select(d => d.Project.Id));
                continue;
            }

            DescribeContent(rename, file);
            scan.Files.Add(file);
        }

        return scan;
    }

    /// <summary>
    /// The documents of the solution grouped by file: the documents of a file that several
    /// projects compile (one per target framework, a linked file) form one group.
    /// </summary>
    private static List<List<Document>> DocumentsByFile(Solution solution)
    {
        var groups = new List<List<Document>>();
        var byPath = new Dictionary<string, List<Document>>(PathResolver.PathComparer);
        foreach (var document in solution.Projects.SelectMany(p => p.Documents))
        {
            if (document.FilePath == null)
            {
                groups.Add([document]);
                continue;
            }

            var path = PathResolver.NormalizePath(document.FilePath);
            if (!byPath.TryGetValue(path, out var group))
            {
                byPath[path] = group = [];
                groups.Add(group);
            }

            group.Add(document);
        }

        return groups;
    }

    /// <summary>
    /// Records the declarations to rewrite: those of the renamed namespaces whose enclosing
    /// declaration is not renamed itself (nested ones are relative to it and stay).
    /// </summary>
    private static void CollectDeclarations(NamespaceRename rename, FileRewrite file, DocumentRewrite version, bool editable)
    {
        foreach (var declaration in version.Root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
        {
            if (!rename.Covers(FullName(declaration)) ||
                (declaration.Parent is BaseNamespaceDeclarationSyntax parent && rename.Covers(FullName(parent))))
            {
                continue;
            }

            file.DeclarationSpans.Add(declaration.Name.Span);
            if (editable)
                RewriteDeclaration(rename, version, declaration);
        }
    }

    /// <summary>
    /// Gives <paramref name="declaration"/> its new name relative to its enclosing declaration.
    /// When the enclosing declaration's name is not a prefix of the new name (or leaves nothing
    /// for this one), enclosing blocks that declare nothing else are renamed too, the new name's
    /// segments spread over the chain.
    /// </summary>
    private static void RewriteDeclaration(NamespaceRename rename, DocumentRewrite version, BaseNamespaceDeclarationSyntax declaration)
    {
        var newName = rename.Map(FullName(declaration)).Split('.');
        var chain = new List<BaseNamespaceDeclarationSyntax> { declaration };
        var outer = declaration.Parent as BaseNamespaceDeclarationSyntax;
        string[] prefix;
        while (true)
        {
            prefix = outer == null ? [] : FullName(outer).Split('.');
            if (newName.Take(prefix.Length).SequenceEqual(prefix) && newName.Length - prefix.Length >= chain.Count)
                break;

            if (outer == null)
            {
                var block = chain[^1];
                throw new RefactoringException(
                    ErrorCodes.NamespaceBlockShared,
                    $"Renaming '{rename.Old}' to '{rename.New}' would merge the nested namespace blocks at " +
                    $"{Where(version, block)} into one; merge them by hand first.");
            }

            if (outer.Members.Count != 1)
            {
                var others = outer.Members.Where(m => m != chain[^1]).Select(Describe).Take(3);
                throw new RefactoringException(
                    ErrorCodes.NamespaceBlockShared,
                    $"Renaming '{rename.Old}' to '{rename.New}' would have to rename the enclosing block " +
                    $"'namespace {outer.Name}' at {Where(version, outer)}, which also declares {string.Join(", ", others)}. " +
                    $"Move those out of the block, or choose a new name starting with '{FullName(outer)}.'.");
            }

            chain.Add(outer);
            outer = outer.Parent as BaseNamespaceDeclarationSyntax;
        }

        // Inner declarations keep as many segments as they had, as far as the outer ones still get one each.
        var remaining = newName[prefix.Length..];
        for (var i = 0; i < chain.Count; i++)
        {
            var count = i == chain.Count - 1
                ? remaining.Length
                : Math.Min(chain[i].Name.ToString().Split('.').Length, remaining.Length - (chain.Count - 1 - i));
            var segments = remaining[^count..];
            remaining = remaining[..^count];
            version.Replacements[chain[i].Name] = SyntaxFactory.ParseName(string.Join('.', segments)).WithTriviaFrom(chain[i].Name);
        }

        version.RenamedBlocks.Add(chain[^1]);
    }

    private static string Describe(MemberDeclarationSyntax member) => member switch
    {
        BaseNamespaceDeclarationSyntax ns => $"namespace '{ns.Name}'",
        BaseTypeDeclarationSyntax type => $"'{type.Identifier.ValueText}'",
        DelegateDeclarationSyntax @delegate => $"'{@delegate.Identifier.ValueText}'",
        _ => "other members"
    };

    private static string Where(DocumentRewrite version, SyntaxNode node) =>
        $"{version.Document.FilePath}({node.GetLocation().GetLineSpan().StartLinePosition.Line + 1})";

    /// <summary>The full name a namespace declaration declares, from its enclosing declarations.</summary>
    private static string FullName(BaseNamespaceDeclarationSyntax declaration) =>
        string.Join('.', declaration.AncestorsAndSelf().OfType<BaseNamespaceDeclarationSyntax>().Reverse()
            .Select(d => string.Join('.', d.Name.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.ValueText))));

    /// <summary>
    /// Records every name that refers to the old namespace itself, from the identifier spelling
    /// its last segment, in code and in documentation comments.
    /// </summary>
    private static async Task CollectReferencesAsync(NamespaceRename rename, FileRewrite file, DocumentRewrite version, CancellationToken cancellationToken)
    {
        SemanticModel? model = null;
        foreach (var token in version.Root.DescendantTokens(descendIntoTrivia: true))
        {
            if (!token.IsKind(SyntaxKind.IdentifierToken) || token.ValueText != rename.LastSegment ||
                token.Parent is not IdentifierNameSyntax identifier ||
                identifier.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Any(ns => ns.Name.Span.Contains(identifier.Span)))
            {
                continue;
            }

            model ??= await version.Document.GetSemanticModelAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not analyze file.");

            var name = NameEndingWith(identifier);
            if (model.GetSymbolInfo(name, cancellationToken).Symbol is not INamespaceSymbol ns || ns.ToDisplayString() != rename.Old)
                continue;

            if (Replacement(rename, name, model, cancellationToken) is { } replacement)
            {
                version.Replacements[name] = replacement;
                version.NeedsSimplification |= replacement.HasAnnotation(Simplifier.Annotation);
                file.ReferenceSpans.Add(name.Span);
            }
        }
    }

    /// <summary>The name or member access <paramref name="identifier"/> ends (<c>A.B</c> for <c>B</c>).</summary>
    private static ExpressionSyntax NameEndingWith(IdentifierNameSyntax identifier) => identifier.Parent switch
    {
        QualifiedNameSyntax qualified when qualified.Right == identifier => qualified,
        MemberAccessExpressionSyntax access when access.Name == identifier => access,
        AliasQualifiedNameSyntax alias when alias.Name == identifier => alias,
        _ => identifier
    };

    /// <summary>
    /// What a name referring to the old namespace becomes, or null when it stays: relative to an
    /// alias of a renamed namespace (the alias is rewritten), or qualified by an extern alias.
    /// </summary>
    private static SyntaxNode? Replacement(NamespaceRename rename, ExpressionSyntax name, SemanticModel model, CancellationToken cancellationToken)
    {
        var leftmost = name;
        while (true)
        {
            if (leftmost is QualifiedNameSyntax qualified)
                leftmost = qualified.Left;
            else if (leftmost is MemberAccessExpressionSyntax access)
                leftmost = access.Expression;
            else
                break;
        }

        if (leftmost is AliasQualifiedNameSyntax aliasQualified)
        {
            return aliasQualified.Alias.Identifier.IsKind(SyntaxKind.GlobalKeyword)
                ? Write("global::" + rename.New, name, simplify: false)
                : null;
        }

        if (leftmost is IdentifierNameSyntax first && model.GetAliasInfo(first, cancellationToken) is { } alias)
        {
            return alias.Target is INamespaceSymbol target && rename.Covers(target.ToDisplayString())
                ? null
                : Write("global::" + rename.New, name, simplify: true);
        }

        var written = name.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.ValueText);
        return written.SequenceEqual(rename.OldSegments)
            ? Write(rename.New, name, simplify: false)
            : Write("global::" + rename.New, name, simplify: true);
    }

    private static SyntaxNode Write(string text, ExpressionSyntax original, bool simplify)
    {
        SyntaxNode written = original is MemberAccessExpressionSyntax ||
            (original.Parent is MemberAccessExpressionSyntax access && access.Expression == original)
                ? SyntaxFactory.ParseExpression(text)
                : SyntaxFactory.ParseName(text);
        written = written.WithTriviaFrom(original);
        return simplify ? written.WithAdditionalAnnotations(Simplifier.Annotation) : written;
    }

    private const string EnclosedNameKind = "RoslynMcp.RenameNamespace.EnclosedName";

    /// <summary>
    /// Records the simple names inside the renamed declarations that bind to a namespace or a
    /// top-level type, with what they bind to. Inside <c>namespace Company.Old</c>,
    /// <c>Sibling.Part</c> and <c>Helper</c> find <c>Company.Sibling</c> and <c>Company.Helper</c>
    /// through the enclosing namespace; renamed to <c>Other.New</c>, they would find nothing, or
    /// an <c>Other.Helper</c>. <see cref="QualifyEnclosedNamesAsync"/> qualifies those that no
    /// longer bind to the same symbol. Names the rewrite replaces, and the parts of a name right of
    /// its first, are left out.
    /// </summary>
    private static async Task CollectEnclosedNamesAsync(NamespaceRename rename, DocumentRewrite version, CancellationToken cancellationToken)
    {
        var model = await version.Document.GetSemanticModelAsync(cancellationToken)
            ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not analyze file.");

        foreach (var block in version.RenamedBlocks)
        {
            foreach (var name in block.DescendantNodes(descendIntoTrivia: true).OfType<SimpleNameSyntax>())
            {
                if (!StartsAName(name) ||
                    name.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Any(ns => ns.Name.Span.Contains(name.Span)) ||
                    name.AncestorsAndSelf().Any(version.Replacements.ContainsKey) ||
                    BoundNamespaceOrType(model, name, cancellationToken) is not { } symbol)
                {
                    continue;
                }

                var container = symbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? rename.MapAny(ns.ToDisplayString()) : null;
                version.EnclosedNames[name] = new EnclosedName(
                    new SyntaxAnnotation(EnclosedNameKind, version.EnclosedNames.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    NamespaceOrTypeKey(symbol, rename.MapAny),
                    container);
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="name"/> is looked up in the enclosing scopes: it is not the part of
    /// a qualified name, member access or cref after a dot, nor a name being declared or named.
    /// </summary>
    private static bool StartsAName(SimpleNameSyntax name) => name.Parent switch
    {
        QualifiedNameSyntax qualified => qualified.Left == name,
        MemberAccessExpressionSyntax access => access.Expression == name,
        AliasQualifiedNameSyntax => false,
        MemberBindingExpressionSyntax or NameEqualsSyntax or NameColonSyntax => false,
        NameMemberCrefSyntax member => member.Parent is not QualifiedCrefSyntax,
        _ => true
    };

    /// <summary>
    /// The namespace or top-level type <paramref name="name"/> binds to, or null: an alias, a
    /// nested type (found through its containing type, which the rename does not change), a
    /// keyword-like name (<c>var</c>, <c>nint</c>) or anything else.
    /// </summary>
    private static ISymbol? BoundNamespaceOrType(SemanticModel model, SimpleNameSyntax name, CancellationToken cancellationToken)
    {
        if (name.IsVar || model.GetAliasInfo(name, cancellationToken) != null)
            return null;

        var text = name.Identifier.ValueText;
        var inAttribute = name.Parent is AttributeSyntax;
        var symbol = model.GetSymbolInfo(name, cancellationToken).Symbol;
        if (inAttribute && symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor)
            symbol = constructor.ContainingType;

        return symbol switch
        {
            INamespaceSymbol { IsGlobalNamespace: false } ns when ns.Name == text => ns,
            INamedTypeSymbol { ContainingType: null, TypeKind: not TypeKind.Error } type
                when type.Name == text || (inAttribute && type.Name == text + "Attribute") => type.OriginalDefinition,
            _ => null
        };
    }

    /// <summary>
    /// Identifies a namespace or top-level type across the rename: <paramref name="map"/> gives a
    /// namespace its name after the rename (the identity for a symbol of the renamed solution).
    /// </summary>
    private static string NamespaceOrTypeKey(ISymbol symbol, Func<string, string> map) => symbol switch
    {
        INamespaceSymbol ns => map(ns.ToDisplayString()),
        _ => $"{(symbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? map(ns.ToDisplayString()) : "")}+{symbol.MetadataName}|{symbol.ContainingAssembly?.Name}"
    };

    /// <summary>
    /// Qualifies the names <see cref="CollectEnclosedNamesAsync"/> recorded that bind to
    /// something else (or nothing) in <paramref name="solution"/>, where every file has its new
    /// names: <c>global::</c> plus the namespace of what they bound to, marked for simplification
    /// (<c>Sibling.Part</c> becomes <c>Company.Sibling.Part</c>).
    /// </summary>
    private static async Task<Solution> QualifyEnclosedNamesAsync(Solution solution, IEnumerable<DocumentRewrite> versions, CancellationToken cancellationToken)
    {
        var result = solution;
        foreach (var version in versions.Where(v => v.EnclosedNames.Count > 0))
        {
            var document = result.GetDocument(version.Document.Id)!;
            if (await document.GetSyntaxRootAsync(cancellationToken) is not { } root ||
                await document.GetSemanticModelAsync(cancellationToken) is not { } model)
            {
                continue;
            }

            var recorded = version.EnclosedNames.Values.ToDictionary(n => n.Annotation.Data!, StringComparer.Ordinal);
            var fixes = new Dictionary<SyntaxNode, EnclosedName>();
            foreach (var node in root.GetAnnotatedNodes(EnclosedNameKind))
            {
                var name = recorded[node.GetAnnotations(EnclosedNameKind).First().Data!];
                if (node is not SimpleNameSyntax simple ||
                    BoundNamespaceOrType(model, simple, cancellationToken) is not { } now ||
                    NamespaceOrTypeKey(now, ns => ns) != name.Key)
                {
                    fixes[node] = name;
                }
            }

            if (fixes.Count == 0)
                continue;

            root = root.ReplaceNodes(fixes.Keys, (original, rewritten) =>
                Qualify(fixes[original].Container, (SimpleNameSyntax)original, (SimpleNameSyntax)rewritten));
            result = result.WithDocumentSyntaxRoot(version.Document.Id, root);
            version.NeedsSimplification = true;
        }

        return result;
    }

    /// <summary>
    /// <paramref name="rewritten"/> (<paramref name="original"/> with its own fixes) qualified by
    /// <c>global::</c> and <paramref name="container"/>, as a name or as a member access,
    /// whichever <paramref name="original"/>'s place takes.
    /// </summary>
    private static ExpressionSyntax Qualify(string? container, SimpleNameSyntax original, SimpleNameSyntax rewritten)
    {
        var simple = rewritten.WithoutTrivia();
        ExpressionSyntax qualified;
        if (container == null)
        {
            qualified = SyntaxFactory.AliasQualifiedName(SyntaxFactory.IdentifierName(SyntaxFactory.Token(SyntaxKind.GlobalKeyword)), simple);
        }
        else if (original.Parent is UsingDirectiveSyntax or QualifiedNameSyntax or AttributeSyntax ||
                 original.Ancestors().Any(a => a is CrefSyntax) ||
                 SyntaxFacts.IsInNamespaceOrTypeContext(original))
        {
            qualified = SyntaxFactory.QualifiedName(SyntaxFactory.ParseName("global::" + container), simple);
        }
        else
        {
            qualified = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.ParseExpression("global::" + container), simple);
        }

        return qualified.WithTriviaFrom(original).WithAdditionalAnnotations(Simplifier.Annotation);
    }

    /// <summary>
    /// The one namespace the types of the file end up in, for the folder move, or why the file
    /// has no such namespace.
    /// </summary>
    private static void DescribeContent(NamespaceRename rename, FileRewrite file)
    {
        var name = Path.GetFileName(file.Document.FilePath);
        if (file.Root.Members.OfType<GlobalStatementSyntax>().Any())
        {
            file.ContentReason = $"'{name}' has top-level statements";
            return;
        }

        var namespaces = file.Root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .Where(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
            .Select(n => n.Parent is BaseNamespaceDeclarationSyntax ns ? FullName(ns) : "")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (namespaces.Count == 0)
            file.ContentReason = $"'{name}' declares no types";
        else if (!namespaces.All(rename.Covers))
            file.ContentReason = $"'{name}' also declares types outside '{rename.Old}'";
        else if (namespaces.Count > 1)
            file.ContentReason = $"'{name}' declares types in several namespaces";
        else
        {
            file.OldNamespace = namespaces[0];
            file.TargetNamespace = rename.Map(namespaces[0]);
        }
    }

    /// <summary>
    /// Applies the rewrites of every document, qualifies the names inside renamed declarations
    /// that would bind to something else (<see cref="QualifyEnclosedNamesAsync"/>), simplifies
    /// the names marked for it once every file has its new names, and gives the documents
    /// compiling one file one text with the rewrites of all of them
    /// (<see cref="LinkedDocuments.MergeRenamesAsync"/>: each line from the document that changed it).
    /// </summary>
    private static async Task<Solution> ApplyAsync(Solution solution, Scan scan, CancellationToken cancellationToken)
    {
        var result = solution;
        var versions = scan.Files.SelectMany(f => f.Versions).Where(v => v.Replacements.Count > 0).ToList();
        foreach (var version in versions)
        {
            // The names to check afterwards are marked, to be found in the rewritten tree.
            var root = version.Root.ReplaceNodes(
                version.Replacements.Keys.Concat(version.EnclosedNames.Keys),
                (original, rewritten) => version.Replacements.TryGetValue(original, out var replacement)
                    ? replacement
                    : rewritten.WithAdditionalAnnotations(version.EnclosedNames[original].Annotation));
            result = result.WithDocumentSyntaxRoot(version.Document.Id, root);
        }

        result = await QualifyEnclosedNamesAsync(result, versions, cancellationToken);

        foreach (var version in versions.Where(v => v.NeedsSimplification))
        {
            var document = await Simplifier.ReduceAsync(
                result.GetDocument(version.Document.Id)!, Simplifier.Annotation, cancellationToken: cancellationToken);
            result = document.Project.Solution;
        }

        result = await LinkedDocuments.MergeRenamesAsync(solution, result, preferred: null, cancellationToken);

        // The merge leaves a file alone whose versions differ in their number of lines, which
        // the rewrite never changes; should it happen anyway, the file gets its first version.
        foreach (var file in scan.Files.Where(f => f.Versions.Count > 1))
        {
            var text = await result.GetDocument(file.Versions[0].Document.Id)!.GetTextAsync(cancellationToken);
            foreach (var version in file.Versions.Skip(1))
            {
                if (!(await result.GetDocument(version.Document.Id)!.GetTextAsync(cancellationToken)).ContentEquals(text))
                    result = result.WithDocumentText(version.Document.Id, text);
            }
        }

        return result;
    }

    private static IEnumerable<DocumentId> DocumentIds(Solution solution, Document document) =>
        document.FilePath == null ? [document.Id] : solution.GetDocumentIdsWithFilePath(document.FilePath);

    /// <summary>The documents whose text differs between the two solutions.</summary>
    private static IEnumerable<DocumentId> ChangedDocuments(Solution solution, Solution newSolution) =>
        newSolution.GetChanges(solution).GetProjectChanges()
            .SelectMany(p => p.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true));

    /// <summary>
    /// Removes the using directives the rewrite made redundant or broke: one that now duplicates
    /// another in its scope (CS0105, a warning) or a global using of its project (CS8933), and an
    /// import of an enclosing namespace of the old one that the rename emptied, which no longer
    /// exists (CS0246; see <see cref="StaleUsingDirectives"/>). Duplicates there were before stay.
    /// Returns the namespaces that vanished, whose remaining imports are checked for errors.
    /// </summary>
    private static async Task<(Solution Solution, int Removed, List<string> Vanished)> RemoveRedundantUsingsAsync(
        NamespaceRename rename,
        Solution solution,
        Solution newSolution,
        Scan scan,
        CancellationToken cancellationToken)
    {
        var documents = new List<DocumentId>(ChangedDocuments(solution, newSolution));

        // A global using renamed into one its project has already makes the other files' plain
        // usings of that namespace redundant too.
        var newSegment = rename.NewSegments[^1];
        var projectsWithRenamedGlobalUsings = scan.Files.SelectMany(f => f.Versions)
            .Where(v => v.Replacements.Keys.Any(k => k.FirstAncestorOrSelf<UsingDirectiveSyntax>() is { } u &&
                u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)))
            .Select(v => v.Document.Project.Id)
            .ToHashSet();
        foreach (var project in projectsWithRenamedGlobalUsings.Select(newSolution.GetProject).OfType<Project>())
        {
            foreach (var document in project.Documents)
            {
                var text = (await document.GetTextAsync(cancellationToken)).ToString();
                if (text.Contains("using", StringComparison.Ordinal) && text.Contains(newSegment, StringComparison.Ordinal))
                    documents.Add(document.Id);
            }
        }

        var (result, removed) = await NewlyReportedUsings.RemoveAsync(
            solution,
            newSolution,
            documents,
            [NewlyReportedUsings.DuplicateUsing, NewlyReportedUsings.DuplicateOfGlobalUsing],
            includeGlobal: true,
            cancellationToken);
        var count = removed.Count;
        var vanished = new List<string>();

        // An enclosing namespace of the old one that held nothing else is gone with it.
        var declaring = scan.Files.Where(f => f.Declarations > 0).SelectMany(f => f.Versions).Select(v => v.Document.Project.Id).ToHashSet();
        for (var length = rename.OldSegments.Length - 1; length > 0; length--)
        {
            var parent = string.Join('.', rename.OldSegments.Take(length));
            if (!await VanishesAsync(parent, declaring, solution, result, cancellationToken))
                break;

            var stale = await StaleUsingDirectives.FixAsync(solution, result, parent, _ => null, cancellationToken);
            result = stale.Solution;
            count += stale.Removed;
            vanished.Add(parent);
        }

        return (result, count, vanished);
    }

    /// <summary>
    /// The documents with a using directive that may name one of <paramref name="namespaces"/>:
    /// those the cleanup leaves (aliases, generated files) are checked for the errors they get.
    /// </summary>
    private static async Task<List<DocumentId>> DocumentsMentioningAsync(
        Solution solution,
        List<string> namespaces,
        CancellationToken cancellationToken)
    {
        var documents = new List<DocumentId>();
        if (namespaces.Count == 0)
            return documents;

        var segments = namespaces.Select(n => n[(n.LastIndexOf('.') + 1)..]).Distinct(StringComparer.Ordinal).ToList();
        foreach (var document in solution.Projects.SelectMany(p => p.Documents))
        {
            var text = (await document.GetTextAsync(cancellationToken)).ToString();
            if (text.Contains("using", StringComparison.Ordinal) && segments.Any(s => text.Contains(s, StringComparison.Ordinal)))
                documents.Add(document.Id);
        }

        return documents;
    }

    /// <summary>
    /// Whether namespace <paramref name="name"/> exists in one of <paramref name="projects"/>
    /// before the rename but not after it.
    /// </summary>
    private static async Task<bool> VanishesAsync(
        string name,
        IEnumerable<ProjectId> projects,
        Solution solution,
        Solution newSolution,
        CancellationToken cancellationToken)
    {
        foreach (var id in projects)
        {
            if (solution.GetProject(id) is { } before && await before.GetCompilationAsync(cancellationToken) is { } beforeCompilation &&
                FindNamespace(beforeCompilation.GlobalNamespace, name) != null &&
                newSolution.GetProject(id) is { } after && await after.GetCompilationAsync(cancellationToken) is { } afterCompilation &&
                FindNamespace(afterCompilation.GlobalNamespace, name) == null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Warns about project-level usings and root namespaces under the old name, which are not
    /// rewritten, in the project files and the <c>Directory.Build.props</c>/<c>.targets</c> above
    /// them; <paramref name="affectedProjects"/> gets the projects they apply to.
    /// </summary>
    private static List<string> ProjectFileWarnings(NamespaceRename rename, Solution solution, out HashSet<ProjectId> affectedProjects)
    {
        var warnings = new List<string>();
        affectedProjects = [];
        var projects = solution.Projects.Where(p => p.FilePath != null).ToList();
        var files = new SortedSet<string>(PathResolver.PathComparer);
        foreach (var project in projects)
        {
            files.Add(PathResolver.NormalizePath(project.FilePath!));
            for (var dir = Path.GetDirectoryName(PathResolver.NormalizePath(project.FilePath!)); dir != null; dir = Path.GetDirectoryName(dir))
            {
                foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets" })
                {
                    if (File.Exists(Path.Combine(dir, name)))
                        files.Add(Path.Combine(dir, name));
                }
            }
        }

        foreach (var file in files)
        {
            XElement root;
            try
            {
                if (!File.Exists(file))
                    continue;
                root = XDocument.Load(file).Root!;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
            {
                continue;
            }

            var applies = Path.GetFileName(file).StartsWith("Directory.Build.", StringComparison.OrdinalIgnoreCase)
                ? projects.Where(p => PathResolver.NormalizePath(p.FilePath!).StartsWith(Path.GetDirectoryName(file)! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                : projects.Where(p => PathResolver.PathsEqual(p.FilePath, file));

            foreach (var element in root.Descendants())
            {
                if (element.Name.LocalName == "Using" &&
                    NormalizeName(element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value) is { Length: > 0 } value &&
                    rename.Covers(value))
                {
                    warnings.Add($"'{file}' has <Using Include=\"{value}\" />, which rename_namespace does not change; " +
                        $"change it to '{rename.Map(value)}'.");
                    affectedProjects.UnionWith(applies.Select(p => p.Id));
                }
                else if (element.Name.LocalName == "RootNamespace" && rename.Covers(element.Value.Trim()))
                {
                    warnings.Add($"'{file}' sets <RootNamespace>{element.Value.Trim()}</RootNamespace>, which rename_namespace " +
                        $"does not change; new files still get that namespace. Consider '{rename.Map(element.Value.Trim())}'.");
                }
            }
        }

        return warnings;
    }

    /// <summary>
    /// Plans the folder moves of the files declaring renamed namespaces; returns why a file stays.
    /// </summary>
    private static List<string> PlanMoves(Scan scan, Solution solution, List<PlannedMove> plans)
    {
        var skipped = new List<string>();
        var claimed = new HashSet<string>(PathResolver.PathComparer);
        foreach (var file in scan.Files.Where(f => f.Declarations > 0))
        {
            var document = file.Document;
            if (file.TargetNamespace == null)
            {
                skipped.Add($"'{document.FilePath}' was not moved: {file.ContentReason}.");
                continue;
            }

            var move = NamespaceFolderMove.Plan(
                document, file.TargetNamespace, solution, () => WhyNotInItsFolder(document, file.OldNamespace!), claimed);

            if (move.TargetPath != null)
            {
                claimed.Add(move.TargetPath);
                plans.Add(new PlannedMove(document.Project.FilePath!, document.FilePath!, move));
            }
            else if (move.SkipReason != null)
            {
                skipped.Add($"'{document.FilePath}' was not moved: {move.SkipReason}.");
            }
        }

        return skipped;
    }

    /// <summary>
    /// Why <paramref name="document"/> does not follow the folder convention, or null when it sits
    /// in the folder of its namespace: only such a file is moved to the folder of the new name,
    /// so the rename does not change a layout the project chose otherwise.
    /// </summary>
    private static string? WhyNotInItsFolder(Document document, string oldNamespace)
    {
        var folder = NamespaceFolderMove.FolderOf(document.Project, oldNamespace);
        if (folder == null)
        {
            return $"its namespace '{oldNamespace}' is not inside the project's root namespace " +
                $"'{document.Project.DefaultNamespace}', so it has no folder to follow";
        }

        if (PathResolver.PathComparer.Equals(
                PathResolver.NormalizePath(folder),
                Path.GetDirectoryName(PathResolver.NormalizePath(document.FilePath!))))
        {
            return null;
        }

        var projectDir = Path.GetDirectoryName(PathResolver.NormalizePath(document.Project.FilePath!))!;
        var relative = Path.GetRelativePath(projectDir, folder);
        return $"it is not in '{(relative == "." ? "the project folder" : relative)}', the folder of its namespace " +
            $"'{oldNamespace}', so it does not follow the folder convention";
    }

    /// <summary>
    /// Deletes the folders the moves left empty, up to (not including) each project's folder.
    /// </summary>
    private static void DeleteEmptiedFolders(IEnumerable<(string Directory, string ProjectDirectory)> folders)
    {
        foreach (var (start, projectDirectory) in folders)
        {
            var root = PathResolver.NormalizePath(projectDirectory);
            var chain = new List<string>();
            for (var dir = PathResolver.NormalizePath(start);
                 dir.Length > root.Length && dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                 dir = Path.GetDirectoryName(dir)!)
            {
                chain.Add(dir);
            }

            NamespaceFolderMove.DeleteEmptyDirectories(chain);
        }
    }

    private static async Task<List<PendingChange>> CreatePendingChangesAsync(
        NamespaceRename rename,
        Scan scan,
        RazorNamespaceDirectives.Plan razor,
        Solution solution,
        Solution newSolution,
        List<PlannedMove> plans,
        CancellationToken cancellationToken)
    {
        var changes = new List<PendingChange>();
        var files = scan.Files.Where(f => f.Document.FilePath != null)
            .ToDictionary(f => PathResolver.NormalizePath(f.Document.FilePath!), PathResolver.PathComparer);
        var paths = new HashSet<string>(PathResolver.PathComparer);
        foreach (var id in ChangedDocuments(solution, newSolution))
        {
            var document = solution.GetDocument(id)!;
            if (document.FilePath != null && !paths.Add(PathResolver.NormalizePath(document.FilePath)))
                continue;

            var before = await document.GetTextAsync(cancellationToken);
            var after = await newSolution.GetDocument(id)!.GetTextAsync(cancellationToken);
            var line = Enumerable.Range(0, Math.Min(before.Lines.Count, after.Lines.Count))
                .FirstOrDefault(i => before.Lines[i].ToString() != after.Lines[i].ToString());

            changes.Add(new PendingChange
            {
                File = document.FilePath ?? document.Name,
                ChangeType = ChangeKind.Modify,
                Description = document.FilePath != null && files.TryGetValue(PathResolver.NormalizePath(document.FilePath), out var file)
                    ? $"Rename namespace '{rename.Old}' to '{rename.New}': {file.Declarations} declaration(s), {file.References} reference(s)"
                    : "Remove using directives the rename made redundant",
                BeforeSnippet = before.Lines[line].ToString().Trim(),
                AfterSnippet = after.Lines[line].ToString().Trim(),
                StartLine = line + 1,
                EndLine = line + 1
            });
        }

        foreach (var file in razor.Files)
        {
            var line = Enumerable.Range(0, file.Text.Lines.Count)
                .First(i => file.Text.Lines[i].ToString() != file.NewText.Lines[i].ToString());
            changes.Add(new PendingChange
            {
                File = file.Path,
                ChangeType = ChangeKind.Modify,
                Description = $"Rename namespace '{rename.Old}' to '{rename.New}': {file.Directives} @using / @namespace directive(s)",
                BeforeSnippet = file.Text.Lines[line].ToString().Trim(),
                AfterSnippet = file.NewText.Lines[line].ToString().Trim(),
                StartLine = line + 1,
                EndLine = line + 1
            });
        }

        foreach (var plan in plans)
        {
            changes.Add(new PendingChange
            {
                File = plan.Move.TargetPath!,
                ChangeType = ChangeKind.Create,
                Description = $"Move {Path.GetFileName(plan.SourcePath)} here (namespace folder)"
            });
            changes.Add(new PendingChange
            {
                File = plan.SourcePath,
                ChangeType = ChangeKind.Delete,
                Description = $"Moved to {plan.Move.TargetPath}"
            });
        }

        return changes;
    }

    /// <summary>The rename: <c>Old</c> and every namespace under it map to <c>New</c> and below.</summary>
    private sealed class NamespaceRename(string oldName, string newName)
    {
        public string Old { get; } = oldName;

        public string New { get; } = newName;

        public string[] OldSegments { get; } = oldName.Split('.');

        public string[] NewSegments { get; } = newName.Split('.');

        public string LastSegment => OldSegments[^1];

        /// <summary>Whether <paramref name="ns"/> is the old namespace or lies under it.</summary>
        public bool Covers(string ns) =>
            ns == Old || ns.StartsWith(Old + ".", StringComparison.Ordinal);

        /// <summary>The new name of a namespace <see cref="Covers"/> accepts.</summary>
        public string Map(string ns) => New + ns[Old.Length..];

        /// <summary>The new name of any namespace: mapped when renamed, else as it is.</summary>
        public string MapAny(string ns) => Covers(ns) ? Map(ns) : ns;

        /// <summary>Whether the new namespace exists before the rename (the rename merges into it).</summary>
        public bool NewExists { get; set; }

        /// <summary>
        /// Whether a simple name inside a renamed declaration may bind to something else afterwards:
        /// the declarations move under another parent namespace, whose members are found instead of
        /// the old parent's, or into a namespace that already has members of its own.
        /// </summary>
        public bool ChangesLookup => NewExists || Parent(Old) != Parent(New);

        private static string Parent(string ns) => ns.LastIndexOf('.') is var dot and >= 0 ? ns[..dot] : "";
    }

    /// <summary>What the scan found to rewrite.</summary>
    private sealed class Scan
    {
        public List<FileRewrite> Files { get; } = [];

        public List<string> Generated { get; } = [];

        public HashSet<ProjectId> ProjectsWithGenerated { get; } = [];
    }

    /// <summary>The rewrites of one file, in each document that compiles it.</summary>
    private sealed class FileRewrite(Document document, CompilationUnitSyntax root)
    {
        /// <summary>The file's first document.</summary>
        public Document Document { get; } = document;

        /// <summary>The syntax root of <see cref="Document"/>.</summary>
        public CompilationUnitSyntax Root { get; } = root;

        /// <summary>One per document compiling the file (one per target framework).</summary>
        public List<DocumentRewrite> Versions { get; } = [];

        /// <summary>The names of the declarations to rename, found in any of the documents.</summary>
        public HashSet<TextSpan> DeclarationSpans { get; } = [];

        /// <summary>The names referring to the old namespace, found in any of the documents.</summary>
        public HashSet<TextSpan> ReferenceSpans { get; } = [];

        public int Declarations => DeclarationSpans.Count;

        public int References => ReferenceSpans.Count;

        /// <summary>The namespace all the file's types are in before the rename, when there is one.</summary>
        public string? OldNamespace { get; set; }

        /// <summary>The namespace all the file's types end up in, when there is one.</summary>
        public string? TargetNamespace { get; set; }

        /// <summary>Why the file has no such namespace.</summary>
        public string? ContentReason { get; set; }
    }

    /// <summary>
    /// The rewrites of one document of a file: a file compiled for several target frameworks has
    /// code in <c>#if</c> branches that only some of its documents see.
    /// </summary>
    private sealed class DocumentRewrite(Document document, CompilationUnitSyntax root)
    {
        public Document Document { get; } = document;

        public CompilationUnitSyntax Root { get; } = root;

        public Dictionary<SyntaxNode, SyntaxNode> Replacements { get; } = [];

        public bool NeedsSimplification { get; set; }

        /// <summary>The outermost declaration blocks the rewrite renames.</summary>
        public List<BaseNamespaceDeclarationSyntax> RenamedBlocks { get; } = [];

        /// <summary>
        /// The simple names inside <see cref="RenamedBlocks"/> that bind to a namespace or type,
        /// with what they must bind to after the rename.
        /// </summary>
        public Dictionary<SyntaxNode, EnclosedName> EnclosedNames { get; } = [];
    }

    /// <summary>
    /// A simple name inside a renamed declaration: what it must bind to after the rename
    /// (<see cref="NamespaceOrTypeKey"/>), and the namespace that qualifies it when it would not
    /// (null for the global namespace).
    /// </summary>
    private sealed record EnclosedName(SyntaxAnnotation Annotation, string Key, string? Container);
}
