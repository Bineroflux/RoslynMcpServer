using Microsoft.CodeAnalysis.MSBuild;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Tests for <see cref="GetDiagnosticsOperation"/> behaviour that doesn't require
/// a real solution on disk — specifically, the synthetic RMCP0001 diagnostic
/// emitted when the workspace reports generator-load issues.
/// </summary>
public sealed class GetDiagnosticsOperationTests
{
    [Fact]
    public async Task ExecuteAsync_WhenGeneratorIssues_EmitsRmcp0001()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        using var workspace = MSBuildWorkspace.Create();
        var issues = new[]
        {
            "RazorSourceGenerator failed to load for project 'Foo' (unresolved analyzer reference: bar.dll). .razor / .cshtml diagnostics will not appear."
        };
        using var ctx = new WorkspaceContext(
            workspace, workspace.CurrentSolution, "fake.sln",
            fileWriter: null, generatorLoadIssues: issues);

        var op = new GetDiagnosticsOperation(ctx);
        var result = await op.ExecuteAsync(new GetDiagnosticsParams(),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var synthetic = result.Data!.Diagnostics
            .Where(d => d.Id == "RMCP0001")
            .ToList();
        Assert.Single(synthetic);
        Assert.Equal("Info", synthetic[0].Severity);
        Assert.Contains("RazorSourceGenerator", synthetic[0].Message);
        Assert.Null(synthetic[0].File);
    }

    [Fact]
    public async Task ExecuteAsync_WhenErrorOnlyFilter_SuppressesRmcp0001()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        using var workspace = MSBuildWorkspace.Create();
        using var ctx = new WorkspaceContext(
            workspace, workspace.CurrentSolution, "fake.sln",
            fileWriter: null,
            generatorLoadIssues: new[] { "Razor generator failed" });

        var op = new GetDiagnosticsOperation(ctx);
        var result = await op.ExecuteAsync(
            new GetDiagnosticsParams { SeverityFilter = "Error" },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Data!.Diagnostics, d => d.Id == "RMCP0001");
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoIssues_DoesNotEmitRmcp0001()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        using var workspace = MSBuildWorkspace.Create();
        using var ctx = new WorkspaceContext(
            workspace, workspace.CurrentSolution, "fake.sln");

        var op = new GetDiagnosticsOperation(ctx);
        var result = await op.ExecuteAsync(new GetDiagnosticsParams(),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Data!.Diagnostics, d => d.Id == "RMCP0001");
    }
}
