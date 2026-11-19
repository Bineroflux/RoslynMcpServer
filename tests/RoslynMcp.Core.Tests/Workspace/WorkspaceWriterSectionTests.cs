using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Rename;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

/// <summary>
/// Refactorings of one workspace run one at a time (<see cref="WorkspaceContext.RunWriterAsync{T}"/>).
/// The MCP host runs every request on its own task, so two refactorings used to read the same
/// snapshot, and whichever committed last wrote that snapshot's text over the other's edits.
/// Each test holds the first writer inside its section with a task the test completes, so the
/// order of every step is fixed.
/// </summary>
public sealed class WorkspaceWriterSectionTests
{
    private const string Widget =
        "namespace App;\n\ninternal sealed class Widget\n{\n    public int Alpha() => 1;\n\n    public int Beta() => Alpha() + 1;\n}\n";

    private const string WidgetWithGamma =
        "namespace App;\n\ninternal sealed class Widget\n{\n    public int Alpha() => 1;\n\n    public int Beta() => Alpha() + 1;\n\n    public int Gamma() => 3;\n}\n";

    [Fact]
    public async Task RunWriterAsync_SecondWriterWaitsForTheFirst_AndBuildsOnItsCommit()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ws = TestWorkspace.Create(("Widget.cs", Widget));
        var ctx = ws.Context;
        var id = ctx.GetDocumentByPath(ws.PathOf("Widget.cs"))!.Id;

        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = ctx.RunWriterAsync(async c =>
        {
            // Computes its change from the snapshot it entered with, then commits it later.
            var change = ctx.Solution.WithDocumentText(id, SourceText.From(WidgetWithGamma));
            firstEntered.SetResult();
            await releaseFirst.Task;
            return await ctx.CommitChangesAsync(change, c);
        }, ct);
        await firstEntered.Task;

        Solution? secondSaw = null;
        var second = ctx.RunWriterAsync(async c =>
        {
            secondSaw = ctx.Solution;
            var text = (await secondSaw.GetDocument(id)!.GetTextAsync(c)).ToString();
            return await ctx.CommitChangesAsync(
                secondSaw.WithDocumentText(id, SourceText.From(text.Replace("Beta", "Bravo", StringComparison.Ordinal))), c);
        }, ct);
        Assert.False(second.IsCompleted, "The second writer must wait while the first holds the section.");
        Assert.Null(secondSaw);

        releaseFirst.SetResult();
        Assert.True((await first).Success);
        Assert.True((await second).Success);

