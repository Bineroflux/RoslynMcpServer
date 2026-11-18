using System.Text.Json.Serialization;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;

namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Result of the diagnose operation.
/// </summary>
public sealed class DiagnoseResult
{
    /// <summary>
    /// Whether all components are healthy.
    /// </summary>
    public required bool Healthy { get; init; }

    /// <summary>
    /// Component health status.
    /// </summary>
    public required ComponentStatus Components { get; init; }

    /// <summary>
    /// Current workspace status.
    /// </summary>
    public required WorkspaceStatus Workspace { get; init; }

    /// <summary>
    /// Available tool capabilities.
    /// </summary>
    public required IReadOnlyList<string> Capabilities { get; init; }

    /// <summary>
    /// Any errors encountered.
    /// </summary>
    public required IReadOnlyList<RefactoringError> Errors { get; init; }

    /// <summary>
    /// Any warnings: workspace load failures and analyzers/generators that could not be
    /// loaded.
    /// </summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>
    /// Detailed information, only with <c>verbose</c> (omitted otherwise).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DiagnoseDetails? Details { get; init; }
}

/// <summary>
/// Status of system components.
/// </summary>
public sealed class ComponentStatus
{
    /// <summary>
    /// Whether Roslyn assemblies are available.
    /// </summary>
    public required bool RoslynAvailable { get; init; }

    /// <summary>
    /// Roslyn version if available.
    /// </summary>
    public string? RoslynVersion { get; init; }

    /// <summary>
    /// Whether MSBuild was found.
    /// </summary>
    public required bool MsBuildFound { get; init; }

    /// <summary>
    /// Version of the MSBuild that loads projects (of its <c>Microsoft.Build.dll</c>, e.g.
    /// <c>18.12.2</c>), if found.
    /// </summary>
    public string? MsBuildVersion { get; init; }

    /// <summary>
    /// Whether a .NET SDK is available.
    /// </summary>
    public required bool DotnetSdkAvailable { get; init; }

    /// <summary>
    /// Version of the .NET SDK whose MSBuild is used (e.g. <c>11.0.100</c>); with a Visual
    /// Studio MSBuild, the newest SDK found.
    /// </summary>
    public string? DotnetSdkVersion { get; init; }

    /// <summary>
    /// Version of the .NET runtime the server runs on (e.g. <c>11.0.0</c>).
    /// </summary>
    public string? DotnetRuntimeVersion { get; init; }
}

/// <summary>
/// Status of the current workspace.
/// </summary>
public sealed class WorkspaceStatus
{
    /// <summary>
    /// Current workspace state.
    /// </summary>
    public required WorkspaceState State { get; init; }

    /// <summary>
    /// Whether a solution is loaded.
    /// </summary>
    public required bool SolutionLoaded { get; init; }

    /// <summary>
    /// Path to loaded solution (if any).
    /// </summary>
    public string? SolutionPath { get; init; }

    /// <summary>
    /// Number of projects in solution.
    /// </summary>
    public int ProjectCount { get; init; }

    /// <summary>
    /// Number of documents across all projects.
    /// </summary>
    public int DocumentCount { get; init; }
}

/// <summary>
/// What <c>diagnose</c> reports with <c>verbose</c>.
/// </summary>
public sealed class DiagnoseDetails
{
    /// <summary>
    /// Every MSBuild instance found: MSBuildLocator's, or the probed SDKs when it found none.
    /// </summary>
    public required IReadOnlyList<MsBuildInstanceInfo> MsBuildInstances { get; init; }

    /// <summary>
    /// Why the selected instance was chosen.
    /// </summary>
    public string? SelectionReason { get; init; }

    /// <summary>
    /// MSBuild directory registered in this process, or null before the first load.
    /// </summary>
    public string? RegisteredMsBuildPath { get; init; }

    /// <summary>
    /// dotnet roots probed for SDKs.
    /// </summary>
    public required IReadOnlyList<string> SdkSearchRoots { get; init; }

    /// <summary>
    /// Projects of the loaded workspace, when a <c>solutionPath</c> was given.
    /// </summary>
    public IReadOnlyList<ProjectDiagnostics>? Projects { get; init; }

    /// <summary>
    /// Diagnostics reported while loading the workspace (<c>Kind: message</c>).
    /// </summary>
    public IReadOnlyList<string>? WorkspaceLoadDiagnostics { get; init; }
}

/// <summary>
/// An MSBuild instance <c>diagnose</c> found.
/// </summary>
public sealed class MsBuildInstanceInfo
{
    /// <summary>
    /// Instance name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Version of the SDK or Visual Studio installation.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Version of its MSBuild, if it could be read.
    /// </summary>
    public string? MsBuildVersion { get; init; }

    /// <summary>
    /// MSBuild directory.
    /// </summary>
    public required string MsBuildPath { get; init; }

    /// <summary>
    /// How it was found: DotNetSdk, VisualStudioSetup or DeveloperConsole (MSBuildLocator), or
    /// SdkDirectoryProbe.
    /// </summary>
    public required string DiscoveryType { get; init; }

    /// <summary>
    /// Whether it is the one used.
    /// </summary>
    public required bool Selected { get; init; }

    /// <summary>
    /// Why it cannot be used, if so.
    /// </summary>
    public string? Note { get; init; }
}

/// <summary>
/// A loaded project, as <c>diagnose</c> reports it with <c>verbose</c>.
/// </summary>
public sealed class ProjectDiagnostics
{
    /// <summary>
    /// Project name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Project file path.
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// Target framework of this project instance (one per framework when multi-targeted).
    /// </summary>
    public string? TargetFramework { get; init; }

    /// <summary>
    /// Number of source documents.
    /// </summary>
    public required int DocumentCount { get; init; }

    /// <summary>
    /// Analyzer and source generator references.
    /// </summary>
    public required IReadOnlyList<AnalyzerReferenceInfo> AnalyzerReferences { get; init; }

    /// <summary>
    /// Analyzers or generators of this project that could not be loaded.
    /// </summary>
    public required IReadOnlyList<string> AnalyzerLoadFailures { get; init; }
}

/// <summary>
/// An analyzer or source generator reference of a project.
/// </summary>
public sealed class AnalyzerReferenceInfo
{
    /// <summary>
    /// Display name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Assembly path, if file-based.
    /// </summary>
    public string? Path { get; init; }

    /// <summary>
    /// Number of C# diagnostic analyzers it provides.
    /// </summary>
    public int AnalyzerCount { get; init; }

    /// <summary>
    /// Number of C# source generators it provides.
    /// </summary>
    public int GeneratorCount { get; init; }
}
