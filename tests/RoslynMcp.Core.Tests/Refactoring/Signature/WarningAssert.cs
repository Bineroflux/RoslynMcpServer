using System.Text.RegularExpressions;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Signature;

/// <summary>
/// Assertions on the <c>warnings</c> of the signature operations.
/// </summary>
internal static partial class WarningAssert
{
    /// <summary>
    /// The compiler-error warnings name exactly the errors (file, line, column, id) the
    /// compiler reports for the files on disk; other warnings (TODO placeholders) are ignored.
    /// </summary>
    public static void AreTheCompilerErrors(TestWorkspace ws, IEnumerable<string>? warnings)
    {
        static string Key(Match match) =>
            $"{Path.GetFileName(match.Groups["path"].Value)}({match.Groups["position"].Value}) {match.Groups["id"].Value}";

        Assert.Equal(
            ws.GetDiskCompilationErrors().Select(e => DiagnosticPattern().Match(e)).Select(Key).Order(StringComparer.Ordinal),
            (warnings ?? []).Select(w => DiagnosticPattern().Match(w)).Where(m => m.Success).Select(Key).Order(StringComparer.Ordinal));
    }

    [GeneratedRegex(@"^(?<path>.+)\((?<position>\d+,\d+)\): error (?<id>CS\d+)")]
    private static partial Regex DiagnosticPattern();
}
