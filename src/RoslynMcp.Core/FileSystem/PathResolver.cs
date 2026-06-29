namespace RoslynMcp.Core.FileSystem;

/// <summary>
/// Cross-platform path resolution utilities.
/// </summary>
public static class PathResolver
{
    /// <summary>
    /// Normalizes a path to the current platform's format.
    /// </summary>
    /// <param name="path">Path to normalize.</param>
    /// <returns>Normalized absolute path.</returns>
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        // Get full path and normalize separators
        var fullPath = Path.GetFullPath(path);
        return fullPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Whether two paths refer to the same file. Both are run through
    /// <see cref="NormalizePath"/> (Path.GetFullPath + separator unification, and on
    /// Windows 8.3 short-name expansion), so equivalent-but-differently-spelled paths
    /// — short-name components (e.g. <c>ROBERT~1.HAE</c> vs <c>Robert.Haeusl</c>),
    /// mixed <c>/</c> vs <c>\</c> separators, or <c>..</c> segments — are treated as
    /// equal. Case is compared the way the platform's file system treats it:
    /// case-insensitively on Windows, case-sensitively elsewhere. Two null/empty
    /// inputs are equal; a null/empty input never equals a real path.
    /// </summary>
    public static bool PathsEqual(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b);

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(NormalizePath(a), NormalizePath(b), comparison);
    }

    /// <summary>
    /// Makes a path relative to a base path.
    /// </summary>
    /// <param name="basePath">Base path (directory).</param>
    /// <param name="fullPath">Full path to make relative.</param>
    /// <returns>Relative path.</returns>
    public static string MakeRelative(string basePath, string fullPath)
    {
        var baseUri = new Uri(EnsureTrailingSlash(NormalizePath(basePath)));
        var fullUri = new Uri(NormalizePath(fullPath));

        var relativeUri = baseUri.MakeRelativeUri(fullUri);
        var relativePath = Uri.UnescapeDataString(relativeUri.ToString());

        return relativePath.Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Checks if a path is absolute.
    /// </summary>
    /// <param name="path">Path to check.</param>
    /// <returns>True if absolute.</returns>
    public static bool IsAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        return Path.IsPathRooted(path);
    }

    /// <summary>
    /// Validates that a path is a valid C# source file path.
    /// </summary>
    /// <param name="path">Path to validate.</param>
    /// <returns>True if valid C# file path.</returns>
    public static bool IsValidCSharpFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (!IsAbsolutePath(path))
            return false;

        return path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates that a path is a Razor (.razor) or legacy Razor (.cshtml) source file.
    /// </summary>
    /// <remarks>
    /// Razor sources are not Roslyn <c>Document</c>s — they live in the project as
    /// <c>AdditionalDocument</c>s and are consumed by the Razor source generator,
    /// which emits <c>*_razor.g.cs</c> files into the compilation.
    /// </remarks>
    public static bool IsValidRazorFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (!IsAbsolutePath(path))
            return false;

        return path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates that a path is acceptable as the <c>sourceFile</c> argument for
    /// the <c>get_diagnostics</c> tool. Accepts both regular C# files and Razor
    /// markup files; diagnostics from .razor/.cshtml flow through Roslyn via the
    /// Razor source generator.
    /// </summary>
    public static bool IsValidDiagnosticsSourcePath(string path)
        => IsValidCSharpFilePath(path) || IsValidRazorFilePath(path);

    /// <summary>
    /// Returns the error message used when a C#-only operation receives a
    /// non-.cs file. Razor inputs get a more actionable message that directs the
    /// caller toward <c>get_diagnostics</c> (for diagnostics) or the Razor
    /// language server (for refactors).
    /// </summary>
    /// <param name="path">The offending path supplied by the caller.</param>
    /// <param name="parameterName">Name of the parameter (e.g. "sourceFile"/"targetFile") used in the message.</param>
    public static string GetCSharpFileRejectionReason(string path, string parameterName = "sourceFile")
    {
        return IsValidRazorFilePath(path)
            ? $"{parameterName} is a Razor/CSHTML file. This operation only supports .cs files. " +
              "Razor diagnostics are available via get_diagnostics; Razor refactors require the Razor language server."
            : $"{parameterName} must be a .cs file.";
    }

    /// <summary>
    /// Validates that a path is a valid solution or project path.
    /// </summary>
    /// <param name="path">Path to validate.</param>
    /// <returns>True if valid solution/project path.</returns>
    public static bool IsValidSolutionOrProjectPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (!IsAbsolutePath(path))
            return false;

        return path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates that a path is a standalone C# source file — a <c>.cs</c> file used on
    /// its own, not as part of a <c>.csproj</c>/<c>.sln</c>. The canonical example is a
    /// .NET 10 "file-based program" you launch with <c>dotnet run file.cs</c>.
    /// </summary>
    public static bool IsStandaloneCSharpFile(string path)
        => IsValidCSharpFilePath(path);

    /// <summary>
    /// Validates that a path is acceptable as the workspace root for a tool's
    /// <c>solutionPath</c> argument: a <c>.sln</c>/<c>.slnx</c>/<c>.csproj</c>, or a
    /// standalone <c>.cs</c> file that is loaded into an ad-hoc, project-less workspace.
    /// </summary>
    public static bool IsValidWorkspacePath(string path)
        => IsValidSolutionOrProjectPath(path) || IsStandaloneCSharpFile(path);

    /// <summary>
    /// Gets the directory containing a file path.
    /// </summary>
    /// <param name="filePath">File path.</param>
    /// <returns>Directory path.</returns>
    public static string GetDirectory(string filePath)
    {
        return Path.GetDirectoryName(NormalizePath(filePath)) ?? string.Empty;
    }

    /// <summary>
    /// Combines path segments.
    /// </summary>
    /// <param name="paths">Path segments to combine.</param>
    /// <returns>Combined path.</returns>
    public static string Combine(params string[] paths)
    {
        return NormalizePath(Path.Combine(paths));
    }

    private static string EnsureTrailingSlash(string path)
    {
        if (!path.EndsWith(Path.DirectorySeparatorChar))
            return path + Path.DirectorySeparatorChar;
        return path;
    }
}
