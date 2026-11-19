using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
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
    private static MsBuildSelection? _registration;

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

        // Required for both real projects/solutions and standalone .cs files: the latter are
        // materialized into a project and loaded through MSBuildWorkspace too (only the
        // framework-only ad-hoc fallback would not need it).
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

        if (!PathResolver.IsAbsolutePath(projectOrSolutionPath))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSourcePath,
                $"Path must be absolute: {projectOrSolutionPath}");
        }

        if (!PathResolver.IsValidWorkspacePath(projectOrSolutionPath))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSourcePath,
                "Path must be a .sln, .slnx, .csproj, or standalone .cs file.");
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
        var normalizedPath = PathResolver.NormalizePath(projectOrSolutionPath);

        // What MSBuild actually opens, what the workspace reports as its loaded path, and
        // (for a standalone file) its file-based-program identity. These coincide for a
        // normal .sln/.slnx/.csproj.
        var openPath = normalizedPath;
        var loadedPath = normalizedPath;
        FileBasedProgramInfo? fileBasedProgram = null;

        // Standalone .cs file: turn it into a real project via the SDK ("file-based program")
        // and load that through the normal MSBuild path, so it gets the SDK's default
        // analyzers/source generators, #:project references as source projects, and the exact
        // references/options dotnet build would use.
        if (PathResolver.IsStandaloneCSharpFile(normalizedPath))
        {
            var materialized = await FileBasedProgramProject.MaterializeAsync(
                normalizedPath,
                _workspaceLoadTimeout,
                msg => LogCallback?.Invoke(msg),
                (msg, ex) => LogErrorCallback?.Invoke(msg, ex),
                cancellationToken);

            if (materialized is null)
            {
                // The SDK couldn't produce a project (run-api unavailable, build error, etc.).
                // Degrade to a framework-only ad-hoc workspace so the file still loads — its
                // own symbols resolve and diagnostics explain anything that's missing.
                LogCallback?.Invoke(
                    "Could not materialize the file-based program; using a framework-only ad-hoc workspace.");
                return await StandaloneFileWorkspace.CreateAsync(
                    normalizedPath, _fileWriter, msg => LogCallback?.Invoke(msg), cancellationToken);
            }

            openPath = materialized.CsprojPath;
            // Keep the .cs as the workspace identity (cache key, watch seed), not the temp csproj.
            loadedPath = normalizedPath;
            fileBasedProgram = new FileBasedProgramInfo(normalizedPath, materialized.DirectiveSignature);
        }

        LogCallback?.Invoke($"Creating workspace for: {openPath}");

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

        // Create a linked cancellation token that includes both the caller's token and a timeout
        using var timeoutCts = new CancellationTokenSource(_workspaceLoadTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            if (openPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                openPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                LogCallback?.Invoke($"Opening solution: {openPath}");
                solution = await workspace.OpenSolutionAsync(openPath, cancellationToken: linkedCts.Token);
                LogCallback?.Invoke($"Solution opened with {solution.ProjectIds.Count} project(s).");
            }
            else
            {
                LogCallback?.Invoke($"Opening project: {openPath}");
                var project = await workspace.OpenProjectAsync(openPath, cancellationToken: linkedCts.Token);
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
        finally
        {
            // For a standalone .cs file, openPath is the transient wrapper .csproj we materialized
            // in the user's tree. Open*Async has fully read it by now, so delete it whether the
            // open succeeded or failed — the in-memory solution no longer needs it on disk.
            if (fileBasedProgram is not null)
                FileBasedProgramProject.TryDeleteFile(openPath);
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

        // Watch for analyzer references that resolve on disk but can't actually be
        // loaded. Must be wired up before compilations materialize — that is what first
        // enumerates a reference's extensions, and the outcome is cached afterwards.
        SubscribeToAnalyzerLoadFailures(solution, generatorIssues);

        // Eagerly materialize compilations so the first symbol query doesn't silently pay
        // a multi-minute lazy-compilation cost. Including this in LoadWorkspaceAsync folds
        // the real cold-load cost into WorkspaceLoadMs and keeps subsequent queries cheap.
        await MaterializeCompilationsAsync(workspace, solution, timeoutCts, linkedCts.Token, cancellationToken);

        // The projects' <CodePage>, which only the documents' loaders were given: read off the
        // texts now, as MSBuild decoded them, so files read later are decoded the same way. The
        // texts are loaded by now, so this takes no time worth cancelling.
        IReadOnlyDictionary<ProjectId, Encoding>? declaredEncodings = null;
        try
        {
            declaredEncodings = await ProjectFileConventions.InferDeclaredEncodingsAsync(solution, CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogErrorCallback?.Invoke(
                "Could not tell the projects' <CodePage> from their documents; files changed later are decoded without it.", ex);
        }

        return new WorkspaceContext(
            workspace, solution, loadedPath, _fileWriter,
            generatorLoadIssues: generatorIssues.Count == 0 ? null : generatorIssues,
            analyzerAssemblyLoader: analyzerLoader,
            msbuildProperties: properties,
            fileBasedProgram: fileBasedProgram,
            loadDiagnostics: diagnostics,
            declaredEncodings: declaredEncodings);
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
        // The assembly has been named several things across SDK bands: the older
        // Microsoft.NET.Sdk.Razor.SourceGenerators, and (since .NET 8) the Razor compiler
        // itself, shipped as Microsoft.CodeAnalysis.Razor.Compiler under the SDK's
        // "source-generators" folder. Match both rather than only the historical name.
        var display = reference.Display ?? string.Empty;
        var fullPath = reference.FullPath ?? string.Empty;
        return Matches(display) || Matches(fullPath);

        static bool Matches(string value) =>
            value.Contains("Razor.SourceGenerators", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Razor.Compiler", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Subscribes to <see cref="AnalyzerFileReference.AnalyzerLoadFailed"/> on every
    /// file-backed analyzer reference in the solution, recording each failure into
    /// <paramref name="issues"/> so <c>get_diagnostics</c> can explain it as an
    /// <c>RMCP0001</c> info diagnostic.
    /// </summary>
    /// <remarks>
    /// The motivating case is <c>ReferencesNewerCompiler</c>. The Razor generator shipped
    /// with an SDK binds against the Roslyn version that SDK ships, assembly binding never
    /// rolls backward, and an older host then gets an <em>empty</em> generator array back
    /// from <c>GetGenerators</c> — no exception, no unresolved reference, nothing. Every
    /// RZ diagnostic disappears, and so do the CS diagnostics from the <c>.g.cs</c> that
    /// was never generated. Without this hook the server reports a confident zero.
    ///
    /// Must run before compilations are materialized: the event fires the first time a
    /// reference's extensions are enumerated and the result is cached, so a subscriber
    /// that arrives later sees nothing.
    /// </remarks>
    internal void SubscribeToAnalyzerLoadFailures(Solution solution, List<string> issues)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var projectId in solution.ProjectIds)
        {
            var project = solution.GetProject(projectId);
            if (project is null) continue;

            var projectName = project.Name;
            foreach (var reference in project.AnalyzerReferences)
            {
                if (reference is not AnalyzerFileReference fileRef) continue;

                fileRef.AnalyzerLoadFailed += (_, e) =>
                {
                    // NoAnalyzers only means the assembly carries no extension of the
                    // requested kind. That is the normal state of the support libraries
                    // sitting next to a generator, not a failure.
                    if (e.ErrorCode == AnalyzerLoadFailureEventArgs.FailureErrorCode.NoAnalyzers)
                        return;

                    var message = DescribeAnalyzerLoadFailure(projectName, fileRef, e);
                    lock (issues)
                    {
                        // Analyzer and generator enumeration each raise the event, and a
                        // reference instance may be shared between projects.
                        if (!seen.Add(message)) return;
                        issues.Add(message);
                    }

                    LogErrorCallback?.Invoke(message, e.Exception);
                };
            }
        }
    }

    /// <summary>
    /// Adapts a Roslyn load-failure event to <see cref="BuildAnalyzerLoadFailureMessage"/>.
    /// </summary>
    private static string DescribeAnalyzerLoadFailure(
        string projectName,
        AnalyzerFileReference reference,
        AnalyzerLoadFailureEventArgs e)
    {
        var name = string.IsNullOrEmpty(reference.FullPath)
            ? reference.Display ?? "<unknown>"
            : Path.GetFileNameWithoutExtension(reference.FullPath);

        var detail = !string.IsNullOrWhiteSpace(e.Message)
            ? e.Message
            : e.Exception?.Message;

        return BuildAnalyzerLoadFailureMessage(
            projectName, name, e.ErrorCode, detail, typeof(Compilation).Assembly.GetName().Version);
    }

    /// <summary>How <see cref="BuildAnalyzerLoadFailureMessage"/>'s messages start.</summary>
    private const string AnalyzerLoadFailurePrefix = "Analyzer/source generator '";

    /// <summary>
    /// Whether a generator load issue says that an analyzer reference could not be loaded
    /// (<see cref="BuildAnalyzerLoadFailureMessage"/>), so its analyzers are missing too.
    /// </summary>
    internal static bool IsAnalyzerLoadFailure(string issue) =>
        issue.StartsWith(AnalyzerLoadFailurePrefix, StringComparison.Ordinal);

    /// <summary>
    /// Builds the human-readable explanation for a failed analyzer/generator load.
    /// </summary>
    /// <param name="projectName">Project whose reference failed to load.</param>
    /// <param name="analyzerName">Assembly name of the analyzer/generator.</param>
    /// <param name="errorCode">Reason Roslyn reported.</param>
    /// <param name="detail">Roslyn's message or exception text, when it supplied one.</param>
    /// <param name="hostRoslynVersion">Version of Microsoft.CodeAnalysis this process hosts.</param>
    internal static string BuildAnalyzerLoadFailureMessage(
        string projectName,
        string analyzerName,
        AnalyzerLoadFailureEventArgs.FailureErrorCode errorCode,
        string? detail,
        Version? hostRoslynVersion)
    {
        if (errorCode == AnalyzerLoadFailureEventArgs.FailureErrorCode.ReferencesNewerCompiler)
        {
            // Roslyn supplies neither a message nor an exception for this code, so the
            // explanation has to be written here. This is the one the user can act on:
            // the SDK moved ahead of the server's Roslyn.
            return
                $"{AnalyzerLoadFailurePrefix}{analyzerName}' could not be loaded for project '{projectName}': " +
                $"it targets a newer Roslyn than this server hosts ({hostRoslynVersion}). " +
                "Everything it contributes is missing — for the Razor generator that means no RZ " +
                "diagnostics for .razor/.cshtml and no CS diagnostics from @code blocks. " +
                "Upgrade roslyn-mcp to a build whose Microsoft.CodeAnalysis version is at least the " +
                "one shipped with your .NET SDK.";
        }

        return
            $"{AnalyzerLoadFailurePrefix}{analyzerName}' could not be loaded for project '{projectName}' " +
            $"({errorCode}): {detail ?? errorCode.ToString()}. " +
            "Its diagnostics and generated code will be missing.";
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
    /// <remarks>
    /// Reports the MSBuild this process registered, or, before the first workspace load,
    /// the one <see cref="EnsureMsBuildRegistered"/> would register: both go through
    /// <see cref="SelectMsBuild"/>, so "found" here means a load can register it.
    /// </remarks>
    public EnvironmentDiagnostics CheckEnvironment()
    {
        var runtimeVersion = Environment.Version.ToString();
        try
        {
            var selection = _registration ?? SelectMsBuild();
            var registeredPath = _registration?.Selected?.Info.MsBuildPath
                ?? (MSBuildLocator.IsRegistered ? LoadedMsBuildDirectory() : null);
            var msBuildPath = registeredPath ?? selection.Selected?.Info.MsBuildPath;

            return new EnvironmentDiagnostics
            {
                MsBuildFound = msBuildPath != null || MSBuildLocator.IsRegistered,
                MsBuildPath = msBuildPath,
                MsBuildVersion = msBuildPath == null ? null : ReadMsBuildVersion(msBuildPath),
                DotnetSdkVersion = (msBuildPath == null ? null : SdkFolderVersion(msBuildPath))
                    ?? DotNetSdkLocator.SelectNewest(DotNetSdkLocator.FindAll(selection.SearchRoots), Environment.Version.Major)?.Version,
                DotnetRuntimeVersion = runtimeVersion,
                ErrorMessage = msBuildPath == null && !MSBuildLocator.IsRegistered
                    ? $"MSBuild not found. Install Visual Studio, Build Tools, or .NET SDK. {selection.Reason}"
                    : null,
                Instances = selection.Candidates.Select(c => c.Info).ToList(),
                SelectionReason = selection.Reason,
                RegisteredMsBuildPath = registeredPath,
                SdkSearchRoots = selection.SearchRoots
            };
        }
        catch (Exception ex)
        {
            return new EnvironmentDiagnostics
            {
                MsBuildFound = MSBuildLocator.IsRegistered,
                DotnetRuntimeVersion = runtimeVersion,
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

            LogCallback?.Invoke("Looking for MSBuild...");
            var selection = SelectMsBuild();
            LogCallback?.Invoke($"Found {selection.Candidates.Count} MSBuild candidate(s). {selection.Reason}");

            if (selection.Selected is not { } selected)
            {
                var errorMsg = "MSBuild not found. Install Visual Studio, Build Tools, or .NET SDK.";
                LogErrorCallback?.Invoke(errorMsg, null);
                throw new RefactoringException(
                    ErrorCodes.MsBuildNotFound,
                    errorMsg);
            }

            LogCallback?.Invoke($"Selected MSBuild: {selected.Info.Name} {selected.Info.Version} at {selected.Info.MsBuildPath}");
            if (selected.Instance != null)
                MSBuildLocator.RegisterInstance(selected.Instance);
            else
                MSBuildLocator.RegisterMSBuildPath(selected.Info.MsBuildPath);

            LogCallback?.Invoke("MSBuild registered successfully.");
            _registration = selection;
            _msBuildRegistered = true;
        }
    }

    /// <summary>
    /// An MSBuild that could load projects, and the MSBuildLocator instance behind it (null
    /// for an SDK found by probing).
    /// </summary>
    private sealed record MsBuildCandidate(MsBuildInstanceInfo Info, VisualStudioInstance? Instance);

    /// <summary>
    /// Every MSBuild found, the one to use (if any) and why.
    /// </summary>
    private sealed record MsBuildSelection(
        IReadOnlyList<MsBuildCandidate> Candidates,
        MsBuildCandidate? Selected,
        string Reason,
        IReadOnlyList<string> SearchRoots);

    /// <summary>
    /// Picks the MSBuild to register. MSBuildLocator's instances come first, preferring a
    /// .NET SDK over Visual Studio and then the highest version; when it finds none, the
    /// SDKs under the usual dotnet roots are probed and the newest one this runtime can host
    /// is used (see <see cref="DotNetSdkLocator"/>).
    /// </summary>
    private static MsBuildSelection SelectMsBuild()
    {
        var roots = DotNetSdkLocator.GetSearchRoots();
        var instances = MSBuildLocator.QueryVisualStudioInstances().ToArray();
        if (instances.Length > 0)
        {
            var preferred = SelectPreferredInstance(instances);
            var candidates = instances
                .Select(i => new MsBuildCandidate(
                    new MsBuildInstanceInfo
                    {
                        Name = i.Name,
                        Version = i.DiscoveryType == DiscoveryType.DotNetSdk
                            ? SdkFolderVersion(i.MSBuildPath) ?? i.Version.ToString()
                            : i.Version.ToString(),
                        MsBuildVersion = ReadMsBuildVersion(i.MSBuildPath),
                        MsBuildPath = i.MSBuildPath,
                        DiscoveryType = i.DiscoveryType.ToString(),
                        Selected = ReferenceEquals(i, preferred)
                    },
                    i))
                .ToList();
            var reason = preferred.DiscoveryType == DiscoveryType.DotNetSdk
                ? $"MSBuildLocator found {instances.Length} instance(s); a .NET SDK is preferred over Visual Studio, then the highest version."
                : $"MSBuildLocator found {instances.Length} instance(s) but no .NET SDK; the highest Visual Studio version is used.";
            return new MsBuildSelection(candidates, candidates.Single(c => c.Info.Selected), reason, roots);
        }

        var runtimeMajor = Environment.Version.Major;
        var sdks = DotNetSdkLocator.FindAll(roots);
        var newest = DotNetSdkLocator.SelectNewest(sdks, runtimeMajor);
        var probed = sdks
            .Select(sdk => new MsBuildCandidate(
                new MsBuildInstanceInfo
                {
                    Name = ".NET SDK",
                    Version = sdk.Version,
                    MsBuildVersion = ReadMsBuildVersion(sdk.Path),
                    MsBuildPath = sdk.Path,
                    DiscoveryType = "SdkDirectoryProbe",
                    Selected = ReferenceEquals(sdk, newest),
                    Note = DotNetSdkLocator.CanHost(sdk, runtimeMajor)
                        ? null
                        : $"needs a newer runtime than {Environment.Version}, so its MSBuild cannot be loaded"
                },
                null))
            .ToList();
        var probeReason = newest != null
            ? $"MSBuildLocator found no instance; the newest SDK under the probed dotnet roots that the {Environment.Version} runtime can host is used."
            : $"MSBuildLocator found no instance, and no SDK the {Environment.Version} runtime can host was found under: {string.Join(", ", roots)}.";
        return new MsBuildSelection(probed, probed.SingleOrDefault(c => c.Info.Selected), probeReason, roots);
    }

    private static VisualStudioInstance SelectPreferredInstance(VisualStudioInstance[] instances)
    {
        // Prefer .NET SDK over Visual Studio installations (more predictable)
        return instances
            .OrderByDescending(i => i.DiscoveryType == DiscoveryType.DotNetSdk)
            .ThenByDescending(i => i.Version)
            .First();
    }

    /// <summary>
    /// The SDK version an MSBuild directory belongs to, when it is <c>&lt;root&gt;/sdk/&lt;version&gt;</c>.
    /// </summary>
    private static string? SdkFolderVersion(string msBuildPath)
    {
        var dir = Path.TrimEndingDirectorySeparator(msBuildPath);
        return string.Equals(Path.GetFileName(Path.GetDirectoryName(dir)), "sdk", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(dir)
            : null;
    }

    /// <summary>
    /// The product version of <c>Microsoft.Build.dll</c> in <paramref name="msBuildPath"/>
    /// (e.g. <c>18.12.2</c>, without the commit hash), or null.
    /// </summary>
    private static string? ReadMsBuildVersion(string msBuildPath)
    {
        try
        {
            var dll = Path.Combine(msBuildPath, "Microsoft.Build.dll");
            if (!File.Exists(dll))
                return null;

            var info = FileVersionInfo.GetVersionInfo(dll);
            var version = string.IsNullOrEmpty(info.ProductVersion) ? info.FileVersion : info.ProductVersion;
            return version?.Split('+')[0];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Directory of the Microsoft.Build assembly loaded in this process, for an MSBuild that
    /// someone else registered.
    /// </summary>
    private static string? LoadedMsBuildDirectory() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Build" && !string.IsNullOrEmpty(a.Location)) is { } assembly
            ? Path.GetDirectoryName(assembly.Location)
            : null;
}
