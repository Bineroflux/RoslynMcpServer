using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

/// <summary>
/// diagnose ignored <c>verbose</c>, reported the SDK version as <c>msBuildVersion</c> and the
/// runtime version as <c>dotnetSdkVersion</c>, and listed three hard-coded capabilities.
/// </summary>
public sealed class WorkspaceDiagnoserTests
{
    private static readonly string[] ToolNames = ["rename_symbol", "diagnose", "find_references"];

    [Fact]
    public async Task NotVerbose_ReportsHonestVersionsAndAllToolsCompactly()
    {
        var provider = new FakeProvider(Environment(msBuildFound: true));

        var result = await WorkspaceDiagnoser.RunAsync(provider, new DiagnoseParams(), ToolNames, TestContext.Current.CancellationToken);

        Assert.True(result.Healthy);
        Assert.Equal("18.12.2", result.Components.MsBuildVersion);
        Assert.Equal("11.0.100", result.Components.DotnetSdkVersion);
        Assert.Equal(System.Environment.Version.ToString(), result.Components.DotnetRuntimeVersion);
        Assert.Equal(["diagnose", "find_references", "rename_symbol"], result.Capabilities);
        Assert.Null(result.Details);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task WithoutMsBuild_OnlyDiagnoseIsAvailable()
    {
        var provider = new FakeProvider(Environment(msBuildFound: false));

        var result = await WorkspaceDiagnoser.RunAsync(provider, new DiagnoseParams(), ToolNames, TestContext.Current.CancellationToken);

        Assert.False(result.Healthy);
        Assert.Equal(["diagnose"], result.Capabilities);
        Assert.Equal(ErrorCodes.MsBuildNotFound, Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task Verbose_WithSolution_DescribesMsBuildSelectionAndEveryProject()
    {
        var provider = new FakeProvider(Environment(msBuildFound: true), CreateContext);

        var result = await WorkspaceDiagnoser.RunAsync(
            provider,
            new DiagnoseParams { SolutionPath = "App.sln", Verbose = true },
            ToolNames,
            TestContext.Current.CancellationToken);

        // The environment is checked after the load, which is what registers MSBuild.
        Assert.Equal(["load", "environment"], provider.Calls);

        var details = Assert.IsType<DiagnoseDetails>(result.Details);
        Assert.Equal("Picked the newest SDK.", details.SelectionReason);
        Assert.Equal(@"C:\dotnet\sdk\11.0.100", details.RegisteredMsBuildPath);
        Assert.True(Assert.Single(details.MsBuildInstances).Selected);
        Assert.Equal(["Failure: restore failed for Lib", "Warning: a warning"], details.WorkspaceLoadDiagnostics);

        var app = Assert.Single(details.Projects!, p => p.Name == "App");
        Assert.Equal("net11.0", app.TargetFramework);
        Assert.Equal(2, app.DocumentCount);
        var analyzer = Assert.Single(app.AnalyzerReferences);
        Assert.Equal(1, analyzer.AnalyzerCount);
        Assert.Equal(0, analyzer.GeneratorCount);
        Assert.Contains("'Gen'", Assert.Single(app.AnalyzerLoadFailures));

        var lib = Assert.Single(details.Projects!, p => p.Name == "Lib(net8.0)");
        Assert.Equal("net8.0", lib.TargetFramework);
        Assert.Empty(lib.AnalyzerLoadFailures);

        // Failures are warnings in any mode; plain workspace warnings only show in the details.
        Assert.Equal(2, result.Warnings.Count);
        Assert.Contains("Workspace load failure: restore failed for Lib", result.Warnings);
        Assert.Equal(2, result.Workspace.ProjectCount);
    }

    [Fact]
    public void MsBuildWorkspaceProvider_ReportsMsBuildSdkAndRuntimeVersionsSeparately()
    {
        if (!ModuleInitializer.MsBuildAvailable)
        {
            Assert.Skip($"MSBuild not available: {ModuleInitializer.MsBuildError}");
        }

        using var provider = new MSBuildWorkspaceProvider();
        var environment = provider.CheckEnvironment();

        Assert.True(environment.MsBuildFound, environment.ErrorMessage);
        Assert.Equal(System.Environment.Version.ToString(), environment.DotnetRuntimeVersion);
        Assert.NotNull(environment.DotnetSdkVersion);
        Assert.NotNull(environment.MsBuildVersion);
        Assert.NotEqual(environment.DotnetSdkVersion, environment.MsBuildVersion);
        Assert.True(DotNetSdkLocator.TryParseCore(environment.MsBuildVersion)!.Major >= 17, environment.MsBuildVersion);
        Assert.True(DotNetSdkLocator.TryParseCore(environment.DotnetSdkVersion)!.Major <= System.Environment.Version.Major);
        Assert.Single(environment.Instances, i => i.Selected);
        Assert.NotNull(environment.SelectionReason);
    }

    private static EnvironmentDiagnostics Environment(bool msBuildFound) => new()
    {
        MsBuildFound = msBuildFound,
        MsBuildVersion = msBuildFound ? "18.12.2" : null,
        MsBuildPath = msBuildFound ? @"C:\dotnet\sdk\11.0.100" : null,
        DotnetSdkVersion = msBuildFound ? "11.0.100" : null,
        DotnetRuntimeVersion = System.Environment.Version.ToString(),
        ErrorMessage = msBuildFound ? null : "MSBuild not found.",
        Instances = msBuildFound
            ? [new MsBuildInstanceInfo { Name = ".NET SDK", Version = "11.0.100", MsBuildPath = @"C:\dotnet\sdk\11.0.100", DiscoveryType = "DotNetSdk", Selected = true }]
            : [],
        SelectionReason = "Picked the newest SDK.",
        RegisteredMsBuildPath = msBuildFound ? @"C:\dotnet\sdk\11.0.100" : null
    };

    private static WorkspaceContext CreateContext()
    {
        var workspace = new AdhocWorkspace();
        var app = ProjectId.CreateNewId();
        var lib = ProjectId.CreateNewId();
        var root = Path.Combine(Path.GetTempPath(), "RoslynMcpServer.DiagnoserTests");
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(app, VersionStamp.Default, "App", "App", LanguageNames.CSharp,
                filePath: Path.Combine(root, "App.csproj")))
            .AddProject(ProjectInfo.Create(lib, VersionStamp.Default, "Lib(net8.0)", "Lib", LanguageNames.CSharp,
                filePath: Path.Combine(root, "Lib.csproj")))
            .AddDocument(DocumentId.CreateNewId(app), "A.cs", "class A {}")
            .AddDocument(DocumentId.CreateNewId(app), "B.cs", "class B {}")
            .AddDocument(DocumentId.CreateNewId(lib), "C.cs", "class C {}")
            .AddAnalyzerConfigDocument(DocumentId.CreateNewId(app), "App.globalconfig",
                SourceText.From("is_global = true\nbuild_property.TargetFramework = net11.0\n"),
                filePath: Path.Combine(root, "App.globalconfig"))
            .AddAnalyzerReference(app, new AnalyzerImageReference([new NoOpAnalyzer()]));

        return new WorkspaceContext(
            workspace, solution, "App.sln",
            generatorLoadIssues:
            [
                "Analyzer/source generator 'Gen' could not be loaded for project 'App' (ReferencesFramework): boom. Its diagnostics and generated code will be missing."
            ],
            loadDiagnostics:
            [
                new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Failure, "restore failed for Lib"),
                new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Warning, "a warning")
            ]);
    }

    private sealed class FakeProvider(EnvironmentDiagnostics environment, Func<WorkspaceContext>? load = null) : IWorkspaceProvider
    {
        public List<string> Calls { get; } = [];

        public Task<WorkspaceContext> CreateContextAsync(string projectOrSolutionPath, CancellationToken cancellationToken = default)
        {
            Calls.Add("load");
            return Task.FromResult((load ?? throw new InvalidOperationException("no workspace"))());
        }

        public EnvironmentDiagnostics CheckEnvironment()
        {
            Calls.Add("environment");
            return environment;
        }
    }

#pragma warning disable RS1036, RS1038, RS1041 // a do-nothing analyzer that is only counted, never shipped
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class NoOpAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
        }
    }
#pragma warning restore RS1036, RS1038, RS1041
}
