using System.Collections.Concurrent;
using System.Diagnostics;
using RoslynMcp.Core.FileSystem;

namespace RoslynMcp.Core.Workspace;

/// <summary>
/// Caches <see cref="WorkspaceContext"/> instances keyed by normalized solution path.
/// Entries are kept warm across MCP calls, incrementally updated from filesystem
/// events, and evicted after being unused for longer than the configured TTL.
/// </summary>
/// <remarks>
/// Concurrency model:
/// - Acquire is lock-free (CAS on ref count) and tombstone-aware.
/// - Cache-miss loads are de-duplicated per path via a <see cref="SemaphoreSlim"/>,
///   so ten parallel calls against a cold workspace pay the MSBuild cost once.
/// - Text updates are applied under <see cref="WorkspaceContext"/>'s commit lock,
///   so they never race with an in-flight refactoring commit.
/// - Project-file changes (<c>.csproj</c>, <c>.sln</c>, etc.) invalidate the entry
///   rather than attempting an incremental reload.
/// </remarks>
public sealed class WorkspaceCache : IDisposable
{
    /// <summary>Default time-to-live for unreferenced entries.</summary>
    public static readonly TimeSpan DefaultIdleTtl = TimeSpan.FromMinutes(20);

    /// <summary>How often the sweeper checks for idle entries.</summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromMinutes(1);

    private static readonly string[] ProjectFileExtensions =
        { ".csproj", ".sln", ".slnx", ".props", ".targets" };

    private readonly ConcurrentDictionary<string, CachedEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _loadGuards = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _idleTtl;
    private readonly Timer _sweepTimer;
    private bool _disposed;

    /// <summary>Optional diagnostics callback.</summary>
    public Action<string>? LogCallback { get; set; }

    /// <summary>Optional error-diagnostics callback.</summary>
    public Action<string, Exception?>? LogErrorCallback { get; set; }

    /// <summary>
    /// Creates a new workspace cache.
    /// </summary>
    /// <param name="idleTtl">Idle time-to-live before an entry is eligible for eviction.</param>
    /// <param name="sweepInterval">How often the background sweeper runs.</param>
    public WorkspaceCache(TimeSpan? idleTtl = null, TimeSpan? sweepInterval = null)
    {
        _idleTtl = idleTtl ?? DefaultIdleTtl;
        var interval = sweepInterval ?? DefaultSweepInterval;
        _sweepTimer = new Timer(_ => Sweep(), null, interval, interval);
    }

