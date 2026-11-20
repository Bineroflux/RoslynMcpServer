using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Rename;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

/// <summary>
/// Every RoslynMcp process (each server, the CLI) holds one lock file while a commit checks its
/// files against the disk and writes them, so two processes cannot interleave between one's check
/// and its write. Two contexts with writer coordinators of their own stand for two processes
/// here; they share only the lock file, which each test puts in its own directory. The hooks of
/// <see cref="CrossProcessWriteLock"/> order the steps, so nothing depends on timing.
/// </summary>
public sealed class CrossProcessWriteLockTests
{
    private const string Widget =
        "namespace App;\n\ninternal sealed class Widget\n{\n    public int Alpha() => 1;\n\n    public int Beta() => Alpha() + 1;\n}\n";

    [Fact]
    public async Task CommitsOfTwoProcesses_CheckAndWriteOneAfterTheOther_SoTheSecondSeesTheFirstsWrite()
    {
        var ct = TestContext.Current.CancellationToken;
        using var first = TestWorkspace.Create(("Widget.cs", Widget));
        var path = first.PathOf("Widget.cs");
        var secondWorkspace = new AdhocWorkspace();
        var project = secondWorkspace.CurrentSolution.AddProject("Second", "Second", LanguageNames.CSharp);
        using var second = new WorkspaceContext(
            secondWorkspace,
            project.AddDocument("Widget.cs", SourceText.From(Widget), filePath: path).Project.Solution,
            first.Directory);

        var lockPath = Path.Combine(first.Directory, "locks", "writers.lock");
        var events = new ConcurrentQueue<string>();
        var firstHolds = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWaits = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstLock = new CrossProcessWriteLock(lockPath)
        {
            Acquired = async _ =>
            {
                events.Enqueue("first locks");
                firstHolds.TrySetResult();
                await releaseFirst.Task;
            },
            Releasing = () => events.Enqueue("first releases"),
        };
        var secondLock = new CrossProcessWriteLock(lockPath)
        {
            Acquired = _ =>
            {
                events.Enqueue("second locks");
                return Task.CompletedTask;
            },
            Releasing = () => events.Enqueue("second releases"),
            Contended = () => secondWaits.TrySetResult(),
        };
        var firstWriters = new WorkspaceWriteCoordinator(firstLock);
        first.Context.ShareWriters(firstWriters, firstWriters.Sequence);
        var secondWriters = new WorkspaceWriteCoordinator(secondLock);
        second.ShareWriters(secondWriters, secondWriters.Sequence);
        first.Context.BeforeCommitReadsDisk = _ => Enqueue("first checks");
        second.BeforeCommitReadsDisk = _ => Enqueue("second checks");

        var firstId = first.Context.GetDocumentByPath(path)!.Id;
        var secondId = second.GetDocumentByPath(path)!.Id;
        var firstText = Widget.Replace("Alpha", "Bravo", StringComparison.Ordinal);
        var firstCommit = first.Context.CommitInWriterAsync(s => s.WithDocumentText(firstId, SourceText.From(firstText)), ct);
        await firstHolds.Task;

        // Both have computed their change from the same text; the second waits for the lock.
        var secondCommit = second.CommitInWriterAsync(
            s => s.WithDocumentText(secondId, SourceText.From(Widget.Replace("=> 1", "=> 2", StringComparison.Ordinal))), ct);
        await secondWaits.Task;
        Assert.Equal(new[] { "first locks" }, events);

        releaseFirst.SetResult();
        var firstResult = await firstCommit;
        var secondResult = await secondCommit;

        Assert.True(firstResult.Success, firstResult.Error);
        Assert.Equal(new[] { "first locks", "first checks", "first releases", "second locks", "second checks", "second releases" }, events);

        // The second checked after the first had written: it found the file changed and wrote nothing.
        Assert.False(secondResult.Success);
        Assert.Equal(new[] { new DiskConflict(path, DiskConflictKind.Changed) }, secondResult.Conflicts);
        Assert.Equal(firstText, first.ReadFile("Widget.cs"));

        // Released both times: nobody holds the lock file now.
        using (File.OpenHandle(lockPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
        }

        Task Enqueue(string step)
        {
            events.Enqueue(step);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Refactoring_LockHeldLongerThanTheTimeout_FailsWithWorkspaceBusy_AndWritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ws = TestWorkspace.Create(("Widget.cs", Widget));
        var lockPath = Path.Combine(ws.Directory, "writers.lock");
        var writers = new WorkspaceWriteCoordinator(new CrossProcessWriteLock(lockPath, TimeSpan.FromMilliseconds(200)));
        ws.Context.ShareWriters(writers, writers.Sequence);
        var rename = new RenameSymbolParams { SourceFile = ws.PathOf("Widget.cs"), SymbolName = "Beta", NewName = "Bravo" };

        // Another process holds the lock.
        using (File.OpenHandle(lockPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.None))
        {
            var failed = await Assert.ThrowsAsync<RefactoringException>(() => new RenameSymbolOperation(ws.Context).ExecuteAsync(rename, ct));
            Assert.Equal(ErrorCodes.WorkspaceBusy, failed.ErrorCode);
            Assert.Contains($"('{lockPath}')", failed.Message, StringComparison.Ordinal);
            Assert.Contains("Nothing was written", failed.Message, StringComparison.Ordinal);
        }

        Assert.Equal(Widget, ws.ReadFile("Widget.cs"));
        Assert.Equal(Widget, (await ws.Context.GetDocumentByPath(ws.PathOf("Widget.cs"))!.GetTextAsync(ct)).ToString());
        Assert.Equal(WorkspaceState.Ready, ws.Context.State);

        // Once it is free, the same refactoring goes through.
        var result = await new RenameSymbolOperation(ws.Context).ExecuteAsync(rename, ct);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("public int Bravo()", ws.ReadFile("Widget.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refactoring_WaitingForTheLock_IsCancelled_AndWritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ws = TestWorkspace.Create(("Widget.cs", Widget));
        var lockPath = Path.Combine(ws.Directory, "writers.lock");
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writers = new WorkspaceWriteCoordinator(
            new CrossProcessWriteLock(lockPath, TimeSpan.FromMinutes(10)) { Contended = () => waiting.TrySetResult() });
        ws.Context.ShareWriters(writers, writers.Sequence);
        var id = ws.Context.GetDocumentByPath(ws.PathOf("Widget.cs"))!.Id;

        using (File.OpenHandle(lockPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.None))
        {
            // A refactoring reports the cancellation as such.
            using var cancelRename = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var rename = new RenameSymbolOperation(ws.Context).ExecuteAsync(
                new RenameSymbolParams { SourceFile = ws.PathOf("Widget.cs"), SymbolName = "Beta", NewName = "Bravo" }, cancelRename.Token);
            await waiting.Task;
            cancelRename.Cancel();
            Assert.Equal(ErrorCodes.Timeout, (await Assert.ThrowsAsync<RefactoringException>(() => rename)).ErrorCode);

            // A commit made directly throws it.
            waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancelCommit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var commit = ws.Context.CommitInWriterAsync(
                s => s.WithDocumentText(id, SourceText.From(Widget.Replace("=> 1", "=> 2", StringComparison.Ordinal))), cancelCommit.Token);
            await waiting.Task;
            cancelCommit.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commit);
        }

        Assert.Equal(Widget, ws.ReadFile("Widget.cs"));
        Assert.Equal(WorkspaceState.Ready, ws.Context.State);
    }

    [Fact]
    public async Task MoveTypeToNamespace_MovesTheFileWhileHoldingTheLock()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ws = TestWorkspace.Create(
            [("Widget.cs", "namespace App;\n\npublic class Widget\n{\n}\n")],
            [],
            defaultNamespace: "App");
        File.WriteAllText(ws.ProjectFilePath, "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net11.0</TargetFramework>\n    <RootNamespace>App</RootNamespace>\n  </PropertyGroup>\n</Project>\n");
        var source = ws.PathOf("Widget.cs");
        var target = Path.Combine(ws.Directory, "Services", "Widget.cs");
        var held = new List<(bool SourceExists, bool TargetExists)>();
        var released = new List<(bool SourceExists, bool TargetExists)>();
        var writers = new WorkspaceWriteCoordinator(new CrossProcessWriteLock(Path.Combine(ws.Directory, "writers.lock"))
        {
            Acquired = _ =>
            {
                held.Add((File.Exists(source), File.Exists(target)));
                return Task.CompletedTask;
            },
            Releasing = () => released.Add((File.Exists(source), File.Exists(target))),
        });
        ws.Context.ShareWriters(writers, writers.Sequence);

        var result = await new MoveTypeToNamespaceOperation(ws.Context).ExecuteAsync(
            new MoveTypeToNamespaceParams { SourceFile = source, SymbolName = "Widget", TargetNamespace = "App.Services", UpdateFileLocation = true }, ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Null(result.Error);

        // The commit (namespace changed in place), then the move: both under the lock.
        Assert.Equal(new[] { (true, false), (true, false) }, held);
        Assert.Equal(new[] { (true, false), (false, true) }, released);
    }

    [Fact]
    public void DefaultLock_IsTheUsersTempLockFile()
    {
        var writers = new WorkspaceWriteCoordinator();

        Assert.Equal(Path.Combine(Path.GetTempPath(), "RoslynMcp", "writers.lock"), writers.DiskLock.Path);
        Assert.Equal(TimeSpan.FromSeconds(30), writers.DiskLock.Timeout);
    }
}
