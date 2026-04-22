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
    public static readonly TimeSpan DefaultIdleTtl = TimeSpan.FromMinutes(5);

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
            stopwatch.Stop();
            return (existing.Context, stopwatch.ElapsedMilliseconds);
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
                stopwatch.Stop();
                return (existing.Context, stopwatch.ElapsedMilliseconds);
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
        // Sentinel ref-count values:
        //   >= 0 : active; value is the number of outstanding leases.
        //   int.MinValue : tombstoned; entry is disposed or in teardown.
        private const int Tombstone = int.MinValue;

        private readonly WorkspaceCache _cache;
        private readonly string _key;
        private readonly List<FileSystemWatcher> _watchers = new();
        private int _refCount;
        private long _lastAccessTicks;
        private int _torndown;

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
                var cur = Volatile.Read(ref _refCount);
                if (cur < 0) return false;
                if (Interlocked.CompareExchange(ref _refCount, cur + 1, cur) == cur)
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
            Interlocked.Decrement(ref _refCount);
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

            // Commit the eviction atomically. Only succeeds when ref count is 0
            // AND no one else has concurrently tombstoned us.
            if (Interlocked.CompareExchange(ref _refCount, Tombstone, 0) != 0)
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
            // Mark the entry as tombstoned so new acquires miss; let outstanding
            // leases finish. Dispose only when ref count drops to zero.
            while (true)
            {
                var cur = Volatile.Read(ref _refCount);
                if (cur == Tombstone) return;
                if (Interlocked.CompareExchange(ref _refCount, Tombstone, cur) == cur)
                {
                    if (cur == 0) Teardown();
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
            if (Volatile.Read(ref _refCount) == Tombstone) return;
            _ = HandleFileSystemEventAsync(e.ChangeType, e.FullPath, oldFullPath: null);
        }

        private void OnFileRenamed(object sender, RenamedEventArgs e)
        {
            if (Volatile.Read(ref _refCount) == Tombstone) return;
            _ = HandleFileSystemEventAsync(e.ChangeType, e.FullPath, e.OldFullPath);
        }

        private async Task HandleFileSystemEventAsync(
            WatcherChangeTypes changeType, string fullPath, string? oldFullPath)
        {
            try
            {
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

                if (!string.Equals(ext, ".cs", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(oldExt, ".cs", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                switch (changeType)
                {
                    case WatcherChangeTypes.Changed:
                        await ApplyTextChangeAsync(fullPath);
                        break;

                    case WatcherChangeTypes.Renamed when oldFullPath != null &&
                            string.Equals(ext, oldExt, StringComparison.OrdinalIgnoreCase):
                        // Rename that keeps the extension: treat as a text change on
                        // the new path IF the workspace already knows it (unusual);
                        // otherwise invalidate since the document-id map is now stale.
                        _cache.LogCallback?.Invoke(
                            $"File renamed '{oldFullPath}' -> '{fullPath}'; invalidating '{_key}'.");
                        _cache.Invalidate(_key);
                        break;

                    case WatcherChangeTypes.Created:
                    case WatcherChangeTypes.Deleted:
                    case WatcherChangeTypes.Renamed:
                        // Add/remove/rename of a .cs file affects the project's
                        // document list; full reload is the safe path.
                        _cache.LogCallback?.Invoke(
                            $"File {changeType} '{fullPath}'; invalidating '{_key}'.");
                        _cache.Invalidate(_key);
                        break;
                }
            }
            catch (Exception ex)
            {
                _cache.LogErrorCallback?.Invoke(
                    $"Error handling filesystem event for '{fullPath}'", ex);
            }
        }

        private async Task ApplyTextChangeAsync(string filePath)
        {
            // Editors often touch the file several times during save; retry briefly
            // on IO contention before giving up.
            const int maxAttempts = 4;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    if (Volatile.Read(ref _refCount) == Tombstone) return;

                    // Read via a shared stream so editors holding the file open don't block us.
                    string text;
                    using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                               FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(fs))
                    {
                        text = await reader.ReadToEndAsync();
                    }

                    await Context.ApplyExternalTextChangeAsync(filePath, text);
                    return;
                }
                catch (FileNotFoundException)
                {
                    return; // File no longer present; Deleted event will invalidate.
                }
                catch (DirectoryNotFoundException)
                {
                    return;
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    await Task.Delay(50 * attempt);
                }
                catch (UnauthorizedAccessException) when (attempt < maxAttempts)
                {
                    await Task.Delay(50 * attempt);
                }
                catch (ObjectDisposedException)
                {
                    return; // Context was disposed mid-update.
                }
            }
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
    }
}
