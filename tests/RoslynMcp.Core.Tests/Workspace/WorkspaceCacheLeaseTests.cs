using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Rename;
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

    [Fact]
    public async Task DisposedHandle_ThrowsWhenUsed_WhileOtherHandlesWork()
    {
        var ct = TestContext.Current.CancellationToken;
        const string widget = "internal sealed class Widget\n{\n}\n";
        var directory = Path.Combine(Path.GetTempPath(), $"RoslynMcpServer.CacheLease-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Widget.cs");
        File.WriteAllText(path, widget);
        var solutionPath = Path.Combine(directory, "App.slnx");
        using var cache = new WorkspaceCache(idleTtl: TimeSpan.FromHours(1), sweepInterval: TimeSpan.FromHours(1));
        try
        {
            var (released, _) = await cache.GetOrCreateAsync(solutionPath, _ => Task.FromResult(Load()), ct);
            var (held, _) = await cache.GetOrCreateAsync(solutionPath, _ => throw new InvalidOperationException("loaded twice"), ct);
            var resolverMadeEarlier = released.CreateSymbolResolver();
            var renameMadeEarlier = new RenameSymbolOperation(released);
            released.Dispose();

            // Without its lease, file updates may change the solution under it: every use throws.
            Assert.Throws<ObjectDisposedException>(() => released.Solution);
            Assert.Throws<ObjectDisposedException>(() => released.Workspace);
            Assert.Throws<ObjectDisposedException>(() => released.GetDocumentByPath(path));
            Assert.Throws<ObjectDisposedException>(() => released.CreateSymbolResolver());
            Assert.Throws<ObjectDisposedException>(() => released.CreateGeneralSymbolResolver());
            Assert.Throws<ObjectDisposedException>(() => released.CreateReferenceTracker());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => resolverMadeEarlier.FindTypeInFileAsync(path, "Widget", cancellationToken: ct));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => released.RunWriterAsync(_ => Task.CompletedTask, ct));

            // A refactoring made before the release fails without writing anything.
            var refused = await Assert.ThrowsAsync<RefactoringException>(() => renameMadeEarlier.ExecuteAsync(
                new RenameSymbolParams { SourceFile = path, SymbolName = "Widget", NewName = "Gadget" }, ct));
            Assert.IsType<ObjectDisposedException>(refused.InnerException);
            Assert.Equal(widget, File.ReadAllText(path));

            // Its state, a second Dispose and the other caller's handle are unaffected.
            Assert.Equal(WorkspaceState.Ready, released.State);
            released.Dispose();
            Assert.NotNull(held.GetDocumentByPath(path));
            held.Dispose();
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { /* best effort */ }
        }

        WorkspaceContext Load()
        {
            var workspace = new AdhocWorkspace();
            var project = workspace.CurrentSolution.AddProject("App", "App", LanguageNames.CSharp);
            var solution = project.AddDocument("Widget.cs", SourceText.From(widget), filePath: path).Project.Solution;
            return new WorkspaceContext(workspace, solution, solutionPath);
        }
    }
}
