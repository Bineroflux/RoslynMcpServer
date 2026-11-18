using System.Runtime.InteropServices;

namespace RoslynMcp.Core.Workspace;

/// <summary>
/// A .NET SDK found on disk: its folder name (e.g. <c>11.0.100</c> or
/// <c>11.0.100-rc.2.25502.107</c>) and the folder itself, which holds its MSBuild.
/// </summary>
internal sealed record DotNetSdk(string Version, string Path);

/// <summary>
/// Finds .NET SDKs by probing the usual dotnet roots. This is the fallback for when
/// MSBuildLocator reports no instance; it never runs the <c>dotnet</c> host.
/// </summary>
internal static class DotNetSdkLocator
{
    /// <summary>
    /// The dotnet roots worth probing, existing ones only, in order: <c>DOTNET_ROOT</c>, the
    /// root of the runtime running this process, the directory of the <c>dotnet</c> host on
    /// <c>PATH</c> (symlinks resolved), <c>%ProgramFiles%\dotnet</c>, and the usual Unix
    /// locations.
    /// </summary>
    public static IReadOnlyList<string> GetSearchRoots() => GetSearchRoots(Environment.GetEnvironmentVariable);

    /// <inheritdoc cref="GetSearchRoots()"/>
    /// <param name="environment">Reads an environment variable (tests pass their own).</param>
    internal static IReadOnlyList<string> GetSearchRoots(Func<string, string?> environment)
    {
        var candidates = new List<string?>
        {
            environment("DOTNET_ROOT"),
            // <root>/shared/Microsoft.NETCore.App/<version>/
            Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."),
            FindHostOnPath(environment("PATH")),
        };

        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));
        }
        else
        {
            candidates.Add("/usr/share/dotnet");
            candidates.Add("/usr/lib/dotnet");
            candidates.Add("/usr/local/share/dotnet");
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
                candidates.Add(Path.Combine(home, ".dotnet"));
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return candidates
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root!)))
            .Where(Directory.Exists)
            .Distinct(comparer)
            .ToList();
    }

    /// <summary>
    /// The SDKs under <c>&lt;root&gt;/sdk</c> of each root: folders with a parseable version
    /// name that contain MSBuild.
    /// </summary>
    public static IReadOnlyList<DotNetSdk> FindAll(IEnumerable<string> roots)
    {
        var sdks = new List<DotNetSdk>();
        foreach (var root in roots)
        {
            try
            {
                var sdkDir = Path.Combine(root, "sdk");
                if (!Directory.Exists(sdkDir))
                    continue;

                sdks.AddRange(Directory.EnumerateDirectories(sdkDir)
                    .Where(dir => TryParseCore(Path.GetFileName(dir)) != null &&
                        File.Exists(Path.Combine(dir, "Microsoft.Build.dll")))
                    .Select(dir => new DotNetSdk(Path.GetFileName(dir), dir)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable root must not hide the others.
            }
        }

        return sdks;
    }

    /// <summary>
    /// The newest SDK whose MSBuild this process can load: SDKs with a major version above
    /// the runtime's are skipped. A release outranks a prerelease of the same version.
    /// </summary>
    public static DotNetSdk? SelectNewest(IEnumerable<DotNetSdk> sdks, int runtimeMajor) =>
        sdks.Where(sdk => CanHost(sdk, runtimeMajor))
            .OrderByDescending(sdk => sdk.Version, Comparer<string>.Create(CompareVersions))
            .FirstOrDefault();

    /// <summary>Whether a process on a runtime with <paramref name="runtimeMajor"/> can load the SDK's MSBuild.</summary>
    public static bool CanHost(DotNetSdk sdk, int runtimeMajor) =>
        TryParseCore(sdk.Version) is { } version && version.Major <= runtimeMajor;

    /// <summary>
    /// Compares SDK version labels the SemVer way: by number first, then a release above
    /// any prerelease, then prerelease identifiers one by one (numeric ones numerically and
    /// below alphanumeric ones).
    /// </summary>
    public static int CompareVersions(string? left, string? right)
    {
        var (leftCore, leftPre) = Split(left);
        var (rightCore, rightPre) = Split(right);
        var result = Comparer<Version?>.Default.Compare(leftCore, rightCore);
        if (result != 0)
            return result;

        if (leftPre.Length == 0 || rightPre.Length == 0)
            return rightPre.Length.CompareTo(leftPre.Length);

        for (var i = 0; i < Math.Min(leftPre.Length, rightPre.Length); i++)
        {
            var (l, r) = (leftPre[i], rightPre[i]);
            var lNumeric = l.All(char.IsAsciiDigit);
            var rNumeric = r.All(char.IsAsciiDigit);
            result = (lNumeric, rNumeric) switch
            {
                (true, true) => l.TrimStart('0').Length != r.TrimStart('0').Length
                    ? l.TrimStart('0').Length.CompareTo(r.TrimStart('0').Length)
                    : string.CompareOrdinal(l.TrimStart('0'), r.TrimStart('0')),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(l, r)
            };
            if (result != 0)
                return result;
        }

        return leftPre.Length.CompareTo(rightPre.Length);

        static (Version? Core, string[] Prerelease) Split(string? label)
        {
            var dash = label?.IndexOf('-') ?? -1;
            var prerelease = dash >= 0 ? label![(dash + 1)..].Split('.', StringSplitOptions.RemoveEmptyEntries) : [];
            return (TryParseCore(label), prerelease);
        }
    }

    /// <summary>
    /// The numeric part of a version label (<c>11.0.100</c> of <c>11.0.100-rc.2</c>), or null.
    /// </summary>
    public static Version? TryParseCore(string? label)
    {
        if (string.IsNullOrEmpty(label))
            return null;

        var dash = label.IndexOf('-');
        return Version.TryParse(dash >= 0 ? label[..dash] : label, out var version) ? version : null;
    }

    private static string? FindHostOnPath(string? path)
    {
        var hostName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (var dir in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var host = new FileInfo(Path.Combine(dir.Trim('"'), hostName));
                if (!host.Exists)
                    continue;

                // /usr/bin/dotnet is usually a link into the real root.
                var target = host.ResolveLinkTarget(returnFinalTarget: true) ?? host;
                return Path.GetDirectoryName(target.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Malformed or unreadable PATH entry.
            }
        }

        return null;
    }
}
