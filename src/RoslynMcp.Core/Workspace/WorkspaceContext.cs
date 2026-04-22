using Microsoft.CodeAnalysis;
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
    private readonly SemaphoreSlim _commitLock = new(1, 1);
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

    internal WorkspaceContext(
        MSBuildWorkspace workspace,
        Solution solution,
        string loadedPath,
        IFileWriter? fileWriter = null)
    {
        _workspace = workspace;
        _solution = solution;
        _fileWriter = fileWriter ?? new AtomicFileWriter();
        LoadedPath = loadedPath;
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
    /// Applies an external text change to the in-memory solution snapshot.
    /// Called by the cache in response to filesystem change events.
    /// </summary>
    /// <remarks>
    /// Serialized with commits via the internal commit lock so that
    /// <see cref="CommitChangesAsync"/> never computes a diff against a
    /// concurrently mutated baseline. If no document in the solution matches
    /// <paramref name="filePath"/>, the call is a no-op.
    /// </remarks>
    internal async Task ApplyExternalTextChangeAsync(
        string filePath,
        string newText,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _commitLock.WaitAsync(cancellationToken);
        try
        {
            if (_disposed) return;
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
        }
        finally
        {
            _commitLock.Release();
        }
    }

    /// <summary>
    /// Returns the distinct absolute directories of every project in the solution,
    /// plus the solution file's directory. Used to scope filesystem watchers.
    /// </summary>
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
        }
        return dirs;
    }

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
            // the last-access timestamp and ref count.
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
