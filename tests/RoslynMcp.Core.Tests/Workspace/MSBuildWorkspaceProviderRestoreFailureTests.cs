using System.Diagnostics;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

/// <summary>
/// Verifies workspace loading tolerates per-project failures (e.g. NuGet
/// vulnerability advisories surfaced as MSBuild errors) the way Visual Studio
/// does, instead of aborting the entire load.
/// </summary>
public sealed class MSBuildWorkspaceProviderRestoreFailureTests
{
    [Fact]
    public async Task CreateContextAsync_WithVulnerablePackageReference_StillLoadsSolution()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        using var sln = VulnerablePackageSolution.Create();
        if (sln.SkipReason is { } skip)
        {
            Assert.Skip(skip);
        }

        // Capture failure log so we can both restore it after the test and assert
        // the audit advisory actually fired (otherwise the test isn't proving
        // the fix — it's just loading a normal project).
        var failures = new List<string>();
        var previousErrorLog = MSBuildWorkspaceProvider.LogErrorCallback;
        MSBuildWorkspaceProvider.LogErrorCallback = (msg, _) =>
        {
            lock (failures) { failures.Add(msg); }
        };

        try
        {
            using var provider = new MSBuildWorkspaceProvider();

            // Under the old code path, any Failure-kind WorkspaceDiagnostic threw
            // RefactoringException(SolutionLoadFailed). With the fix, the load
            // tolerates per-project failures and returns a usable context.
            using var ctx = await provider.CreateContextAsync(
                sln.SolutionPath, TestContext.Current.CancellationToken);

            Assert.True(ctx.Solution.ProjectIds.Count > 0,
                "Workspace should have loaded the project despite the audit advisory.");

            // The advisory must have surfaced as a workspace failure, otherwise
            // the test is not exercising the tolerated-failure path. MSBuildWorkspace
            // strips the NU190x prefix and replaces it with a localized "Warning
            // as Error" label, but the GitHub advisory ID (GHSA-…) is preserved
            // verbatim and is locale-independent.
            List<string> snapshot;
            lock (failures) { snapshot = [.. failures]; }
            Assert.NotEmpty(snapshot);
            Assert.True(
                snapshot.Any(m => m.Contains("GHSA-", StringComparison.Ordinal)),
                "Expected a workspace failure log mentioning a GHSA advisory code. Got:\n  "
                    + string.Join("\n  ", snapshot));
        }
        catch (RefactoringException ex) when (ex.ErrorCode == ErrorCodes.SolutionLoadFailed)
        {
            Assert.Fail(
                "Workspace load failed despite the fix that should tolerate audit " +
                $"advisories. Error: {ex.Message}");
        }
        finally
        {
            MSBuildWorkspaceProvider.LogErrorCallback = previousErrorLog;
        }
    }

    /// <summary>
    /// Synthesises a minimal solution + csproj on disk that references a NuGet
    /// package version with a known vulnerability advisory, restores it, and
    /// promotes the audit warnings to errors so MSBuildWorkspace surfaces them
    /// as Failure-kind diagnostics.
    /// </summary>
    private sealed class VulnerablePackageSolution : IDisposable
    {
        // Magick.NET-Q16-AnyCPU 14.11.1 has a known advisory.
        private const string VulnerablePackageId = "Magick.NET-Q16-AnyCPU";
        private const string VulnerablePackageVersion = "14.11.1";

        public string RootDir { get; }
        public string SolutionPath { get; }
        public string? SkipReason { get; }

        private VulnerablePackageSolution(string rootDir, string solutionPath, string? skipReason)
        {
            RootDir = rootDir;
            SolutionPath = solutionPath;
            SkipReason = skipReason;
        }

        public static VulnerablePackageSolution Create()
        {
            var dest = Path.Combine(Path.GetTempPath(),
                $"RoslynMcpServer.VulnPkgTests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dest);

            var projDir = Path.Combine(dest, "VulnProject");
            Directory.CreateDirectory(projDir);

            var csprojPath = Path.Combine(projDir, "VulnProject.csproj");
            File.WriteAllText(csprojPath,
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <NuGetAudit>true</NuGetAudit>
                    <NuGetAuditMode>direct</NuGetAuditMode>
                    <NuGetAuditLevel>low</NuGetAuditLevel>
                    <WarningsAsErrors>NU1901;NU1902;NU1903;NU1904;NU1905</WarningsAsErrors>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="{VulnerablePackageId}" Version="{VulnerablePackageVersion}" />
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(Path.Combine(projDir, "Program.cs"),
                "namespace VulnProject; public static class Program { public static void Main() {} }");

            var slnxPath = Path.Combine(dest, "VulnSolution.slnx");
            File.WriteAllText(slnxPath,
                """
                <Solution>
                  <Project Path="VulnProject/VulnProject.csproj" />
                </Solution>
                """);

            // Restore is required for MSBuildWorkspace to see the audit advisory.
            // If it fails (offline, network blocked, dotnet missing), skip cleanly.
            var skipReason = TryRestore(slnxPath);
            return new VulnerablePackageSolution(dest, slnxPath, skipReason);
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
                psi.ArgumentList.Add("--force-evaluate");

                using var proc = Process.Start(psi);
                if (proc == null)
                    return "Could not start 'dotnet restore'.";

                // Drain stdout/stderr concurrently. The restore emits one
                // audit-warning line per advisory, which fills the pipe buffer
                // and deadlocks the child if we just call WaitForExit.
                var stdoutTask = Task.Run(() => proc.StandardOutput.ReadToEnd());
                var stderrTask = Task.Run(() => proc.StandardError.ReadToEnd());

                if (!proc.WaitForExit(60_000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                    return "'dotnet restore' timed out after 60s.";
                }

                // Pipes close when the child exits; the drain tasks complete on
                // their own immediately after.
                Task.WaitAll([stdoutTask, stderrTask], TimeSpan.FromSeconds(3));

                // Exit code is non-zero because audit warnings were promoted to
                // errors — that is exactly the state we want to feed into the
                // workspace. Only treat this as a real failure if the assets
                // file did not get written.
                var assetsFile = Path.Combine(
                    Path.GetDirectoryName(slnPath)!,
                    "VulnProject", "obj", "project.assets.json");
                if (!File.Exists(assetsFile))
                {
                    return $"'dotnet restore' did not produce project.assets.json " +
                           $"(exit {proc.ExitCode}). " +
                           $"stdout: {stdoutTask.Result.Trim()} " +
                           $"stderr: {stderrTask.Result.Trim()}";
                }

                return null;
            }
            catch (Exception ex)
            {
                return $"'dotnet restore' could not run: {ex.Message}";
            }
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootDir))
                    Directory.Delete(RootDir, recursive: true);
            }
            catch
            {
                // Ignore — temp cleanup races with file watchers occasionally.
            }
        }
    }
}