        // The second writer read the solution only after the first had committed.
        Assert.Contains("Gamma", (await secondSaw!.GetDocument(id)!.GetTextAsync(ct)).ToString(), StringComparison.Ordinal);
        var expected = WidgetWithGamma.Replace("Beta", "Bravo", StringComparison.Ordinal);
        Assert.Equal(expected, ws.ReadFile("Widget.cs"));
        Assert.Equal(expected, (await ctx.Solution.GetDocument(id)!.GetTextAsync(ct)).ToString());
    }

    [Fact]
    public async Task Refactoring_StartedWhileAnotherHoldsTheWriter_RunsAfterItsCommit_AndBothEditsSurvive()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ws = TestWorkspace.Create(("Widget.cs", Widget));
        var ctx = ws.Context;
        var id = ctx.GetDocumentByPath(ws.PathOf("Widget.cs"))!.Id;

        // The first refactoring has computed its change when the second one arrives. (It is
        // started from the test, outside the section: the section is not re-entrant.)
        var computed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commitNow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = ctx.RunWriterAsync(async c =>
        {
            var change = ctx.Solution.WithDocumentText(id, SourceText.From(WidgetWithGamma));
            computed.SetResult();
            await commitNow.Task;
            return await ctx.CommitChangesAsync(change, c);
        }, ct);
        await computed.Task;

        var rename = new RenameSymbolOperation(ctx).ExecuteAsync(
            new RenameSymbolParams { SourceFile = ws.PathOf("Widget.cs"), SymbolName = "Beta", NewName = "Bravo" }, ct);
        Assert.False(rename.IsCompleted, "rename_symbol must wait for the refactoring holding the writer.");

        commitNow.SetResult();
        var commit = await first;
        Assert.True(commit.Success, commit.Error);

        var result = await rename;
        Assert.True(result.Success, result.Error?.Message);
        var onDisk = ws.ReadFile("Widget.cs");
        Assert.Contains("public int Gamma() => 3;", onDisk, StringComparison.Ordinal);
        Assert.Contains("public int Bravo() => Alpha() + 1;", onDisk, StringComparison.Ordinal);
        Assert.DoesNotContain("Beta", onDisk, StringComparison.Ordinal);
        Assert.Equal(onDisk, (await ctx.Solution.GetDocument(id)!.GetTextAsync(ct)).ToString());
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task CommitChangesAsync_SolutionReplacedOutsideTheSection_RefusesAndWritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ws = TestWorkspace.Create(("Widget.cs", Widget));
        var ctx = ws.Context;
        var path = ws.PathOf("Widget.cs");
        var id = ctx.GetDocumentByPath(path)!.Id;

        var computed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = ctx.RunWriterAsync(async c =>
        {
            var change = ctx.Solution.WithDocumentText(id, SourceText.From(WidgetWithGamma));
            computed.SetResult();
            await proceed.Task;
            return await ctx.CommitChangesAsync(change, c);
        }, ct);
        await computed.Task;

        // Something bypasses the section and changes the solution: on this uncached context
        // nothing holds an operation lease, so an external edit is synced right away.
        const string external = "namespace App;\n\ninternal sealed class Widget\n{\n}\n";
        await File.WriteAllTextAsync(path, external, ct);
        Assert.Equal(DiskSyncOutcome.Updated, await ctx.SyncDocumentFromDiskAsync(path, ct));

        proceed.SetResult();
        var refused = await Assert.ThrowsAsync<RefactoringException>(() => writer);
        Assert.Equal(ErrorCodes.WorkspaceBusy, refused.ErrorCode);
        Assert.Equal(external, ws.ReadFile("Widget.cs"));
        Assert.Equal(external, (await ctx.Solution.GetDocument(id)!.GetTextAsync(ct)).ToString());
    }

    [Fact]
    public async Task CommitChangesAsync_AndUpdateSolution_OutsideAWriterSection_Throw()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ws = TestWorkspace.Create(("Widget.cs", Widget));
        var ctx = ws.Context;
        var id = ctx.GetDocumentByPath(ws.PathOf("Widget.cs"))!.Id;
        var change = ctx.Solution.WithDocumentText(id, SourceText.From(WidgetWithGamma));

        await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.CommitChangesAsync(change, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.GetCommitWarningsAsync(change, cancellationToken: ct));
        Assert.Throws<InvalidOperationException>(() => ctx.UpdateSolution(change));
        Assert.Equal(Widget, ws.ReadFile("Widget.cs"));

        // Inside a section a file update would wait for the section's own operation lease.
        await ctx.RunWriterAsync(
            c => Assert.ThrowsAsync<InvalidOperationException>(() => ctx.SyncDocumentFromDiskAsync(ws.PathOf("Widget.cs"), c)),
            ct);
    }

    [Fact]
    public async Task RunWriterAsync_CancelledWhileWaiting_OrBodyThrows_ReleasesTheSection()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ws = TestWorkspace.Create(("Widget.cs", Widget));
        var ctx = ws.Context;

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = ctx.RunWriterAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
        }, ct);
        await entered.Task;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var bodyRan = false;
        var waiter = ctx.RunWriterAsync(_ =>
        {
            bodyRan = true;
            return Task.CompletedTask;
        }, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.False(bodyRan);

        // A refactoring cancelled while it waits reports it as such.
        using var cancelled = new CancellationTokenSource();
        var renameTask = new RenameSymbolOperation(ctx).ExecuteAsync(
            new RenameSymbolParams { SourceFile = ws.PathOf("Widget.cs"), SymbolName = "Beta", NewName = "Bravo" }, cancelled.Token);
        cancelled.Cancel();
        Assert.Equal(ErrorCodes.Timeout, (await Assert.ThrowsAsync<RefactoringException>(() => renameTask)).ErrorCode);

        release.SetResult();
        await holder;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ctx.RunWriterAsync(_ => throw new InvalidDataException("body failed"), ct));

        // Neither the cancelled waiters nor the failed body kept the section.
        var next = ctx.RunWriterAsync(_ => Task.FromResult(42), ct);
        Assert.True(next.IsCompleted, "The section must be free again.");
        Assert.Equal(42, await next);
    }

    [Fact]
    public async Task WriterQueuedBehindAnother_WithAFileUpdatePending_AllComplete()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ws = TestWorkspace.Create(("Widget.cs", Widget));
        var ctx = ws.Context;
        var path = ws.PathOf("Widget.cs");
        var id = ctx.GetDocumentByPath(path)!.Id;

        // Two operations hold leases, as two concurrent tool calls on a cached workspace do.
        await ctx.EnterOperationAsync(ct);
        await ctx.EnterOperationAsync(ct);

        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = RunAsOperationAsync(() => ctx.RunWriterAsync(async c =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task;
            return await ctx.CommitChangesAsync(ctx.Solution.WithDocumentText(id, SourceText.From(WidgetWithGamma)), c);
        }, ct));
        await firstEntered.Task;

        var second = RunAsOperationAsync(() => ctx.RunWriterAsync(async c =>
        {
            var text = (await ctx.Solution.GetDocument(id)!.GetTextAsync(c)).ToString();
            return await ctx.CommitChangesAsync(
                ctx.Solution.WithDocumentText(id, SourceText.From(text.Replace("Beta", "Bravo", StringComparison.Ordinal))), c);
        }, ct));

        // The first commit's watcher event: queued behind both leases.
        var update = ctx.SyncDocumentFromDiskAsync(path, ct);
        await ctx.WaitUntilFileUpdateQueuedAsync(ct);
        Assert.False(second.IsCompleted);
        Assert.False(update.IsCompleted);

        releaseFirst.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30), ct);
        Assert.Equal(DiskSyncOutcome.Unchanged, await update.WaitAsync(TimeSpan.FromSeconds(30), ct));

        var expected = WidgetWithGamma.Replace("Beta", "Bravo", StringComparison.Ordinal);
        Assert.Equal(expected, ws.ReadFile("Widget.cs"));
        Assert.Equal(expected, (await ctx.Solution.GetDocument(id)!.GetTextAsync(ct)).ToString());

        // Leaves its operation lease when done, as a tool call disposing its context does.
        async Task RunAsOperationAsync(Func<Task<CommitResult>> operation)
        {
            try
            {
                Assert.True((await operation()).Success);
            }
            finally
            {
                ctx.ExitOperation();
            }
        }
    }
}
