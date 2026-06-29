using System.Runtime.InteropServices;
using System.Text;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Regression tests for the <c>get_diagnostics</c> <c>sourceFile</c> filter. The
/// workspace normalizes document (and therefore diagnostic span) paths at load
/// time via <see cref="PathResolver.NormalizePath"/>, so the caller-supplied
/// <c>sourceFile</c> must be normalized the same way before comparison. If it is
/// not, an equivalent-but-not-byte-identical path — an 8.3 short-name component,
/// mixed <c>/</c> vs <c>\</c> separators, or <c>..</c> segments — matches nothing
/// and the filter silently drops every diagnostic for a file that genuinely has
/// errors.
///
/// These load a real <c>.cs</c> file through the ad-hoc standalone path (host
/// framework references, no SDK/MSBuild required) so they always run, then query
/// diagnostics with a deliberately denormalized-but-equivalent <c>sourceFile</c>.
/// </summary>
public sealed class GetDiagnosticsSourceFilePathNormalizationTests
{
    // `int wrong = "not an int";` => CS0029 (cannot implicitly convert string to int).
    private const string TypeErrorSource = "int wrong = \"not an int\";\n";

    [Fact]
    public async Task GetDiagnostics_SourceFileWith83ShortName_StillSurfacesDiagnostics()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("8.3 short names are a Windows-only path feature.");

        using var file = TempCsFile.Write(TypeErrorSource);
        var ct = TestContext.Current.CancellationToken;

        // The exact reported repro: a path whose components have 8.3 short forms
        // (e.g. ROBERT~1.HAE for Robert.Haeusl) that NormalizePath / Path.GetFullPath
        // expand back to the long form the workspace loaded the document under.
        var shortPath = TryGetShortPath(file.Path);
        if (shortPath is null || string.Equals(shortPath, file.Path, StringComparison.OrdinalIgnoreCase))
            Assert.Skip("8.3 short-name generation appears disabled for the temp volume.");

        Assert.Contains("~", shortPath, StringComparison.Ordinal);
        Assert.True(File.Exists(shortPath!), "short-name path should resolve to the real file");

        using var ctx = await LoadStandaloneAsync(file.Path, ct);
        await AssertCs0029Surfaces(ctx, shortPath!, ct);
    }

    [Fact]
    public async Task GetDiagnostics_SourceFileWithDotDotSegments_StillSurfacesDiagnostics()
    {
        using var file = TempCsFile.Write(TypeErrorSource);
        var ct = TestContext.Current.CancellationToken;

        // Round-trip through a '..' segment: lexically collapses back to the real
        // path (the intermediate directory need not exist), but is not byte-identical.
        var dir = Path.GetDirectoryName(file.Path)!;
        var name = Path.GetFileName(file.Path);
        var denormalized = Path.Combine(dir, "nonexistent", "..", name);

        Assert.NotEqual(file.Path, denormalized);                  // differ as raw strings
        Assert.True(File.Exists(denormalized), "'..' path should resolve to the real file");

        using var ctx = await LoadStandaloneAsync(file.Path, ct);
        await AssertCs0029Surfaces(ctx, denormalized, ct);
    }

    [Fact]
    public async Task GetDiagnostics_SourceFileWithForwardSlashes_StillSurfacesDiagnostics()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("Forward/backslash mismatch only arises where '\\' is the native separator.");

        using var file = TempCsFile.Write(TypeErrorSource);
        var ct = TestContext.Current.CancellationToken;

        var denormalized = file.Path.Replace('\\', '/');
        Assert.NotEqual(file.Path, denormalized);                  // separators differ
        Assert.True(File.Exists(denormalized), "forward-slash path should resolve to the real file");

        using var ctx = await LoadStandaloneAsync(file.Path, ct);
        await AssertCs0029Surfaces(ctx, denormalized, ct);
    }

    [Fact]
    public async Task GetDiagnostics_SourceFileForDifferentFile_ReturnsNoDiagnostics()
    {
        // The normalization fix must not turn the filter into a pass-through: a
        // genuinely different (but existing) .cs path must still match nothing.
        using var file = TempCsFile.Write(TypeErrorSource);
        using var other = TempCsFile.Write("class Other { }\n");
        var ct = TestContext.Current.CancellationToken;

        using var ctx = await LoadStandaloneAsync(file.Path, ct);

        var op = new GetDiagnosticsOperation(ctx);
        var result = await op.ExecuteAsync(
            new GetDiagnosticsParams { SourceFile = other.Path, SeverityFilter = "Error" }, ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Empty(result.Data!.Diagnostics);
    }

    private static async Task<WorkspaceContext> LoadStandaloneAsync(string filePath, CancellationToken ct)
        => await StandaloneFileWorkspace.CreateAsync(filePath, new AtomicFileWriter(), log: null, ct);

    private static async Task AssertCs0029Surfaces(WorkspaceContext ctx, string sourceFile, CancellationToken ct)
    {
        var op = new GetDiagnosticsOperation(ctx);
        var result = await op.ExecuteAsync(
            new GetDiagnosticsParams { SourceFile = sourceFile, SeverityFilter = "Error" }, ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.NotNull(result.Data);
        Assert.Contains(result.Data!.Diagnostics, d => d.Id == "CS0029");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);

    /// <summary>
    /// Returns the 8.3 short-name form of <paramref name="longPath"/>, or null if it
    /// can't be obtained (e.g. 8dot3name generation is disabled on the volume).
    /// </summary>
    private static string? TryGetShortPath(string longPath)
    {
        var sb = new StringBuilder(longPath.Length + 260);
        var len = GetShortPathName(longPath, sb, (uint)sb.Capacity);
        if (len == 0 || len > sb.Capacity) return null;
        return sb.ToString();
    }

    /// <summary>A temporary <c>.cs</c> file on disk, deleted on dispose.</summary>
    private sealed class TempCsFile : IDisposable
    {
        public string Path { get; private init; } = "";

        public static TempCsFile Write(string contents)
        {
            var path = PathResolver.NormalizePath(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"RoslynMcp.PathNorm-{Guid.NewGuid():N}.cs"));
            File.WriteAllText(path, contents);
            return new TempCsFile { Path = path };
        }

        public void Dispose()
        {
            try { if (File.Exists(Path)) File.Delete(Path); }
            catch { /* best-effort cleanup */ }
        }
    }
}
