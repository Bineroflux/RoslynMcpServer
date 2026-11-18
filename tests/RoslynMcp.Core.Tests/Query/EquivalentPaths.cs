using System.Runtime.InteropServices;
using System.Text;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Spellings of an existing file's path that name the same file without being
/// byte-identical, for testing that path parameters are normalized before comparing.
/// </summary>
internal static class EquivalentPaths
{
    /// <summary>
    /// A <c>..</c> round trip and, on Windows, forward slashes, other letter case and the
    /// other of the 8.3 short and long forms (when the volume has short names).
    /// </summary>
    public static IEnumerable<string> Of(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        yield return Path.Combine(directory, "nonexistent", "..", Path.GetFileName(path));

        if (!OperatingSystem.IsWindows())
            yield break;

        yield return path.Replace('\\', '/');
        yield return path.ToUpperInvariant();

        var shortForm = Convert(path, GetShortPathName);
        var longForm = Convert(path, GetLongPathName);
        if (shortForm is not null && !string.Equals(shortForm, path, StringComparison.OrdinalIgnoreCase))
            yield return shortForm;
        if (longForm is not null && !string.Equals(longForm, path, StringComparison.OrdinalIgnoreCase))
            yield return longForm;
    }

    private static string? Convert(string path, Func<string, StringBuilder, uint, uint> win32)
    {
        var buffer = new StringBuilder(path.Length + 260);
        var length = win32(path, buffer, (uint)buffer.Capacity);
        return length == 0 || length > buffer.Capacity ? null : buffer.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string lpszShortPath, StringBuilder lpszLongPath, uint cchBuffer);
}
