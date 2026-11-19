using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Tests.Refactoring.Rename;
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
            var commit = await ctx.CommitInWriterAsync(
                s => s.WithDocumentText(document.Id, SourceText.From(committed)), ct);
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

    [Fact]
    public async Task ReconcileProjectDocumentsAsync_EvaluatedBeforeAnOperationCreatesAndMovesFiles_KeepsThem()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        var ct = TestContext.Current.CancellationToken;
        using var working = RenameTempSolution.Create();
        var projectFile = Path.Combine(working.ProjectDir, "TestProject.csproj");
        using var ctx = await OpenProjectAsync(projectFile, ct);

        var created = Path.Combine(working.ProjectDir, "Created.cs");
        var moveFrom = Path.Combine(working.ProjectDir, "Models.cs");
        var moveTo = Path.Combine(working.ProjectDir, "Domain", "Models.cs");
        Assert.NotNull(ctx.GetDocumentByPath(moveFrom));

        await ctx.EnterOperationAsync(ct);
        Task<bool> reconcile;
        try
        {
            // A re-evaluation (scheduled by some earlier event) evaluates the project now, then
            // waits for the lease the operation holds.
            reconcile = ctx.ReconcileProjectDocumentsAsync(projectFile, ct);
            await Task.WhenAny(ctx.WaitUntilFileUpdateQueuedAsync(ct), reconcile);
            Assert.False(reconcile.IsCompleted, "The re-evaluation must wait for the operation's lease.");

            // The operation creates a file (a commit writes disk and solution) ...
            var commit = await ctx.CommitInWriterAsync(
                s => s.AddDocument(
                    DocumentId.CreateNewId(s.ProjectIds.Single()),
                    "Created.cs",
                    SourceText.From("namespace TestProject;\n\ninternal sealed class Created\n{\n}\n"),
                    filePath: created),
                ct);
            Assert.True(commit.Success, commit.Error);

            // ... and moves another one, repointing its document (what a namespace move does).
            Directory.CreateDirectory(Path.GetDirectoryName(moveTo)!);
            File.Move(moveFrom, moveTo);
            await ctx.UpdateSolutionInWriterAsync(
                s =>
                {
                    foreach (var documentId in s.GetDocumentIdsWithFilePath(moveFrom))
                        s = s.WithDocumentFilePath(documentId, moveTo);
                    return s;
                },
                ct);
        }
        finally
        {
            ctx.ExitOperation();
        }

        // The evaluation predates both files; they exist, so the project still compiles them.
        Assert.False(await reconcile);
        Assert.NotNull(ctx.GetDocumentByPath(created));
        Assert.NotNull(ctx.GetDocumentByPath(moveTo));
        Assert.Null(ctx.GetDocumentByPath(moveFrom));

        // A fresh evaluation agrees.
        Assert.False(await ctx.ReconcileProjectDocumentsAsync(projectFile, ct));
    }

    [Fact]
    public async Task ReconcileProjectDocumentsAsync_TrackedFileDeletedExternally_RemovesIt()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        var ct = TestContext.Current.CancellationToken;
        using var working = RenameTempSolution.Create();
        var projectFile = Path.Combine(working.ProjectDir, "TestProject.csproj");
        using var ctx = await OpenProjectAsync(projectFile, ct);

        // No operation knows about this deletion: the evaluation omits the file and it is
        // gone from disk, so the document goes.
        var deleted = Path.Combine(working.ProjectDir, "Models.cs");
        File.Delete(deleted);
        Assert.True(await ctx.ReconcileProjectDocumentsAsync(projectFile, ct));
        Assert.Null(ctx.GetDocumentByPath(deleted));
    }

    /// <summary>
    /// Loads <paramref name="projectFile"/> with MSBuild into an uncached context: the
    /// re-evaluation needs an MSBuild workspace, and without the cache no watcher reacts to
    /// the files a test writes.
    /// </summary>
    private static async Task<WorkspaceContext> OpenProjectAsync(string projectFile, CancellationToken ct)
    {
        var properties = new Dictionary<string, string>
        {
            ["CheckForSystemRuntimeDependency"] = "true",
            ["DesignTimeBuild"] = "true",
            ["BuildingInsideVisualStudio"] = "true",
        };
        var workspace = MSBuildWorkspace.Create(properties);
        var project = await workspace.OpenProjectAsync(projectFile, cancellationToken: ct);
        return new WorkspaceContext(workspace, project.Solution, projectFile, msbuildProperties: properties);
    }
}
