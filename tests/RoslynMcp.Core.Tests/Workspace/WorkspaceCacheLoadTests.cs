using Microsoft.CodeAnalysis.MSBuild;
using RoslynMcp.Core.Tests.Refactoring.Rename;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

/// <summary>
/// A file changed while a workspace loads — after MSBuild read it, before the cache's file
/// watchers start — is in the workspace the cache hands out. The watchers only report changes
/// made once they run, so such an edit used to be missed until the file changed again, and the
/// next refactoring of that file wrote the older text back. Each test changes the file inside the
/// loader, once the compilation holds the file's text: exactly that window.
/// </summary>
public sealed class WorkspaceCacheLoadTests
{
    [Fact]
    public async Task FileEditedDuringLoad_AfterItWasRead_IsInTheWorkspace()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        var ct = TestContext.Current.CancellationToken;
        using var working = RenameTempSolution.Create();
        var projectFile = Path.Combine(working.ProjectDir, "TestProject.csproj");
        var models = Path.Combine(working.ProjectDir, "Models.cs");
        var original = File.ReadAllText(models);
        var edited = original + "\npublic sealed class EditedDuringLoad\n{\n}\n";
        using var cache = new WorkspaceCache(idleTtl: TimeSpan.FromHours(1), sweepInterval: TimeSpan.FromHours(1));

        var (context, _) = await cache.GetOrCreateAsync(
            projectFile,
            async c =>
            {
                var loaded = await LoadAsync(projectFile, c);
                Assert.Equal(original, (await loaded.GetDocumentByPath(models)!.GetTextAsync(c)).ToString());

                // An editor saves the file before the cache watches it.
                await File.WriteAllTextAsync(models, edited, c);
                return loaded;
            },
            ct);

        using (context)
        {
            Assert.Equal(edited, (await context.GetDocumentByPath(models)!.GetTextAsync(ct)).ToString());
        }
    }

    [Fact]
    public async Task FileDeletedDuringLoad_AfterItWasRead_IsNotInTheWorkspace()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        var ct = TestContext.Current.CancellationToken;
        using var working = RenameTempSolution.Create();
        var projectFile = Path.Combine(working.ProjectDir, "TestProject.csproj");
        var orderService = Path.Combine(working.ProjectDir, "Services", "OrderService.cs");
        using var cache = new WorkspaceCache(idleTtl: TimeSpan.FromHours(1), sweepInterval: TimeSpan.FromHours(1));

        var (context, _) = await cache.GetOrCreateAsync(
            projectFile,
            async c =>
            {
                var loaded = await LoadAsync(projectFile, c);
                Assert.NotNull(loaded.GetDocumentByPath(orderService));
                File.Delete(orderService);
                return loaded;
            },
            ct);

        using (context)
        {
            Assert.Null(context.GetDocumentByPath(orderService));
        }
    }

    /// <summary>
    /// Loads <paramref name="projectFile"/> the way the workspace provider does, compilation
    /// included, so every document's text has been read from disk.
    /// </summary>
    private static async Task<WorkspaceContext> LoadAsync(string projectFile, CancellationToken ct)
    {
        var properties = new Dictionary<string, string>
        {
            ["CheckForSystemRuntimeDependency"] = "true",
            ["DesignTimeBuild"] = "true",
            ["BuildingInsideVisualStudio"] = "true",
        };
        var workspace = MSBuildWorkspace.Create(properties);
        var project = await workspace.OpenProjectAsync(projectFile, cancellationToken: ct);
        await project.GetCompilationAsync(ct);
        return new WorkspaceContext(workspace, project.Solution, projectFile, msbuildProperties: properties);
    }
}
