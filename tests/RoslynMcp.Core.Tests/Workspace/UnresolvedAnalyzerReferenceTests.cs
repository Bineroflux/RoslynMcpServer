using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Tests.Refactoring.Rename;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

/// <summary>
/// Analyzer references MSBuild names but Roslyn can't resolve (a missing DLL) are removed at
/// load, since several Roslyn paths throw on them. They used to vanish silently (unless they
/// were the Razor generator); now the load records them per project, and get_diagnostics with
/// includeAnalyzers reports them as RMCP0003 for the projects whose analyzers it runs.
/// </summary>
public sealed class UnresolvedAnalyzerReferenceTests
{
    [Fact]
    public void Strip_RecordsEveryRemovedReference_WithItsReason()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"RoslynMcp.Missing-{Guid.NewGuid():N}", "Ghost.Analyzers.dll");
        var missingRazor = Path.Combine(Path.GetTempPath(), $"RoslynMcp.Missing-{Guid.NewGuid():N}", "Microsoft.CodeAnalysis.Razor.Compiler.dll");
        var kept = new AnalyzerImageReference(ImmutableArray<DiagnosticAnalyzer>.Empty);
        var projectDirectory = Path.Combine(Path.GetTempPath(), $"RoslynMcp.App-{Guid.NewGuid():N}");
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(
            ProjectInfo.Create(
                    ProjectId.CreateNewId(), VersionStamp.Default, "App", "App", LanguageNames.CSharp,
                    filePath: Path.Combine(projectDirectory, "App.csproj"))
                .WithAnalyzerReferences(
                [
                    new UnresolvedAnalyzerReference(missing),
                    kept,
                    new UnresolvedAnalyzerReference(missingRazor),
                    new UnresolvedAnalyzerReference(Path.Combine("analyzers", "Relative.dll")),
                ]));
        var other = workspace.AddProject("Other", LanguageNames.CSharp);
        var issues = new List<string>();
        var unresolved = new Dictionary<ProjectId, IReadOnlyList<UnresolvedAnalyzerReferenceInfo>>();
        using var provider = new MSBuildWorkspaceProvider();

        var stripped = provider.StripUnresolvedAnalyzerReferences(workspace.CurrentSolution, issues, unresolved);

        Assert.Equal([kept], stripped.GetProject(project.Id)!.AnalyzerReferences);
        Assert.Equal(
            [
                new UnresolvedAnalyzerReferenceInfo(missing, "the file does not exist"),
                new UnresolvedAnalyzerReferenceInfo(missingRazor, "the file does not exist"),
                // A relative path is made absolute against the project's directory.
                new UnresolvedAnalyzerReferenceInfo(Path.Combine(projectDirectory, "analyzers", "Relative.dll"), "the file does not exist"),
            ],
            unresolved[project.Id]);
        Assert.False(unresolved.ContainsKey(other.Id));
        // Only the Razor generator is reported as a load issue, as before.
        var issue = Assert.Single(issues);
        Assert.StartsWith("RazorSourceGenerator failed to load for project 'App'", issue);
    }

    [Fact]
    public async Task MissingAnalyzerDll_OfAnMSBuildProject_IsReportedOnlyWithIncludeAnalyzers()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        using var working = RenameTempSolution.Create();
        var projectFile = Path.Combine(working.ProjectDir, "TestProject.csproj");
        File.WriteAllText(projectFile, File.ReadAllText(projectFile).Replace(
            "</Project>",
            "  <ItemGroup>\n    <Analyzer Include=\"missing\\Ghost.Analyzers.dll\" />\n  </ItemGroup>\n</Project>"));
        var ct = TestContext.Current.CancellationToken;

        using var provider = new MSBuildWorkspaceProvider();
        using var ctx = await provider.CreateContextAsync(working.SolutionPath, ct);

        var project = Assert.Single(ctx.Solution.Projects);
        var recorded = Assert.Single(ctx.UnresolvedAnalyzerReferencesOf(project.Id));
        Assert.True(Path.IsPathRooted(recorded.Path), recorded.Path);
        Assert.EndsWith(Path.Combine("TestProject", "missing", "Ghost.Analyzers.dll"), recorded.Path);
        Assert.Equal("the file does not exist", recorded.Reason);
        Assert.DoesNotContain(project.AnalyzerReferences, r => r is UnresolvedAnalyzerReference);

        var with = await new GetDiagnosticsOperation(ctx).ExecuteAsync(
            new GetDiagnosticsParams { IncludeAnalyzers = true, DiagnosticIds = ["CA1822"] }, ct);
        var without = await new GetDiagnosticsOperation(ctx).ExecuteAsync(
            new GetDiagnosticsParams { SeverityFilter = "All" }, ct);

        var failure = Assert.Single(with.Data!.Diagnostics);
        Assert.Equal("RMCP0003", failure.Id);
        Assert.Contains("Ghost.Analyzers.dll", failure.Message);
        Assert.Contains("of project 'TestProject' could not be resolved (the file does not exist)", failure.Message);
        Assert.DoesNotContain(without.Data!.Diagnostics, d => d.Id.StartsWith("RMCP", StringComparison.Ordinal));
    }
}
