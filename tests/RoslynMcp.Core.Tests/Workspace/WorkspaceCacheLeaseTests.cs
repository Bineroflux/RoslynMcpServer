using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

/// <summary>
/// Every caller of <see cref="WorkspaceCache.GetOrCreateAsync"/> gets a handle of its own on the
/// cached workspace, and disposing it releases that caller's lease once. All callers used to share
/// one context object, so a second <c>Dispose</c> released a lease another caller still held: file
/// updates could change the solution under that caller, and an invalidated workspace was torn down
/// while it was still in use.
/// </summary>
public sealed class WorkspaceCacheLeaseTests
{
    [Fact]
    public async Task DisposingAHandleTwice_ReleasesOnlyItsOwnLease()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"RoslynMcpServer.CacheLease-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var solutionPath = Path.Combine(directory, "App.slnx");
        var loads = 0;
        using var cache = new WorkspaceCache(idleTtl: TimeSpan.FromHours(1), sweepInterval: TimeSpan.FromHours(1));
        try
        {
            var (first, _) = await cache.GetOrCreateAsync(solutionPath, LoadAsync, ct);
            var (second, _) = await cache.GetOrCreateAsync(solutionPath, LoadAsync, ct);
            Assert.Equal(1, loads);
            Assert.NotSame(first, second);
            Assert.Same(first.Workspace, second.Workspace);

            // Invalidated while both leases are held: torn down once the last one is released.
            cache.Invalidate(solutionPath);

            first.Dispose();
            first.Dispose();
            Assert.NotEqual(WorkspaceState.Disposed, second.State);

            // The second caller still holds its operation lease, so a file update waits.
            var update = second.SyncDocumentFromDiskAsync(Path.Combine(directory, "Widget.cs"), ct);
            await second.WaitUntilFileUpdateQueuedAsync(ct);
            Assert.False(update.IsCompleted, "A file update must wait while the second caller holds its lease.");

            second.Dispose();
            Assert.Equal(WorkspaceState.Disposed, second.State);

            // The update then runs, or finds the workspace torn down; either way it ends.
            try
            {
                await update.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
            catch (ObjectDisposedException)
            {
            }
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { /* best effort */ }
        }

        Task<WorkspaceContext> LoadAsync(CancellationToken _)
        {
            Interlocked.Increment(ref loads);
            var workspace = new AdhocWorkspace();
            return Task.FromResult(new WorkspaceContext(workspace, workspace.CurrentSolution, solutionPath));
        }
    }
}
