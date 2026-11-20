using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Rename;
using RoslynMcp.Core.Tests.Refactoring.Rename;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

/// <summary>
/// A commit checks every file it writes, deletes or creates against the snapshot the refactoring
/// was computed on. Another program — an IDE saving a file, another RoslynMcp server — may write
/// it after the workspace read it, and the writer section of this process does not keep that out:
/// the commit used to write the older text back over the edit. Now it writes nothing, takes the
/// files over and the refactoring is computed once more. The other program's write lands in
/// <see cref="WorkspaceContext.BeforeCommitReadsDisk"/>, right before the commit reads the disk,
/// so "between the refactoring's read and its commit" is not a matter of timing.
/// </summary>
public sealed class WorkspaceDiskConflictTests
{
    private const string Widget =
        "namespace App;\n\ninternal sealed class Widget\n{\n    public int Alpha() => 1;\n\n    public int Beta() => Alpha() + 1;\n}\n";

    private const string WidgetWithGamma =
        "namespace App;\n\ninternal sealed class Widget\n{\n    public int Alpha() => 1;\n\n    public int Beta() => Alpha() + 1;\n\n    public int Gamma() => 3;\n}\n";

    private const string WidgetWithDelta =
        "namespace App;\n\ninternal sealed class Widget\n{\n    public int Alpha() => 1;\n\n    public int Beta() => Alpha() + 1;\n\n    public int Delta() => 4;\n}\n";

    private const string WidgetWithEpsilon =
        "namespace App;\n\ninternal sealed class Widget\n{\n    public int Alpha() => 1;\n\n    public int Beta() => Alpha() + 1;\n\n    public int Epsilon() => 5;\n}\n";

    [Fact]
    public async Task Rename_FileWrittenByAnotherProgramBeforeTheCommit_IsComputedAgain_AndBothEditsSurvive()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ws = TestWorkspace.Create(("Widget.cs", Widget));
        var path = ws.PathOf("Widget.cs");
        var commits = 0;
        ws.Context.BeforeCommitReadsDisk = async c =>
        {
            if (++commits == 1)
                await File.WriteAllTextAsync(path, WidgetWithGamma, c);
        };

