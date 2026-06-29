using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Resolution;

namespace RoslynMcp.Core.Workspace;

/// <summary>
/// Scoped workspace session. Encapsulates MSBuildWorkspace lifecycle.
/// </summary>
/// <remarks>
/// When created directly (non-cached), callers must <see cref="Dispose"/> when done.
/// When obtained from <see cref="WorkspaceCache"/>, <see cref="Dispose"/> releases a
/// lease (ref count) and the cache owns the actual workspace lifetime. Concurrent
/// read access to <see cref="Solution"/> is safe; mutations (commits, external text
/// updates) are serialized via an internal lock.
/// </remarks>
public sealed class WorkspaceContext : IDisposable
{
    private readonly Microsoft.CodeAnalysis.Workspace _workspace;
    private readonly IFileWriter _fileWriter;
    private readonly IDisposable? _analyzerAssemblyLoader;
    private readonly IReadOnlyList<AnalyzerFileStamp> _analyzerStamps;
    private readonly ImmutableDictionary<string, string> _msbuildProperties;
    private readonly FileBasedProgramInfo? _fileBasedProgram;
    private readonly SemaphoreSlim _commitLock = new(1, 1);
    private readonly WorkspaceOperationGate _gate = new();
    private Solution _solution;
    private bool _disposed;
    private Action? _onLeaseReleased;
    private bool _cacheOwned;

    /// <summary>
    /// Current solution snapshot.
    /// </summary>
    public Solution Solution => _solution;

    /// <summary>
    /// The underlying Roslyn workspace.
    /// </summary>
    public Microsoft.CodeAnalysis.Workspace Workspace => _workspace;

    /// <summary>
    /// Path to the loaded solution or project.
    /// </summary>
    public string LoadedPath { get; }

    /// <summary>
    /// Current workspace state.
    /// </summary>
    public WorkspaceState State { get; private set; }

    /// <summary>
    /// Issues encountered while loading the workspace that affect downstream
    /// analysis but are not themselves fatal — most notably source generators
    /// (e.g. the Razor source generator) that failed to load and were stripped
    /// from analyzer references. Tools may surface these as synthetic info-level
    /// diagnostics so callers can tell apart "no diagnostics" from "the
    /// generator that would have produced them never ran".
    /// </summary>
    public IReadOnlyList<string> GeneratorLoadIssues { get; }

