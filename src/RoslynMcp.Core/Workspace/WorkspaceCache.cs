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
/// - Watcher-driven updates run under <see cref="WorkspaceContext"/>'s exclusive
///   file-update lease, which no operation holds at the same time, and read the disk only
///   inside it — so they never race with an in-flight refactoring commit, nor apply text
///   older than one that committed while they waited.
/// - Project-file changes (<c>.csproj</c>, <c>.sln</c>, etc.) invalidate the entry
///   rather than attempting an incremental reload.
/// - Every context the cache loads shares one writer section
///   (<see cref="WorkspaceWriteCoordinator"/>): entries for different roots over the same files
///   (a <c>.slnx</c> and a <c>.csproj</c> of one tree), or an invalidated entry and its reloaded
///   successor, refactor one at a time, and each takes over what the others wrote before it
///   computes, without waiting for its file watcher.
/// </remarks>
public sealed class WorkspaceCache : IDisposable
{
    /// <summary>Default time-to-live for unreferenced entries.</summary>
    public static readonly TimeSpan DefaultIdleTtl = TimeSpan.FromMinutes(20);

    /// <summary>How often the sweeper checks for idle entries.</summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromMinutes(1);

    private static readonly string[] ProjectFileExtensions =
        { ".csproj", ".sln", ".slnx", ".props", ".targets" };

    private readonly ConcurrentDictionary<string, CachedEntry> _entries = new(PathResolver.PathComparer);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _loadGuards = new(PathResolver.PathComparer);
    private readonly WorkspaceWriteCoordinator _writers = new();
    private Task? _watcherEventsHeldUntil;
    private readonly TimeSpan _idleTtl;
    private readonly Timer _sweepTimer;
    private bool _disposed;

    /// <summary>Optional diagnostics callback.</summary>
    public Action<string>? LogCallback { get; set; }

    /// <summary>Optional error-diagnostics callback.</summary>
    public Action<string, Exception?>? LogErrorCallback { get; set; }

