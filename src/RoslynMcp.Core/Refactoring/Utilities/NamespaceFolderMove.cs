using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.FileSystem;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Where <c>move_type_to_namespace</c> with <c>updateFileLocation</c> moves a file, or why
/// it leaves the file where it is.
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
    /// <c>MoveTypeToNamespaceOperation.MoveFileAsync</c>).
    /// </remarks>
    public static NamespaceFolderMove Plan(
        Document document,
        SyntaxNode typeDeclaration,
        string targetNamespace,
        Solution solution)
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

        if (!DeclaresOnly(typeDeclaration))
            return Skip($"'{Path.GetFileName(sourcePath)}' declares more than the moved type");

        if (folders.Length > 0 && (folders[0].Equals("bin", StringComparison.OrdinalIgnoreCase) || folders[0].Equals("obj", StringComparison.OrdinalIgnoreCase)))
            return Skip($"the SDK does not compile files under '{folders[0]}'");

        var projectFileReason = CheckProjectFiles(project.FilePath, projectDir, sourceRelative, Path.GetRelativePath(projectDir, targetPath));
        if (projectFileReason != null)
            return Skip(projectFileReason);

        if (File.Exists(targetPath) || Directory.Exists(targetPath) ||
            solution.Projects.SelectMany(p => p.Documents).Any(d => PathResolver.PathsEqual(d.FilePath, targetPath)))
        {
            return Skip($"'{targetPath}' already exists");
        }

        return new NamespaceFolderMove(targetPath, folders, null);
    }

    private static NamespaceFolderMove Skip(string reason) => new(null, [], reason);

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
