using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.Win32.SafeHandles;

namespace RoslynMcp.Core.Workspace;

/// <summary>
/// An <see cref="IAnalyzerAssemblyLoader"/> that shadow-copies analyzer and
/// source-generator assemblies into a private temp directory and loads the copies,
/// the way Visual Studio and the Roslyn language server do.
/// </summary>
/// <remarks>
/// <para>
/// Roslyn's default loader memory-maps the analyzer/generator assembly in place
/// (<c>AssemblyLoadContext.LoadFromAssemblyPath(originalPath)</c>). On Windows that
/// holds an exclusive lock on the file, so a concurrent <c>dotnet build</c> cannot
/// copy a freshly built <c>*.SourceGenerator.dll</c> from <c>obj</c> to <c>bin</c>
/// (MSB3026/MSB3027/MSB3021: "being used by another process"). Loading a copy from a
/// temp directory leaves the build output untouched.
/// </para>
/// <para>
/// Following Roslyn's own model, there is one collectible
/// <see cref="AssemblyLoadContext"/> per analyzer <em>directory</em>, so analyzers from
/// different directories are isolated and can't clash over differently-versioned
/// dependencies. Compiler (<c>Microsoft.CodeAnalysis.*</c>) and runtime/BCL assemblies
/// are deliberately resolved from the host's default context so analyzer/generator
/// types unify with the Roslyn the server is already running; an analyzer's own private
/// dependencies are shadow-copied and loaded into its context. When the same simple
/// name is registered from multiple paths, the highest version wins.
/// </para>
/// <para>
/// Copies live under a per-OS-process directory (<c>pid-{pid}</c>) so several servers
/// — e.g. parallel Claude Code sessions — never collide even when they shadow
/// equally-named generator assemblies. Each copy is pinned with a
/// <see cref="FileOptions.DeleteOnClose"/> handle opened <em>before</em> the assembly
/// is mapped, so the kernel removes it when this process exits, whether the exit is
/// clean or a forced kill/crash.
/// </para>
/// <para>
/// Liveness is tracked by an <c>in_use.lock</c> file at the root of the process
/// directory, also held open with delete-on-close. On startup a process sweeps sibling
/// <c>pid-*</c> directories and deletes any with no lock file (their owner has exited).
/// In addition, when a loader is disposed (a workspace is evicted/reloaded) its
/// directory is queued for best-effort mid-session reclamation, so temp copies don't
/// accumulate for the whole process lifetime.
/// </para>
/// <para>Thread-safe: shared mutable state is guarded by <see cref="_gate"/>.</para>
/// </remarks>
internal sealed class ShadowCopyAnalyzerAssemblyLoader : IAnalyzerAssemblyLoader, IDisposable
{
    /// <summary>Shared root holding one sub-directory per OS process.</summary>
    private static readonly string ShadowCopyRoot =
        Path.Combine(Path.GetTempPath(), "RoslynMcp", "AnalyzerShadowCopy");

    /// <summary>
    /// This process's directory, named purely by PID so it is deterministic and
    /// sweepable. PID reuse is safe: a reused PID maps to the same directory, which the
    /// new owner simply re-locks.
    /// </summary>
    private static readonly string ProcessShadowRoot =
        Path.Combine(ShadowCopyRoot, $"pid-{Environment.ProcessId}");

    /// <summary>Name of the per-process liveness lock file.</summary>
    private const string LockFileName = "in_use.lock";

    // Held for the whole process lifetime; the kernel deletes it (delete-on-close) when
    // the process exits, marking this directory as collectible by a later sweep.
    private static SafeFileHandle? _processLock;

    // Distinguishes the sub-directory of each loader within the process directory
    // without a GUID, so loaders in the same process never overwrite each other.
    private static int _loaderCounter;

    // Shadow directories of disposed loaders awaiting best-effort deletion. A loaded
    // assembly can't be deleted while its (collectible) context is still mapped, so we
    // retry as contexts unload rather than blocking disposal.
    private static readonly ConcurrentQueue<string> RetiredShadowDirs = new();
    private static int _reclaiming;

    private readonly string _shadowDir;
    private readonly object _gate = new();
    private readonly Action<string>? _log;

