using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// find_references' <c>includeDeclaration</c>, <c>referenceFile</c> and <c>projectPath</c>.
/// </summary>
public sealed class FindReferencesScopeTests
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
    public async Task WithoutOptions_ReportsTheDeclarationAndEveryUsage()
    {
        using var ws = CreateWorkspace();

        var result = await FindRenderAsync(ws, new FindReferencesParams { SourceFile = "", SymbolName = "Render" });

        Assert.Equal(4, result.TotalCount);
        Assert.Single(result.References, r => r.IsDefinition);
        Assert.Equal(["AppUsage.cs", "CoreUsage.cs", "CoreUsage.cs", "Widget.cs"], Files(result));
    }

    [Fact]
    public async Task IncludeDeclarationFalse_ReturnsUsagesOnly()
    {
        using var ws = CreateWorkspace();

        var result = await FindRenderAsync(ws, new FindReferencesParams { SourceFile = "", SymbolName = "Render", IncludeDeclaration = false });

        Assert.Equal(3, result.TotalCount);
        Assert.False(result.Truncated);
        Assert.All(result.References, r => Assert.False(r.IsDefinition));
        Assert.Equal(["AppUsage.cs", "CoreUsage.cs", "CoreUsage.cs"], Files(result));
    }

    [Fact]
    public async Task IncludeDeclarationFalse_IsAppliedBeforeMaxResults()
    {
        using var ws = CreateWorkspace();

        var result = await FindRenderAsync(ws, new FindReferencesParams
        {
            SourceFile = "",
            SymbolName = "Render",
            IncludeDeclaration = false,
            MaxResults = 1
        });

        var reference = Assert.Single(result.References);
        Assert.False(reference.IsDefinition);
        Assert.Equal(3, result.TotalCount);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task ReferenceFile_KeepsOnlyReferencesInThatFile()
    {
        using var ws = CreateWorkspace();
        var coreUsage = ws.PathOf("Core", "CoreUsage.cs");

        foreach (var spelling in EquivalentPaths.Of(coreUsage).Prepend(coreUsage))
        {
            var result = await FindRenderAsync(ws, new FindReferencesParams { SourceFile = "", SymbolName = "Render", ReferenceFile = spelling });

            Assert.Equal(["CoreUsage.cs", "CoreUsage.cs"], Files(result));
            Assert.Equal(2, result.TotalCount);
        }
    }

    [Fact]
    public async Task ReferenceFile_OfTheDeclaration_KeepsTheDeclaration()
    {
        using var ws = CreateWorkspace();

        var result = await FindRenderAsync(ws, new FindReferencesParams
        {
            SourceFile = "",
            SymbolName = "Render",
            ReferenceFile = ws.PathOf("Core", "Widget.cs")
        });

        var reference = Assert.Single(result.References);
        Assert.True(reference.IsDefinition);
    }

    [Fact]
    public async Task ProjectPath_KeepsOnlyReferencesInThatProject()
    {
        using var ws = CreateWorkspace();

        foreach (var spelling in EquivalentPaths.Of(ws.ProjectPathOf("App")).Prepend(ws.ProjectPathOf("App")))
        {
            var app = await FindRenderAsync(ws, new FindReferencesParams { SourceFile = "", SymbolName = "Render", ProjectPath = spelling });

            Assert.Equal(["AppUsage.cs"], Files(app));
            Assert.Equal(1, app.TotalCount);
        }

        var core = await FindRenderAsync(ws, new FindReferencesParams { SourceFile = "", SymbolName = "Render", ProjectPath = ws.ProjectPathOf("Core") });
        Assert.Equal(["CoreUsage.cs", "CoreUsage.cs", "Widget.cs"], Files(core));
    }

    [Fact]
    public async Task ProjectPath_CoversEveryTargetFramework()
    {
        using var ws = TestWorkspace.CreateSolution(
            new TestWorkspace.ProjectSpec("Core", [("Widget.cs", Widget), ("CoreUsage.cs", CoreUsage)], TargetFrameworks: ["net8", "net9"]),
            new TestWorkspace.ProjectSpec("App", [("AppUsage.cs", AppUsage)], References: ["Core"]));

        var result = await FindRenderAsync(ws, new FindReferencesParams
        {
            SourceFile = "",
            SymbolName = "Render",
            ProjectPath = ws.ProjectPathOf("Core"),
            IncludeDeclaration = false
        });

        Assert.Equal(["CoreUsage.cs", "CoreUsage.cs"], Files(result));
        Assert.Equal(2, result.TotalCount);
    }

    [Fact]
    public async Task ScopesCombine()
    {
        using var ws = CreateWorkspace();

        var none = await FindRenderAsync(ws, new FindReferencesParams
        {
            SourceFile = "",
            SymbolName = "Render",
            ReferenceFile = ws.PathOf("App", "AppUsage.cs"),
            ProjectPath = ws.ProjectPathOf("Core")
        });
        var usagesInCore = await FindRenderAsync(ws, new FindReferencesParams
        {
            SourceFile = "",
            SymbolName = "Render",
            ProjectPath = ws.ProjectPathOf("Core"),
            IncludeDeclaration = false
        });

        Assert.Empty(none.References);
        Assert.Equal(0, none.TotalCount);
        Assert.Equal(["CoreUsage.cs", "CoreUsage.cs"], Files(usagesInCore));
    }

    [Fact]
    public async Task ReferenceFile_NotInTheWorkspace_IsRejected()
    {
        using var ws = CreateWorkspace();
        var stray = Path.Combine(ws.Directory, "Stray.cs");
        File.WriteAllText(stray, "class Stray { }");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => FindRenderAsync(ws, new FindReferencesParams
        {
            SourceFile = "",
            SymbolName = "Render",
            ReferenceFile = stray
        }));

        Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
        Assert.Contains("referenceFile", ex.Message);
    }

    [Fact]
    public async Task ProjectPath_NotInTheWorkspace_IsRejected()
    {
        using var ws = CreateWorkspace();

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => FindRenderAsync(ws, new FindReferencesParams
        {
            SourceFile = "",
            SymbolName = "Render",
            ProjectPath = Path.Combine(ws.Directory, "Missing.csproj")
        }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    private static TestWorkspace CreateWorkspace() =>
        TestWorkspace.CreateSolution(
            new TestWorkspace.ProjectSpec("Core", [("Widget.cs", Widget), ("CoreUsage.cs", CoreUsage)]),
            new TestWorkspace.ProjectSpec("App", [("AppUsage.cs", AppUsage)], References: ["Core"]));

    /// <summary>Runs the query for <c>Widget.Render</c>, declared in Core/Widget.cs.</summary>
    private static async Task<FindReferencesResult> FindRenderAsync(TestWorkspace ws, FindReferencesParams @params)
    {
        var withSource = new FindReferencesParams
        {
            SourceFile = ws.PathOf("Core", "Widget.cs"),
            SymbolName = @params.SymbolName,
            MaxResults = @params.MaxResults,
            IncludeDeclaration = @params.IncludeDeclaration,
            ReferenceFile = @params.ReferenceFile,
            ProjectPath = @params.ProjectPath
        };

        var result = await new FindReferencesOperation(ws.Context).ExecuteAsync(withSource, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message);
        return result.Data!;
    }

    private static List<string> Files(FindReferencesResult result) =>
        result.References.Select(r => Path.GetFileName(r.File)).Order(StringComparer.Ordinal).ToList();
}
