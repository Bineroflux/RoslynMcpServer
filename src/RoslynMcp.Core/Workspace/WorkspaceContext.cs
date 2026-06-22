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
    private readonly MSBuildWorkspace _workspace;
    private readonly IFileWriter _fileWriter;
    private readonly IDisposable? _analyzerAssemblyLoader;
    private readonly IReadOnlyList<AnalyzerFileStamp> _analyzerStamps;
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
        MSBuildWorkspace workspace,
        Solution solution,
        string loadedPath,
        IFileWriter? fileWriter = null,
        IReadOnlyList<string>? generatorLoadIssues = null,
        IDisposable? analyzerAssemblyLoader = null)
    {
        _workspace = workspace;
        _solution = solution;
        _fileWriter = fileWriter ?? new AtomicFileWriter();
        _analyzerAssemblyLoader = analyzerAssemblyLoader;
        _analyzerStamps = CaptureMutableAnalyzerStamps(solution);
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
    /// Removes every document matching <paramref name="filePath"/> from the in-memory
    /// solution snapshot, so a deleted <c>.cs</c> file is reflected without a full
    /// MSBuild reload. Called by the cache when a tracked source file disappears from
    /// disk for good.
    /// </summary>
    /// <returns>True if at least one document was removed; false if none matched.</returns>
    /// <remarks>
    /// Removing a document is a pure in-memory <see cref="Solution"/> edit — it does not
    /// touch the <c>.csproj</c>. For SDK-style projects the file stays globbed-in, so a
    /// later reload (e.g. after a build) re-materializes it; until then queries simply
    /// no longer see the deleted file. Runs under the same exclusive file-update lease as
    /// <see cref="ApplyExternalTextChangeAsync"/>.
    /// </remarks>
    internal async Task<bool> ApplyExternalDocumentRemovalAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _gate.EnterFileUpdateAsync(cancellationToken);
        try
        {
            if (_disposed) return false;
            var normalized = PathResolver.NormalizePath(filePath);
            var sol = _solution;
            var toRemove = new List<DocumentId>();
            foreach (var project in sol.Projects)
            {
                foreach (var doc in project.Documents)
                {
                    if (doc.FilePath == null) continue;
                    if (string.Equals(
                            PathResolver.NormalizePath(doc.FilePath),
                            normalized,
                            StringComparison.OrdinalIgnoreCase))
                        toRemove.Add(doc.Id);
                }
            }
            if (toRemove.Count == 0)
                return false;

            sol = sol.RemoveDocuments(toRemove.ToImmutableArray());
            _solution = sol;
            return true;
        }
        finally
        {
            _gate.ExitFileUpdate();
        }
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