    // One collectible load context per analyzer directory (Roslyn's isolation model).
    private readonly ConcurrentDictionary<string, ShadowCopyLoadContext> _loadContextsByDir =
        new(StringComparer.OrdinalIgnoreCase);

    // Delete-on-close handles keeping each shadow copy pinned for this process's
    // lifetime; closed on Dispose and, ultimately, by the OS on process termination.
    private readonly List<SafeFileHandle> _pins = new();

    // Simple assembly name -> all registered original paths (multiple versions possible).
    private readonly Dictionary<string, List<string>> _dependencyPathsByName =
        new(StringComparer.OrdinalIgnoreCase);

    // Directories to probe for sibling dependencies not explicitly registered.
    private readonly HashSet<string> _probeDirectories =
        new(StringComparer.OrdinalIgnoreCase);

    // Normalized original path -> shadow-copied path (so each file is copied once).
    private readonly Dictionary<string, string> _shadowByOriginal =
        new(StringComparer.OrdinalIgnoreCase);

    // Original path -> its AssemblyName (cached; null if unreadable), for version compare.
    private readonly Dictionary<string, AssemblyName?> _assemblyNameByPath =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    static ShadowCopyAnalyzerAssemblyLoader()
    {
        // Create this process's directory + held lock, then prune directories left by
        // processes that have since exited (their lock file is gone).
        InitializeProcessRoot();
        SweepDeadProcessDirectories();
    }

