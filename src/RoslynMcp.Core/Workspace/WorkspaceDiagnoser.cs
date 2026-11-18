using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;

namespace RoslynMcp.Core.Workspace;

/// <summary>
/// The <c>diagnose</c> tool, shared by the MCP server and the CLI: environment health,
/// optionally a test load of a solution, and with <c>verbose</c> the details behind both.
/// </summary>
public static class WorkspaceDiagnoser
{
    /// <summary>
    /// Runs the diagnosis.
    /// </summary>
    /// <param name="provider">Workspace provider whose environment and loading are checked.</param>
    /// <param name="params">Optional solution to load, and whether to report details.</param>
    /// <param name="toolNames">Names of the tools the caller exposes, reported as capabilities.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<DiagnoseResult> RunAsync(
        IWorkspaceProvider provider,
        DiagnoseParams @params,
        IEnumerable<string> toolNames,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<RefactoringError>();
        var warnings = new List<string>();
        var workspace = new WorkspaceStatus { State = WorkspaceState.Unloaded, SolutionLoaded = false };
        List<ProjectDiagnostics>? projects = null;
        List<string>? loadDiagnostics = null;

        // Load first: that is what registers MSBuild, which the environment report then shows.
        if (!string.IsNullOrEmpty(@params.SolutionPath))
        {
            try
            {
                using var context = await provider.CreateContextAsync(@params.SolutionPath, cancellationToken);
                workspace = new WorkspaceStatus
                {
                    State = WorkspaceState.Ready,
                    SolutionLoaded = true,
                    SolutionPath = context.LoadedPath,
                    ProjectCount = context.Solution.Projects.Count(),
                    DocumentCount = context.Solution.Projects.Sum(p => p.Documents.Count())
                };

                List<string> analyzerIssues;
                lock (context.GeneratorLoadIssues)
                {
                    // The list grows when an analyzer fails to load later; it is locked while it does.
                    analyzerIssues = context.GeneratorLoadIssues.ToList();
                }

                var diagnostics = context.LoadDiagnostics.ToList();
                warnings.AddRange(diagnostics
                    .Where(d => d.Kind == WorkspaceDiagnosticKind.Failure)
                    .Select(d => "Workspace load failure: " + d.Message));
                warnings.AddRange(analyzerIssues);

                if (@params.Verbose)
                {
                    projects = context.Solution.Projects.Select(p => DescribeProject(p, analyzerIssues)).ToList();
                    loadDiagnostics = diagnostics.Select(d => $"{d.Kind}: {d.Message}").ToList();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                workspace = new WorkspaceStatus { State = WorkspaceState.Error, SolutionLoaded = false };
                errors.Add(RefactoringError.Create(ErrorCodes.SolutionLoadFailed, ex.Message));
            }
        }

        var environment = provider.CheckEnvironment();
        if (!environment.MsBuildFound)
        {
            errors.Insert(0, RefactoringError.Create(
                ErrorCodes.MsBuildNotFound,
                environment.ErrorMessage ?? "MSBuild not found"));
        }

        var roslynVersion = typeof(Compilation).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? typeof(Compilation).Assembly.GetName().Version?.ToString();

        return new DiagnoseResult
        {
            Healthy = environment.MsBuildFound && errors.Count == 0,
            Components = new ComponentStatus
            {
                RoslynAvailable = true,
                RoslynVersion = roslynVersion,
                MsBuildFound = environment.MsBuildFound,
                MsBuildVersion = environment.MsBuildVersion,
                DotnetSdkAvailable = environment.DotnetSdkVersion != null,
                DotnetSdkVersion = environment.DotnetSdkVersion,
                DotnetRuntimeVersion = environment.DotnetRuntimeVersion
            },
            Workspace = workspace,
            // Without MSBuild nothing but diagnose itself can load a workspace.
            Capabilities = toolNames
                .Where(name => environment.MsBuildFound || name == "diagnose")
                .Order(StringComparer.Ordinal)
                .ToList(),
            Errors = errors,
            Warnings = warnings,
            Details = @params.Verbose
                ? new DiagnoseDetails
                {
                    MsBuildInstances = environment.Instances,
                    SelectionReason = environment.SelectionReason,
                    RegisteredMsBuildPath = environment.RegisteredMsBuildPath,
                    SdkSearchRoots = environment.SdkSearchRoots,
                    Projects = projects,
                    WorkspaceLoadDiagnostics = loadDiagnostics
                }
                : null
        };
    }

    private static ProjectDiagnostics DescribeProject(Project project, IReadOnlyList<string> analyzerIssues) => new()
    {
        Name = project.Name,
        FilePath = project.FilePath,
        TargetFramework = TargetFrameworkOf(project),
        DocumentCount = project.DocumentIds.Count,
        AnalyzerReferences = project.AnalyzerReferences.Select(DescribeAnalyzerReference).ToList(),
        // The load-failure messages name their project ("... for project 'X'").
        AnalyzerLoadFailures = analyzerIssues
            .Where(issue => issue.Contains($"project '{project.Name}'", StringComparison.Ordinal))
            .ToList()
    };

    /// <summary>
    /// The project's target framework: the SDK hands it to analyzers as a global option;
    /// failing that, a multi-targeted project's name ends in <c>(tfm)</c>.
    /// </summary>
    private static string? TargetFrameworkOf(Project project)
    {
        if (project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions
                .TryGetValue("build_property.TargetFramework", out var targetFramework) &&
            !string.IsNullOrWhiteSpace(targetFramework))
        {
            return targetFramework;
        }

        var open = project.Name.LastIndexOf('(');
        return open > 0 && project.Name.EndsWith(')') ? project.Name[(open + 1)..^1] : null;
    }

    private static AnalyzerReferenceInfo DescribeAnalyzerReference(AnalyzerReference reference)
    {
        int analyzers = 0, generators = 0;
        try
        {
            // Loads the assembly (if no compilation has yet); a failure is recorded by the
            // load-failure handler and shows up under AnalyzerLoadFailures.
            analyzers = reference.GetAnalyzers(LanguageNames.CSharp).Length;
            generators = reference.GetGenerators(LanguageNames.CSharp).Length;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Counts stay 0.
        }

        return new AnalyzerReferenceInfo
        {
            Name = reference.Display ?? Path.GetFileNameWithoutExtension(reference.FullPath) ?? "<unknown>",
            Path = reference.FullPath,
            AnalyzerCount = analyzers,
            GeneratorCount = generators
        };
    }
}
