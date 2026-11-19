using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Rename;
using RoslynMcp.Core.Tests.Refactoring.Rename;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

/// <summary>
/// Two workspace contexts over the same files — here a <c>.slnx</c> and the <c>.csproj</c> it
/// contains, loaded side by side in one cache — refactor one at a time, and each builds on what
/// the other committed even before its own file watcher has seen it. Each had a writer section
/// of its own: a refactoring through one root computed on its stale text and wrote it back over
/// the other root's edit, in parallel and also one after the other. The watcher callbacks are
/// held back (<see cref="WorkspaceCache.WatcherEventsHeldUntil"/>), so "before the watcher has
/// run" is not a matter of timing.
/// </summary>
public sealed class WorkspaceSharedWriterTests
{
    // The line of `public class Address` in the test solution's Models.cs (a property is named
    // Address too).
    private const int AddressLine = 19;

    [Fact]
    public async Task RefactoringThroughSecondRoot_BeforeItsWatcherRan_BuildsOnTheFirstRootsCommit()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        var ct = TestContext.Current.CancellationToken;
        using var roots = await TwoRoots.LoadAsync(ct);

        using (var a = await roots.Provider.CreateContextAsync(roots.Slnx, ct))
        {
            var renamed = await new RenameSymbolOperation(a).ExecuteAsync(
                new RenameSymbolParams { SourceFile = roots.Models, SymbolName = "User", NewName = "Member" }, ct);
            Assert.True(renamed.Success, renamed.Error?.Message);
        }

        using var b = await roots.Provider.CreateContextAsync(roots.Csproj, ct);
        Assert.Contains("public class User", await TextAsync(b, roots.Models, ct), StringComparison.Ordinal);

        var result = await new RenameSymbolOperation(b).ExecuteAsync(
            new RenameSymbolParams { SourceFile = roots.Models, SymbolName = "Address", Line = AddressLine, NewName = "Location" }, ct);
        Assert.True(result.Success, result.Error?.Message);