    public ShadowCopyAnalyzerAssemblyLoader(Action<string>? log = null)
    {
        _log = log;
        var index = Interlocked.Increment(ref _loaderCounter);
        _shadowDir = Path.Combine(ProcessShadowRoot, index.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(_shadowDir);

        // Opportunistically reclaim directories left by loaders disposed earlier in this
        // process; by now their contexts may have unloaded.
        ReclaimRetiredDirectories();
    }

    /// <inheritdoc />
    public void AddDependencyLocation(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return;

        lock (_gate)
        {
            RegisterLocationNoLock(fullPath);
        }
    }

    /// <inheritdoc />
    public Assembly LoadFromPath(string fullPath)
    {
        var context = GetOrCreateContext(GetContextDirectory(fullPath));

        string shadow;
        lock (_gate)
        {
            // Register the analyzer's own directory so its siblings can be probed
            // for as dependencies even if the host didn't add them explicitly.
            RegisterLocationNoLock(fullPath);
            shadow = GetOrCreateShadowCopyNoLock(fullPath) ?? fullPath;
        }

        return context.LoadFromAssemblyPath(shadow);
    }

    /// <summary>
    /// Resolves a dependency requested by a per-directory load context. The compiler
    /// itself and runtime/BCL assemblies are shared with the host so analyzer types
    /// unify with the Roslyn we're already running; every other dependency — including
    /// analyzer assemblies that merely live under the <c>Microsoft.CodeAnalysis.*</c>
    /// namespace, such as the Razor source generator — is shadow-copied and loaded into
    /// the requesting context (keeping each analyzer directory isolated).
    /// </summary>
    internal Assembly? Resolve(AssemblyName name, ShadowCopyLoadContext context)
    {
        var simpleName = name.Name;
        if (string.IsNullOrEmpty(simpleName))
            return null;

        // Compiler + framework/runtime: share with the host. Returning the host's
        // already-loaded instance (or null, so the runtime resolves from the default
        // context) keeps analyzer/generator types bound to the same Roslyn/BCL.
        if (ShouldShareWithHost(simpleName))
            return FindInDefault(simpleName);

        // Unify with anything the host already loaded (e.g. its own private deps).
        var hostLoaded = FindInDefault(simpleName);
        if (hostLoaded is not null)
            return hostLoaded;

        lock (_gate)
        {
            var original = ResolveBestDependencyPathNoLock(simpleName, name, context.Directory);
            if (original is not null)
            {
                var shadow = GetOrCreateShadowCopyNoLock(original) ?? original;
                return context.LoadFromAssemblyPath(shadow);
            }
        }

        // Not a known private dependency — let the runtime fall back to the default context.
        return null;
    }

    private void RegisterLocationNoLock(string fullPath)
    {
        var name = Path.GetFileNameWithoutExtension(fullPath);
        if (!string.IsNullOrEmpty(name))
        {
            if (!_dependencyPathsByName.TryGetValue(name, out var list))
                _dependencyPathsByName[name] = list = new List<string>(1);
            if (!list.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                list.Add(fullPath);
        }

        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            _probeDirectories.Add(dir);
    }

    /// <summary>
    /// Picks the best original path for <paramref name="simpleName"/>: prefers an exact
    /// version match to <paramref name="requested"/>, otherwise the highest version.
    /// Candidates are the requesting analyzer's own directory first, then every
    /// registered/probed location. Mirrors Roslyn's <c>GetBestResolvedPath</c>.
    /// </summary>
    private string? ResolveBestDependencyPathNoLock(
        string simpleName, AssemblyName requested, string? preferDirectory)
    {
        var candidates = new List<string>();

        void AddCandidateFromDir(string? dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            var candidate = Path.Combine(dir, simpleName + ".dll");
            if (File.Exists(candidate) &&
                !candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                candidates.Add(candidate);
        }

        AddCandidateFromDir(preferDirectory);
        if (_dependencyPathsByName.TryGetValue(simpleName, out var registered))
        {
            foreach (var path in registered)
                if (File.Exists(path) &&
                    !candidates.Contains(path, StringComparer.OrdinalIgnoreCase))
                    candidates.Add(path);
        }
        foreach (var dir in _probeDirectories)
            AddCandidateFromDir(dir);

        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        string? best = null;
        Version? bestVersion = null;
        foreach (var path in candidates)
        {
            var version = GetAssemblyNameNoLock(path)?.Version;
            if (version is null)
            {
                best ??= path; // keep an unreadable candidate only as a last resort
                continue;
            }
            if (requested.Version is not null && version == requested.Version)
                return path; // exact match wins immediately
            if (bestVersion is null || version > bestVersion)
            {
                best = path;
                bestVersion = version;
            }
        }
        return best;
    }

    private AssemblyName? GetAssemblyNameNoLock(string path)
    {
        if (_assemblyNameByPath.TryGetValue(path, out var cached))
            return cached;

        AssemblyName? name = null;
        try { name = AssemblyName.GetAssemblyName(path); }
        catch { /* corrupt / native / unreadable: treat as version-less */ }

        _assemblyNameByPath[path] = name;
        return name;
    }

    private ShadowCopyLoadContext GetOrCreateContext(string directory)
        => _loadContextsByDir.GetOrAdd(directory, dir => new ShadowCopyLoadContext(this, dir));

    private static string GetContextDirectory(string fullPath)
    {
        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(fullPath)) ?? fullPath;
        }
        catch
        {
            return Path.GetDirectoryName(fullPath) ?? fullPath;
        }
    }

    private string? GetOrCreateShadowCopyNoLock(string originalPath)
    {
        string key;
        try
        {
            key = Path.GetFullPath(originalPath);
        }
        catch
        {
            key = originalPath;
        }

        if (_shadowByOriginal.TryGetValue(key, out var existing))
            return existing;

        try
        {
            // Keep files that originate from the same directory together so the
            // load context can probe siblings using relative layout.
            var sourceDir = Path.GetDirectoryName(key) ?? string.Empty;
            var destDir = Path.Combine(_shadowDir, HashDirectory(sourceDir));
            Directory.CreateDirectory(destDir);

            var dest = Path.Combine(destDir, Path.GetFileName(key));
            File.Copy(key, dest, overwrite: true);
            PinForDeleteOnClose(dest);

            _shadowByOriginal[key] = dest;
            return dest;
        }
        catch (Exception ex)
        {
            // Fall back to loading the original (re-introduces the lock, but keeps
            // analysis working) rather than failing the whole workspace load.
            _log?.Invoke(
                $"Shadow-copy failed for '{originalPath}': {ex.Message}. " +
                "Loading the original file (it may be locked).");
            return null;
        }
    }

    /// <summary>
    /// Opens a <see cref="FileOptions.DeleteOnClose"/> handle on a freshly copied
    /// assembly so the kernel removes it once every handle closes — i.e. when this
    /// process exits, including on a crash or forced kill. Must be called BEFORE the
    /// CLR maps the assembly: once it is mapped as an image, Windows refuses to grant
    /// the DELETE access this open requires. The CLR opens assemblies with
    /// <c>FILE_SHARE_DELETE</c>, so this handle and the image mapping coexist.
    /// </summary>
    private void PinForDeleteOnClose(string shadowPath)
    {
        try
        {
            _pins.Add(File.OpenHandle(
                shadowPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                FileOptions.DeleteOnClose));
        }
        catch (Exception ex)
        {
            // Without the pin the copy just lingers until a later sweep; not fatal,
            // so keep loading rather than failing the workspace.
            _log?.Invoke($"Could not pin shadow copy '{shadowPath}' for delete-on-close: {ex.Message}.");
        }
    }

    /// <summary>
    /// Assemblies that must come from the host so analyzer/generator types unify with
    /// the running Roslyn and the shared framework. This is the compiler proper plus
    /// the runtime/BCL — deliberately NOT a blanket <c>Microsoft.CodeAnalysis.*</c>
    /// prefix, since real analyzers and source generators (e.g. the Razor generator,
    /// <c>Microsoft.CodeAnalysis.Razor.Compiler</c>) live under that namespace and must
    /// load from their own shadow-copied assemblies rather than the (absent) host copy.
    /// </summary>
    private static bool ShouldShareWithHost(string simpleName)
    {
        switch (simpleName)
        {
            case "Microsoft.CodeAnalysis":
            case "Microsoft.CodeAnalysis.CSharp":
            case "Microsoft.CodeAnalysis.VisualBasic":
            case "Microsoft.CodeAnalysis.Workspaces":
            case "Microsoft.CodeAnalysis.CSharp.Workspaces":
            case "Microsoft.CodeAnalysis.VisualBasic.Workspaces":
                return true;
        }

        // Framework / runtime assemblies (System.Collections.Immutable and
        // System.Reflection.Metadata are compiler dependencies that must unify too).
        if (simpleName.StartsWith("System.", StringComparison.OrdinalIgnoreCase))
            return true;

        return simpleName.Equals("System", StringComparison.OrdinalIgnoreCase)
            || simpleName.Equals("mscorlib", StringComparison.OrdinalIgnoreCase)
            || simpleName.Equals("netstandard", StringComparison.OrdinalIgnoreCase)
            || simpleName.Equals("WindowsBase", StringComparison.OrdinalIgnoreCase)
            || simpleName.Equals("Microsoft.CSharp", StringComparison.OrdinalIgnoreCase)
            || simpleName.Equals("Microsoft.VisualBasic", StringComparison.OrdinalIgnoreCase);
    }

    private static Assembly? FindInDefault(string simpleName)
    {
        foreach (var asm in AssemblyLoadContext.Default.Assemblies)
        {
            if (string.Equals(asm.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                return asm;
        }
        return null;
    }

    private static string HashDirectory(string dir)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(dir.ToLowerInvariant()));
        return Convert.ToHexString(bytes, 0, 8);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_gate)
        {
            foreach (var pin in _pins)
            {
                try { pin.Dispose(); }
                catch { /* ignore */ }
            }
            _pins.Clear();
        }

        foreach (var context in _loadContextsByDir.Values)
        {
            try { context.Unload(); }
            catch { /* non-collectible or already unloading */ }
        }
        _loadContextsByDir.Clear();

        // The copies can't be deleted until the (now unloaded) contexts are GC'd and the
        // CLR releases their image mappings. Queue this directory and try to reclaim it
        // (plus any earlier ones) now and on future loads; the OS reclaims anything left
        // at process exit via the delete-on-close handles.
        RetiredShadowDirs.Enqueue(_shadowDir);
        ReclaimRetiredDirectories();
    }

