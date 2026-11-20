using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Where <c>move_type_to_namespace</c> with <c>updateFileLocation</c> or <c>rename_namespace</c>
/// with <c>updateFolders</c> moves a file, or why it leaves the file where it is.
/// </summary>
/// <param name="TargetPath">New path, or null when the file stays.</param>
/// <param name="Folders">Project-relative folders of <paramref name="TargetPath"/>.</param>
/// <param name="SkipReason">Why the file stays, or null when it moves or is already in place.</param>
internal sealed record NamespaceFolderMove(string? TargetPath, IReadOnlyList<string> Folders, string? SkipReason)
{
    private static readonly NamespaceFolderMove InPlace = new(null, [], null);

    /// <summary>
    /// Plans moving <paramref name="document"/>, which declares the moved type, into the
    /// folder that matches <paramref name="targetNamespace"/> relative to its project's root
    /// namespace (<c>App</c> + <c>App.Services.Auth</c> → <c>&lt;projectDir&gt;/Services/Auth</c>).
    /// </summary>
    /// <remarks>
    /// This is a cheap pre-filter on the project files themselves: an SDK-style project whose
    /// default compile items are not disabled (in the project or a
    /// <c>Directory.Build.props</c>/<c>.targets</c> above it), no <c>Compile</c> item that
    /// matches the old path but not the new one or vice versa (an explicit <c>Include</c>,
    /// <c>Update</c> metadata, a <c>Remove</c> of the new folder), a file that holds nothing
    /// but the moved type, and a free target path. It cannot see other imports (shared props,
    /// NuGet build props), so after the move MSBuild itself has the last word (see
    /// <see cref="MoveAsync"/>).
    /// </remarks>
    public static NamespaceFolderMove Plan(
        Document document,
        SyntaxNode typeDeclaration,
        string targetNamespace,
        Solution solution) =>
        Plan(
            document,
            targetNamespace,
            solution,
            () => DeclaresOnly(typeDeclaration) ? null : $"'{Path.GetFileName(document.FilePath)}' declares more than the moved type",
            claimedTargets: null);

    /// <summary>
    /// Plans moving <paramref name="document"/> into the folder that matches
    /// <paramref name="targetNamespace"/>, like the overload for one type does.
    /// </summary>
    /// <param name="document">The document to move.</param>
    /// <param name="targetNamespace">The namespace whose folder it moves to.</param>
    /// <param name="solution">The solution the document belongs to.</param>
    /// <param name="whyContentStays">Why the file's content keeps it in place, or null.</param>
    /// <param name="claimedTargets">Target paths other planned moves take; they count as taken.</param>
    public static NamespaceFolderMove Plan(
        Document document,
        string targetNamespace,
        Solution solution,
        Func<string?> whyContentStays,
        IReadOnlySet<string>? claimedTargets)
    {
        var sourcePath = document.FilePath;
        var project = document.Project;
        if (sourcePath == null)
            return Skip("the document has no file path");

        if (project.FilePath == null || !File.Exists(project.FilePath))
            return Skip("the type's project has no project file on disk");

        var projectDir = Path.GetDirectoryName(PathResolver.NormalizePath(project.FilePath))!;
        var sourceRelative = Path.GetRelativePath(projectDir, PathResolver.NormalizePath(sourcePath));
        if (sourceRelative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(sourceRelative))
            return Skip($"'{sourcePath}' is not inside the project directory '{projectDir}'");

        var otherProject = solution.GetDocumentIdsWithFilePath(sourcePath)
            .Select(id => solution.GetProject(id.ProjectId)!)
            .FirstOrDefault(p => !PathResolver.PathsEqual(p.FilePath, project.FilePath));
        if (otherProject != null)
            return Skip($"the file is also compiled by project '{otherProject.Name}'");

        var rootNamespace = project.DefaultNamespace;
        if (string.IsNullOrEmpty(rootNamespace))
            return Skip($"project '{project.Name}' has no root namespace");

        if (targetNamespace != rootNamespace && !targetNamespace.StartsWith(rootNamespace + ".", StringComparison.Ordinal))
            return Skip($"'{targetNamespace}' is not inside the project's root namespace '{rootNamespace}'");

        var folders = targetNamespace.Length == rootNamespace.Length
            ? []
            : targetNamespace[(rootNamespace.Length + 1)..].Split('.');
        var targetPath = Path.Combine([projectDir, .. folders, Path.GetFileName(sourcePath)]);
        if (PathResolver.PathsEqual(targetPath, sourcePath))
            return InPlace;

        if (whyContentStays() is { } contentReason)
            return Skip(contentReason);

        if (folders.Length > 0 && (folders[0].Equals("bin", StringComparison.OrdinalIgnoreCase) || folders[0].Equals("obj", StringComparison.OrdinalIgnoreCase)))
            return Skip($"the SDK does not compile files under '{folders[0]}'");

        var projectFileReason = CheckProjectFiles(project.FilePath, projectDir, sourceRelative, Path.GetRelativePath(projectDir, targetPath));
        if (projectFileReason != null)
            return Skip(projectFileReason);

        if (File.Exists(targetPath) || Directory.Exists(targetPath) || claimedTargets?.Contains(targetPath) == true ||
            solution.Projects.SelectMany(p => p.Documents).Any(d => PathResolver.PathsEqual(d.FilePath, targetPath)))
        {
            return Skip($"'{targetPath}' already exists");
        }

        return new NamespaceFolderMove(targetPath, folders, null);
    }

