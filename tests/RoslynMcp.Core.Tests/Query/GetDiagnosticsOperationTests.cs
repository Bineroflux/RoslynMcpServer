using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
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

    [Fact]
    public async Task ExecuteAsync_WhenGeneratorEnumerationThrows_EmitsRmcp0002()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        // Build a synthetic project whose AnalyzerReferences include a reference
        // that throws when asked for its generators. This drives the catch in
        // GetGeneratorReportedDiagnosticsAsync and must surface as RMCP0002.
        using var workspace = MSBuildWorkspace.Create();
        var projectId = ProjectId.CreateNewId();
        var info = ProjectInfo.Create(
            projectId,
            VersionStamp.Default,
            name: "Throwy",
            assemblyName: "Throwy",
            language: LanguageNames.CSharp,
            compilationOptions: new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(),
            analyzerReferences: new[] { new ThrowingAnalyzerReference() });
        var solution = workspace.CurrentSolution.AddProject(info);

        using var ctx = new WorkspaceContext(workspace, solution, "fake.sln");
        var op = new GetDiagnosticsOperation(ctx);
        var result = await op.ExecuteAsync(new GetDiagnosticsParams(),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        var rmcp = result.Data!.Diagnostics.Where(d => d.Id == "RMCP0002").ToList();
        Assert.Single(rmcp);
        Assert.Equal("Info", rmcp[0].Severity);
        Assert.Contains("Throwy", rmcp[0].Message);
        Assert.Contains("ThrowingAnalyzerReference test failure", rmcp[0].Message);
    }

    [Fact]
    public async Task ExecuteAsync_WhenGeneratorEnumerationThrows_WithErrorOnlyFilter_SuppressesRmcp0002()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        using var workspace = MSBuildWorkspace.Create();
        var projectId = ProjectId.CreateNewId();
        var info = ProjectInfo.Create(
            projectId, VersionStamp.Default,
            "Throwy", "Throwy", LanguageNames.CSharp,
            compilationOptions: new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(),
            analyzerReferences: new[] { new ThrowingAnalyzerReference() });
        var solution = workspace.CurrentSolution.AddProject(info);

        using var ctx = new WorkspaceContext(workspace, solution, "fake.sln");
        var op = new GetDiagnosticsOperation(ctx);
        var result = await op.ExecuteAsync(
            new GetDiagnosticsParams { SeverityFilter = "Error" },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Data!.Diagnostics, d => d.Id == "RMCP0002");
    }

    /// <summary>
    /// AnalyzerReference whose GetGenerators always throws. Used to drive the
    /// failure path in GetGeneratorReportedDiagnosticsAsync.
    /// </summary>
    private sealed class ThrowingAnalyzerReference : AnalyzerReference
    {
        public override string FullPath => "throwing.dll";
        public override string Display => "ThrowingAnalyzerReference";
        public override object Id => "throwing-id";

        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages()
            => ImmutableArray<DiagnosticAnalyzer>.Empty;
        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language)
            => ImmutableArray<DiagnosticAnalyzer>.Empty;

        public override ImmutableArray<ISourceGenerator> GetGeneratorsForAllLanguages()
            => throw new InvalidOperationException("ThrowingAnalyzerReference test failure");
        public override ImmutableArray<ISourceGenerator> GetGenerators(string language)
            => throw new InvalidOperationException("ThrowingAnalyzerReference test failure");
    }
}
