using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring;

namespace RoslynMcp.Core.Query.Utilities;

/// <summary>
/// Validates and resolves the scoping parameters of the query tools: <c>projectPath</c>
/// and the file scopes (<c>referenceFile</c>, <c>callerFile</c>, <c>sourceFile</c>).
/// </summary>
/// <remarks>
/// Every comparison runs both sides through <see cref="PathResolver.NormalizePath"/> and
/// compares with <see cref="PathResolver.PathComparer"/>, like
/// <see cref="PathResolver.PathsEqual"/>: an 8.3 short name, mixed separators, <c>..</c>
/// segments or (on Windows) different letter case don't keep a path from matching.
/// </remarks>
internal static class QueryScope
{
    private const int MaxListedProjects = 20;

    /// <summary>
    /// Checks a <c>projectPath</c> argument without the workspace: absolute and existing.
    /// Whether it belongs to the solution is checked by <see cref="ResolveProject"/>.
    /// </summary>
    public static void ValidateProjectPath(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            return;

        if (!PathResolver.IsAbsolutePath(projectPath))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, "projectPath must be an absolute path.");

        if (!File.Exists(projectPath))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"Project file not found: {projectPath}");
    }

    /// <summary>
    /// Checks a file-scope argument without the workspace: an absolute path to an existing
    /// <c>.cs</c> file. Whether it belongs to the solution is checked by <see cref="ResolveFile"/>.
    /// </summary>
    public static void ValidateFilePath(string? path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (!PathResolver.IsAbsolutePath(path))
            throw new RefactoringException(ErrorCodes.InvalidSourcePath, $"{parameterName} must be an absolute path.");

        if (!PathResolver.IsValidCSharpFilePath(path))
            throw new RefactoringException(
                ErrorCodes.InvalidSourcePath, PathResolver.GetCSharpFileRejectionReason(path, parameterName));

        if (!File.Exists(path))
            throw new RefactoringException(ErrorCodes.SourceFileNotFound, $"{parameterName} not found: {path}");
    }

    /// <summary>
    /// The projects loaded from <paramref name="projectPath"/>: one per target framework of
    /// a multi-targeted project. Null when no project path was given.
    /// </summary>
    /// <exception cref="RefactoringException">No project of the solution was loaded from that file.</exception>
    public static ProjectScope? ResolveProject(Solution solution, string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            return null;

        var normalized = PathResolver.NormalizePath(projectPath);
        var projects = solution.Projects
            .Where(p => !string.IsNullOrEmpty(p.FilePath)
                && PathResolver.PathComparer.Equals(PathResolver.NormalizePath(p.FilePath), normalized))
            .ToList();

        if (projects.Count == 0)
        {
            var known = solution.Projects
                .Select(p => p.FilePath)
                .OfType<string>()
                .Distinct(PathResolver.PathComparer)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var listed = string.Join(", ", known.Take(MaxListedProjects));
            if (known.Count > MaxListedProjects)
                listed += $", and {known.Count - MaxListedProjects} more";

            throw new RefactoringException(
                ErrorCodes.ProjectNotInWorkspace,
                $"projectPath is not a project of the loaded workspace: {projectPath}. Projects: {listed}");
        }

        return new ProjectScope(projects);
    }

    /// <summary>
    /// The documents of the solution at <paramref name="path"/>: more than one when the file
    /// is compiled into several projects (target frameworks, linked files). Null when no path
    /// was given.
    /// </summary>
    /// <exception cref="RefactoringException">No document of the solution has that path.</exception>
    public static FileScope? ResolveFile(Solution solution, string? path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var normalized = PathResolver.NormalizePath(path);
        var documents = solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => !string.IsNullOrEmpty(d.FilePath)
                && PathResolver.PathComparer.Equals(PathResolver.NormalizePath(d.FilePath), normalized))
            .ToList();

        if (documents.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.SourceNotInWorkspace,
                $"{parameterName} is not a document of the loaded workspace: {path}");
        }

        return new FileScope(normalized, documents);
    }

    /// <summary>
    /// The documents a <c>SymbolFinder</c> search has to look at to find every location the
    /// scopes accept, or null for the whole solution. Results still have to be checked
    /// against the scopes: definitions are reported regardless of the documents searched.
    /// </summary>
    public static async Task<IImmutableSet<Document>?> GetSearchDocumentsAsync(
        FileScope? fileScope, ProjectScope? projectScope, CancellationToken cancellationToken)
    {
        if (fileScope is null && projectScope is null)
            return null;

        IEnumerable<Document> documents = fileScope is not null
            ? fileScope.Documents
            : await projectScope!.GetAllDocumentsAsync(cancellationToken);

        if (fileScope is not null && projectScope is not null)
            documents = documents.Where(projectScope.Contains);

        return ImmutableHashSet.CreateRange(documents);
    }
}