        var models = File.ReadAllText(roots.Models);
        Assert.Contains("public class Member", models, StringComparison.Ordinal);
        Assert.Contains("public class Location", models, StringComparison.Ordinal);
        Assert.DoesNotContain("class User", models, StringComparison.Ordinal);
        Assert.DoesNotContain("class Address", models, StringComparison.Ordinal);
        Assert.Contains(
            "public void SetAddress(Member user, Location address)",
            File.ReadAllText(Path.Combine(roots.ProjectDir, "Services", "UserService.cs")),
            StringComparison.Ordinal);
        await roots.AssertMatchesDiskAndCompilesAsync(b, ct);
    }

    [Fact]
    public async Task RefactoringThroughSecondRoot_WhileFirstRootCommits_WaitsAndBuildsOnIt()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        var ct = TestContext.Current.CancellationToken;
        using var roots = await TwoRoots.LoadAsync(ct);
        using var a = await roots.Provider.CreateContextAsync(roots.Slnx, ct);
        using var b = await roots.Provider.CreateContextAsync(roots.Csproj, ct);

        // A refactoring through the .slnx has computed its change and is about to commit it.
        var computed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commitNow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = a.RunWriterAsync(async c =>
        {
            var document = a.GetDocumentByPath(roots.Models)!;
            var text = (await document.GetTextAsync(c)).ToString() +
                "\npublic sealed class Marker\n{\n}\n";
            var change = a.Solution.WithDocumentText(document.Id, SourceText.From(text));
            computed.SetResult();
            await commitNow.Task;
            return await a.CommitChangesAsync(change, c);
        }, ct);
        await computed.Task;

        var rename = new RenameSymbolOperation(b).ExecuteAsync(
            new RenameSymbolParams { SourceFile = roots.Models, SymbolName = "Address", Line = AddressLine, NewName = "Location" }, ct);
        Assert.False(rename.IsCompleted, "A refactoring through the .csproj must wait for the one through the .slnx.");

        commitNow.SetResult();
        var commit = await first;
        Assert.True(commit.Success, commit.Error);
        var result = await rename;
        Assert.True(result.Success, result.Error?.Message);

        var models = File.ReadAllText(roots.Models);
        Assert.Contains("public sealed class Marker", models, StringComparison.Ordinal);
        Assert.Contains("public class Location", models, StringComparison.Ordinal);
        Assert.DoesNotContain("class Address", models, StringComparison.Ordinal);
        await roots.AssertMatchesDiskAndCompilesAsync(b, ct);
    }

    [Fact]
    public async Task RefactoringThroughSecondRoot_AfterFirstRootMovedAType_SeesTheNewFile()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        var ct = TestContext.Current.CancellationToken;
        using var roots = await TwoRoots.LoadAsync(ct);
        var addressFile = Path.Combine(roots.ProjectDir, "Address.cs");

        using (var a = await roots.Provider.CreateContextAsync(roots.Slnx, ct))
        {
            var moved = await new MoveTypeToFileOperation(a).ExecuteAsync(
                new MoveTypeToFileParams { SourceFile = roots.Models, SymbolName = "Address", TargetFile = addressFile }, ct);
            Assert.True(moved.Success, moved.Error?.Message);
        }

        // The .csproj's workspace learns of the new file and the shorter Models.cs only when its
        // next refactoring starts: the project is re-evaluated then.
        using var b = await roots.Provider.CreateContextAsync(roots.Csproj, ct);
        Assert.Null(b.GetDocumentByPath(addressFile));

        // The rename also renames the file named after the type.
        var locationFile = Path.Combine(roots.ProjectDir, "Location.cs");
        var result = await new RenameSymbolOperation(b).ExecuteAsync(
            new RenameSymbolParams { SourceFile = addressFile, SymbolName = "Address", NewName = "Location" }, ct);
        Assert.True(result.Success, result.Error?.Message);

        Assert.False(File.Exists(addressFile));
        Assert.Contains("public class Location", File.ReadAllText(locationFile), StringComparison.Ordinal);
        var models = File.ReadAllText(roots.Models);
        Assert.DoesNotContain("class Location", models, StringComparison.Ordinal);
        Assert.DoesNotContain("class Address", models, StringComparison.Ordinal);
        Assert.Contains("public Location? Address { get; set; }", models, StringComparison.Ordinal);
        Assert.Null(b.GetDocumentByPath(addressFile));
        Assert.NotNull(b.GetDocumentByPath(locationFile));
        await roots.AssertMatchesDiskAndCompilesAsync(b, ct);
    }

    [Fact]
    public async Task ContextsSharingWriters_TakeOverEachOthersCommits_WithoutAWatcher()
    {
        var ct = TestContext.Current.CancellationToken;
        const string widget = "namespace App;\n\ninternal sealed class Widget\n{\n    public int Alpha() => 1;\n}\n";
        using var first = TestWorkspace.Create(("Widget.cs", widget));
        var path = first.PathOf("Widget.cs");
        var secondWorkspace = new AdhocWorkspace();
        var project = secondWorkspace.CurrentSolution.AddProject("Second", "Second", LanguageNames.CSharp);
        using var second = new WorkspaceContext(
            secondWorkspace,
            project.AddDocument("Widget.cs", SourceText.From(widget), filePath: path).Project.Solution,
            first.Directory);

        var writers = new WorkspaceWriteCoordinator();
        first.Context.ShareWriters(writers, writers.Sequence);
        second.ShareWriters(writers, writers.Sequence);

        var id = first.Context.GetDocumentByPath(path)!.Id;
        Assert.True((await first.Context.CommitInWriterAsync(
            s => s.WithDocumentText(id, SourceText.From(widget.Replace("Alpha", "Bravo", StringComparison.Ordinal))), ct)).Success);

        // Nothing told the second context about the file; its next section takes it over.
        string? seen = null;
        var commit = await second.RunWriterAsync(async c =>
        {
            var document = second.GetDocumentByPath(path)!;
            seen = (await document.GetTextAsync(c)).ToString();
            return await second.CommitChangesAsync(
                second.Solution.WithDocumentText(document.Id, SourceText.From(seen.Replace("=> 1", "=> 2", StringComparison.Ordinal))), c);
        }, ct);

        Assert.True(commit.Success, commit.Error);
        Assert.Contains("Bravo", seen, StringComparison.Ordinal);
        Assert.Equal(widget.Replace("Alpha", "Bravo", StringComparison.Ordinal).Replace("=> 1", "=> 2", StringComparison.Ordinal), first.ReadFile("Widget.cs"));

        // And the first takes over the second's commit the same way.
        var back = await first.Context.RunWriterAsync(
            async c => (await first.Context.GetDocumentByPath(path)!.GetTextAsync(c)).ToString(), ct);
        Assert.Equal(first.ReadFile("Widget.cs"), back);
    }

    private static async Task<string> TextAsync(WorkspaceContext context, string path, CancellationToken ct) =>
        (await context.GetDocumentByPath(path)!.GetTextAsync(ct)).ToString();

    /// <summary>
    /// A copy of the test solution loaded twice into one cache, through its <c>.slnx</c> and
    /// through its <c>.csproj</c>, with every file-watcher callback held back until disposal.
    /// </summary>
    private sealed class TwoRoots : IDisposable
    {
        private readonly RenameTempSolution _working;
        private readonly TaskCompletionSource _hold;
        private readonly IReadOnlyList<string> _errorsAtLoad;

        private TwoRoots(RenameTempSolution working, TaskCompletionSource hold, MSBuildWorkspaceProvider provider, IReadOnlyList<string> errorsAtLoad)
        {
            _working = working;
            _hold = hold;
            Provider = provider;
            _errorsAtLoad = errorsAtLoad;
        }

        public MSBuildWorkspaceProvider Provider { get; }

        public string ProjectDir => _working.ProjectDir;

        public string Slnx => Path.Combine(_working.RootDir, "TestSolution.slnx");

        public string Csproj => Path.Combine(_working.ProjectDir, "TestProject.csproj");

        public string Models => Path.Combine(_working.ProjectDir, "Models.cs");

        public static async Task<TwoRoots> LoadAsync(CancellationToken ct)
        {
            var working = RenameTempSolution.Create();
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cache = new WorkspaceCache(idleTtl: TimeSpan.FromHours(1), sweepInterval: TimeSpan.FromHours(1))
            {
                WatcherEventsHeldUntil = hold.Task,
            };
            var provider = new MSBuildWorkspaceProvider(cache: cache);

            IReadOnlyList<string> errors;
            using (var a = await provider.CreateContextAsync(Path.Combine(working.RootDir, "TestSolution.slnx"), ct))
                Assert.NotNull(a.GetDocumentByPath(Path.Combine(working.ProjectDir, "Models.cs")));
            using (var b = await provider.CreateContextAsync(Path.Combine(working.ProjectDir, "TestProject.csproj"), ct))
                errors = await ErrorsAsync(b, ct);

            return new TwoRoots(working, hold, provider, errors);
        }

        /// <summary>
        /// The context's documents hold what is on disk, and its compilation reports no error
        /// that the freshly loaded project did not.
        /// </summary>
        public async Task AssertMatchesDiskAndCompilesAsync(WorkspaceContext context, CancellationToken ct)
        {
            foreach (var file in Directory.GetFiles(ProjectDir, "*.cs", SearchOption.AllDirectories)
                         .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
            {
                var document = context.GetDocumentByPath(file);
                Assert.True(document != null, $"'{file}' is not in the workspace.");
                Assert.Equal(File.ReadAllText(file), (await document.GetTextAsync(ct)).ToString());
            }

            Assert.Empty((await ErrorsAsync(context, ct)).Except(_errorsAtLoad));
        }

        private static async Task<IReadOnlyList<string>> ErrorsAsync(WorkspaceContext context, CancellationToken ct)
        {
            var errors = new List<string>();
            foreach (var project in context.Solution.Projects)
            {
                var compilation = await project.GetCompilationAsync(ct);
                errors.AddRange(compilation!.GetDiagnostics(ct)
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => $"{d.Id}: {d.GetMessage()}"));
            }

            return errors;
        }

        public void Dispose()
        {
            _hold.TrySetResult();
            Provider.Dispose();
            _working.Dispose();
        }
    }
}
