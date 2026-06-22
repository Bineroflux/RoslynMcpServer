using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring;

namespace RoslynMcp.Core.Workspace;

/// <summary>
/// Creates MSBuildWorkspace instances with proper configuration and caches them
/// across requests via <see cref="WorkspaceCache"/>.
/// </summary>
public sealed class MSBuildWorkspaceProvider : IWorkspaceProvider, IDisposable
{
    private static bool _msBuildRegistered;
    private static readonly object _registrationLock = new();
    private static VisualStudioInstance? _registeredInstance;

    /// <summary>
    /// Optional logging callback for diagnostics.
    /// Set this to capture MSBuild registration and workspace loading events.
    /// </summary>
    public static Action<string>? LogCallback { get; set; }

    /// <summary>
    /// Optional error logging callback for diagnostics.
    /// Set this to capture errors and exceptions.
    /// </summary>
    public static Action<string, Exception?>? LogErrorCallback { get; set; }

    /// <summary>
    /// Default timeout for workspace loading operations (5 minutes).
    /// </summary>
    public static readonly TimeSpan DefaultWorkspaceLoadTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Environment variable that disables analyzer/source-generator shadow copying.
    /// Set to <c>1</c> / <c>true</c> to fall back to Roslyn's default in-place loader
    /// (which locks the analyzer DLLs and can break concurrent <c>dotnet build</c>).
    /// </summary>
    public const string DisableShadowCopyEnvVar = "ROSLYNMCP_DISABLE_ANALYZER_SHADOW_COPY";

    private readonly IFileWriter _fileWriter;
    private readonly TimeSpan _workspaceLoadTimeout;
    private readonly WorkspaceCache _cache;

    /// <summary>
    /// Creates a new workspace provider.
    /// </summary>
    /// <param name="fileWriter">Optional file writer for atomic operations.</param>
    /// <param name="workspaceLoadTimeout">
    /// Optional timeout for workspace loading operations.
    /// Defaults to <see cref="DefaultWorkspaceLoadTimeout"/> (5 minutes).
    /// </param>
    /// <param name="cache">Optional workspace cache; a default one is created if null.</param>
    public MSBuildWorkspaceProvider(
        IFileWriter? fileWriter = null,
        TimeSpan? workspaceLoadTimeout = null,
        WorkspaceCache? cache = null)
    {
        _fileWriter = fileWriter ?? new AtomicFileWriter();
        _workspaceLoadTimeout = workspaceLoadTimeout ?? DefaultWorkspaceLoadTimeout;
        _cache = cache ?? new WorkspaceCache();
        _cache.LogCallback ??= msg => LogCallback?.Invoke(msg);
        _cache.LogErrorCallback ??= (msg, ex) => LogErrorCallback?.Invoke(msg, ex);
    }

    /// <inheritdoc />
    public async Task<WorkspaceContext> CreateContextAsync(
        string projectOrSolutionPath,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(projectOrSolutionPath);
        EnsureMsBuildRegistered();

        var (context, loadMs) = await _cache.GetOrCreateAsync(
            projectOrSolutionPath,
            ct => LoadWorkspaceAsync(projectOrSolutionPath, ct),
            cancellationToken);

        WorkspaceTimingContext.RecordLoadMs(loadMs);
        return context;
    }

