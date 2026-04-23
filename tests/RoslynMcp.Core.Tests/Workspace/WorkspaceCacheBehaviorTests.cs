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

        var modelsPath = Path.Combine(working.ProjectDir, "Models.cs");
        Assert.True(File.Exists(modelsPath), $"Models.cs not found at {modelsPath}.");

        // Baseline: the marker type must not exist in the workspace before we edit.
        const string markerClassName = "FileWatcherIncrementalUpdateMarker";
        using (var baseline = await provider.CreateContextAsync(
                   working.SolutionPath, TestContext.Current.CancellationToken))
        {
            Assert.False(
                await TypeExistsInWorkspaceAsync(baseline, markerClassName, TestContext.Current.CancellationToken),
                $"Marker type '{markerClassName}' unexpectedly exists before the file edit.");
        }

        var originalText = await File.ReadAllTextAsync(modelsPath, TestContext.Current.CancellationToken);

        // Edit the file on disk. WriteAllText replaces the full contents and bumps
        // LastWrite, which is what the FileSystemWatcher in the cache listens for.
        var newContent = originalText +
            $"\n\nnamespace TestProject.Models {{ public class {markerClassName} {{ }} }}\n";
        await File.WriteAllTextAsync(modelsPath, newContent, TestContext.Current.CancellationToken);

        // Poll via Roslyn symbol lookup. Each iteration acquires and releases a
        // context so the queued external-text-change update can drain between
        // probes — the operation gate blocks file updates while any context
        // lease is outstanding.
        var deadline = DateTime.UtcNow + IncrementalUpdateDeadline;
        while (DateTime.UtcNow < deadline)
        {
            using (var probe = await provider.CreateContextAsync(
                       working.SolutionPath, TestContext.Current.CancellationToken))
            {
                if (await TypeExistsInWorkspaceAsync(probe, markerClassName, TestContext.Current.CancellationToken))
                    return;
            }
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Fail(
            $"Symbol '{markerClassName}' did not appear in any project compilation within " +
            $"{IncrementalUpdateDeadline} after modifying Models.cs on disk.");
    }

    [Fact]
    public async Task ApplyExternalTextChangeAsync_WhileOperationHoldsLease_DoesNotMutateSolution()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        using var working = TempSolutionCopy.Create();
        var ct = TestContext.Current.CancellationToken;

        var cache = new WorkspaceCache(
            idleTtl: TimeSpan.FromHours(1),
            sweepInterval: TimeSpan.FromHours(1));
        using var provider = new MSBuildWorkspaceProvider(cache: cache);

        var modelsPath = Path.Combine(working.ProjectDir, "Models.cs");
        Assert.True(File.Exists(modelsPath), $"Models.cs not found at {modelsPath}.");

        const string markerClassName = "UpdateBlockedWhileOperationHoldsLeaseMarker";
        var originalText = await File.ReadAllTextAsync(modelsPath, ct);
        var newText = originalText +
            $"\n\nnamespace TestProject.Models {{ public class {markerClassName} {{ }} }}\n";

        // Hold an operation lease for the entire critical section.
        var holder = await provider.CreateContextAsync(working.SolutionPath, ct);
        try
        {
            Assert.False(
                await TypeExistsInWorkspaceAsync(holder, markerClassName, ct),
                $"Marker type '{markerClassName}' unexpectedly exists before the update.");

            // Start the file update. EnterFileUpdateAsync increments the pending
            // counter synchronously before the first await, so the gate has
            // registered the pending update by the time the call returns.
            var updateTask = holder.ApplyExternalTextChangeAsync(modelsPath, newText, ct);

            // Give the update generous time to (incorrectly) apply if the gate
            // failed. A single 100 ms quantum is long enough for the state
            // machine to progress through several scheduling cycles.
            await Task.Delay(500, ct);

            Assert.False(
                updateTask.IsCompleted,
                "File update should remain queued while an operation lease is outstanding.");
            Assert.False(
                await TypeExistsInWorkspaceAsync(holder, markerClassName, ct),
                "Solution must not reflect the pending file update while the operation holds its lease.");

            // Release the operation; the queued update should now drain.
            holder.Dispose();
            holder = null;

            await updateTask.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
        finally
        {
            holder?.Dispose();
        }

        using var probe = await provider.CreateContextAsync(working.SolutionPath, ct);
        Assert.True(
            await TypeExistsInWorkspaceAsync(probe, markerClassName, ct),
            "Once the operation released, the queued file update should have applied.");
    }

    [Fact]
    public async Task ApplyExternalTextChangeAsync_WhenOperationsRaceForLease_UpdatesWinPriority()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        using var working = TempSolutionCopy.Create();
        var ct = TestContext.Current.CancellationToken;

        var cache = new WorkspaceCache(
            idleTtl: TimeSpan.FromHours(1),
            sweepInterval: TimeSpan.FromHours(1));
        using var provider = new MSBuildWorkspaceProvider(cache: cache);

        var modelsPath = Path.Combine(working.ProjectDir, "Models.cs");
        Assert.True(File.Exists(modelsPath), $"Models.cs not found at {modelsPath}.");

        const string markerClassName = "FileUpdatePriorityWinsRaceMarker";
        var originalText = await File.ReadAllTextAsync(modelsPath, ct);
        var newText = originalText +
            $"\n\nnamespace TestProject.Models {{ public class {markerClassName} {{ }} }}\n";

        const int contendingOperations = 10;

        // 1. Hold an initial lease so file updates and new ops both queue.
        var blocker = await provider.CreateContextAsync(working.SolutionPath, ct);
        try
        {
            // 2. Queue the file update. It waits because `blocker` is active.
            var updateTask = blocker.ApplyExternalTextChangeAsync(modelsPath, newText, ct);

            // 3. Fan out contending operations. Each one records whether the
            //    marker class was visible the instant it acquired its lease.
            //    If the "unfair" priority holds, every operation enters after
            //    the file update finishes and therefore sees the marker.
            var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = Enumerable.Range(0, contendingOperations).Select(_ => Task.Run(async () =>
            {
                await startGate.Task;
                using var ctx = await provider.CreateContextAsync(working.SolutionPath, ct);
                return await TypeExistsInWorkspaceAsync(ctx, markerClassName, ct);
            }, ct)).ToArray();

            startGate.SetResult();

            // Give the contending operations time to reach EnterOperationAsync
            // and queue on the gate (they block because the file update is
            // pending). The subsequent blocker.Dispose() then kicks the gate.
            await Task.Delay(250, ct);

            Assert.False(
                updateTask.IsCompleted,
                "File update should still be queued while the blocker holds its lease.");
            Assert.All(tasks, t => Assert.False(
                t.IsCompleted,
                "Contending operations must block while a file update is pending."));

            // 4. Release the blocker. The gate should hand control to the file
            //    update first, then admit the contending operations.
            blocker.Dispose();
            blocker = null;

            await updateTask.WaitAsync(TimeSpan.FromSeconds(5), ct);
            var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15), ct);

            Assert.All(results, saw => Assert.True(
                saw,
                "Every contending operation must observe the file update's effect — " +
                "if any saw the pre-update state, the file update lost the lock race."));
        }
        finally
        {
            blocker?.Dispose();
        }
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