    /// <summary>
    /// Best-effort deletion of shadow directories belonging to disposed loaders. Runs a
    /// plain pass first; if anything is still mapped, nudges the GC to finish unloading
    /// collectible contexts and retries once. Whatever still can't be deleted is requeued
    /// for the next attempt (and is guaranteed to go at process exit via delete-on-close).
    /// </summary>
    private static void ReclaimRetiredDirectories()
    {
        if (RetiredShadowDirs.IsEmpty) return;
        if (Interlocked.CompareExchange(ref _reclaiming, 1, 0) != 0) return; // one reclaim at a time
        try
        {
            var stuck = new List<string>();
            while (RetiredShadowDirs.TryDequeue(out var dir))
            {
                if (!TryDeleteDirectoryReturningSuccess(dir))
                    stuck.Add(dir);
            }

            if (stuck.Count > 0)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                foreach (var dir in stuck)
                {
                    if (!TryDeleteDirectoryReturningSuccess(dir))
                        RetiredShadowDirs.Enqueue(dir); // try again next time
                }
            }
        }
        finally
        {
            Volatile.Write(ref _reclaiming, 0);
        }
    }

    /// <summary>
    /// Creates this process's shadow directory and opens the held <c>in_use.lock</c>
    /// (delete-on-close). The lock's presence is what tells other processes this
    /// directory is alive; the kernel removes it when this process exits.
    /// </summary>
    private static void InitializeProcessRoot()
    {
        try
        {
            Directory.CreateDirectory(ProcessShadowRoot);
            _processLock = File.OpenHandle(
                Path.Combine(ProcessShadowRoot, LockFileName),
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                FileOptions.DeleteOnClose);
        }
        catch { /* best effort: cleanup degrades but shadow copying still works */ }
    }

    private static void SweepDeadProcessDirectories()
        => SweepDeadProcessDirectories(ShadowCopyRoot, ProcessShadowRoot);

    /// <summary>
    /// Deletes sibling directories under <paramref name="root"/> that have no
    /// <c>in_use.lock</c> file — i.e. whose owning process has exited (the kernel
    /// removed its delete-on-close lock). <paramref name="ownDirectory"/> is skipped.
    /// The lock file, not an emptiness check, is the guarantee: a dead process leaves
    /// empty sub-directories behind, so an emptiness check would wrongly keep them
    /// alive forever.
    /// </summary>
    internal static void SweepDeadProcessDirectories(string root, string? ownDirectory)
    {
        try
        {
            if (!Directory.Exists(root)) return;

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (ownDirectory is not null &&
                    string.Equals(dir, ownDirectory, StringComparison.OrdinalIgnoreCase))
                    continue; // our own live directory

                if (File.Exists(Path.Combine(dir, LockFileName)))
                    continue; // a live process holds the lock

                TryDeleteDirectory(dir);
            }
        }
        catch { /* best effort */ }
    }

    private static void TryDeleteDirectory(string dir) => TryDeleteDirectoryReturningSuccess(dir);

    private static bool TryDeleteDirectoryReturningSuccess(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            return true;
        }
        catch
        {
            return false; // still in use by this or another process; leave for later
        }
    }

    // --- Test seams (internal; exercised by RoslynMcp.Core.Tests) ---

    /// <summary>Number of per-directory load contexts currently held.</summary>
    internal int ActiveLoadContextCount => _loadContextsByDir.Count;

    /// <summary>Resolves the best original path for a simple name, as <see cref="Resolve"/> would.</summary>
    internal string? ResolveBestDependencyPathForTest(string simpleName, Version? requestedVersion)
    {
        var requested = new AssemblyName(simpleName);
        if (requestedVersion is not null) requested.Version = requestedVersion;
        lock (_gate)
            return ResolveBestDependencyPathNoLock(simpleName, requested, preferDirectory: null);
    }

    internal static void EnqueueRetiredDirectoryForTest(string directory) => RetiredShadowDirs.Enqueue(directory);

    internal static void ReclaimRetiredDirectoriesForTest() => ReclaimRetiredDirectories();

    /// <summary>
    /// Dedicated, collectible load context for the shadow-copied analyzer assemblies of
    /// a single directory. Delegates dependency resolution back to the owning loader so
    /// compiler/runtime assemblies unify with the host and private dependencies are
    /// shadow-copied into this same context.
    /// </summary>
    internal sealed class ShadowCopyLoadContext : AssemblyLoadContext
    {
        private readonly ShadowCopyAnalyzerAssemblyLoader _owner;

        public ShadowCopyLoadContext(ShadowCopyAnalyzerAssemblyLoader owner, string directory)
            : base($"RoslynMcpAnalyzerShadowCopy:{directory}", isCollectible: true)
        {
            _owner = owner;
            Directory = directory;
        }

        /// <summary>The original analyzer directory this context serves.</summary>
        public string Directory { get; }

        protected override Assembly? Load(AssemblyName assemblyName)
            => _owner.Resolve(assemblyName, this);
    }
}