        var result = await new RenameSymbolOperation(ws.Context).ExecuteAsync(
            new RenameSymbolParams { SourceFile = path, SymbolName = "Beta", NewName = "Bravo" }, ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, commits);
        var expected = WidgetWithGamma.Replace("Beta", "Bravo", StringComparison.Ordinal);
        Assert.Equal(expected, ws.ReadFile("Widget.cs"));
        Assert.Equal(expected, (await ws.Context.GetDocumentByPath(path)!.GetTextAsync(ct)).ToString());
        Assert.Contains(
            $"'{path}' changed on disk while the refactoring ran (another program wrote it); it was computed again on the new text (attempt 2 of 3).",
            result.Warnings ?? []);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task Rename_FileWrittenAgainWhileComputedTheSecondTime_IsComputedAThirdTime_AndAllEditsSurvive()
    {
        // Two servers refactoring one file back to back can each write it while the other
        // computes, more than once in a row.
        var ct = TestContext.Current.CancellationToken;
        var withGammaAndDelta = WidgetWithGamma.Replace(
            "    public int Gamma() => 3;\n", "    public int Gamma() => 3;\n\n    public int Delta() => 4;\n", StringComparison.Ordinal);
        using var ws = TestWorkspace.Create(("Widget.cs", Widget));
        var path = ws.PathOf("Widget.cs");
        string[] writes = [WidgetWithGamma, withGammaAndDelta];
        var commits = 0;
        ws.Context.BeforeCommitReadsDisk = async c =>
        {
            if (commits < writes.Length)
                await File.WriteAllTextAsync(path, writes[commits], c);
            commits++;
        };

        var result = await new RenameSymbolOperation(ws.Context).ExecuteAsync(
            new RenameSymbolParams { SourceFile = path, SymbolName = "Beta", NewName = "Bravo" }, ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(3, commits);
        Assert.Equal(withGammaAndDelta.Replace("Beta", "Bravo", StringComparison.Ordinal), ws.ReadFile("Widget.cs"));
        Assert.Equal(
            new[]
            {
                $"'{path}' changed on disk while the refactoring ran (another program wrote it); it was computed again on the new text (attempt 2 of 3).",
                $"'{path}' changed on disk while the refactoring ran (another program wrote it); it was computed again on the new text (attempt 3 of 3).",
            },
            result.Warnings ?? []);
        Assert.Empty(ws.GetDiskCompilationErrors());
    }

    [Fact]
    public async Task Rename_FileWrittenAgainEachTimeItIsComputed_FailsWithWorkspaceBusy_AndWritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ws = TestWorkspace.Create(("Widget.cs", Widget));
        var path = ws.PathOf("Widget.cs");
        string[] writes = [WidgetWithGamma, WidgetWithDelta, WidgetWithEpsilon];
        var commits = 0;
        ws.Context.BeforeCommitReadsDisk = async c => await File.WriteAllTextAsync(path, writes[commits++], c);

        var failed = await Assert.ThrowsAsync<RefactoringException>(() => new RenameSymbolOperation(ws.Context).ExecuteAsync(
            new RenameSymbolParams { SourceFile = path, SymbolName = "Beta", NewName = "Bravo" }, ct));

        Assert.Equal(ErrorCodes.WorkspaceBusy, failed.ErrorCode);
        Assert.Contains("each of the 3 times it was computed", failed.Message, StringComparison.Ordinal);
        Assert.Contains($"'{path}' changed on disk", failed.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was written", failed.Message, StringComparison.Ordinal);
        Assert.Equal(3, commits);

        // The other program's last write stands; the solution holds no trace of the rename (it
        // took over the writes it computed the second and third attempts on).
        Assert.Equal(WidgetWithEpsilon, ws.ReadFile("Widget.cs"));
        Assert.Equal(WidgetWithDelta, (await ws.Context.GetDocumentByPath(path)!.GetTextAsync(ct)).ToString());
    }

    [Fact]
    public async Task CommitChangesAsync_FileChangedOnDisk_ReportsTheConflict_AndWritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ws = TestWorkspace.Create(("Widget.cs", Widget), ("Other.cs", "namespace App;\n\ninternal sealed class Other { }\n"));
        var ctx = ws.Context;
        var widget = ws.PathOf("Widget.cs");
        var other = ws.PathOf("Other.cs");
        var created = ws.PathOf("Created.cs");
        var widgetId = ctx.GetDocumentByPath(widget)!.Id;
        var otherId = ctx.GetDocumentByPath(other)!.Id;
        ctx.BeforeCommitReadsDisk = async c =>
        {
            await File.WriteAllTextAsync(widget, WidgetWithGamma, c);
            File.Delete(other);
            await File.WriteAllTextAsync(created, "// another program's file\n", c);
        };

        var before = ctx.Solution;
        var commit = await ctx.CommitInWriterAsync(
            s =>
            {
                var project = s.GetDocument(widgetId)!.Project;
                return project.AddDocument("Created.cs", "namespace App;\n\ninternal sealed class Created { }\n", filePath: created)
                    .Project.Solution
                    .WithDocumentText(widgetId, SourceText.From(Widget.Replace("Beta", "Bravo", StringComparison.Ordinal)))
                    .WithDocumentText(otherId, SourceText.From("namespace App;\n\ninternal sealed class Other2 { }\n"));
            },
            ct);

        Assert.False(commit.Success);
        Assert.Equal(
            new[] { new DiskConflict(created, DiskConflictKind.Created), new DiskConflict(other, DiskConflictKind.Deleted), new DiskConflict(widget, DiskConflictKind.Changed) },
            commit.Conflicts);
        Assert.Contains("nothing was written", commit.Error, StringComparison.Ordinal);
        Assert.Equal(WidgetWithGamma, ws.ReadFile("Widget.cs"));
        Assert.False(File.Exists(other));
        Assert.Equal("// another program's file\n", ws.ReadFile("Created.cs"));
        Assert.Same(before, ctx.Solution);
        Assert.Equal(WorkspaceState.Ready, ctx.State);
    }

    [Fact]
    public async Task Rename_TrackedFileDeletedByAnotherProgram_WithoutAProjectToReEvaluate_FailsAndDoesNotRecreateIt()
    {
        var ct = TestContext.Current.CancellationToken;
        const string uses = "namespace App;\n\ninternal static class Uses\n{\n    public static int Run() => new Widget().Beta();\n}\n";
        using var ws = TestWorkspace.Create(("Widget.cs", Widget), ("Uses.cs", uses));
        var usesPath = ws.PathOf("Uses.cs");
        ws.Context.BeforeCommitReadsDisk = _ =>
        {
            File.Delete(usesPath);
            return Task.CompletedTask;
        };

        // An in-memory workspace cannot be re-evaluated, so the file stays in it: the second
        // attempt finds it gone again.
        var failed = await Assert.ThrowsAsync<RefactoringException>(() => new RenameSymbolOperation(ws.Context).ExecuteAsync(
            new RenameSymbolParams { SourceFile = ws.PathOf("Widget.cs"), SymbolName = "Beta", NewName = "Bravo" }, ct));

        Assert.Equal(ErrorCodes.WorkspaceBusy, failed.ErrorCode);
        Assert.Contains($"'{usesPath}' is gone from disk", failed.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(usesPath), "The commit must not write a file another program deleted back.");
        Assert.Equal(Widget, ws.ReadFile("Widget.cs"));
    }

