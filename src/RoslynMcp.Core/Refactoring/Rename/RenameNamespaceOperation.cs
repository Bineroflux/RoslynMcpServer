using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Simplification;
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
        if (!scan.Files.Any(f => f.Declarations > 0))
        {
            throw new RefactoringException(
                ErrorCodes.SymbolNotFound,
                scan.Generated.Count > 0
                    ? $"Namespace '{rename.Old}' is declared only in generated files, which are not renamed."
                    : $"Namespace '{rename.Old}' is not declared in a source file of the loaded workspace.");
        }

        var newSolution = await ApplyAsync(solution, scan, cancellationToken);

        var warnings = ProjectFileWarnings(rename, solution, out var projectsWithUsings);
        warnings.AddRange(scan.Generated.Select(path =>
            $"'{path}' is generated and was not changed; it still refers to namespace '{rename.Old}'."));

        var plans = new List<PlannedMove>();
        if (@params.UpdateFolders)
            warnings.AddRange(PlanMoves(scan, solution, plans));

        var affected = new HashSet<DocumentId>(scan.Files.SelectMany(f => DocumentIds(solution, f.Document)));
        foreach (var projectId in scan.ProjectsWithGenerated.Concat(projectsWithUsings))
            affected.UnionWith(solution.GetProject(projectId)?.DocumentIds ?? []);
        warnings.AddRange(await IntroducedErrors.FindAsync(solution, newSolution, affected, cancellationToken) ?? []);

        var references = scan.Files.Sum(f => f.References);
        if (@params.Preview)
        {
            await PreviewCommitAsync(newSolution, cancellationToken);
            return new RefactoringResult
            {
                Success = true,
                OperationId = operationId,
                Preview = true,
                PendingChanges = await CreatePendingChangesAsync(rename, scan, newSolution, plans, cancellationToken),
                ReferencesUpdated = references,
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
    /// Finds what to rewrite in every file that spells the old namespace's last segment, once
    /// per file (a file compiled by several projects is read through its first document).
    /// Generated files are only inspected.
    /// </summary>
    private static async Task<Scan> ScanAsync(NamespaceRename rename, Solution solution, CancellationToken cancellationToken)
    {
        var scan = new Scan();
        var seen = new HashSet<string>(PathResolver.PathComparer);
        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath != null && !seen.Add(PathResolver.NormalizePath(document.FilePath)))
                    continue;

                var text = await document.GetTextAsync(cancellationToken);
                if (text.ToString().IndexOf(rename.LastSegment, StringComparison.Ordinal) < 0 ||
                    await document.GetSyntaxRootAsync(cancellationToken) is not CompilationUnitSyntax root)
                {
                    continue;
                }

                var generated = GeneratedCode.IsGenerated(root.SyntaxTree, cancellationToken);
                var file = new FileRewrite(document, root);
                CollectDeclarations(rename, file, editable: !generated);
                await CollectReferencesAsync(rename, file, cancellationToken);
                if (file.Declarations + file.References == 0)
                    continue;

                if (generated)
                {
                    scan.Generated.Add(document.FilePath ?? document.Name);
                    scan.ProjectsWithGenerated.Add(project.Id);
                    continue;
                }

                DescribeContent(rename, file);
                scan.Files.Add(file);
            }
        }

        return scan;
    }

    /// <summary>
    /// Records the declarations to rewrite: those of the renamed namespaces whose enclosing
    /// declaration is not renamed itself (nested ones are relative to it and stay).
    /// </summary>
    private static void CollectDeclarations(NamespaceRename rename, FileRewrite file, bool editable)
    {
        foreach (var declaration in file.Root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
        {
            if (!rename.Covers(FullName(declaration)) ||
                (declaration.Parent is BaseNamespaceDeclarationSyntax parent && rename.Covers(FullName(parent))))
            {
                continue;
            }

            file.Declarations++;
            if (editable)
                RewriteDeclaration(rename, file, declaration);
        }
    }

    /// <summary>
    /// Gives <paramref name="declaration"/> its new name relative to its enclosing declaration.
    /// When the enclosing declaration's name is not a prefix of the new name (or leaves nothing
    /// for this one), enclosing blocks that declare nothing else are renamed too, the new name's
    /// segments spread over the chain.
    /// </summary>
    private static void RewriteDeclaration(NamespaceRename rename, FileRewrite file, BaseNamespaceDeclarationSyntax declaration)
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
                    $"{Where(file, block)} into one; merge them by hand first.");
            }

            if (outer.Members.Count != 1)
            {
                var others = outer.Members.Where(m => m != chain[^1]).Select(Describe).Take(3);
                throw new RefactoringException(
                    ErrorCodes.NamespaceBlockShared,
                    $"Renaming '{rename.Old}' to '{rename.New}' would have to rename the enclosing block " +
                    $"'namespace {outer.Name}' at {Where(file, outer)}, which also declares {string.Join(", ", others)}. " +
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
            file.Replacements[chain[i].Name] = SyntaxFactory.ParseName(string.Join('.', segments)).WithTriviaFrom(chain[i].Name);
        }
    }

    private static string Describe(MemberDeclarationSyntax member) => member switch
    {
        BaseNamespaceDeclarationSyntax ns => $"namespace '{ns.Name}'",
        BaseTypeDeclarationSyntax type => $"'{type.Identifier.ValueText}'",
        DelegateDeclarationSyntax @delegate => $"'{@delegate.Identifier.ValueText}'",
        _ => "other members"
    };

    private static string Where(FileRewrite file, SyntaxNode node) =>
        $"{file.Document.FilePath}({node.GetLocation().GetLineSpan().StartLinePosition.Line + 1})";

    /// <summary>The full name a namespace declaration declares, from its enclosing declarations.</summary>
    private static string FullName(BaseNamespaceDeclarationSyntax declaration) =>
        string.Join('.', declaration.AncestorsAndSelf().OfType<BaseNamespaceDeclarationSyntax>().Reverse()
            .Select(d => string.Join('.', d.Name.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.ValueText))));

    /// <summary>
    /// Records every name that refers to the old namespace itself, from the identifier spelling
    /// its last segment, in code and in documentation comments.
    /// </summary>
    private static async Task CollectReferencesAsync(NamespaceRename rename, FileRewrite file, CancellationToken cancellationToken)
    {
        SemanticModel? model = null;
        foreach (var token in file.Root.DescendantTokens(descendIntoTrivia: true))
        {
            if (!token.IsKind(SyntaxKind.IdentifierToken) || token.ValueText != rename.LastSegment ||
                token.Parent is not IdentifierNameSyntax identifier ||
                identifier.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Any(ns => ns.Name.Span.Contains(identifier.Span)))
            {
                continue;
            }

            model ??= await file.Document.GetSemanticModelAsync(cancellationToken)
                ?? throw new RefactoringException(ErrorCodes.RoslynError, "Could not analyze file.");

            var name = NameEndingWith(identifier);
            if (model.GetSymbolInfo(name, cancellationToken).Symbol is not INamespaceSymbol ns || ns.ToDisplayString() != rename.Old)
                continue;

            if (Replacement(rename, name, model, cancellationToken) is { } replacement)
            {
                file.Replacements[name] = replacement;
                file.References++;
                file.NeedsSimplification |= replacement.HasAnnotation(Simplifier.Annotation);
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
    /// Applies the rewrites, simplifies the names marked for it once every file has its new
    /// names, and gives every document compiling a file the same new text.
    /// </summary>
    private static async Task<Solution> ApplyAsync(Solution solution, Scan scan, CancellationToken cancellationToken)
    {
        var result = solution;
        foreach (var file in scan.Files.Where(f => f.Replacements.Count > 0))
        {
            var root = file.Root.ReplaceNodes(file.Replacements.Keys, (original, _) => file.Replacements[original]);
            result = result.WithDocumentSyntaxRoot(file.Document.Id, root);
        }

        foreach (var file in scan.Files.Where(f => f.NeedsSimplification))
        {
            var document = await Simplifier.ReduceAsync(
                result.GetDocument(file.Document.Id)!, Simplifier.Annotation, cancellationToken: cancellationToken);
            result = document.Project.Solution;
        }

        foreach (var file in scan.Files.Where(f => f.Replacements.Count > 0))
        {
            var text = await result.GetDocument(file.Document.Id)!.GetTextAsync(cancellationToken);
            foreach (var id in DocumentIds(solution, file.Document).Where(id => id != file.Document.Id))
                result = result.WithDocumentText(id, text);
        }

        return result;
    }

    private static IEnumerable<DocumentId> DocumentIds(Solution solution, Document document) =>
        document.FilePath == null ? [document.Id] : solution.GetDocumentIdsWithFilePath(document.FilePath);

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
        Solution newSolution,
        List<PlannedMove> plans,
        CancellationToken cancellationToken)
    {
        var changes = new List<PendingChange>();
        foreach (var file in scan.Files.Where(f => f.Replacements.Count > 0))
        {
            var before = await file.Document.GetTextAsync(cancellationToken);
            var after = await newSolution.GetDocument(file.Document.Id)!.GetTextAsync(cancellationToken);
            var line = Enumerable.Range(0, Math.Min(before.Lines.Count, after.Lines.Count))
                .FirstOrDefault(i => before.Lines[i].ToString() != after.Lines[i].ToString());

            changes.Add(new PendingChange
            {
                File = file.Document.FilePath ?? file.Document.Name,
                ChangeType = ChangeKind.Modify,
                Description = $"Rename namespace '{rename.Old}' to '{rename.New}': " +
                    $"{file.Declarations} declaration(s), {file.References} reference(s)",
                BeforeSnippet = before.Lines[line].ToString().Trim(),
                AfterSnippet = after.Lines[line].ToString().Trim(),
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
    }

    /// <summary>What the scan found to rewrite.</summary>
    private sealed class Scan
    {
        public List<FileRewrite> Files { get; } = [];

        public List<string> Generated { get; } = [];

        public HashSet<ProjectId> ProjectsWithGenerated { get; } = [];
    }

    /// <summary>The rewrites of one file.</summary>
    private sealed class FileRewrite(Document document, CompilationUnitSyntax root)
    {
        public Document Document { get; } = document;

        public CompilationUnitSyntax Root { get; } = root;

        public Dictionary<SyntaxNode, SyntaxNode> Replacements { get; } = [];

        public int Declarations { get; set; }

        public int References { get; set; }

        public bool NeedsSimplification { get; set; }

        /// <summary>The namespace all the file's types are in before the rename, when there is one.</summary>
        public string? OldNamespace { get; set; }

        /// <summary>The namespace all the file's types end up in, when there is one.</summary>
        public string? TargetNamespace { get; set; }

        /// <summary>Why the file has no such namespace.</summary>
        public string? ContentReason { get; set; }
    }
}