    /// <summary>
    /// For tests: while this task is pending, the file watchers' callbacks wait for it before
    /// they handle an event, as if the file system reported changes late. Null (the default)
    /// lets them run at once.
    /// </summary>
    internal Task? WatcherEventsHeldUntil
    {
        get => Volatile.Read(ref _watcherEventsHeldUntil);
        set => Volatile.Write(ref _watcherEventsHeldUntil, value);
    }

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
    /// The returned context is the caller's own handle on the cached workspace: calling
    /// <see cref="WorkspaceContext.Dispose"/> releases the caller's lease (once, however
    /// often it is called) rather than tearing down the workspace.
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
                var handle = await EnterOperationOrRelease(existing, cancellationToken);
                stopwatch.Stop();
                return (handle, stopwatch.ElapsedMilliseconds);
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
                    var handle = await EnterOperationOrRelease(existing, cancellationToken);
                    stopwatch.Stop();
                    return (handle, stopwatch.ElapsedMilliseconds);
                }
                InvalidateStaleEntry(key, existing);
            }

            LogCallback?.Invoke($"Cache miss for '{key}'; loading workspace...");

            // Writes other contexts commit from here on may be missing from what the load reads;
            // the new context takes them over from disk when its first writer section starts.
            // Files anyone changes from here on may be missing too, and the watchers start only
            // after the load: the files written since are synced once they run.
            var writesBeforeLoad = _writers.Sequence;
            var loadStartUtc = DateTime.UtcNow;
            var fresh = await loader(cancellationToken);
            var entry = new CachedEntry(this, key, fresh);
            fresh.MarkCacheOwned();
            fresh.ShareWriters(_writers, writesBeforeLoad);

            // Take the first lease for the caller before publishing, so a sweep
            // can never see RefCount == 0 between publish and return.
            if (!entry.TryAcquire())
                throw new InvalidOperationException("Failed to acquire freshly constructed cache entry.");

            // Watch first, then sync what changed since the load started: a change after the
            // watchers start raises an event, one before is found by the sync. Both happen before
            // the entry is published, so no caller sees the workspace without them.
            entry.StartWatching();
            bool rebuild;
            try
            {
                // Not cancelled with the caller: the load is paid for, and the next caller gets
                // the entry even if this one gives up (as when it cancels just below).
                rebuild = await entry.SyncChangedDuringLoadAsync(loadStartUtc);
            }
            catch
            {
                entry.BeginTeardown();
                entry.OnLeaseReleased();
                throw;
            }

            _entries[key] = entry;
            if (rebuild)
            {
                // The caller still gets this workspace; the next one loads afresh.
                InvalidateEntry(key, entry);
            }
            else if (entry.IsInvalidated)
            {
                // A watcher invalidated the entry before it was published, finding nothing to
                // remove: remove it now, or it would stay published until the next load.
                _entries.TryRemove(new KeyValuePair<string, CachedEntry>(key, entry));
            }

            // Waits only for a file update a watcher queued meanwhile.
            var loadedHandle = await EnterOperationOrRelease(entry, cancellationToken);

            stopwatch.Stop();
            LogCallback?.Invoke(
                $"Workspace loaded for '{key}' in {stopwatch.ElapsedMilliseconds} ms.");
            return (loadedHandle, stopwatch.ElapsedMilliseconds);
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

    /// <summary>
    /// Takes an operation lease on the entry the caller has acquired, and returns the caller's
    /// own handle on the workspace, which releases both when disposed.
    /// </summary>
    private static async Task<WorkspaceContext> EnterOperationOrRelease(CachedEntry entry, CancellationToken ct)
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

        return entry.Context.CreateLeaseHandle(entry.OnLeaseReleased);
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

    /// <summary>
    /// Invalidates <paramref name="entry"/> on behalf of its own watcher callbacks: removes it
    /// from the cache only if it is still the entry published for <paramref name="key"/>, and
    /// tears it down either way. A callback can still be running after its entry was
    /// invalidated and a successor loaded (it checked the tombstone when it started, and a
    /// re-evaluation runs for a while); removing by key alone would evict that fresh successor
    /// and force another full reload.
    /// </summary>
    private void InvalidateEntry(string key, CachedEntry entry)
    {
        _entries.TryRemove(new KeyValuePair<string, CachedEntry>(key, entry));
        entry.BeginTeardown();
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
        // Published once by StartWatching and taken (swapped for null) by whichever teardown
        // path gets there first: BeginTeardown, the deferred Teardown on the last lease release
        // and the sweeper's eviction can run on different threads at once, and must never
        // enumerate one collection while another clears it.
        private FileSystemWatcher[]? _watchers;
        // Per-project monotonic generation used to debounce structural re-evaluations: a
        // burst of add/remove events for one project coalesces into a single re-eval.
        private readonly ConcurrentDictionary<string, long> _reevalGeneration = new(PathResolver.PathComparer);
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
        /// Lease-release callback of the handles the entry hands out (CreateLeaseHandle).
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
            var started = new List<FileSystemWatcher>();
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
                    started.Add(watcher);
                }
                catch (Exception ex)
                {
                    watcher?.Dispose();
                    _cache.LogErrorCallback?.Invoke(
                        $"Could not start file watcher for '{dir}'", ex);
                }
            }

            Interlocked.Exchange(ref _watchers, started.ToArray());

            // The watchers started first may already have invalidated the entry (a project file
            // changed) before the array was stored, finding nothing to dispose; then dispose them
            // here. (A teardown that tombstones the entry after this check still disposes them:
            // the final Teardown runs once the lease our caller holds is released.)
            if (IsTombstoned(Volatile.Read(ref _state)))
                DisposeWatchers();
        }

        /// <summary>Whether the entry has been invalidated or evicted.</summary>
        public bool IsInvalidated => IsTombstoned(Volatile.Read(ref _state));

        /// <summary>
        /// Syncs the files changed since <paramref name="loadStartUtc"/>, once the watchers run,
        /// and reads again the files the load decoded with replacement characters (see
        /// <see cref="WorkspaceContext.SyncFilesChangedSinceAsync"/>); re-evaluates the projects
        /// of tracked files that are gone, as the watcher does for a deletion. Runs before the
        /// entry is published, so no operation holds a lease yet.
        /// </summary>
        /// <returns>
        /// True when the workspace must be loaded afresh: a file-based program's directives
        /// changed, or a project could not be re-evaluated.
        /// </returns>
        public async Task<bool> SyncChangedDuringLoadAsync(DateTime loadStartUtc)
        {
            var changed = await Context.SyncFilesChangedSinceAsync(loadStartUtc).ConfigureAwait(false);
            if (changed.Updated > changed.Redecoded)
                _cache.LogCallback?.Invoke(
                    $"{changed.Updated - changed.Redecoded} file(s) of '{_key}' changed while it loaded; took over their text.");
            if (changed.Redecoded > 0)
                _cache.LogCallback?.Invoke(
                    $"{changed.Redecoded} file(s) of '{_key}' were loaded with replacement characters (not UTF-8, read as UTF-8); read them again in their code page.");

            if (changed.ReloadRequired)
            {
                _cache.LogCallback?.Invoke(
                    $"File-based program directives of '{_key}' changed while it loaded; the next call rebuilds it.");
                return true;
            }

            var projectFiles = changed.Missing
                .SelectMany(Context.FindProjectFilesForPath)
                .Distinct(PathResolver.PathComparer);
            foreach (var projectFile in projectFiles)
            {
                try
                {
                    await Context.ReconcileProjectDocumentsAsync(projectFile).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not ObjectDisposedException)
                {
                    _cache.LogErrorCallback?.Invoke(
                        $"Re-evaluating '{projectFile}' for files deleted while '{_key}' loaded failed; the next call reloads it.", ex);
                    return true;
                }
            }

            return false;
        }

        private void DisposeWatchers()
        {
            var watchers = Interlocked.Exchange(ref _watchers, null);
            if (watchers == null) return;

            foreach (var watcher in watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch { /* ignore */ }
            }
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
                if (_cache.WatcherEventsHeldUntil is { } held)
                    await held.ConfigureAwait(false);

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
                    _cache.InvalidateEntry(_key, this);
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
                // picks up the new Razor content. A file this workspace wrote itself
                // (rename_namespace rewrites @using / @namespace directives) is already in
                // the solution as it is on disk, and needs no reload.
                if (isRazor)
                {
                    if (IsRazorExt(ext) && (oldFullPath == null || !IsRazorExt(oldExt)) &&
                        await IsAlreadyInSolutionAsync(fullPath).ConfigureAwait(false))
                    {
                        return;
                    }

                    _cache.LogCallback?.Invoke(
                        $"Razor/CSHTML file {changeType} '{fullPath}'; invalidating '{_key}'.");
                    _cache.InvalidateEntry(_key, this);
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
                // The context reads the file itself, inside its exclusive file-update lease.
                // Text read here, before that lease, could predate an edit that an operation
                // commits while this update waits — and would then overwrite it.
                var outcome = await Context.SyncDocumentFromDiskAsync(filePath).ConfigureAwait(false);
                if (outcome == DiskSyncOutcome.Missing)
                {
                    // Not readable right now. An atomic save deletes then renames into place,
                    // so confirm the file is really gone before treating it as a deletion.
                    if (await StillMissingAsync(filePath).ConfigureAwait(false))
                    {
                        if (IsTombstoned(Volatile.Read(ref _state))) return;
                        // Only a file the workspace actually tracks warrants a re-evaluation;
                        // a stray deletion elsewhere under the project cone is irrelevant. The
                        // re-evaluation checks the disk again under the lease, so a stale
                        // answer here costs at most a redundant evaluation.
                        if (Context.GetDocumentByPath(filePath) != null)
                            ScheduleOwningProjectReeval(filePath);
                        return;
                    }

                    // Reappeared — the atomic write landed; sync again (under the lease, so
                    // whatever has been written since is what gets applied).
                    outcome = await Context.SyncDocumentFromDiskAsync(filePath).ConfigureAwait(false);
                }

                // Updated / Unchanged: done. NotTracked after a mere content change: not ours.
                // Missing again right after reappearing: the event for its next change settles it.
                switch (outcome)
                {
                    case DiskSyncOutcome.ReloadRequired:
                        // A standalone file-based program whose #: directives changed must be
                        // rebuilt from scratch — the resolved references/SDK/properties may now
                        // differ, which an in-place text update can't capture.
                        _cache.LogCallback?.Invoke(
                            $"File-based program directives changed in '{filePath}'; invalidating '{_key}' to rebuild.");
                        _cache.InvalidateEntry(_key, this);
                        break;

                    case DiskSyncOutcome.NotTracked when looksNew && !IsUnderIntermediateOutput(filePath):
                        ScheduleOwningProjectReeval(filePath);
                        break;
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
                _cache.InvalidateEntry(_key, this);
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
        /// Whether a Razor file's text on disk is what the solution's additional documents hold
        /// already, as after this workspace's own commit; an atomic save's brief absence (delete,
        /// then rename into place) is waited out.
        /// </summary>
        private async Task<bool> IsAlreadyInSolutionAsync(string filePath)
        {
            if (!File.Exists(filePath) && await StillMissingAsync(filePath).ConfigureAwait(false))
                return false;

            try
            {
                return await Context.AdditionalDocumentsMatchDiskAsync(filePath).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
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