    [Fact]
    public async Task MoveTypeToFile_TargetCreatedByAnotherProgram_NotInTheWorkspace_IsNotOverwritten()
    {
        var ct = TestContext.Current.CancellationToken;
        const string shapes = "namespace App;\n\npublic sealed class Circle { }\n\npublic sealed class Square { }\n";
        const string theirs = "namespace App;\n\npublic static class SquareDefaults { }\n";
        using var ws = TestWorkspace.Create(("Shapes.cs", shapes));
        var target = ws.PathOf("Square.cs");
        ws.Context.BeforeCommitReadsDisk = async c =>
        {
            if (!File.Exists(target))
                await File.WriteAllTextAsync(target, theirs, c);
        };

        var failed = await Assert.ThrowsAsync<RefactoringException>(() => new MoveTypeToFileOperation(ws.Context).ExecuteAsync(
            new MoveTypeToFileParams { SourceFile = ws.PathOf("Shapes.cs"), SymbolName = "Square", TargetFile = target }, ct));

        Assert.Equal(ErrorCodes.WorkspaceBusy, failed.ErrorCode);
        Assert.Contains($"'{target}' exists on disk but not in the workspace", failed.Message, StringComparison.Ordinal);
        Assert.Equal(theirs, ws.ReadFile("Square.cs"));
        Assert.Equal(shapes, ws.ReadFile("Shapes.cs"));
    }

    [Fact]
    public async Task RenameNamespace_RazorFileChangedByAnotherProgram_IsComputedAgain_AndKeepsTheirLine()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ws = TestWorkspace.Create(
            [("Model.cs", "namespace App.Old;\n\npublic class Model { }\n")],
            [],
            additionalFiles: [("_Imports.razor", "@using App.Old\n")]);
        var imports = ws.PathOf("_Imports.razor");
        var commits = 0;
        ws.Context.BeforeCommitReadsDisk = async c =>
        {
            if (++commits == 1)
                await File.WriteAllTextAsync(imports, "@using App.Old\n@using System.Text\n", c);
        };

        var result = await new RenameNamespaceOperation(ws.Context).ExecuteAsync(
            new RenameNamespaceParams { OldNamespace = "App.Old", NewNamespace = "App.New" }, ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, commits);
        Assert.Equal("@using App.New\n@using System.Text\n", ws.ReadFile("_Imports.razor"));
        Assert.Contains(result.Warnings ?? [], w => w.StartsWith($"'{imports}' changed on disk while the refactoring ran", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preview_DiskAheadOfTheWorkspace_WarnsThatTheWorkspaceIsBehind()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ws = TestWorkspace.Create(("Widget.cs", Widget));
        var path = ws.PathOf("Widget.cs");

        // No file watcher runs here: the workspace keeps the text it read.
        await File.WriteAllTextAsync(path, WidgetWithGamma, ct);

        var preview = await new RenameSymbolOperation(ws.Context).ExecuteAsync(
            new RenameSymbolParams { SourceFile = path, SymbolName = "Beta", NewName = "Bravo", Preview = true }, ct);

        Assert.True(preview.Success, preview.Error?.Message);
        Assert.True(preview.Preview);
        Assert.Contains(
            $"The workspace is behind the disk for '{path}' (another program changed it): this preview was computed without that; " +
            "applying it re-reads the file and computes the change again.",
            preview.Warnings ?? []);
        Assert.Equal(WidgetWithGamma, ws.ReadFile("Widget.cs"));
    }

    [Fact]
    public async Task Preview_DiskAsTheWorkspaceHasIt_HasNoWarning()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ws = TestWorkspace.Create(("Widget.cs", Widget));

        var preview = await new RenameSymbolOperation(ws.Context).ExecuteAsync(
            new RenameSymbolParams { SourceFile = ws.PathOf("Widget.cs"), SymbolName = "Beta", NewName = "Bravo", Preview = true }, ct);

        Assert.True(preview.Success, preview.Error?.Message);
        Assert.Null(preview.Warnings);
    }

