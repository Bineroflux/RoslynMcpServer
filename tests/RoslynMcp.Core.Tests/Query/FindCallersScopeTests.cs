using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// find_callers' <c>callerFile</c> and <c>projectPath</c>.
/// </summary>
public sealed class FindCallersScopeTests
{
    private const string Widget = """
        namespace App.Core;

        public class Widget
        {
            public void Render() { }
        }
        """;

    private const string CoreUsage = """
        namespace App.Core;

        internal class CoreUser
        {
            void M()
            {
                new Widget().Render();
                new Widget().Render();
            }

            void N() => new Widget().Render();
        }
        """;

    private const string AppUsage = """
        using App.Core;

        namespace App;

        internal class AppUser
        {
            void M() => new Widget().Render();
        }
        """;

    [Fact]
    public async Task WithoutOptions_ReportsEveryCallSite()
    {
        using var ws = CreateWorkspace();

        var result = await FindCallersAsync(ws, new FindCallersParams { SourceFile = "" });

        Assert.Equal(4, result.TotalCount);
        Assert.Equal(["AppUsage.cs", "CoreUsage.cs", "CoreUsage.cs", "CoreUsage.cs"], Files(result));
    }

    [Fact]
    public async Task CallerFile_KeepsOnlyCallSitesInThatFile()
    {
        using var ws = CreateWorkspace();
        var appUsage = ws.PathOf("App", "AppUsage.cs");

        foreach (var spelling in EquivalentPaths.Of(appUsage).Prepend(appUsage))
        {
            var result = await FindCallersAsync(ws, new FindCallersParams { SourceFile = "", CallerFile = spelling });

            var caller = Assert.Single(result.Callers);
            Assert.Equal("App.AppUser.M()", caller.CallerFullyQualifiedName);
            Assert.Equal(1, result.TotalCount);
        }
    }

    [Fact]
    public async Task ProjectPath_KeepsOnlyCallSitesInThatProject()
    {
        using var ws = CreateWorkspace();

        foreach (var spelling in EquivalentPaths.Of(ws.ProjectPathOf("Core")).Prepend(ws.ProjectPathOf("Core")))
        {
            var result = await FindCallersAsync(ws, new FindCallersParams { SourceFile = "", ProjectPath = spelling });

            Assert.Equal(["CoreUsage.cs", "CoreUsage.cs", "CoreUsage.cs"], Files(result));
            Assert.Equal(3, result.TotalCount);
        }
    }

    [Fact]
    public async Task Scopes_AreAppliedBeforeMaxResults()
    {
        using var ws = CreateWorkspace();

        var result = await FindCallersAsync(ws, new FindCallersParams
        {
            SourceFile = "",
            ProjectPath = ws.ProjectPathOf("Core"),
            MaxResults = 1
        });

        Assert.Equal(["CoreUsage.cs"], Files(result));
        Assert.Equal(3, result.TotalCount);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task ScopesCombine()
    {
        using var ws = CreateWorkspace();

        var result = await FindCallersAsync(ws, new FindCallersParams
        {
            SourceFile = "",
            CallerFile = ws.PathOf("App", "AppUsage.cs"),
            ProjectPath = ws.ProjectPathOf("Core")
        });

        Assert.Empty(result.Callers);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task ProjectPath_CoversEveryTargetFramework()
    {
        using var ws = TestWorkspace.CreateSolution(
            new TestWorkspace.ProjectSpec("Core", [("Widget.cs", Widget), ("CoreUsage.cs", CoreUsage)], TargetFrameworks: ["net8", "net9"]),
            new TestWorkspace.ProjectSpec("App", [("AppUsage.cs", AppUsage)], References: ["Core"]));

        var all = await FindCallersAsync(ws, new FindCallersParams { SourceFile = "" });
        var core = await FindCallersAsync(ws, new FindCallersParams { SourceFile = "", ProjectPath = ws.ProjectPathOf("Core") });

        Assert.Equal(
            all.Callers.Where(c => Path.GetFileName(c.File) == "CoreUsage.cs").Select(c => (c.Line, c.Column)),
            core.Callers.Select(c => (c.Line, c.Column)));
        Assert.NotEmpty(core.Callers);
    }

    [Fact]
    public async Task CallerFile_NotInTheWorkspace_IsRejected()
    {
        using var ws = CreateWorkspace();
        var stray = Path.Combine(ws.Directory, "Stray.cs");
        File.WriteAllText(stray, "class Stray { }");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            FindCallersAsync(ws, new FindCallersParams { SourceFile = "", CallerFile = stray }));

        Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
        Assert.Contains("callerFile", ex.Message);
    }

    [Fact]
    public async Task ProjectPath_NotInTheWorkspace_IsRejected()
    {
        using var ws = CreateWorkspace();
        var stray = Path.Combine(ws.Directory, "Stray.csproj");
        File.WriteAllText(stray, "<Project />");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            FindCallersAsync(ws, new FindCallersParams { SourceFile = "", ProjectPath = stray }));

        Assert.Equal(ErrorCodes.ProjectNotInWorkspace, ex.ErrorCode);
    }

    private static TestWorkspace CreateWorkspace() =>
        TestWorkspace.CreateSolution(
            new TestWorkspace.ProjectSpec("Core", [("Widget.cs", Widget), ("CoreUsage.cs", CoreUsage)]),
            new TestWorkspace.ProjectSpec("App", [("AppUsage.cs", AppUsage)], References: ["Core"]));

    /// <summary>Runs the query for <c>Widget.Render</c>, declared in Core/Widget.cs.</summary>
    private static async Task<FindCallersResult> FindCallersAsync(TestWorkspace ws, FindCallersParams @params)
    {
        var withSource = new FindCallersParams
        {
            SourceFile = ws.PathOf("Core", "Widget.cs"),
            SymbolName = "Render",
            MaxResults = @params.MaxResults,
            CallerFile = @params.CallerFile,
            ProjectPath = @params.ProjectPath
        };

        var result = await new FindCallersOperation(ws.Context).ExecuteAsync(withSource, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message);
        return result.Data!;
    }

    private static List<string> Files(FindCallersResult result) =>
        result.Callers.Select(c => Path.GetFileName(c.File)).Order(StringComparer.Ordinal).ToList();
}