    private static NamespaceFolderMove Skip(string reason) => new(null, [], reason);

    /// <summary>
    /// The folder the SDK convention gives <paramref name="namespaceName"/> in
    /// <paramref name="project"/>: the project's folder plus the namespace's segments below the
    /// root namespace. Null when the project has no file or root namespace, or the namespace is
    /// neither the root namespace nor under it.
    /// </summary>
    public static string? FolderOf(Project project, string namespaceName)
    {
        var rootNamespace = project.DefaultNamespace;
        if (project.FilePath == null || string.IsNullOrEmpty(rootNamespace) ||
            (namespaceName != rootNamespace && !namespaceName.StartsWith(rootNamespace + ".", StringComparison.Ordinal)))
        {
            return null;
        }

        var projectDir = Path.GetDirectoryName(PathResolver.NormalizePath(project.FilePath))!;
        string[] folders = namespaceName.Length == rootNamespace.Length ? [] : namespaceName[(rootNamespace.Length + 1)..].Split('.');
        return Path.Combine([projectDir, .. folders]);
    }

    /// <summary>
    /// Moves the (already committed) files of <paramref name="moves"/> to their targets and
    /// repoints the cached solution, then lets MSBuild confirm, once per project, that it
    /// compiles each file at its new path. The project files <see cref="Plan(Document, string, Solution, Func{string?}, IReadOnlySet{string}?)"/>
    /// checks cannot see everything (an imported props file may remove the target folder), so a
    /// move MSBuild does not confirm in every target framework is undone: file, created folders
    /// and cached document.
    /// </summary>
    /// <returns>Per move, in order: where the file is now if it moved, and the problem to report, if any.</returns>
    public static async Task<IReadOnlyList<MoveOutcome>> MoveAsync(
        WorkspaceContext context,
        IReadOnlyList<PlannedMove> moves,
        CancellationToken cancellationToken)
    {
        var outcomes = new MoveOutcome[moves.Count];
        var createdDirectories = new List<string>[moves.Count];
        var moved = new List<int>();
        var unmoved = context.Solution;
        var solution = unmoved;

        // Under the lock all RoslynMcp processes share for writing, so another server's commit
        // cannot find a file still in place and write it back there after it moved. Not
        // cancellable, like the moves: the namespace change is committed already.
        IDisposable held;
        try
        {
            held = await context.LockFilesForWritingAsync(CancellationToken.None);
        }
        catch (RefactoringException)
        {
            for (var i = 0; i < moves.Count; i++)
                outcomes[i] = new MoveOutcome(moves[i].SourcePath, null, NotMoved("another RoslynMcp process kept the lock for writing files too long"));
            return outcomes;
        }

        using (held)
        {
            for (var i = 0; i < moves.Count; i++)
            {
                var (_, sourcePath, move) = moves[i];
                var targetPath = move.TargetPath!;
                createdDirectories[i] = [];
                for (var dir = Path.GetDirectoryName(targetPath); dir != null && !Directory.Exists(dir); dir = Path.GetDirectoryName(dir))
                    createdDirectories[i].Add(dir);

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    File.Move(sourcePath, targetPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    DeleteEmptyDirectories(createdDirectories[i]);
                    outcomes[i] = new MoveOutcome(sourcePath, null, NotMoved($"moving it to '{targetPath}' failed: {ex.Message}"));
                    continue;
                }

                // Repoint the cached document, so the solution matches disk without waiting for the
                // file watcher (which then finds the document already in place). A multi-targeted
                // project holds the file once per target framework.
                foreach (var documentId in solution.GetDocumentIdsWithFilePath(sourcePath))
                    solution = solution.WithDocumentFilePath(documentId, targetPath).WithDocumentFolders(documentId, move.Folders);
                moved.Add(i);
            }
        }

        context.UpdateSolution(solution);

        foreach (var project in moved.GroupBy(i => moves[i].ProjectFile, PathResolver.PathComparer))
        {
            IReadOnlySet<string>? compiled;
            string? cause = null;
            try
            {
                // Null: a workspace that is not MSBuild-loaded cannot be re-evaluated, so the
                // project-file checks are all there is.
                compiled = await context.GetFilesCompiledByProjectAsync(project.Key, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                foreach (var i in project)
                    await UndoAsync(i, "the operation was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                compiled = new HashSet<string>();
                cause = $"MSBuild could not re-evaluate '{Path.GetFileName(project.Key)}' to confirm that it compiles the moved file ({ex.Message})";
            }

            foreach (var i in project)
            {
                var targetPath = moves[i].Move.TargetPath!;
                outcomes[i] = compiled == null || compiled.Contains(PathResolver.NormalizePath(targetPath))
                    ? new MoveOutcome(moves[i].SourcePath, targetPath, null)
                    : await UndoAsync(i, cause ?? $"MSBuild does not compile '{targetPath}' (the project's items exclude it)");
            }
        }

        return outcomes;

        async Task<MoveOutcome> UndoAsync(int i, string cause)
        {
            var (_, sourcePath, move) = moves[i];
            var targetPath = move.TargetPath!;
            try
            {
                // Not cancellable: a move that is not undone leaves the file where MSBuild does
                // not compile it. The wait for the lock is bounded by its timeout.
                using (await context.LockFilesForWritingAsync(CancellationToken.None))
                    File.Move(targetPath, sourcePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RefactoringException)
            {
                // The file stays at the target, where the cached solution already has it.
                return new MoveOutcome(sourcePath, targetPath,
                    $"The namespace was changed and the file moved to '{targetPath}', but {cause}, " +
                    $"and moving it back failed: {ex.Message}. Move it back to '{sourcePath}'.");
            }

            DeleteEmptyDirectories(createdDirectories[i]);
            var current = context.Solution;
            foreach (var documentId in current.GetDocumentIdsWithFilePath(targetPath))
            {
                var original = unmoved.GetDocument(documentId);
                current = current.WithDocumentFilePath(documentId, sourcePath)
                    .WithDocumentFolders(documentId, original?.Folders ?? []);
            }

            context.UpdateSolution(current);
            return new MoveOutcome(sourcePath, null, NotMoved($"{cause}, so the move was undone"));
        }
    }

    /// <summary>
    /// Deletes the directories a move created (deepest first), as long as they are empty.
    /// </summary>
    internal static void DeleteEmptyDirectories(IEnumerable<string> directories)
    {
        foreach (var dir in directories)
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: an empty folder left behind is harmless.
            }
        }
    }

    /// <summary>The warning for a file whose namespace changed but which stays where it is.</summary>
    internal static string NotMoved(string reason) =>
        $"The namespace was changed, but the file was not moved: {reason}.";

    /// <summary>
    /// Whether the moved type is the only thing its file declares (namespaces aside): no
    /// other types, delegates or top-level statements.
    /// </summary>
    private static bool DeclaresOnly(SyntaxNode typeDeclaration)
    {
        var members = typeDeclaration.SyntaxTree.GetRoot().DescendantNodes(
                node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .OfType<MemberDeclarationSyntax>()
            .Where(member => member is not BaseNamespaceDeclarationSyntax)
            .ToList();

        return members.Count == 1 && members[0] == typeDeclaration;
    }

    /// <summary>
    /// Checks the project file and every <c>Directory.Build.props</c>/<c>.targets</c> above
    /// it. Returns why the move is unsafe, or null.
    /// </summary>
    private static string? CheckProjectFiles(string projectFile, string projectDir, string sourceRelative, string targetRelative)
    {
        var files = new List<string> { projectFile };
        for (var dir = new DirectoryInfo(projectDir); dir != null; dir = dir.Parent)
        {
            files.AddRange(new[] { "Directory.Build.props", "Directory.Build.targets" }
                .Select(name => Path.Combine(dir.FullName, name))
                .Where(File.Exists));
        }

        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            XElement root;
            try
            {
                root = XDocument.Load(file).Root!;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
            {
                return $"'{file}' could not be read: {ex.Message}";
            }

            if (i == 0 &&
                root.Attribute("Sdk") == null &&
                !root.Elements().Any(e => e.Name.LocalName == "Sdk") &&
                !root.Descendants().Any(e => e.Name.LocalName == "Import" && e.Attribute("Sdk") != null))
            {
                return $"'{Path.GetFileName(file)}' is not an SDK-style project";
            }

            var disabled = root.Descendants()
                .FirstOrDefault(e => e.Name.LocalName is "EnableDefaultCompileItems" or "EnableDefaultItems" &&
                    e.Value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase));
            if (disabled != null)
                return $"'{Path.GetFileName(file)}' sets {disabled.Name.LocalName} to false";

            // Every Compile item has to treat the old and the new path alike, or the move
            // changes what is compiled: an explicit Include or Update of the file, or a
            // Remove of the target folder.
            foreach (var item in root.Descendants().Where(e => e.Name.LocalName == "Compile"))
            {
                foreach (var attribute in new[] { "Include", "Update", "Remove" })
                {
                    var specs = item.Attribute(attribute)?.Value
                        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
                    var spec = specs.FirstOrDefault(s => !s.Contains("$(", StringComparison.Ordinal) &&
                        MatchesItemSpec(s, sourceRelative) != MatchesItemSpec(s, targetRelative));
                    if (spec != null)
                        return $"<Compile {attribute}=\"{spec}\"> in '{Path.GetFileName(file)}' matches only one of the old and new paths";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Whether an MSBuild item spec (a path or a glob with <c>*</c>, <c>?</c> and
    /// <c>**</c>, relative to the project directory) matches <paramref name="relativePath"/>.
    /// </summary>
    internal static bool MatchesItemSpec(string spec, string relativePath)
    {
        var pattern = Regex.Escape(spec.Replace('\\', '/'))
            .Replace(@"\*\*/", "(.*/)?", StringComparison.Ordinal)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", "[^/]", StringComparison.Ordinal);
        var path = relativePath.Replace('\\', '/');
        if (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        if (pattern.StartsWith(@"\./", StringComparison.Ordinal))
            pattern = pattern[3..];

        return Regex.IsMatch(path, "^" + pattern + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

/// <summary>A planned move of a file of <paramref name="ProjectFile"/>.</summary>
internal sealed record PlannedMove(string ProjectFile, string SourcePath, NamespaceFolderMove Move);

/// <summary>What became of a <see cref="PlannedMove"/>.</summary>
/// <param name="SourcePath">The file's path before the move.</param>
/// <param name="MovedTo">Where the file is now, when it moved.</param>
/// <param name="Problem">Why it did not move (or did not move back), when something went wrong.</param>
internal sealed record MoveOutcome(string SourcePath, string? MovedTo, string? Problem);
