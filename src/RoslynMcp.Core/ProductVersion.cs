using System.Reflection;

namespace RoslynMcp.Core;

/// <summary>
/// The version <c>--version</c> prints and the MCP <c>initialize</c> response reports.
/// </summary>
public static class ProductVersion
{
    /// <summary>
    /// The informational version of <paramref name="assembly"/> without the <c>+commit</c>
    /// build metadata the SDK appends, so it equals the package version: <c>0.6.0</c> for a
    /// release, <c>0.6.0-local</c> for a build installed by <c>install-local.ps1</c>.
    /// </summary>
    public static string Of(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString(3) ?? "0.0.0"
            : informational.Split('+')[0];
    }

    /// <summary>
    /// Whether <paramref name="arg"/> asks for the version: <c>--version</c> or <c>-v</c>.
    /// </summary>
    public static bool IsVersionFlag(string arg) =>
        arg.Equals("--version", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("-v", StringComparison.OrdinalIgnoreCase);
}
