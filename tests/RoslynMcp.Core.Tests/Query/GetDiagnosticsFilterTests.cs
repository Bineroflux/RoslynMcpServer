using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// get_diagnostics' scoping and filtering options: <c>projectPath</c>, <c>diagnosticIds</c>,
/// <c>excludeDiagnosticIds</c>, <c>messageFilter</c> and <c>maxResults</c>.
/// </summary>
public sealed class GetDiagnosticsFilterTests
{
    // App: CS0168 (declared, never used), CS0219 (assigned, never used), CS0029 (error).
    private const string AppSource = """
        namespace App;

        public class AppType
        {
            public void M()
            {
                int unusedInApp;
                int assignedInApp = 1;
                int wrong = "text";
            }
        }
        """;

    // Lib: one CS0168.
    private const string LibSource = """
        namespace Lib;

        public class LibType
        {
            public void M()
            {
                int unusedInLib;
            }
        }
        """;

    [Fact]
    public async Task DiagnosticIds_KeepOnlyTheListedIds_CaseInsensitively()
    {
        using var ws = CreateAppAndLib();

        var result = await GetAsync(ws, new GetDiagnosticsParams { DiagnosticIds = ["cs0168"] });

        Assert.Equal(["CS0168", "CS0168"], result.Diagnostics.Select(d => d.Id));
        Assert.Equal(2, result.TotalCount);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task ExcludeDiagnosticIds_WinsOverDiagnosticIds()
    {
        using var ws = CreateAppAndLib();

        var result = await GetAsync(ws, new GetDiagnosticsParams
        {
            DiagnosticIds = ["CS0168", "CS0219"],
            ExcludeDiagnosticIds = ["cs0168"]
        });

        Assert.Equal(["CS0219"], result.Diagnostics.Select(d => d.Id));
    }

    [Fact]
    public async Task ExcludeDiagnosticIds_AloneDropsOnlyThoseIds()
    {
        using var ws = CreateAppAndLib();

        var result = await GetAsync(ws, new GetDiagnosticsParams { ExcludeDiagnosticIds = ["CS0168"] });

        Assert.Equal(["CS0029", "CS0219"], result.Diagnostics.Select(d => d.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task MessageFilter_IsACaseInsensitiveSubstring()
    {
        using var ws = CreateAppAndLib();

        var result = await GetAsync(ws, new GetDiagnosticsParams { MessageFilter = "UNUSEDINLIB" });

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("CS0168", diagnostic.Id);
        Assert.Contains("unusedInLib", diagnostic.Message);
    }

    [Fact]
    public async Task MaxResults_TruncatesAfterFiltering()
    {
        using var ws = CreateAppAndLib();

        var result = await GetAsync(ws, new GetDiagnosticsParams { DiagnosticIds = ["CS0168"], MaxResults = 1 });

        Assert.Single(result.Diagnostics);
        Assert.Equal(2, result.TotalCount);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task WithoutOptions_EverythingIsReturnedUntruncated()
    {
        using var ws = CreateAppAndLib();

        var result = await GetAsync(ws, new GetDiagnosticsParams());

        Assert.Equal(4, result.Diagnostics.Count);
        Assert.Equal(4, result.TotalCount);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task ProjectPath_ReportsOnlyThatProject()
    {
        using var ws = CreateAppAndLib();

        var result = await GetAsync(ws, new GetDiagnosticsParams { ProjectPath = ws.ProjectPathOf("Lib") });

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Contains("unusedInLib", diagnostic.Message);
    }

    [Fact]
    public async Task ProjectPath_MatchesEquivalentSpellings()
    {
        using var ws = CreateAppAndLib();

        foreach (var spelling in EquivalentPaths.Of(ws.ProjectPathOf("Lib")))
        {
            var result = await GetAsync(ws, new GetDiagnosticsParams { ProjectPath = spelling });

            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Contains("unusedInLib", diagnostic.Message);
        }
    }

    [Fact]
    public async Task ProjectPath_CoversEveryTargetFramework()
    {
        const string source = """
            public class Multi
            {
                public void M()
                {
            #if NET8
                    int onlyOnNet8;
            #else
                    int onlyOnNet9;
            #endif
                }
            }
            """;
        using var ws = TestWorkspace.CreateSolution(
            new TestWorkspace.ProjectSpec("Multi", [("Multi.cs", source)], TargetFrameworks: ["net8", "net9"]),
            new TestWorkspace.ProjectSpec("Lib", [("Lib.cs", LibSource)]));

        var result = await GetAsync(ws, new GetDiagnosticsParams { ProjectPath = ws.ProjectPathOf("Multi") });

        Assert.Equal(2, result.Diagnostics.Count);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("onlyOnNet8"));
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("onlyOnNet9"));
    }

    [Fact]
    public async Task ProjectPath_CombinesWithSourceFileAndSeverityFilter()
    {
        using var ws = CreateAppAndLib();

        var inProject = await GetAsync(ws, new GetDiagnosticsParams
        {
            ProjectPath = ws.ProjectPathOf("App"),
            SourceFile = ws.PathOf("App", "App.cs"),
            SeverityFilter = "Error"
        });
        var otherProject = await GetAsync(ws, new GetDiagnosticsParams
        {
            ProjectPath = ws.ProjectPathOf("Lib"),
            SourceFile = ws.PathOf("App", "App.cs")
        });

        Assert.Equal(["CS0029"], inProject.Diagnostics.Select(d => d.Id));
        Assert.Empty(otherProject.Diagnostics);
    }

    [Fact]
    public async Task ProjectPath_CompilesOnlyThatProject()
    {
        // A generator that throws makes App report RMCP0002 whenever App is processed.
        using var ws = CreateAppAndLib();
        var app = ws.Context.Solution.Projects.Single(p => p.Name == "App");
        await ws.Context.UpdateSolutionInWriterAsync(s => s.WithProjectAnalyzerReferences(app.Id, [new ThrowingAnalyzerReference()]), TestContext.Current.CancellationToken);

        var lib = await GetAsync(ws, new GetDiagnosticsParams { ProjectPath = ws.ProjectPathOf("Lib") });
        var appOnly = await GetAsync(ws, new GetDiagnosticsParams { ProjectPath = ws.ProjectPathOf("App"), DiagnosticIds = ["RMCP0002"] });

        Assert.DoesNotContain(lib.Diagnostics, d => d.Id == "RMCP0002");
        var failure = Assert.Single(appOnly.Diagnostics);
        Assert.Contains("'App'", failure.Message);
    }

    [Fact]
    public async Task ProjectPath_KeepsOnlyItsProjectsGeneratorLoadIssues()
    {
        string[] issues =
        [
            "Analyzer/source generator 'Gen' could not be loaded for project 'App' (Unknown): boom. Its diagnostics and generated code will be missing.",
            "Analyzer/source generator 'Gen' could not be loaded for project 'Lib' (Unknown): boom. Its diagnostics and generated code will be missing.",
        ];
        using var ws = CreateAppAndLib(issues);

        var all = await GetAsync(ws, new GetDiagnosticsParams { DiagnosticIds = ["RMCP0001"] });
        var lib = await GetAsync(ws, new GetDiagnosticsParams { DiagnosticIds = ["rmcp0001"], ProjectPath = ws.ProjectPathOf("Lib") });

        Assert.Equal(2, all.Diagnostics.Count);
        var issue = Assert.Single(lib.Diagnostics);
        Assert.Contains("project 'Lib'", issue.Message);
    }

    [Fact]
    public async Task IdAndMessageFilters_ApplyToSyntheticDiagnostics()
    {
        string[] issues = ["Analyzer/source generator 'Gen' could not be loaded for project 'App' (Unknown): boom."];
        using var ws = CreateAppAndLib(issues);

        var excluded = await GetAsync(ws, new GetDiagnosticsParams { ExcludeDiagnosticIds = ["RMCP0001"] });
        var byMessage = await GetAsync(ws, new GetDiagnosticsParams { MessageFilter = "could not be LOADED" });

        Assert.DoesNotContain(excluded.Diagnostics, d => d.Id == "RMCP0001");
        Assert.Equal(4, excluded.TotalCount);
        Assert.Equal(["RMCP0001"], byMessage.Diagnostics.Select(d => d.Id));
    }

    [Fact]
    public async Task ProjectPath_NotInTheSolution_IsRejected()
    {
        using var ws = CreateAppAndLib();
        var stray = Path.Combine(ws.Directory, "Stray.csproj");
        File.WriteAllText(stray, "<Project />");

        var ex = await FailAsync(ws, new GetDiagnosticsParams { ProjectPath = stray });

        Assert.Equal(ErrorCodes.ProjectNotInWorkspace, ex.ErrorCode);
        Assert.Contains("Lib.csproj", ex.Message);
    }

    [Theory]
    [InlineData("Missing.csproj", ErrorCodes.SourceFileNotFound)]
    [InlineData("relative", ErrorCodes.InvalidSourcePath)]
    public async Task ProjectPath_ThatIsMissingOrRelative_IsRejected(string projectPath, string expectedCode)
    {
        using var ws = CreateAppAndLib();
        var path = projectPath == "relative" ? "App/App.csproj" : Path.Combine(ws.Directory, projectPath);

        var ex = await FailAsync(ws, new GetDiagnosticsParams { ProjectPath = path });

        Assert.Equal(expectedCode, ex.ErrorCode);
    }

    [Fact]
    public async Task MaxResults_BelowOne_IsRejected()
    {
        using var ws = CreateAppAndLib();

        var ex = await FailAsync(ws, new GetDiagnosticsParams { MaxResults = 0 });

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    private static TestWorkspace CreateAppAndLib(IReadOnlyList<string>? generatorLoadIssues = null) =>
        TestWorkspace.CreateSolution(
            [
                new TestWorkspace.ProjectSpec("App", [("App.cs", AppSource)]),
                new TestWorkspace.ProjectSpec("Lib", [("Lib.cs", LibSource)]),
            ],
            generatorLoadIssues);

    private static async Task<GetDiagnosticsResult> GetAsync(TestWorkspace ws, GetDiagnosticsParams @params)
    {
        var result = await new GetDiagnosticsOperation(ws.Context).ExecuteAsync(@params, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message);
        return result.Data!;
    }

    private static Task<RefactoringException> FailAsync(TestWorkspace ws, GetDiagnosticsParams @params) =>
        Assert.ThrowsAsync<RefactoringException>(() =>
            new GetDiagnosticsOperation(ws.Context).ExecuteAsync(@params, TestContext.Current.CancellationToken));

    /// <summary>An analyzer reference whose generators can't be enumerated.</summary>
    private sealed class ThrowingAnalyzerReference : AnalyzerReference
    {
        public override string FullPath => "throwing.dll";
        public override string Display => "ThrowingAnalyzerReference";
        public override object Id => "throwing-id";

        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages() => [];
        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language) => [];

        public override ImmutableArray<ISourceGenerator> GetGeneratorsForAllLanguages()
            => throw new InvalidOperationException("ThrowingAnalyzerReference test failure");
        public override ImmutableArray<ISourceGenerator> GetGenerators(string language)
            => throw new InvalidOperationException("ThrowingAnalyzerReference test failure");
    }
}
