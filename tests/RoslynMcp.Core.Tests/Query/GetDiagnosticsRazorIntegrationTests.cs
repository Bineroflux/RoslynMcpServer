using System.Diagnostics;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Integration tests for <see cref="GetDiagnosticsOperation"/> against a real
/// Razor (Blazor) project. Validates that:
/// - .razor is accepted as a sourceFile filter
/// - CS errors from @code blocks are mapped back to the .razor path
/// - RZ-prefixed Razor parser errors surface and are filtered correctly
/// - Clean .razor files return no error/warning-level diagnostics
///
/// Tests skip cleanly when the Razor SDK / AspNetCore shared framework isn't
/// available in the test environment.
/// </summary>
public sealed class GetDiagnosticsRazorIntegrationTests
{
    [Fact]
    public async Task GetDiagnostics_FilteredToCsErrorRazor_ReturnsCsDiagnosticMappedToSource()
    {
        using var fixture = RazorFixture.TryCreate(out var skip);
        if (skip is not null) Assert.Skip(skip);

        using var provider = new MSBuildWorkspaceProvider();
        using var ctx = await provider.CreateContextAsync(
            fixture!.SolutionPath, TestContext.Current.CancellationToken);

        var op = new GetDiagnosticsOperation(ctx);
        var result = await op.ExecuteAsync(
            new GetDiagnosticsParams
            {
                SourceFile = fixture.CsErrorRazor,
                SeverityFilter = "Error"
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);

        var diagnostics = result.Data!.Diagnostics;
        Assert.NotEmpty(diagnostics);
        Assert.All(diagnostics, d =>
            Assert.EndsWith("CsError.razor", d.File ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(diagnostics, d => d.Id == "CS0029");
    }

    [Fact]
    public async Task GetDiagnostics_FilteredToRazorErrorRazor_ReturnsRzDiagnosticMappedToSource()
    {
        using var fixture = RazorFixture.TryCreate(out var skip);
        if (skip is not null) Assert.Skip(skip);

        using var provider = new MSBuildWorkspaceProvider();
        using var ctx = await provider.CreateContextAsync(
            fixture!.SolutionPath, TestContext.Current.CancellationToken);

        var op = new GetDiagnosticsOperation(ctx);

        // First, run unfiltered to capture what Roslyn actually surfaces — useful
        // when the assertion below fails on a fresh SDK version.
        var allResult = await op.ExecuteAsync(
            new GetDiagnosticsParams { SeverityFilter = "Error" },
            TestContext.Current.CancellationToken);
        Assert.True(allResult.Success);
        var allDump = string.Join("\n  ",
            allResult.Data!.Diagnostics.Select(d => $"{d.Id} {d.Severity} {d.File}:{d.Line}:{d.Column} - {d.Message}"));

        var result = await op.ExecuteAsync(
            new GetDiagnosticsParams
            {
                SourceFile = fixture.RazorErrorRazor,
                SeverityFilter = "Error"
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);

        var diagnostics = result.Data!.Diagnostics;
        Assert.True(diagnostics.Count > 0,
            $"Expected at least one diagnostic for RazorError.razor.\nUnfiltered diagnostics were:\n  {allDump}");
        Assert.Contains(diagnostics, d => d.Id.StartsWith("RZ", StringComparison.Ordinal));
        Assert.All(diagnostics, d =>
            Assert.EndsWith("RazorError.razor", d.File ?? string.Empty, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetDiagnostics_FilteredToCleanRazor_ReturnsNoErrorOrWarning()
    {
        using var fixture = RazorFixture.TryCreate(out var skip);
        if (skip is not null) Assert.Skip(skip);

        using var provider = new MSBuildWorkspaceProvider();
        using var ctx = await provider.CreateContextAsync(
            fixture!.SolutionPath, TestContext.Current.CancellationToken);

        var op = new GetDiagnosticsOperation(ctx);
        var result = await op.ExecuteAsync(
            new GetDiagnosticsParams
            {
                SourceFile = fixture.CounterRazor,
                SeverityFilter = "Warning"
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        // The synthetic RMCP0001 has File == null so it never matches a sourceFile filter.
        Assert.Empty(result.Data!.Diagnostics);
    }

    [Fact]
    public async Task GetDiagnostics_FilteredToUnclosedTagRazor_EmitsRz9980()
    {
        using var fixture = RazorFixture.TryCreate(out var skip);
        if (skip is not null) Assert.Skip(skip);

        using var provider = new MSBuildWorkspaceProvider();
        using var ctx = await provider.CreateContextAsync(
            fixture!.SolutionPath, TestContext.Current.CancellationToken);

        var op = new GetDiagnosticsOperation(ctx);
        var result = await op.ExecuteAsync(
            new GetDiagnosticsParams
            {
                SourceFile = fixture.UnclosedTagRazor,
                SeverityFilter = "Error"
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);

        var diagnostics = result.Data!.Diagnostics;
        Assert.NotEmpty(diagnostics);
        Assert.Contains(diagnostics, d => d.Id == "RZ9980");
        Assert.All(diagnostics, d =>
            Assert.EndsWith("UnclosedTag.razor", d.File ?? string.Empty, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetDiagnostics_TxtFile_ThrowsValidation()
    {
        using var fixture = RazorFixture.TryCreate(out var skip);
        if (skip is not null) Assert.Skip(skip);

        using var provider = new MSBuildWorkspaceProvider();
        using var ctx = await provider.CreateContextAsync(
            fixture!.SolutionPath, TestContext.Current.CancellationToken);

        var op = new GetDiagnosticsOperation(ctx);
        var ex = await Assert.ThrowsAsync<RoslynMcp.Core.Refactoring.RefactoringException>(
            () => op.ExecuteAsync(
                new GetDiagnosticsParams { SourceFile = fixture.RazorErrorRazor + ".txt" },
                TestContext.Current.CancellationToken));
        Assert.Equal(RoslynMcp.Contracts.Errors.ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    /// <summary>
    /// Locates the testdata/RazorSolution fixture, copies it to a temp dir,
    /// and runs <c>dotnet restore</c>. Returns null + a skip reason if any
    /// step fails so the test can skip cleanly on environments without the
    /// Razor SDK or AspNetCore shared framework.
    /// </summary>
    private sealed class RazorFixture : IDisposable
    {
        public string RootDir { get; }
        public string SolutionPath { get; }
        public string CounterRazor { get; }
        public string CsErrorRazor { get; }
        public string RazorErrorRazor { get; }
        public string UnclosedTagRazor { get; }

        private RazorFixture(
            string rootDir, string solutionPath,
            string counter, string csError, string razorError, string unclosedTag)
        {
            RootDir = rootDir;
            SolutionPath = solutionPath;
            CounterRazor = counter;
            CsErrorRazor = csError;
            RazorErrorRazor = razorError;
            UnclosedTagRazor = unclosedTag;
        }

        public static RazorFixture? TryCreate(out string? skipReason)
        {
            if (!ModuleInitializer.MsBuildAvailable)
            {
                skipReason = $"MSBuild not available: {ModuleInitializer.MsBuildError}";
                return null;
            }

            string source;
            try
            {
                source = LocateFixtureSource();
            }
            catch (DirectoryNotFoundException ex)
            {
                skipReason = ex.Message;
                return null;
            }

            var dest = Path.Combine(Path.GetTempPath(),
                $"RoslynMcpServer.RazorIntegrationTests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dest);
            CopyDirectory(source, dest);

            var slnPath = Path.Combine(dest, "RazorSolution.slnx");
            var projDir = Path.Combine(dest, "RazorProject");

            var restoreSkip = TryRestore(slnPath);
            if (restoreSkip is not null)
            {
                skipReason = restoreSkip;
                TryDelete(dest);
                return null;
            }

            skipReason = null;
            return new RazorFixture(
                dest,
                slnPath,
                Path.Combine(projDir, "Counter.razor"),
                Path.Combine(projDir, "CsError.razor"),
                Path.Combine(projDir, "RazorError.razor"),
                Path.Combine(projDir, "UnclosedTag.razor"));
        }

        private static string LocateFixtureSource()
        {
            var probe = AppContext.BaseDirectory;
            for (var i = 0; i < 8; i++)
            {
                var candidate = Path.Combine(probe, "testdata", "RazorSolution");
                if (Directory.Exists(candidate)) return candidate;
                var parent = Directory.GetParent(probe);
                if (parent == null) break;
                probe = parent.FullName;
            }
            throw new DirectoryNotFoundException("Could not locate testdata/RazorSolution.");
        }

        private static void CopyDirectory(string source, string dest)
        {
            foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dir.Replace(source, dest, StringComparison.Ordinal));
            }
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                File.Copy(file, file.Replace(source, dest, StringComparison.Ordinal), overwrite: true);
            }
        }

        private static string? TryRestore(string slnPath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("restore");
                psi.ArgumentList.Add(slnPath);

                using var proc = Process.Start(psi);
                if (proc == null) return "Could not start 'dotnet restore'.";

                var stdoutTask = Task.Run(() => proc.StandardOutput.ReadToEnd());
                var stderrTask = Task.Run(() => proc.StandardError.ReadToEnd());

                if (!proc.WaitForExit(60_000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    return "'dotnet restore' timed out after 60s.";
                }
                Task.WaitAll([stdoutTask, stderrTask], TimeSpan.FromSeconds(3));

                var assetsFile = Path.Combine(
                    Path.GetDirectoryName(slnPath)!,
                    "RazorProject", "obj", "project.assets.json");
                if (!File.Exists(assetsFile))
                {
                    return $"'dotnet restore' did not produce project.assets.json " +
                           $"(exit {proc.ExitCode}). stderr: {stderrTask.Result.Trim()}";
                }
                return null;
            }
            catch (Exception ex)
            {
                return $"'dotnet restore' could not run: {ex.Message}";
            }
        }

        private static void TryDelete(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
        }

        public void Dispose() => TryDelete(RootDir);
    }
}
