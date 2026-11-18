using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// A multi-targeted project is loaded once per target framework, and each framework's
/// compilation reports the same diagnostic. get_diagnostics listed (and counted) it once
/// per framework.
/// </summary>
public sealed class GetDiagnosticsMultiTargetTests
{
    private const string Source = """
        public class Multi
        {
            public void M()
            {
                int unusedEverywhere;
        #if NET8
                int onlyOnNet8;
        #endif
            }
        }
        """;

    private const string LibSource = """
        public class Lib
        {
            public void M() { int unusedInLib; }
        }
        """;

    [Fact]
    public async Task ADiagnosticOfEveryFramework_IsReportedOnce_WithItsFrameworks()
    {
        using var ws = CreateWorkspace();

        var result = await GetAsync(ws, new GetDiagnosticsParams());

        var everywhere = Assert.Single(result.Diagnostics, d => d.Message.Contains("unusedEverywhere"));
        Assert.Equal(["net8", "net9"], everywhere.TargetFrameworks);
    }

    [Fact]
    public async Task ADiagnosticOfOneFramework_NamesOnlyThatFramework()
    {
        using var ws = CreateWorkspace();

        var result = await GetAsync(ws, new GetDiagnosticsParams());

        var net8Only = Assert.Single(result.Diagnostics, d => d.Message.Contains("onlyOnNet8"));
        Assert.Equal(["net8"], net8Only.TargetFrameworks);
    }

    [Fact]
    public async Task ASingleTargetProjectsDiagnostic_HasNoFrameworks()
    {
        using var ws = CreateWorkspace();

        var result = await GetAsync(ws, new GetDiagnosticsParams());

        var lib = Assert.Single(result.Diagnostics, d => d.Message.Contains("unusedInLib"));
        Assert.Null(lib.TargetFrameworks);
    }

    [Fact]
    public async Task TotalCount_CountsEachDiagnosticOnce_WithProjectPathAndFilters()
    {
        using var ws = CreateWorkspace();

        var all = await GetAsync(ws, new GetDiagnosticsParams());
        var scoped = await GetAsync(ws, new GetDiagnosticsParams
        {
            ProjectPath = ws.ProjectPathOf("Multi"),
            DiagnosticIds = ["CS0168"],
            MaxResults = 1
        });

        Assert.Equal(3, all.TotalCount);
        Assert.Equal(3, all.Diagnostics.Count);
        Assert.Equal(2, scoped.TotalCount);
        Assert.Single(scoped.Diagnostics);
        Assert.True(scoped.Truncated);
    }

    private static TestWorkspace CreateWorkspace() =>
        TestWorkspace.CreateSolution(
            new TestWorkspace.ProjectSpec("Multi", [("Multi.cs", Source)], TargetFrameworks: ["net8", "net9"]),
            new TestWorkspace.ProjectSpec("Lib", [("Lib.cs", LibSource)]));

    private static async Task<GetDiagnosticsResult> GetAsync(TestWorkspace ws, GetDiagnosticsParams @params)
    {
        var result = await new GetDiagnosticsOperation(ws.Context).ExecuteAsync(@params, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message);
        return result.Data!;
    }
}
