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
/// Each instance owns one collectible <see cref="AssemblyLoadContext"/>. Compiler
/// (<c>Microsoft.CodeAnalysis.*</c>) and runtime/BCL assemblies are deliberately
/// resolved from the host's default context so analyzer/generator types unify with
/// the Roslyn the server is already running — otherwise the generators would be
/// rejected. The analyzer's own private dependencies are shadow-copied and loaded
/// into the dedicated context.
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
/// directory, also held open with delete-on-close. While the process lives the lock
/// exists; when it dies the kernel removes it. On startup — right after creating its
/// own directory and lock — a process sweeps the sibling <c>pid-*</c> directories and
/// deletes any that have no lock file. The lock's presence, not an emptiness check, is
/// the guarantee: a dead process leaves behind empty sub-directories that an emptiness
/// check would wrongly preserve.
/// </para>
/// <para>Thread-safe: all mutable state is guarded by <see cref="_gate"/>.</para>
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

    private readonly string _shadowDir;
    private readonly ShadowCopyLoadContext _loadContext;
    private readonly object _gate = new();
    private readonly Action<string>? _log;

    // Delete-on-close handles keeping each shadow copy pinned for this process's
    // lifetime; closed on Dispose and, ultimately, by the OS on process termination.
    private readonly List<SafeFileHandle> _pins = new();

    // Simple assembly name -> original full path, populated via AddDependencyLocation.
    private readonly Dictionary<string, string> _dependencyPathsByName =
        new(StringComparer.OrdinalIgnoreCase);

    // Directories to probe for sibling dependencies not explicitly registered.
    private readonly HashSet<string> _probeDirectories =
        new(StringComparer.OrdinalIgnoreCase);

    // Normalized original path -> shadow-copied path (so each file is copied once).
    private readonly Dictionary<string, string> _shadowByOriginal =
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
        _loadContext = new ShadowCopyLoadContext(this);
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
        string shadow;
        lock (_gate)
        {
            // Register the analyzer's own directory so its siblings can be probed
            // for as dependencies even if the host didn't add them explicitly.
            RegisterLocationNoLock(fullPath);
            shadow = GetOrCreateShadowCopyNoLock(fullPath) ?? fullPath;
        }

        return _loadContext.LoadFromAssemblyPath(shadow);
    }

    /// <summary>
    /// Resolves a dependency requested by the dedicated load context. The compiler
    /// itself and runtime/BCL assemblies are shared with the host so analyzer types
    /// unify with the Roslyn we're already running; every other dependency — including
    /// analyzer assemblies that merely live under the <c>Microsoft.CodeAnalysis.*</c>
    /// namespace, such as the Razor source generator — is shadow-copied and loaded
    /// into the dedicated context.
    /// </summary>
    internal Assembly? Resolve(AssemblyName name)
    {
        var simpleName = name.Name;

        // Compiler + framework/runtime: share with the host. Returning the host's
        // already-loaded instance (or null, so the runtime resolves from the default
        // context) keeps analyzer/generator types bound to the same Roslyn/BCL.
        if (ShouldShareWithHost(simpleName))
            return FindInDefault(simpleName);

        // Private analyzer dependency: load from the analyzer's own (shadow-copied) folder.
        lock (_gate)
        {
            string? original = null;
            if (simpleName is not null &&
                _dependencyPathsByName.TryGetValue(simpleName, out var registered))
            {
                original = registered;
            }
            original ??= ProbeForDependencyNoLock(simpleName);

            if (original is not null)
            {
                var shadow = GetOrCreateShadowCopyNoLock(original) ?? original;
                return _loadContext.LoadFromAssemblyPath(shadow);
            }
        }

        // Not a known private dependency — fall back to whatever the host already has.
        return FindInDefault(simpleName);
    }

    private void RegisterLocationNoLock(string fullPath)
    {
        var name = Path.GetFileNameWithoutExtension(fullPath);
        if (!string.IsNullOrEmpty(name))
            _dependencyPathsByName[name] = fullPath;

        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            _probeDirectories.Add(dir);
    }

    private string? ProbeForDependencyNoLock(string? simpleName)
    {
        if (string.IsNullOrEmpty(simpleName)) return null;

        foreach (var dir in _probeDirectories)
        {
            var candidate = Path.Combine(dir, simpleName + ".dll");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
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
            // Without the pin the copy just lingers until a later startup sweep; not
            // fatal, so keep loading rather than failing the workspace.
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
    private static bool ShouldShareWithHost(string? simpleName)
    {
        if (string.IsNullOrEmpty(simpleName)) return false;

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

    private static Assembly? FindInDefault(string? simpleName)
    {
        if (string.IsNullOrEmpty(simpleName)) return null;

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

        try { _loadContext.Unload(); }
        catch { /* non-collectible or already unloading */ }

        // The copies are removed by the kernel once every handle closes — ours above
        // plus the CLR's image mapping, released after the unloaded context is GC'd.
        // The unload is asynchronous, so this directory delete usually fails here and
        // the now-empty directory is pruned by a later process's startup sweep.
        TryDeleteDirectory(_shadowDir);
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

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch { /* still in use by this or another process; leave for later */ }
    }

    /// <summary>
    /// Dedicated, collectible load context for shadow-copied analyzer assemblies.
    /// Delegates dependency resolution back to the owning loader so compiler/runtime
    /// assemblies unify with the host and private dependencies are shadow-copied.
    /// </summary>
    private sealed class ShadowCopyLoadContext : AssemblyLoadContext
    {
        private readonly ShadowCopyAnalyzerAssemblyLoader _owner;

        public ShadowCopyLoadContext(ShadowCopyAnalyzerAssemblyLoader owner)
            : base("RoslynMcpAnalyzerShadowCopy", isCollectible: true)
        {
            _owner = owner;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
            => _owner.Resolve(assemblyName);
    }
}
