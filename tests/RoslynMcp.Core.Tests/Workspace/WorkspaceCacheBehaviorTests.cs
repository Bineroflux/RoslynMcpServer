using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

/// <summary>
/// Integration tests for <see cref="WorkspaceCache"/> behavior under parallel load
/// and incremental filesystem updates.
/// </summary>
public sealed class WorkspaceCacheBehaviorTests
{
    private const int ParallelCallers = 20;
    private static readonly TimeSpan ParallelDeadline = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan IncrementalUpdateDeadline = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task CreateContextAsync_ParallelRequests_LoadsSolutionOnceAndDoesNotDeadlock()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        using var working = TempSolutionCopy.Create();

        var missCount = 0;
        var cache = new WorkspaceCache(
            idleTtl: TimeSpan.FromHours(1),
            sweepInterval: TimeSpan.FromHours(1))
        {
            LogCallback = msg =>
            {
                if (msg.StartsWith("Cache miss", StringComparison.Ordinal))
                    Interlocked.Increment(ref missCount);
            }
        };
        using var provider = new MSBuildWorkspaceProvider(cache: cache);

        // Kick off ParallelCallers requests as close to simultaneously as possible.
        // The `gate` makes sure they all unblock at once, rather than serializing
        // on the task-scheduler.
        var gate = new TaskCompletionSource();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(ParallelDeadline);

        var tasks = Enumerable.Range(0, ParallelCallers).Select(_ => Task.Run(async () =>
        {
            await gate.Task;
            using var ctx = await provider.CreateContextAsync(working.SolutionPath, cts.Token);
            // Touch the solution so we can confirm every caller got a live workspace.
            var projectCount = ctx.Solution.Projects.Count();
            Assert.True(projectCount > 0, "Solution should contain at least one project.");
            return ctx;
        }, cts.Token)).ToArray();

        var wallClock = Stopwatch.StartNew();
        gate.SetResult();

        var contexts = await Task.WhenAll(tasks);
        wallClock.Stop();

        Assert.Equal(1, missCount);
        Assert.True(wallClock.Elapsed < ParallelDeadline,
            $"Parallel calls took {wallClock.Elapsed}, expected < {ParallelDeadline}.");

        // Every caller should observe the same underlying context instance.
        var firstContext = contexts[0];
        foreach (var c in contexts)
            Assert.Same(firstContext, c);
    }

    [Fact]
    public async Task CreateContextAsync_WhenCsFileChangesOnDisk_ReflectsChangeInWorkspace()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        using var working = TempSolutionCopy.Create();

        var cache = new WorkspaceCache(
            idleTtl: TimeSpan.FromHours(1),
            sweepInterval: TimeSpan.FromHours(1));
        using var provider = new MSBuildWorkspaceProvider(cache: cache);

        using var ctx = await provider.CreateContextAsync(
            working.SolutionPath, TestContext.Current.CancellationToken);

        var modelsPath = Path.Combine(working.ProjectDir, "Models.cs");
        Assert.True(File.Exists(modelsPath), $"Models.cs not found at {modelsPath}.");

        // Baseline: the marker type must not exist in the workspace before we edit.
        const string markerClassName = "FileWatcherIncrementalUpdateMarker";
        Assert.False(
            await TypeExistsInWorkspaceAsync(ctx, markerClassName, TestContext.Current.CancellationToken),
            $"Marker type '{markerClassName}' unexpectedly exists before the file edit.");

        var originalText = await File.ReadAllTextAsync(modelsPath, TestContext.Current.CancellationToken);

        // Edit the file on disk. WriteAllText replaces the full contents and bumps
        // LastWrite, which is what the FileSystemWatcher in the cache listens for.
        var newContent = originalText +
            $"\n\nnamespace TestProject.Models {{ public class {markerClassName} {{ }} }}\n";
        await File.WriteAllTextAsync(modelsPath, newContent, TestContext.Current.CancellationToken);

        // Poll via Roslyn symbol lookup. This proves the compilation — not just
        // the document text — has been updated in response to the filesystem change.
        var deadline = DateTime.UtcNow + IncrementalUpdateDeadline;
        while (DateTime.UtcNow < deadline)
        {
            if (await TypeExistsInWorkspaceAsync(ctx, markerClassName, TestContext.Current.CancellationToken))
                return;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Fail(
            $"Symbol '{markerClassName}' did not appear in any project compilation within " +
            $"{IncrementalUpdateDeadline} after modifying Models.cs on disk.");
    }

    /// <summary>
    /// Uses Roslyn's <see cref="SymbolFinder"/> to search every project in the
    /// solution for a type declaration with the given name. Returns true as soon
    /// as a match is found.
    /// </summary>
    private static async Task<bool> TypeExistsInWorkspaceAsync(
        WorkspaceContext ctx, string typeName, CancellationToken cancellationToken)
    {
        foreach (var project in ctx.Solution.Projects)
        {
            var found = await SymbolFinder.FindDeclarationsAsync(
                project, typeName, ignoreCase: false, SymbolFilter.Type, cancellationToken);
            if (found.Any(s => s is INamedTypeSymbol named
                && string.Equals(named.Name, typeName, StringComparison.Ordinal)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Copies the checked-in test solution to a unique temp directory so tests
    /// can mutate the working tree without races against other tests or the repo.
    /// </summary>
    private sealed class TempSolutionCopy : IDisposable
    {
        public string RootDir { get; }
        public string SolutionPath { get; }
        public string ProjectDir { get; }

        private TempSolutionCopy(string rootDir, string solutionPath, string projectDir)
        {
            RootDir = rootDir;
            SolutionPath = solutionPath;
            ProjectDir = projectDir;
        }

        public static TempSolutionCopy Create()
        {
            var source = LocateTestSolution();
            var dest = Path.Combine(Path.GetTempPath(),
                $"RoslynMcpServer.WorkspaceCacheTests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dest);
            CopyDirectory(source, dest);

            var slnPath = Path.Combine(dest, "TestSolution.sln");
            var projDir = Path.Combine(dest, "TestProject");
            if (!File.Exists(slnPath))
                throw new FileNotFoundException($"Copied TestSolution.sln missing at {slnPath}.");
            if (!Directory.Exists(projDir))
                throw new DirectoryNotFoundException($"Copied TestProject dir missing at {projDir}.");

            return new TempSolutionCopy(dest, slnPath, projDir);
        }

        private static string LocateTestSolution()
        {
            // The test binary lives at .../tests/RoslynMcp.Core.Tests/bin/<cfg>/<tfm>/...
            // testdata/TestSolution lives at the repo root.
            var probe = AppContext.BaseDirectory;
            for (var i = 0; i < 8; i++)
            {
                var candidate = Path.Combine(probe, "testdata", "TestSolution");
                if (Directory.Exists(candidate)) return candidate;
                var parent = Directory.GetParent(probe);
                if (parent == null) break;
                probe = parent.FullName;
            }
            throw new DirectoryNotFoundException("Could not locate testdata/TestSolution.");
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

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootDir))
                    Directory.Delete(RootDir, recursive: true);
            }
            catch
            {
                // File watchers may still be unhooking; ignore cleanup failures.
            }
        }
    }
}
