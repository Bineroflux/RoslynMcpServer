using RoslynMcp.Contracts.Models;

namespace RoslynMcp.Core.Workspace;

/// <summary>
/// Factory for creating scoped workspace contexts.
/// Ensures proper MSBuildWorkspace lifecycle management.
/// </summary>
public interface IWorkspaceProvider
{
    /// <summary>
    /// Creates a workspace context for the given project/solution.
    /// Caller must dispose to release resources.
    /// </summary>
    /// <param name="projectOrSolutionPath">Path to .sln or .csproj file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Scoped workspace context.</returns>
    Task<WorkspaceContext> CreateContextAsync(
        string projectOrSolutionPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if the environment is properly configured for workspace loading.
    /// </summary>
    /// <returns>Diagnostic information about the environment.</returns>
    EnvironmentDiagnostics CheckEnvironment();
}

/// <summary>
/// Diagnostic information about the runtime environment.
/// </summary>
public sealed class EnvironmentDiagnostics
{
    /// <summary>
    /// Whether an MSBuild that can load projects was found (or is already registered).
    /// </summary>
    public required bool MsBuildFound { get; init; }

    /// <summary>
    /// Version of that MSBuild's <c>Microsoft.Build.dll</c> (e.g. <c>18.12.2</c>).
    /// </summary>
    public string? MsBuildVersion { get; init; }

    /// <summary>
    /// That MSBuild's directory.
    /// </summary>
    public string? MsBuildPath { get; init; }

    /// <summary>
    /// Version of the .NET SDK the MSBuild belongs to; for a Visual Studio MSBuild, the newest
    /// SDK found.
    /// </summary>
    public string? DotnetSdkVersion { get; init; }

    /// <summary>
    /// Version of the .NET runtime this process runs on.
    /// </summary>
    public required string DotnetRuntimeVersion { get; init; }

    /// <summary>
    /// Any error message if environment check failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Every MSBuild instance found, the selected one marked.
    /// </summary>
    public IReadOnlyList<MsBuildInstanceInfo> Instances { get; init; } = [];

    /// <summary>
    /// Why the selected instance was chosen (or why none was).
    /// </summary>
    public string? SelectionReason { get; init; }

    /// <summary>
    /// MSBuild directory registered in this process, or null before the first load.
    /// </summary>
    public string? RegisteredMsBuildPath { get; init; }

    /// <summary>
    /// dotnet roots probed for SDKs.
    /// </summary>
    public IReadOnlyList<string> SdkSearchRoots { get; init; } = [];
}