/// <summary>
/// Whether result locations lie in a set of files. Each distinct path is normalized once.
/// </summary>
internal abstract class PathScope
{
    private readonly Dictionary<string, bool> _matches = new(StringComparer.Ordinal);

    /// <summary>Whether <paramref name="path"/>, spelled any equivalent way, is in scope.</summary>
    public bool ContainsFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        if (!_matches.TryGetValue(path, out var match))
        {
            match = ContainsNormalizedFile(PathResolver.NormalizePath(path));
            _matches[path] = match;
        }

        return match;
    }

    /// <summary>Whether a path already run through <see cref="PathResolver.NormalizePath"/> is in scope.</summary>
    protected abstract bool ContainsNormalizedFile(string normalizedPath);
}

/// <summary>
/// The projects a <c>projectPath</c> names (all target frameworks of the project file).
/// A location is in scope when its document belongs to one of them, or its file is a
/// document of one of them (a linked file found through another project).
/// </summary>
internal sealed class ProjectScope : PathScope
{
    private readonly HashSet<ProjectId> _projectIds;
    private readonly HashSet<string> _documentPaths;

    public ProjectScope(IReadOnlyList<Project> projects)
    {
        Projects = projects;
        _projectIds = projects.Select(p => p.Id).ToHashSet();
        _documentPaths = projects
            .SelectMany(p => p.Documents)
            .Select(d => d.FilePath)
            .OfType<string>()
            .Select(PathResolver.NormalizePath)
            .ToHashSet(PathResolver.PathComparer);
    }

    /// <summary>The matched projects, one per target framework.</summary>
    public IReadOnlyList<Project> Projects { get; }

    /// <summary>Whether <paramref name="project"/> is one of the matched projects.</summary>
    public bool Contains(Project project) => _projectIds.Contains(project.Id);

    /// <summary>Whether <paramref name="document"/> belongs to the scope.</summary>
    public bool Contains(Document document) => Contains(document.Project) || ContainsFile(document.FilePath);

    /// <summary>
    /// Whether a location in <paramref name="document"/> (null when unknown) at
    /// <paramref name="path"/> belongs to the scope.
    /// </summary>
    public bool Contains(Document? document, string? path) =>
        (document is not null && Contains(document.Project)) || ContainsFile(path);

    /// <summary>
    /// The projects' documents, source-generated ones included, so a restricted search
    /// still finds what a whole-solution search would have found in them.
    /// </summary>
    public async Task<IReadOnlyList<Document>> GetAllDocumentsAsync(CancellationToken cancellationToken)
    {
        var documents = new List<Document>();
        foreach (var project in Projects)
        {
            documents.AddRange(project.Documents);
            documents.AddRange(await project.GetSourceGeneratedDocumentsAsync(cancellationToken));
        }

        return documents;
    }

    /// <inheritdoc />
    protected override bool ContainsNormalizedFile(string normalizedPath) => _documentPaths.Contains(normalizedPath);
}

/// <summary>
/// A single file named by a file-scope parameter, with the documents it is compiled as.
/// </summary>
internal sealed class FileScope : PathScope
{
    public FileScope(string normalizedPath, IReadOnlyList<Document> documents)
    {
        NormalizedPath = normalizedPath;
        Documents = documents;
    }

    /// <summary>The file's path, normalized.</summary>
    public string NormalizedPath { get; }

    /// <summary>The solution's documents for the file: one per project compiling it.</summary>
    public IReadOnlyList<Document> Documents { get; }

    /// <inheritdoc />
    protected override bool ContainsNormalizedFile(string normalizedPath) =>
        PathResolver.PathComparer.Equals(normalizedPath, NormalizedPath);
}