    internal WorkspaceContext(
        Microsoft.CodeAnalysis.Workspace workspace,
        Solution solution,
        string loadedPath,
        IFileWriter? fileWriter = null,
        IReadOnlyList<string>? generatorLoadIssues = null,
        IDisposable? analyzerAssemblyLoader = null,
        IReadOnlyDictionary<string, string>? msbuildProperties = null,
        FileBasedProgramInfo? fileBasedProgram = null)
    {
        _workspace = workspace;
        _solution = solution;
        _fileWriter = fileWriter ?? new AtomicFileWriter();
        _analyzerAssemblyLoader = analyzerAssemblyLoader;
        _analyzerStamps = CaptureMutableAnalyzerStamps(solution);
        _msbuildProperties = msbuildProperties?.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase)
            ?? ImmutableDictionary<string, string>.Empty;
        _fileBasedProgram = fileBasedProgram;
        LoadedPath = loadedPath;
        GeneratorLoadIssues = generatorLoadIssues ?? Array.Empty<string>();
        State = WorkspaceState.Ready;
    }

    /// <summary>
    /// Creates a type symbol resolver for this workspace.
    /// </summary>
    public TypeSymbolResolver CreateSymbolResolver() => new(this);

    /// <summary>
    /// Creates a general-purpose symbol resolver that can find any symbol by position or name.
    /// </summary>
    public SymbolResolver CreateGeneralSymbolResolver() => new(this);

    /// <summary>
    /// Creates a reference tracker for this workspace.
    /// </summary>
    public ReferenceTracker CreateReferenceTracker() => new(this);

    /// <summary>
    /// Gets a document by its file path.
    /// </summary>
    /// <param name="filePath">Absolute path to the file.</param>
    /// <returns>Document if found, null otherwise.</returns>
    public Document? GetDocumentByPath(string filePath)
    {
        var normalizedPath = PathResolver.NormalizePath(filePath);
        return _solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => PathResolver.NormalizePath(d.FilePath ?? "") == normalizedPath);
    }

    /// <summary>
    /// Updates the solution with a new snapshot.
    /// </summary>
    /// <param name="newSolution">New solution snapshot.</param>
    public void UpdateSolution(Solution newSolution)
    {
        _solution = newSolution;
    }

    /// <summary>
    /// For a standalone "file-based program" workspace, decides whether an external edit to
    /// its entry <c>.cs</c> file must trigger a full reload instead of an in-place text
    /// update. A change to the file's <c>#:</c> directives can alter the resolved project
    /// (references, SDK, properties), so the materialized project has to be regenerated; a
    /// code-only edit can still be applied incrementally. Always false for ordinary
    /// project/solution-backed workspaces.
    /// </summary>
    internal bool ShouldReloadOnSourceChange(string filePath, string newText)
    {
        if (_fileBasedProgram is null) return false;
        if (!string.Equals(
                PathResolver.NormalizePath(filePath),
                _fileBasedProgram.EntryPath,
                StringComparison.OrdinalIgnoreCase))
            return false;

        var newSignature = FileBasedProgramProject.ComputeDirectiveSignature(newText);
        return !string.Equals(newSignature, _fileBasedProgram.DirectiveSignature, StringComparison.Ordinal);
    }

    /// <summary>
    /// Marks this context as owned by a cache. After this call, <see cref="Dispose"/>
    /// only signals the lease-release callback; actual disposal is performed by the
    /// cache via <see cref="DisposeOwned"/>.
    /// </summary>
    internal void MarkCacheOwned(Action onLeaseReleased)
    {
        _cacheOwned = true;
        _onLeaseReleased = onLeaseReleased;
    }

    /// <summary>
    /// Takes a shared operation lease. All MCP tool invocations run while
    /// holding a lease, so filesystem-driven updates can't mutate the solution
    /// under them. Multiple leases may be held simultaneously; the cache is
    /// expected to call <see cref="ExitOperation"/> when it releases the lease.
    /// </summary>
    internal Task EnterOperationAsync(CancellationToken cancellationToken)
        => _gate.EnterOperationAsync(cancellationToken);

    /// <summary>
    /// Releases an operation lease previously obtained via
    /// <see cref="EnterOperationAsync"/>.
    /// </summary>
    internal void ExitOperation() => _gate.ExitOperation();

    /// <summary>
    /// Applies an external text change to the in-memory solution snapshot.
    /// Called by the cache in response to filesystem change events.
    /// </summary>
    /// <returns>
    /// True if at least one document in the solution matched <paramref name="filePath"/>
    /// and was updated; false if the path is not (yet) part of the workspace — which the
    /// caller uses to tell "edit to a known file" (apply incrementally) apart from "a
    /// brand-new file appeared" (needs a reload so MSBuild can place it).
    /// </returns>
    /// <remarks>
    /// Waits for the exclusive file-update lease so the mutation only runs
    /// when no operation is active; once queued, the gate prioritises it over
    /// newly arriving operations.
    /// </remarks>
    internal async Task<bool> ApplyExternalTextChangeAsync(
        string filePath,
        string newText,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _gate.EnterFileUpdateAsync(cancellationToken);
        try
        {
            if (_disposed) return false;
            var normalized = PathResolver.NormalizePath(filePath);
            var sol = _solution;
            var sourceText = SourceText.From(newText);
            var updated = false;
            foreach (var project in sol.Projects)
            {
                foreach (var doc in project.Documents)
                {
                    if (doc.FilePath == null) continue;
                    if (!string.Equals(
                            PathResolver.NormalizePath(doc.FilePath),
                            normalized,
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    sol = sol.WithDocumentText(doc.Id, sourceText, PreservationMode.PreserveIdentity);
                    updated = true;
                }
            }
            if (updated)
                _solution = sol;
            return updated;
        }
        finally
        {
            _gate.ExitFileUpdate();
        }
    }

    /// <summary>
    /// Re-evaluates a single project with MSBuild and reconciles its source-document set
    /// into the in-memory solution as an incremental delta — adding files that newly
    /// match the project's globs and removing files that no longer do — without a full
    /// workspace reload.
    /// </summary>
    /// <returns>True if the solution snapshot changed.</returns>
    /// <remarks>
    /// This mirrors how the Roslyn language-server host (<c>LoadedProject</c>) reacts to a
    /// source-file create/delete: re-run the project's MSBuild evaluation, diff the
    /// resulting document list against the live snapshot, and apply only the differences
    /// via the workspace's add/remove-document primitives. We let MSBuild — not a path
    /// heuristic — decide membership (globs, <c>&lt;Compile Remove&gt;</c>, links,
    /// multi-targeting), so the result matches a full reload while costing a single
    /// project evaluation. The evaluation runs outside the file-update gate (it is
    /// comparatively slow); only the snapshot edit is gated. Throws if the evaluation
    /// fails, so the caller can fall back to a full reload.
    /// </remarks>
    internal async Task<bool> ReconcileProjectDocumentsAsync(
        string projectFilePath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // Only an MSBuild-backed workspace can be re-evaluated. The framework-only fallback for
        // a standalone .cs file lives in an AdhocWorkspace with no project on disk, so there is
        // nothing to reconcile — its single document is kept current via ApplyExternalTextChangeAsync.
        if (_workspace is not MSBuildWorkspace)
            return false;

        // A standalone file-based program's wrapper .csproj is materialized only transiently and
        // deleted right after load; it has a fixed document set and no longer exists on disk, so
        // it can't (and needn't) be re-evaluated. Referenced real projects still reconcile normally.
        if (!File.Exists(projectFilePath))
            return false;

        var normalizedProjectPath = PathResolver.NormalizePath(projectFilePath);

        // Evaluate outside the gate — an MSBuild evaluation must not block operations.
        // No ProjectMap: it would mark this project "already known" and short-circuit the
        // load. LoadMetadataForReferencedProjects stays false (the default), so referenced
        // projects surface only as metadata and the result is just this project's info(s).
        var loader = new MSBuildProjectLoader(_workspace, _msbuildProperties);
        var evaluated = await loader.LoadProjectInfoAsync(
            projectFilePath, cancellationToken: cancellationToken);

        // A load surfaces referenced projects too; keep only the project we re-evaluated.
        var freshInfos = evaluated
            .Where(i => i.FilePath != null && string.Equals(
                PathResolver.NormalizePath(i.FilePath), normalizedProjectPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (freshInfos.Count == 0)
            return false;

        await _gate.EnterFileUpdateAsync(cancellationToken);
        try
        {
            if (_disposed) return false;
            var sol = _solution;

            // Existing in-memory projects for this csproj. A multi-targeted project shares
            // its file path across one entry per target framework, matched 1:1 below.
            var candidates = sol.Projects
                .Where(p => p.FilePath != null && string.Equals(
                    PathResolver.NormalizePath(p.FilePath), normalizedProjectPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var changed = false;
            foreach (var fresh in freshInfos)
            {
                var existing = MatchExistingProject(candidates, fresh);
                if (existing == null)
                {
                    // Ambiguous (e.g. a target framework was added/removed): bail without
                    // mutating the snapshot so the caller falls back to a safe full reload.
                    throw new InvalidOperationException(
                        $"Re-evaluated project '{fresh.Name}' did not map to exactly one loaded project.");
                }

                var freshDocs = new Dictionary<string, DocumentInfo>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in fresh.Documents)
                    if (d.FilePath != null) freshDocs[PathResolver.NormalizePath(d.FilePath)] = d;

                var existingDocs = new Dictionary<string, DocumentId>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in existing.Documents)
                    if (d.FilePath != null) existingDocs[PathResolver.NormalizePath(d.FilePath)] = d.Id;

                // Remove documents that are no longer part of the evaluated project.
                var toRemove = existingDocs
                    .Where(kv => !freshDocs.ContainsKey(kv.Key))
                    .Select(kv => kv.Value)
                    .ToImmutableArray();
                if (toRemove.Length > 0)
                {
                    sol = sol.RemoveDocuments(toRemove);
                    changed = true;
                }

                // Add documents that newly match the project (preserving MSBuild-evaluated
                // name, folders, and source-code kind).
                foreach (var (path, info) in freshDocs)
                {
                    if (existingDocs.ContainsKey(path)) continue;
                    var text = await TryReadSourceTextAsync(info.FilePath!, cancellationToken);
                    if (text == null) continue; // vanished again mid-reconcile; a later event settles it
                    sol = sol.AddDocument(DocumentInfo.Create(
                        DocumentId.CreateNewId(existing.Id),
                        info.Name,
                        folders: info.Folders,
                        sourceCodeKind: info.SourceCodeKind,
                        loader: TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create(), info.FilePath)),
                        filePath: info.FilePath));
                    changed = true;
                }
            }

            if (changed)
                _solution = sol;
            return changed;
        }
        finally
        {
            _gate.ExitFileUpdate();
        }
    }

    /// <summary>
    /// Returns the project files that might own <paramref name="filePath"/>: every project
    /// already tracking a document at that path (so a delete or rename is reconciled), plus
    /// the most specific (deepest-rooted) project whose directory contains it (so a newly
    /// created file is considered). The caller re-evaluates these and lets MSBuild decide
    /// actual membership.
    /// </summary>
    internal IReadOnlyCollection<string> FindProjectFilesForPath(string filePath)
    {
        var normalized = PathResolver.NormalizePath(filePath);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? deepestProjectFile = null;
        var deepestDirLen = -1;

        foreach (var project in _solution.Projects)
        {
            if (project.FilePath == null) continue;
            var projFile = PathResolver.NormalizePath(project.FilePath);

            foreach (var doc in project.Documents)
            {
                if (doc.FilePath != null && string.Equals(
                        PathResolver.NormalizePath(doc.FilePath), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(projFile);
                    break;
                }
            }

            var projDir = Path.GetDirectoryName(projFile);
            if (!string.IsNullOrEmpty(projDir) && IsSameOrDescendant(normalized, projDir) &&
                projDir.Length > deepestDirLen)
            {
                deepestDirLen = projDir.Length;
                deepestProjectFile = projFile;
            }
        }

        if (deepestProjectFile != null)
            result.Add(deepestProjectFile);
        return result;
    }

    private static Project? MatchExistingProject(List<Project> candidates, ProjectInfo fresh)
    {
        // Single-targeted project: the one candidate is the match. Multi-targeted: match
        // on the TFM-qualified Name (e.g. "Proj(net8.0)"); anything else is treated as
        // ambiguous by the caller and triggers a safe reload rather than a risky guess.
        if (candidates.Count == 1) return candidates[0];
        return candidates.FirstOrDefault(p => string.Equals(p.Name, fresh.Name, StringComparison.Ordinal));
    }

    private static async Task<SourceText?> TryReadSourceTextAsync(string filePath, CancellationToken ct)
    {
        const int maxAttempts = 4;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                return SourceText.From(fs);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (IOException) when (attempt < maxAttempts) { await Task.Delay(50 * attempt, ct); }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts) { await Task.Delay(50 * attempt, ct); }
        }
        return null;
    }

    /// <summary>
    /// Returns a minimal set of absolute directories that recursively cover every
    /// document in the solution, plus each project file's directory and the
    /// solution file's directory. Used to scope filesystem watchers.
    /// </summary>
    /// <remarks>
    /// Collecting per-document folders and then collapsing descendants (e.g.
    /// dropping <c>/a/b/c</c> when <c>/a/b</c> is already present) keeps the
    /// watcher count bounded by the shape of the source tree rather than the
    /// number of projects.
    /// </remarks>
    internal IReadOnlyCollection<string> GetWatchDirectories()
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var solutionDir = Path.GetDirectoryName(LoadedPath);
        if (!string.IsNullOrEmpty(solutionDir))
            dirs.Add(PathResolver.NormalizePath(solutionDir));

        foreach (var project in _solution.Projects)
        {
            var projectDir = Path.GetDirectoryName(project.FilePath ?? "");
            if (!string.IsNullOrEmpty(projectDir))
                dirs.Add(PathResolver.NormalizePath(projectDir));

            foreach (var document in project.Documents)
            {
                var docDir = Path.GetDirectoryName(document.FilePath ?? "");
                if (!string.IsNullOrEmpty(docDir))
                    dirs.Add(PathResolver.NormalizePath(docDir));
            }
        }

        return ReduceToRecursiveRoots(dirs);
    }

    /// <summary>
    /// Given a set of absolute directories, returns the minimal subset such that
    /// each input directory is equal to or a descendant of some element in the
    /// result. Because watchers run with <c>IncludeSubdirectories = true</c>, any
    /// descendant is already covered by its ancestor and is redundant.
    /// </summary>
    private static IReadOnlyCollection<string> ReduceToRecursiveRoots(IEnumerable<string> paths)
    {
        var sorted = paths
            .Where(p => !string.IsNullOrEmpty(p))
            .OrderBy(p => p.Length)
            .ToList();

        var roots = new List<string>(sorted.Count);
        foreach (var path in sorted)
        {
            var covered = false;
            foreach (var root in roots)
            {
                if (IsSameOrDescendant(path, root))
                {
                    covered = true;
                    break;
                }
            }
            if (!covered)
                roots.Add(path);
        }
        return roots;
    }

    private static bool IsSameOrDescendant(string candidate, string ancestor)
    {
        if (string.Equals(candidate, ancestor, StringComparison.OrdinalIgnoreCase))
            return true;

        var sep = Path.DirectorySeparatorChar;
        var prefix = ancestor.Length > 0 && ancestor[^1] == sep ? ancestor : ancestor + sep;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// On-disk identity (last-write time + length) of a referenced analyzer assembly,
    /// captured at load time so a later change can be detected.
    /// </summary>
    internal readonly record struct AnalyzerFileStamp(string Path, DateTime LastWriteUtc, long Length);

    /// <summary>
    /// Records the on-disk identity of every project-local analyzer/source-generator
    /// assembly the solution references. Analyzers shipped via NuGet or the .NET SDK are
    /// skipped: they are immutable once restored/installed, so checking them would only
    /// add cost and risk spurious reloads. The build-output generators — the ones a
    /// <c>dotnet build</c> rewrites, and the reason for shadow copying — are exactly the
    /// mutable ones we keep.
    /// </summary>
    internal static IReadOnlyList<AnalyzerFileStamp> CaptureMutableAnalyzerStamps(Solution solution)
    {
        var stamps = new List<AnalyzerFileStamp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in solution.Projects)
        {
            foreach (var reference in project.AnalyzerReferences)
            {
                if (reference is not AnalyzerFileReference fileRef) continue;
                var path = fileRef.FullPath;
                if (string.IsNullOrEmpty(path) || !seen.Add(path)) continue;
                if (!IsMutableAnalyzerPath(path)) continue;

                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists)
                        stamps.Add(new AnalyzerFileStamp(path, info.LastWriteTimeUtc, info.Length));
                }
                catch { /* unreadable: we simply won't track changes to this one */ }
            }
        }

        return stamps;
    }

    /// <summary>
    /// True if any project-local analyzer/generator assembly recorded at load time has
    /// since changed, been replaced, or been removed on disk — meaning this cached
    /// workspace (and its shadow copies) is stale and should be reloaded.
    /// </summary>
    internal bool AnalyzerReferencesChangedOnDisk() => StampsChanged(_analyzerStamps);

    internal static bool StampsChanged(IReadOnlyList<AnalyzerFileStamp> stamps)
    {
        foreach (var stamp in stamps)
        {
            try
            {
                var info = new FileInfo(stamp.Path);
                if (!info.Exists) return true;
                if (info.LastWriteTimeUtc != stamp.LastWriteUtc || info.Length != stamp.Length)
                    return true;
            }
            catch
            {
                return true; // can no longer stat it -> treat as changed
            }
        }
        return false;
    }

    /// <summary>
    /// Whether an analyzer path is a mutable build output (worth tracking) rather than
    /// an immutable NuGet- or SDK-provided assembly.
    /// </summary>
    internal static bool IsMutableAnalyzerPath(string path)
    {
        var normalized = PathResolver.NormalizePath(path);
        foreach (var root in ImmutableAnalyzerRoots.Value)
            if (IsSameOrDescendant(normalized, root))
                return false;
        return true;
    }

    private static readonly Lazy<string[]> ImmutableAnalyzerRoots = new(() =>
    {
        var roots = new List<string>();

        var nuget = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrEmpty(nuget))
            nuget = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        roots.Add(nuget);

        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot)) roots.Add(dotnetRoot);

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles)) roots.Add(Path.Combine(programFiles, "dotnet"));

        return roots
            .Where(r => !string.IsNullOrEmpty(r))
            .Select(PathResolver.NormalizePath)
            .ToArray();
    });

    /// <summary>
    /// Commits all pending changes to the filesystem.
    /// </summary>
    /// <param name="newSolution">Solution with changes to commit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of files that were written.</returns>
    /// <remarks>
    /// This method uses a semaphore to prevent race conditions when multiple
    /// commit operations are attempted concurrently on the same workspace context.
    /// Files are written sequentially to avoid file locking issues.
    /// </remarks>
    public async Task<CommitResult> CommitChangesAsync(
        Solution newSolution,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // Acquire lock to prevent concurrent commits
        await _commitLock.WaitAsync(cancellationToken);

        var filesModified = new List<string>();
        var filesCreated = new List<string>();
        var filesDeleted = new List<string>();

        try
        {
            State = WorkspaceState.Operating;

            var changes = newSolution.GetChanges(_solution);

            // Collect all file operations first, then execute sequentially
            // This prevents interleaved writes to the same file from different documents
            var fileOperations = new List<(string FilePath, Func<Task> Operation, string Category)>();

            foreach (var projectChanges in changes.GetProjectChanges())
            {
                // Handle added documents
                foreach (var docId in projectChanges.GetAddedDocuments())
                {
                    var doc = newSolution.GetDocument(docId);
                    if (doc?.FilePath == null) continue;

                    var filePath = doc.FilePath;
                    fileOperations.Add((filePath, async () =>
                    {
                        var text = await doc.GetTextAsync(cancellationToken);
                        await _fileWriter.WriteAsync(filePath, text.ToString(), cancellationToken);
                    }, "created"));
                    filesCreated.Add(filePath);
                }

                // Handle changed documents
                foreach (var docId in projectChanges.GetChangedDocuments())
                {
                    var doc = newSolution.GetDocument(docId);
                    if (doc?.FilePath == null) continue;

                    var filePath = doc.FilePath;
                    fileOperations.Add((filePath, async () =>
                    {
                        var text = await doc.GetTextAsync(cancellationToken);
                        await _fileWriter.WriteAsync(filePath, text.ToString(), cancellationToken);
                    }, "modified"));
                    filesModified.Add(filePath);
                }

                // Handle removed documents
                foreach (var docId in projectChanges.GetRemovedDocuments())
                {
                    var doc = _solution.GetDocument(docId);
                    if (doc?.FilePath == null) continue;

                    var filePath = doc.FilePath;
                    fileOperations.Add((filePath, () =>
                    {
                        _fileWriter.Delete(filePath);
                        return Task.CompletedTask;
                    }, "deleted"));
                    filesDeleted.Add(filePath);
                }
            }

            // Sort operations by file path to ensure consistent ordering
            // and prevent potential deadlocks with external file locks
            fileOperations.Sort((a, b) => string.Compare(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase));

            // Execute file operations sequentially to prevent race conditions
            foreach (var (_, operation, _) in fileOperations)
            {
                await operation();
            }

            _solution = newSolution;
            State = WorkspaceState.Ready;

            return new CommitResult
            {
                Success = true,
                FilesModified = filesModified,
                FilesCreated = filesCreated,
                FilesDeleted = filesDeleted
            };
        }
        catch (Exception ex)
        {
            State = WorkspaceState.Error;
            return new CommitResult
            {
                Success = false,
                FilesModified = filesModified,
                FilesCreated = filesCreated,
                FilesDeleted = filesDeleted,
                Error = ex.Message
            };
        }
        finally
        {
            _commitLock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_cacheOwned)
        {
            // Lease release: cache owns lifetime; signal it so it can update
            // the last-access timestamp and ref count. The operation lease is
            // exited first so any queued file update can start immediately.
            _gate.ExitOperation();
            _onLeaseReleased?.Invoke();
            return;
        }

        DisposeOwned();
    }

    /// <summary>
    /// Performs the actual workspace disposal. For cache-owned contexts, this is
    /// called by the cache on eviction; otherwise it's invoked directly from
    /// <see cref="Dispose"/>.
    /// </summary>
    internal void DisposeOwned()
    {
        if (_disposed) return;
        _disposed = true;
        State = WorkspaceState.Disposed;
        _commitLock.Dispose();
        _workspace.Dispose();

        // Dispose after the workspace so it has already released its references to
        // the shadow-copied analyzer assemblies; this unloads the dedicated load
        // context and prunes the temp copies.
        _analyzerAssemblyLoader?.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

/// <summary>
/// Identity of a standalone "file-based program" workspace: the (normalized) entry
/// <c>.cs</c> file and a fingerprint of its <c>#:</c> directives captured at load time,
/// used to decide when an edit requires regenerating the materialized project.
/// </summary>
internal sealed record FileBasedProgramInfo(string EntryPath, string DirectiveSignature);

/// <summary>
/// Result of committing changes to the filesystem.
/// </summary>
public sealed class CommitResult
{
    /// <summary>
    /// Whether the commit succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Files that were modified.
    /// </summary>
    public required IReadOnlyList<string> FilesModified { get; init; }

    /// <summary>
    /// Files that were created.
    /// </summary>
    public required IReadOnlyList<string> FilesCreated { get; init; }

    /// <summary>
    /// Files that were deleted.
    /// </summary>
    public required IReadOnlyList<string> FilesDeleted { get; init; }

    /// <summary>
    /// Error message if commit failed.
    /// </summary>
    public string? Error { get; init; }
}