    private static void ValidateRequest(string projectOrSolutionPath)
    {
        if (string.IsNullOrWhiteSpace(projectOrSolutionPath))
        {
            throw new RefactoringException(
                ErrorCodes.MissingRequiredParam,
                "Project or solution path is required.");
        }

        if (!PathResolver.IsValidSolutionOrProjectPath(projectOrSolutionPath))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSourcePath,
                "Path must be a .sln, .slnx, or .csproj file.");
        }

        if (!File.Exists(projectOrSolutionPath))
        {
            throw new RefactoringException(
                ErrorCodes.SourceFileNotFound,
                $"File not found: {projectOrSolutionPath}");
        }
    }

    private async Task<WorkspaceContext> LoadWorkspaceAsync(
        string projectOrSolutionPath,
        CancellationToken cancellationToken)
    {
        LogCallback?.Invoke($"Creating workspace for: {projectOrSolutionPath}");

        var properties = new Dictionary<string, string>
        {
            ["CheckForSystemRuntimeDependency"] = "true",
            ["DesignTimeBuild"] = "true",
            ["BuildingInsideVisualStudio"] = "true"
        };

        var workspace = MSBuildWorkspace.Create(properties);

        // Collect workspace diagnostics but don't fail on warnings
        // Using ConcurrentBag for thread-safe collection as events may fire from multiple threads
        var diagnostics = new ConcurrentBag<WorkspaceDiagnostic>();
        workspace.RegisterWorkspaceFailedHandler(args =>
        {
            if (args.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
            {
                LogErrorCallback?.Invoke($"Workspace failure: {args.Diagnostic.Message}", null);
            }
            else
            {
                LogCallback?.Invoke($"Workspace warning: {args.Diagnostic.Message}");
            }
            diagnostics.Add(args.Diagnostic);
        });

        Solution solution;
        var normalizedPath = PathResolver.NormalizePath(projectOrSolutionPath);

        // Create a linked cancellation token that includes both the caller's token and a timeout
        using var timeoutCts = new CancellationTokenSource(_workspaceLoadTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            if (normalizedPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                LogCallback?.Invoke($"Opening solution: {normalizedPath}");
                solution = await workspace.OpenSolutionAsync(normalizedPath, cancellationToken: linkedCts.Token);
                LogCallback?.Invoke($"Solution opened with {solution.ProjectIds.Count} project(s).");
            }
            else
            {
                LogCallback?.Invoke($"Opening project: {normalizedPath}");
                var project = await workspace.OpenProjectAsync(normalizedPath, cancellationToken: linkedCts.Token);
                solution = project.Solution;
                LogCallback?.Invoke($"Project opened: {project.Name}");
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var errorMsg = $"Workspace loading timed out after {_workspaceLoadTimeout.TotalMinutes:F0} minutes. " +
                "The solution may be too large or MSBuild may be stuck. " +
                "Consider loading a specific project instead of the entire solution.";
            LogErrorCallback?.Invoke(errorMsg, null);
            workspace.Dispose();
            throw new RefactoringException(
                ErrorCodes.SolutionLoadFailed,
                errorMsg);
        }

        // Tolerate per-project failures the way VS IDE does. NuGet restore problems
        // (e.g. NU1903/NU1904 vulnerability advisories) and similar surface as
        // WorkspaceDiagnosticKind.Failure, but the solution and the unaffected
        // projects still load. Only treat it as fatal if nothing came back at all.
        if (solution.ProjectIds.Count == 0)
        {
            workspace.Dispose();
            var errors = diagnostics.Where(d => d.Kind == WorkspaceDiagnosticKind.Failure).ToList();
            var detail = errors.Count > 0
                ? string.Join("; ", errors.Select(e => e.Message))
                : "no projects were loaded";
            throw new RefactoringException(
                ErrorCodes.SolutionLoadFailed,
                $"Failed to load solution: {detail}");
        }

        // Strip UnresolvedAnalyzerReference instances so downstream Roslyn paths
        // (compilation, code actions, SymbolFinder) don't trip on them. Roslyn
        // creates these placeholders when MSBuild reported an analyzer that
        // couldn't actually be loaded (missing DLL, target-framework mismatch,
        // restore artifact gone). Several internal switches don't have a case
        // for them and throw "Unexpected value 'UnresolvedAnalyzerReference'".
        var generatorIssues = new List<string>();
        solution = StripUnresolvedAnalyzerReferences(solution, generatorIssues);

        // Re-wrap analyzer/source-generator references so their assemblies load from
        // shadow copies instead of the build output. This is what lets a concurrent
        // `dotnet build` overwrite the *.SourceGenerator.dll in obj/bin while this
        // server has the solution open (the way Visual Studio behaves). Done before
        // materializing compilations, since that is what first triggers the loader.
        ShadowCopyAnalyzerAssemblyLoader? analyzerLoader = null;
        if (ShadowCopyEnabled)
        {
            analyzerLoader = new ShadowCopyAnalyzerAssemblyLoader(msg => LogCallback?.Invoke(msg));
            solution = ShadowCopyAnalyzerReferences(solution, analyzerLoader);
        }

        // Eagerly materialize compilations so the first symbol query doesn't silently pay
        // a multi-minute lazy-compilation cost. Including this in LoadWorkspaceAsync folds
        // the real cold-load cost into WorkspaceLoadMs and keeps subsequent queries cheap.
        await MaterializeCompilationsAsync(workspace, solution, timeoutCts, linkedCts.Token, cancellationToken);

        return new WorkspaceContext(
            workspace, solution, normalizedPath, _fileWriter,
            generatorLoadIssues: generatorIssues.Count == 0 ? null : generatorIssues,
            analyzerAssemblyLoader: analyzerLoader,
            msbuildProperties: properties);
    }

    /// <summary>
    /// Returns a solution where every project's analyzer reference list has any
    /// <see cref="UnresolvedAnalyzerReference"/> entries removed. Records any stripped
    /// references that look like known source generators (currently: Razor) into
    /// <paramref name="generatorIssues"/> so callers can surface them.
    /// </summary>
    /// <remarks>
    /// The cleaned solution is returned as the in-memory snapshot only; it is
    /// deliberately NOT pushed back via <c>workspace.TryApplyChanges</c>. MSBuildWorkspace
    /// persists analyzer-reference edits to the <c>.csproj</c> on disk, so applying them
    /// would rewrite the user's project files. Every operation reads through
    /// <see cref="WorkspaceContext.Solution"/>, so the held snapshot is authoritative.
    /// </remarks>
    private Solution StripUnresolvedAnalyzerReferences(
        Solution solution, List<string> generatorIssues)
    {
        foreach (var projectId in solution.ProjectIds)
        {
            var project = solution.GetProject(projectId);
            if (project is null) continue;

            var keep = new List<AnalyzerReference>(project.AnalyzerReferences.Count);
            var razorRemovedPaths = new List<string>();
            foreach (var reference in project.AnalyzerReferences)
            {
                if (reference is UnresolvedAnalyzerReference)
                {
                    if (LooksLikeRazorGenerator(reference))
                        razorRemovedPaths.Add(reference.FullPath ?? reference.Display ?? "<unknown>");
                    continue;
                }
                keep.Add(reference);
            }
            var removed = project.AnalyzerReferences.Count - keep.Count;
            if (removed == 0) continue;

            LogCallback?.Invoke(
                $"Stripping {removed} unresolved analyzer reference(s) from project '{project.Name}'.");

            foreach (var path in razorRemovedPaths)
            {
                var msg =
                    $"RazorSourceGenerator failed to load for project '{project.Name}' " +
                    $"(unresolved analyzer reference: {path}). " +
                    ".razor / .cshtml diagnostics will not appear.";
                LogErrorCallback?.Invoke(msg, null);
                generatorIssues.Add(msg);
            }

            solution = solution.WithProjectAnalyzerReferences(projectId, keep);
        }

        return solution;
    }

    private static bool LooksLikeRazorGenerator(AnalyzerReference reference)
    {
        var display = reference.Display ?? string.Empty;
        var fullPath = reference.FullPath ?? string.Empty;
        return display.Contains("Razor.SourceGenerators", StringComparison.OrdinalIgnoreCase)
            || fullPath.Contains("Razor.SourceGenerators", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether analyzer/source-generator assemblies should be shadow-copied before
    /// loading. Enabled unless <see cref="DisableShadowCopyEnvVar"/> is set truthy.
    /// </summary>
    private static bool ShadowCopyEnabled
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(DisableShadowCopyEnvVar);
            return !(string.Equals(value, "1", StringComparison.Ordinal)
                  || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Replaces every project's <see cref="AnalyzerFileReference"/> with an equivalent
    /// reference backed by <paramref name="loader"/>, so the analyzer and
    /// source-generator assemblies are loaded from shadow copies rather than the
    /// on-disk build output. The copy is lazy — it only happens when an analyzer is
    /// actually loaded — so unused analyzers cost nothing.
    /// </summary>
    /// <remarks>
    /// The rewrite lives in the returned in-memory snapshot only. It must NOT be pushed
    /// back via <c>workspace.TryApplyChanges</c>: MSBuildWorkspace persists
    /// analyzer-reference edits to the <c>.csproj</c>, which would rewrite every project
    /// file on disk (with absolute analyzer paths). Reads go through
    /// <see cref="WorkspaceContext.Solution"/>, so the snapshot alone is sufficient.
    /// </remarks>
    private Solution ShadowCopyAnalyzerReferences(
        Solution solution, ShadowCopyAnalyzerAssemblyLoader loader)
    {
        var changed = false;
        foreach (var projectId in solution.ProjectIds)
        {
            var project = solution.GetProject(projectId);
            if (project is null || project.AnalyzerReferences.Count == 0) continue;

            var rewritten = new List<AnalyzerReference>(project.AnalyzerReferences.Count);
            var rewroteAny = false;
            foreach (var reference in project.AnalyzerReferences)
            {
                if (reference is AnalyzerFileReference fileRef && !string.IsNullOrEmpty(fileRef.FullPath))
                {
                    rewritten.Add(new AnalyzerFileReference(fileRef.FullPath, loader));
                    rewroteAny = true;
                }
                else
                {
                    // In-memory (AnalyzerImageReference) and already-stripped unresolved
                    // references have no file to lock; leave them as-is.
                    rewritten.Add(reference);
                }
            }

            if (!rewroteAny) continue;

            solution = solution.WithProjectAnalyzerReferences(projectId, rewritten);
            changed = true;
        }

        if (changed)
            LogCallback?.Invoke("Re-wrapped analyzer references to load from shadow copies.");

        return solution;
    }

    private async Task MaterializeCompilationsAsync(
        MSBuildWorkspace workspace,
        Solution solution,
        CancellationTokenSource timeoutCts,
        CancellationToken linkedToken,
        CancellationToken callerToken)
    {
        var projectCount = solution.ProjectIds.Count;
        LogCallback?.Invoke($"Materializing compilations for {projectCount} project(s)...");
        var sw = Stopwatch.StartNew();
        try
        {
            await Task.WhenAll(solution.Projects.Select(async project =>
            {
                try
                {
                    await project.GetCompilationAsync(linkedToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A single broken project shouldn't fail the whole load; the
                    // workspace diagnostics channel already surfaces these.
                    LogCallback?.Invoke(
                        $"Compilation for '{project.Name}' did not materialize cleanly: {ex.Message}");
                }
            }));
        }
        catch (OperationCanceledException)
            when (timeoutCts.IsCancellationRequested && !callerToken.IsCancellationRequested)
        {
            var errorMsg =
                $"Compilation materialization timed out after {_workspaceLoadTimeout.TotalMinutes:F0} minutes.";
            LogErrorCallback?.Invoke(errorMsg, null);
            workspace.Dispose();
            throw new RefactoringException(ErrorCodes.SolutionLoadFailed, errorMsg);
        }
        sw.Stop();
        LogCallback?.Invoke(
            $"Compilations materialized in {sw.ElapsedMilliseconds} ms ({projectCount} project(s)).");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cache.Dispose();
    }

    /// <inheritdoc />
    public EnvironmentDiagnostics CheckEnvironment()
    {
        try
        {
            var instances = MSBuildLocator.QueryVisualStudioInstances().ToArray();

            if (instances.Length == 0)
            {
                return new EnvironmentDiagnostics
                {
                    MsBuildFound = false,
                    ErrorMessage = "MSBuild not found. Install Visual Studio, Build Tools, or .NET SDK."
                };
            }

            var preferred = SelectPreferredInstance(instances);

            return new EnvironmentDiagnostics
            {
                MsBuildFound = true,
                MsBuildVersion = preferred.Version.ToString(),
                MsBuildPath = preferred.MSBuildPath,
                DotnetSdkVersion = Environment.Version.ToString(),
                SearchPaths = instances.Select(i => i.MSBuildPath).ToList()
            };
        }
        catch (Exception ex)
        {
            return new EnvironmentDiagnostics
            {
                MsBuildFound = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private static void EnsureMsBuildRegistered()
    {
        if (_msBuildRegistered || MSBuildLocator.IsRegistered)
        {
            LogCallback?.Invoke("MSBuild already registered, skipping registration.");
            _msBuildRegistered = true;
            return;
        }

        lock (_registrationLock)
        {
            if (_msBuildRegistered || MSBuildLocator.IsRegistered)
            {
                LogCallback?.Invoke("MSBuild already registered (checked inside lock).");
                _msBuildRegistered = true;
                return;
            }

            LogCallback?.Invoke("Querying Visual Studio instances for MSBuild...");
            var instances = MSBuildLocator.QueryVisualStudioInstances().ToArray();
            LogCallback?.Invoke($"Found {instances.Length} MSBuild instance(s).");

            if (instances.Length == 0)
            {
                // Try to find .NET SDK manually
                LogCallback?.Invoke("No instances found, searching for .NET SDK manually...");
                var sdkPath = FindDotNetSdk();
                if (sdkPath != null)
                {
                    LogCallback?.Invoke($"Found .NET SDK at: {sdkPath}");
                    MSBuildLocator.RegisterMSBuildPath(sdkPath);
                    LogCallback?.Invoke("MSBuild registered via .NET SDK path.");
                    _msBuildRegistered = true;
                    return;
                }

                var errorMsg = "MSBuild not found. Install Visual Studio, Build Tools, or .NET SDK.";
                LogErrorCallback?.Invoke(errorMsg, null);
                throw new RefactoringException(
                    ErrorCodes.MsBuildNotFound,
                    errorMsg);
            }

            _registeredInstance = SelectPreferredInstance(instances);
            LogCallback?.Invoke($"Selected MSBuild instance: {_registeredInstance.Name} v{_registeredInstance.Version} at {_registeredInstance.MSBuildPath}");
            MSBuildLocator.RegisterInstance(_registeredInstance);
            LogCallback?.Invoke("MSBuild registered successfully.");
            _msBuildRegistered = true;
        }
    }

    private static string? FindDotNetSdk()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var sdkBase = Path.Combine(programFiles, "dotnet", "sdk");

        if (!Directory.Exists(sdkBase))
        {
            return null;
        }

        // Find the latest SDK version
        var sdkVersions = Directory.GetDirectories(sdkBase)
            .Select(Path.GetFileName)
            .Where(d => d != null && char.IsDigit(d[0]))
            .OrderByDescending(v => v)
            .ToList();

        if (sdkVersions.Count == 0)
        {
            return null;
        }

        var latestSdk = Path.Combine(sdkBase, sdkVersions[0]!);
        return Directory.Exists(latestSdk) ? latestSdk : null;
    }

    private static VisualStudioInstance SelectPreferredInstance(VisualStudioInstance[] instances)
    {
        // Prefer .NET SDK over Visual Studio installations (more predictable)
        return instances
            .OrderByDescending(i => i.DiscoveryType == DiscoveryType.DotNetSdk)
            .ThenByDescending(i => i.Version)
            .First();
    }
}
