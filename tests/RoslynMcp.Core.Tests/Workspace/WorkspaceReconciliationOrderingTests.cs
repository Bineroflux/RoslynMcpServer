using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

/// <summary>
/// Filesystem-driven updates of the workspace, the way the file watcher drives them. The
/// ordering tests interleave an update with an operation that commits while the update waits
/// for its lease, as back-to-back tool calls do: hold an operation lease, start the update,
/// wait on the gate until it has queued, change disk and solution as an operation would, then
/// release the lease. The contexts here have no file watchers, so only the test touches them.
/// </summary>
public sealed class WorkspaceReconciliationOrderingTests
{
    [Fact]
    public async Task SyncDocumentFromDiskAsync_QueuedWhileAnOperationCommits_KeepsTheCommittedText()
    {
        var ct = TestContext.Current.CancellationToken;
        const string edited = "internal sealed class Widget\n{\n    public int Edit1() => 1;\n}\n";
        const string committed = "internal sealed class Widget\n{\n    public int Edit1() => 1;\n    public int Edit2() => 2;\n}\n";
        using var ws = TestWorkspace.Create(("Widget.cs", edited));
        var ctx = ws.Context;
        var path = Path.Combine(ws.Directory, "Widget.cs");

        // The previous operation left `edited` on disk and in the solution; the watcher event
        // for that write queues its update while the next operation holds its lease.
        await ctx.EnterOperationAsync(ct);
        Task<DiskSyncOutcome> sync;
        try
        {
            sync = ctx.SyncDocumentFromDiskAsync(path, ct);
            await ctx.WaitUntilFileUpdateQueuedAsync(ct);

            var document = ctx.GetDocumentByPath(path)!;
            var commit = await ctx.CommitChangesAsync(
                ctx.Solution.WithDocumentText(document.Id, SourceText.From(committed)), ct);
            Assert.True(commit.Success, commit.Error);
            Assert.False(sync.IsCompleted, "The update must wait while the operation holds its lease.");
        }
        finally
        {
            ctx.ExitOperation();
        }

        // The update reads the disk under its lease, after the commit: nothing to apply. Had it
        // read the file when it queued, it would put `edited` back over the commit.
        var outcome = await sync;
        Assert.Equal(committed, (await ctx.GetDocumentByPath(path)!.GetTextAsync(ct)).ToString());
        Assert.Equal(DiskSyncOutcome.Unchanged, outcome);
        Assert.Equal(committed, await File.ReadAllTextAsync(path, ct));
    }

    [Fact]
    public async Task SyncDocumentFromDiskAsync_FileChangedAfterQueuing_AppliesTheLatestText()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ws = TestWorkspace.Create(("Widget.cs", "internal sealed class Widget { }\n"));
        var ctx = ws.Context;
        var path = Path.Combine(ws.Directory, "Widget.cs");

        await ctx.EnterOperationAsync(ct);
        Task<DiskSyncOutcome> sync;
        try
        {
            await File.WriteAllTextAsync(path, "internal sealed class Widget { int _first; }\n", ct);
            sync = ctx.SyncDocumentFromDiskAsync(path, ct);
            await ctx.WaitUntilFileUpdateQueuedAsync(ct);

            // A second external edit lands before the first one's update gets the lease.
            await File.WriteAllTextAsync(path, "internal sealed class Widget { int _second; }\n", ct);
        }
        finally
        {
            ctx.ExitOperation();
        }

        Assert.Equal(DiskSyncOutcome.Updated, await sync);
        Assert.Equal(
            "internal sealed class Widget { int _second; }\n",
            (await ctx.GetDocumentByPath(path)!.GetTextAsync(ct)).ToString());
    }

    [Fact]
    public async Task SyncDocumentFromDiskAsync_TextAlreadyInSolution_KeepsTheSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ws = TestWorkspace.Create(("Widget.cs", "internal sealed class Widget { }\n"));
        var ctx = ws.Context;
        var before = ctx.Solution;

        Assert.Equal(DiskSyncOutcome.Unchanged, await ctx.SyncDocumentFromDiskAsync(Path.Combine(ws.Directory, "Widget.cs"), ct));
        Assert.Same(before, ctx.Solution);

        Assert.Equal(DiskSyncOutcome.Missing, await ctx.SyncDocumentFromDiskAsync(Path.Combine(ws.Directory, "Gone.cs"), ct));
        await File.WriteAllTextAsync(Path.Combine(ws.Directory, "New.cs"), "internal sealed class New { }\n", ct);
        Assert.Equal(DiskSyncOutcome.NotTracked, await ctx.SyncDocumentFromDiskAsync(Path.Combine(ws.Directory, "New.cs"), ct));
        Assert.Same(before, ctx.Solution);
    }

    [Fact]
    public async Task SyncDocumentFromDiskAsync_FileBasedProgramDirectivesChanged_RequiresReloadAndKeepsTheSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"RoslynMcpServer.ReconcileOrdering-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var entry = Path.Combine(directory, "app.cs");
            const string original = "#:property LangVersion=preview\nConsole.WriteLine(1);\n";
            await File.WriteAllTextAsync(entry, original, ct);

            using var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var solution = workspace.CurrentSolution
                .AddProject(ProjectInfo.Create(projectId, VersionStamp.Default, "app", "app", LanguageNames.CSharp))
                .AddDocument(DocumentId.CreateNewId(projectId), "app.cs", SourceText.From(original), filePath: entry);
            using var ctx = new WorkspaceContext(
                workspace, solution, entry,
                fileBasedProgram: new FileBasedProgramInfo(
                    PathResolver.NormalizePath(entry), FileBasedProgramProject.ComputeDirectiveSignature(original)));

            // A code-only edit is applied in place.
            const string codeEdit = "#:property LangVersion=preview\nConsole.WriteLine(2);\n";
            await File.WriteAllTextAsync(entry, codeEdit, ct);
            Assert.Equal(DiskSyncOutcome.Updated, await ctx.SyncDocumentFromDiskAsync(entry, ct));
            Assert.Equal(codeEdit, (await ctx.GetDocumentByPath(entry)!.GetTextAsync(ct)).ToString());

            // A directive edit asks for a rebuild and leaves the solution alone.
            var before = ctx.Solution;
            await File.WriteAllTextAsync(entry, "#:property LangVersion=latest\nConsole.WriteLine(2);\n", ct);
            Assert.Equal(DiskSyncOutcome.ReloadRequired, await ctx.SyncDocumentFromDiskAsync(entry, ct));
            Assert.Same(before, ctx.Solution);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }
}