    [Fact]
    public async Task MoveTypeToFile_TargetCreatedByAnotherProgram_InAProject_IsTakenOver_AndTheTypeMovesIntoIt()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        var ct = TestContext.Current.CancellationToken;
        using var project = await HeldWatcherProject.LoadAsync(ct);
        var models = Path.Combine(project.ProjectDir, "Models.cs");
        var target = Path.Combine(project.ProjectDir, "Address.cs");
        const string theirs = "namespace TestProject.Models;\n\npublic static class AddressDefaults\n{\n    public const string Country = \"USA\";\n}\n";

        using var ctx = await project.Provider.CreateContextAsync(project.SolutionPath, ct);
        ctx.BeforeCommitReadsDisk = async c =>
        {
            if (!File.Exists(target))
                await File.WriteAllTextAsync(target, theirs, c);
        };

        var result = await new MoveTypeToFileOperation(ctx).ExecuteAsync(
            new MoveTypeToFileParams { SourceFile = models, SymbolName = "Address", TargetFile = target }, ct);

        // The project's re-evaluation found the new file, so the second attempt moved the type
        // into it instead of writing over it.
        Assert.True(result.Success, result.Error?.Message);
        var onDisk = File.ReadAllText(target);
        Assert.Contains("public static class AddressDefaults", onDisk, StringComparison.Ordinal);
        Assert.Contains("public class Address", onDisk, StringComparison.Ordinal);
        Assert.DoesNotContain("class Address\n", File.ReadAllText(models), StringComparison.Ordinal);
        Assert.Contains(
            $"'{target}' was created on disk while the refactoring ran (another program created it); it was computed again with it (attempt 2 of 3).",
            result.Warnings ?? []);
        Assert.Equal(onDisk, (await ctx.GetDocumentByPath(target)!.GetTextAsync(ct)).ToString());
    }

    [Fact]
    public async Task Rename_FileDeletedByAnotherProgram_InAProject_IsTakenOver_AndNotWrittenBack()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        var ct = TestContext.Current.CancellationToken;
        using var project = await HeldWatcherProject.LoadAsync(ct);
        var models = Path.Combine(project.ProjectDir, "Models.cs");
        var orderService = Path.Combine(project.ProjectDir, "Services", "OrderService.cs");

        using var ctx = await project.Provider.CreateContextAsync(project.SolutionPath, ct);
        ctx.BeforeCommitReadsDisk = _ =>
        {
            if (File.Exists(orderService))
                File.Delete(orderService);
            return Task.CompletedTask;
        };

        var result = await new RenameSymbolOperation(ctx).ExecuteAsync(
            new RenameSymbolParams { SourceFile = models, SymbolName = "User", NewName = "Member" }, ct);

        Assert.True(result.Success, result.Error?.Message);
        Assert.False(File.Exists(orderService), "The commit must not write a file another program deleted back.");
        Assert.Null(ctx.GetDocumentByPath(orderService));
        Assert.Contains("public class Member", File.ReadAllText(models), StringComparison.Ordinal);
        Assert.Contains(
            $"'{orderService}' was deleted on disk while the refactoring ran (another program deleted it); it was computed again without it (attempt 2 of 3).",
            result.Warnings ?? []);
    }

    /// <summary>
    /// A copy of the test solution loaded through a cache whose file-watcher callbacks are held
    /// back until disposal, so only the commit's check sees what the test writes.
    /// </summary>
    private sealed class HeldWatcherProject : IDisposable
    {
        private readonly RenameTempSolution _working;
        private readonly TaskCompletionSource _hold;

        private HeldWatcherProject(RenameTempSolution working, TaskCompletionSource hold, MSBuildWorkspaceProvider provider)
        {
            _working = working;
            _hold = hold;
            Provider = provider;
        }

        public MSBuildWorkspaceProvider Provider { get; }

        public string ProjectDir => _working.ProjectDir;

        public string SolutionPath => _working.SolutionPath;

        public static async Task<HeldWatcherProject> LoadAsync(CancellationToken ct)
        {
            var working = RenameTempSolution.Create();
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cache = new WorkspaceCache(idleTtl: TimeSpan.FromHours(1), sweepInterval: TimeSpan.FromHours(1))
            {
                WatcherEventsHeldUntil = hold.Task,
            };
            var provider = new MSBuildWorkspaceProvider(cache: cache);
            using (var loaded = await provider.CreateContextAsync(working.SolutionPath, ct))
                Assert.NotEmpty(loaded.Solution.Projects);

            return new HeldWatcherProject(working, hold, provider);
        }

        public void Dispose()
        {
            _hold.TrySetResult();
            Provider.Dispose();
            _working.Dispose();
        }
    }
}