    /// <summary>
    /// Gets an existing cached context or loads one by invoking <paramref name="loader"/>.
    /// </summary>
    /// <param name="solutionPath">Absolute path to the .sln/.slnx/.csproj.</param>
    /// <param name="loader">Loader invoked on cache miss; must return a fresh, unowned context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple of the leased context and the milliseconds spent acquiring it.</returns>
    /// <remarks>
    /// The returned context is cache-owned: calling <see cref="WorkspaceContext.Dispose"/>
    /// releases the lease rather than tearing down the workspace.
    /// </remarks>
    public async Task<(WorkspaceContext Context, long LoadMs)> GetOrCreateAsync(
        string solutionPath,
        Func<CancellationToken, Task<WorkspaceContext>> loader,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var key = PathResolver.NormalizePath(solutionPath);
        var stopwatch = Stopwatch.StartNew();

        // Fast path: cache hit.
        if (_entries.TryGetValue(key, out var existing) && existing.TryAcquire())
        {
            if (IsFresh(existing, key))
            {
                await EnterOperationOrRelease(existing, cancellationToken);
                stopwatch.Stop();
                return (existing.Context, stopwatch.ElapsedMilliseconds);
            }
            // Stale (a referenced generator/analyzer DLL changed on disk): drop the
            // lease and invalidate so the load below produces a fresh workspace and
            // fresh shadow copies. Fall through to the slow path.
            InvalidateStaleEntry(key, existing);
        }

        // Slow path: load under a per-key guard so parallel cache-miss callers
        // share a single MSBuild solution load.
        var guard = _loadGuards.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await guard.WaitAsync(cancellationToken);
        try
        {
            // Re-check after acquiring the guard; another caller may have populated the cache.
            if (_entries.TryGetValue(key, out existing) && existing.TryAcquire())
            {
                if (IsFresh(existing, key))
                {
                    await EnterOperationOrRelease(existing, cancellationToken);
                    stopwatch.Stop();
                    return (existing.Context, stopwatch.ElapsedMilliseconds);
                }
                InvalidateStaleEntry(key, existing);
            }

            LogCallback?.Invoke($"Cache miss for '{key}'; loading workspace...");
            var fresh = await loader(cancellationToken);
            var entry = new CachedEntry(this, key, fresh);
            fresh.MarkCacheOwned(entry.OnLeaseReleased);

            // Take the first lease for the caller before publishing, so a sweep
            // can never see RefCount == 0 between publish and return.
            if (!entry.TryAcquire())
                throw new InvalidOperationException("Failed to acquire freshly constructed cache entry.");

            _entries[key] = entry;
            entry.StartWatching();

            // The freshly loaded workspace has no outstanding leases, so the
            // operation gate is idle — this await completes synchronously.
            await EnterOperationOrRelease(entry, cancellationToken);

            stopwatch.Stop();
            LogCallback?.Invoke(
                $"Workspace loaded for '{key}' in {stopwatch.ElapsedMilliseconds} ms.");
            return (entry.Context, stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            guard.Release();
        }
    }

    /// <summary>
    /// Whether a leased cache entry is still usable. Returns false when a project-local
    /// analyzer/source-generator assembly the workspace references has changed on disk
    /// since it was loaded (e.g. the project was rebuilt), so the caller reloads instead
    /// of serving stale generator output.
    /// </summary>
    private bool IsFresh(CachedEntry entry, string key)
    {
        try
        {
            if (!entry.Context.AnalyzerReferencesChangedOnDisk())
                return true;
        }
        catch
        {
            return true; // can't tell -> keep the cached workspace rather than churn
        }

        LogCallback?.Invoke(
            $"Analyzer/generator assembly changed on disk for '{key}'; reloading workspace.");
        return false;
    }

    private static async Task EnterOperationOrRelease(CachedEntry entry, CancellationToken ct)
    {
        try
        {
            await entry.Context.EnterOperationAsync(ct);
        }
        catch
        {
            // Gate entry failed (typically cancellation); give the lease back
            // so the entry doesn't leak a reference count.
            entry.OnLeaseReleased();
            throw;
        }
    }

    /// <summary>
    /// Removes and disposes the entry for <paramref name="solutionPath"/> if present
    /// and no longer in use. In-use entries are tombstoned and torn down on release.
    /// </summary>
    public void Invalidate(string solutionPath)
    {
        var key = PathResolver.NormalizePath(solutionPath);
        if (_entries.TryRemove(key, out var entry))
        {
            entry.BeginTeardown();
        }
    }

    /// <summary>
    /// Drops our lease on a stale entry and removes it from the cache — but only
    /// if it is still the published entry for <paramref name="key"/>. Using an
    /// identity-checked removal (rather than the key-only <see cref="Invalidate"/>)
    /// stops a redundant staleness check on one thread from evicting a fresh entry
    /// another thread may have just loaded for the same key — which would otherwise
    /// force a second, wasteful reload and tear the new workspace down mid-use.
    /// </summary>
    private void InvalidateStaleEntry(string key, CachedEntry stale)
    {
        // Tombstone before releasing our lease so no one can acquire the stale
        // entry in the gap; teardown is deferred to our OnLeaseReleased below.
        if (_entries.TryRemove(new KeyValuePair<string, CachedEntry>(key, stale)))
            stale.BeginTeardown();
        stale.OnLeaseReleased();
    }

    private void Sweep()
    {
        if (_disposed) return;
        try
        {
            var cutoff = DateTime.UtcNow - _idleTtl;
            foreach (var kv in _entries)
            {
                if (kv.Value.TryEvictIfIdle(cutoff))
                {
                    _entries.TryRemove(new KeyValuePair<string, CachedEntry>(kv.Key, kv.Value));
                    LogCallback?.Invoke($"Evicted idle workspace cache entry for '{kv.Key}'.");
                }
            }
        }
        catch (Exception ex)
        {
            LogErrorCallback?.Invoke("WorkspaceCache sweep failed", ex);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _sweepTimer.Dispose(); } catch { /* already disposed */ }

        foreach (var kv in _entries)
            kv.Value.BeginTeardown();
        _entries.Clear();

        foreach (var kv in _loadGuards)
        {
            try { kv.Value.Dispose(); } catch { /* ignore */ }
        }
        _loadGuards.Clear();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>
    /// Entry in the workspace cache. Owns a <see cref="WorkspaceContext"/> and the
    /// <see cref="FileSystemWatcher"/>s that keep it in sync with disk state.
    /// </summary>
    private sealed class CachedEntry
    {
        // Lease state packed into a single long for atomic transitions:
        //   bits [31..0] : number of outstanding leases (never negative).
        //   bit  [32]    : tombstone flag, set once the entry is invalidated/evicted.
        // Packing keeps the lease count intact across tombstoning, so whichever
        // caller drives the count to zero *after* the flag is set performs the
        // deferred teardown. An in-use entry that gets invalidated is therefore
        // still torn down when its last lease releases — never leaked.
        private const long TombstoneFlag = 1L << 32;

        private static readonly TimeSpan ReevalDebounce = TimeSpan.FromMilliseconds(250);

        private readonly WorkspaceCache _cache;
        private readonly string _key;
        private readonly List<FileSystemWatcher> _watchers = new();
        // Per-project monotonic generation used to debounce structural re-evaluations: a
        // burst of add/remove events for one project coalesces into a single re-eval.
        private readonly ConcurrentDictionary<string, long> _reevalGeneration = new(StringComparer.OrdinalIgnoreCase);
        private long _state;
        private long _lastAccessTicks;
        private int _torndown;

        private static long LeaseCount(long state) => state & 0xFFFFFFFFL;
        private static bool IsTombstoned(long state) => (state & TombstoneFlag) != 0;

        public WorkspaceContext Context { get; }

        public CachedEntry(WorkspaceCache cache, string key, WorkspaceContext context)
        {
            _cache = cache;
            _key = key;
            Context = context;
            _lastAccessTicks = DateTime.UtcNow.Ticks;
        }

        /// <summary>
        /// Attempts to take a lease. Returns false if the entry is tombstoned.
        /// </summary>
        public bool TryAcquire()
        {
            while (true)
            {
                var cur = Volatile.Read(ref _state);
                if (IsTombstoned(cur)) return false;
                if (Interlocked.CompareExchange(ref _state, cur + 1, cur) == cur)
                {
                    Interlocked.Exchange(ref _lastAccessTicks, DateTime.UtcNow.Ticks);
                    return true;
                }
            }
        }

        /// <summary>
        /// Lease-release callback wired to the context via MarkCacheOwned.
        /// </summary>
        public void OnLeaseReleased()
        {
            Interlocked.Exchange(ref _lastAccessTicks, DateTime.UtcNow.Ticks);
            while (true)
            {
                var cur = Volatile.Read(ref _state);
                var next = cur - 1; // decrement the lease count, preserving the flag
                if (Interlocked.CompareExchange(ref _state, next, cur) == cur)
                {
                    // Released the final lease of an already-tombstoned entry:
                    // perform the teardown that BeginTeardown / eviction deferred.
                    if (next == TombstoneFlag)
                        Teardown();
                    return;
                }
            }
        }

        /// <summary>
        /// Evicts the entry if it has been idle longer than <paramref name="cutoff"/>.
        /// Returns true if the caller should remove it from the cache dictionary.
        /// </summary>
        public bool TryEvictIfIdle(DateTime cutoff)
        {
            // Cheap, lock-free idle check first. Reading last-access and ref-count
            // separately is safe: if they change between these reads, the CAS below
            // will fail and we'll simply decline this eviction attempt.
            var last = new DateTime(Volatile.Read(ref _lastAccessTicks), DateTimeKind.Utc);
            if (last > cutoff) return false;

            // Commit the eviction atomically. Only succeeds when the entry is fully
            // idle: no outstanding leases and not already tombstoned (state == 0).
            if (Interlocked.CompareExchange(ref _state, TombstoneFlag, 0) != 0)
                return false;

            Teardown();
            return true;
        }

        /// <summary>
        /// Initiates teardown (external invalidation). Safe to call multiple times.
        /// If any leases are outstanding, the teardown is deferred until they release
        /// — but we still unhook watchers immediately so no further updates occur.
        /// </summary>
        public void BeginTeardown()
        {
            DisposeWatchers();
            // Set the tombstone flag while preserving the lease count, so new
            // acquires miss but outstanding leases still finish. Dispose now only
            // if nothing is leased; otherwise the final OnLeaseReleased does it.
            while (true)
            {
                var cur = Volatile.Read(ref _state);
                if (IsTombstoned(cur)) return;
                if (Interlocked.CompareExchange(ref _state, cur | TombstoneFlag, cur) == cur)
                {
                    if (LeaseCount(cur) == 0) Teardown();
                    return;
                }
            }
        }

        private void Teardown()
        {
            if (Interlocked.Exchange(ref _torndown, 1) != 0) return;
            DisposeWatchers();
            try { Context.DisposeOwned(); }
            catch (Exception ex)
            {
                _cache.LogErrorCallback?.Invoke($"Error disposing cached workspace '{_key}'", ex);
            }
        }

        public void StartWatching()
        {
            var dirs = Context.GetWatchDirectories();
            foreach (var dir in dirs)
            {
                FileSystemWatcher? watcher = null;
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    watcher = new FileSystemWatcher(dir)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName |
                                       NotifyFilters.CreationTime | NotifyFilters.Size,
                        InternalBufferSize = 64 * 1024
                    };
                    watcher.Changed += OnFileSystemEvent;
                    watcher.Created += OnFileSystemEvent;
                    watcher.Deleted += OnFileSystemEvent;
                    watcher.Renamed += OnFileRenamed;
                    watcher.Error += OnWatcherError;
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                }
                catch (Exception ex)
                {
                    watcher?.Dispose();
                    _cache.LogErrorCallback?.Invoke(
                        $"Could not start file watcher for '{dir}'", ex);
                }
            }
        }

        private void DisposeWatchers()
        {
            foreach (var watcher in _watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch { /* ignore */ }
            }
            _watchers.Clear();
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            _cache.LogErrorCallback?.Invoke(
                $"FileSystemWatcher error for '{_key}'", e.GetException());
        }

        private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
        {
            if (IsTombstoned(Volatile.Read(ref _state))) return;
            _ = HandleFileSystemEventAsync(e.ChangeType, e.FullPath, oldFullPath: null);
        }

        private void OnFileRenamed(object sender, RenamedEventArgs e)
        {
            if (IsTombstoned(Volatile.Read(ref _state))) return;
            _ = HandleFileSystemEventAsync(e.ChangeType, e.FullPath, e.OldFullPath);
        }

        private async Task HandleFileSystemEventAsync(
            WatcherChangeTypes changeType, string fullPath, string? oldFullPath)
        {
            try
            {
                // Ignore the transient wrapper projects materialized for standalone .cs files
                // (named <entry>.cs.csproj). They are created and deleted during a workspace load;
                // treating them as ordinary project files would invalidate or churn the workspace.
                if (IsFileBasedProgramWrapper(fullPath) || IsFileBasedProgramWrapper(oldFullPath))
                    return;

                var ext = Path.GetExtension(fullPath);
                var oldExt = oldFullPath != null ? Path.GetExtension(oldFullPath) : null;

                if (IsProjectFile(ext) || IsProjectFile(oldExt))
                {
                    // Project-graph-affecting change: evict and let the next
                    // access re-load from scratch.
                    _cache.LogCallback?.Invoke(
                        $"Project file '{fullPath}' changed ({changeType}); invalidating cache entry '{_key}'.");
                    _cache.Invalidate(_key);
                    return;
                }

                var isCs = IsCSharpExt(ext) || IsCSharpExt(oldExt);
                var isRazor = IsRazorExt(ext) || IsRazorExt(oldExt);
                if (!isCs && !isRazor)
                {
                    return;
                }

                // Razor/CSHTML files are AdditionalDocuments consumed by the source
                // generator; we can't apply an incremental text update through the
                // Document-centric ApplyExternalTextChangeAsync path, so any edit
                // invalidates the entry. The next access reloads from scratch and
                // picks up the new Razor content.
                if (isRazor)
                {
                    _cache.LogCallback?.Invoke(
                        $"Razor/CSHTML file {changeType} '{fullPath}'; invalidating '{_key}'.");
                    _cache.Invalidate(_key);
                    return;
                }

                // C# source change. Reconcile against the in-memory solution instead of
                // blanket-invalidating. A content change to a file the workspace already
                // tracks — which is what BOTH our own atomic commit (delete + rename +
                // changed, per the FileSystemWatcher) AND a follow-up external edit look
                // like — is applied incrementally with no reload. Only a genuinely new
                // file forces a reload, so MSBuild can determine its project membership.
                if (IsCSharpExt(ext))
                {
                    var looksNew = changeType is WatcherChangeTypes.Created or WatcherChangeTypes.Renamed;
                    await ReconcileCsPathAsync(fullPath, looksNew);
                }

                // A rename also vacates the old path; reflect its removal if it was tracked.
                if (oldFullPath != null && IsCSharpExt(oldExt) &&
                    !string.Equals(oldFullPath, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    await ReconcileCsPathAsync(oldFullPath, looksNew: false);
                }
            }
            catch (Exception ex)
            {
                _cache.LogErrorCallback?.Invoke(
                    $"Error handling filesystem event for '{fullPath}'", ex);
            }
        }

        /// <summary>
        /// Brings the in-memory solution back in line with the current on-disk state of a
        /// single C# file, avoiding a full reload:
        /// <list type="bullet">
        ///   <item>present and tracked → incremental text update (covers our commits and
        ///     subsequent external edits alike);</item>
        ///   <item>present, untracked, and the event looks like a new file → re-evaluate the
        ///     owning project and add the document incrementally;</item>
        ///   <item>gone (confirmed past the brief window of an atomic save) and tracked →
        ///     re-evaluate the owning project so the document is removed incrementally.</item>
        /// </list>
        /// Structural changes (add/remove) defer to a debounced per-project MSBuild
        /// re-evaluation (<see cref="ScheduleOwningProjectReeval"/>) so membership is decided
        /// by MSBuild, exactly as the Roslyn language server does — never a path heuristic.
        /// </summary>
        private async Task ReconcileCsPathAsync(string filePath, bool looksNew)
        {
            if (IsTombstoned(Volatile.Read(ref _state))) return;

            try
            {
                var text = await TryReadAllTextAsync(filePath);
                if (text != null)
                {
                    // A standalone file-based program whose #: directives changed must be
                    // rebuilt from scratch — the resolved references/SDK/properties may now
                    // differ, which an in-place text update can't capture. A code-only edit
                    // falls through to the normal incremental update.
                    if (Context.ShouldReloadOnSourceChange(filePath, text))
                    {
                        _cache.LogCallback?.Invoke(
                            $"File-based program directives changed in '{filePath}'; invalidating '{_key}' to rebuild.");
                        _cache.Invalidate(_key);
                        return;
                    }

                    var applied = await Context.ApplyExternalTextChangeAsync(filePath, text);
                    if (!applied && looksNew && !IsUnderIntermediateOutput(filePath))
                        ScheduleOwningProjectReeval(filePath);
                    return;
                }

                // Not readable right now. An atomic save deletes then renames into place,
                // so confirm the file is really gone before treating it as a deletion.
                if (await StillMissingAsync(filePath))
                {
                    if (IsTombstoned(Volatile.Read(ref _state))) return;
                    // Only a file the workspace actually tracks warrants a re-evaluation;
                    // a stray deletion elsewhere under the project cone is irrelevant.
                    if (Context.GetDocumentByPath(filePath) != null)
                        ScheduleOwningProjectReeval(filePath);
                }
                else
                {
                    // Reappeared — the atomic write landed; pick up the new content.
                    var reread = await TryReadAllTextAsync(filePath);
                    if (reread != null)
                        await Context.ApplyExternalTextChangeAsync(filePath, reread);
                }
            }
            catch (ObjectDisposedException)
            {
                // Context was disposed mid-update (teardown race); nothing to do.
            }
        }

        /// <summary>
        /// Schedules a debounced MSBuild re-evaluation of every project that could own
        /// <paramref name="filePath"/>, so a newly created or deleted source file is
        /// reflected as an incremental document add/remove rather than a full reload.
        /// </summary>
        private void ScheduleOwningProjectReeval(string filePath)
        {
            foreach (var projectFile in Context.FindProjectFilesForPath(filePath))
                ScheduleProjectReeval(projectFile);
        }

        private void ScheduleProjectReeval(string projectFilePath)
        {
            if (IsTombstoned(Volatile.Read(ref _state))) return;
            var key = PathResolver.NormalizePath(projectFilePath);
            var generation = _reevalGeneration.AddOrUpdate(key, 1, (_, g) => g + 1);
            _ = RunProjectReevalAfterDelayAsync(key, generation);
        }

        private async Task RunProjectReevalAfterDelayAsync(string projectKey, long generation)
        {
            try { await Task.Delay(ReevalDebounce); }
            catch { return; }

            // A newer event for the same project superseded this one; let it run instead.
            if (_reevalGeneration.TryGetValue(projectKey, out var current) && current != generation)
                return;
            if (IsTombstoned(Volatile.Read(ref _state))) return;

            try
            {
                var changed = await Context.ReconcileProjectDocumentsAsync(projectKey);
                if (changed)
                    _cache.LogCallback?.Invoke(
                        $"Reconciled documents for project '{projectKey}' in '{_key}' without a reload.");
            }
            catch (ObjectDisposedException)
            {
                // Teardown race; nothing to do.
            }
            catch (Exception ex)
            {
                // Evaluation failed (transient MSBuild error, project mid-edit, etc.).
                // Fall back to a full reload so we never serve a stale document set.
                _cache.LogErrorCallback?.Invoke(
                    $"Incremental re-evaluation of '{projectKey}' failed; invalidating '{_key}'.", ex);
                _cache.Invalidate(_key);
            }
        }

        private static bool IsUnderIntermediateOutput(string path)
        {
            var normalized = PathResolver.NormalizePath(path);
            var sep = Path.DirectorySeparatorChar;
            return normalized.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Reads a file's text, retrying briefly on IO contention (editors touch a file
        /// several times during save). Returns null if the file is absent or unreadable.
        /// </summary>
        private static async Task<string?> TryReadAllTextAsync(string filePath)
        {
            const int maxAttempts = 4;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    // Read via a shared stream so editors holding the file open don't block us.
                    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(fs);
                    return await reader.ReadToEndAsync();
                }
                catch (FileNotFoundException) { return null; }
                catch (DirectoryNotFoundException) { return null; }
                catch (IOException) when (attempt < maxAttempts) { await Task.Delay(50 * attempt); }
                catch (UnauthorizedAccessException) when (attempt < maxAttempts) { await Task.Delay(50 * attempt); }
            }
            return null;
        }

        /// <summary>
        /// Polls briefly to confirm a file is genuinely gone rather than momentarily
        /// absent during an atomic save (delete-then-rename). Returns true only if it
        /// never reappears within the grace window.
        /// </summary>
        private static async Task<bool> StillMissingAsync(string filePath)
        {
            const int attempts = 6;
            for (var i = 0; i < attempts; i++)
            {
                if (File.Exists(filePath)) return false;
                await Task.Delay(50);
            }
            return !File.Exists(filePath);
        }

        private static bool IsProjectFile(string? extension)
        {
            if (string.IsNullOrEmpty(extension)) return false;
            foreach (var candidate in ProjectFileExtensions)
            {
                if (string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool IsFileBasedProgramWrapper(string? path)
            => path != null && path.EndsWith(".cs.csproj", StringComparison.OrdinalIgnoreCase);

        private static bool IsCSharpExt(string? extension)
            => string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase);

        private static bool IsRazorExt(string? extension)
            => string.Equals(extension, ".razor", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".cshtml", StringComparison.OrdinalIgnoreCase);
    }
}
