using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Resolution;

namespace RoslynMcp.Core.Workspace;

/// <summary>
/// Scoped workspace session. Encapsulates MSBuildWorkspace lifecycle.
/// </summary>
/// <remarks>
/// When created directly (non-cached), callers must <see cref="Dispose"/> when done.
/// When obtained from <see cref="WorkspaceCache"/>, <see cref="Dispose"/> releases a
/// lease (ref count) and the cache owns the actual workspace lifetime. Concurrent
/// read access to <see cref="Solution"/> is safe. Mutations are serialized two ways:
/// refactorings run as the single writer (<see cref="RunWriterAsync{T}"/>), and
/// file-watcher updates take the gate's exclusive file-update lease, which no operation
/// holds at the same time.
/// </remarks>
public sealed class WorkspaceContext : IDisposable
{
    private readonly Microsoft.CodeAnalysis.Workspace _workspace;
    private readonly IFileWriter _fileWriter;
    private readonly IDisposable? _analyzerAssemblyLoader;
    private readonly IReadOnlyList<AnalyzerFileStamp> _analyzerStamps;
    private readonly ImmutableDictionary<string, string> _msbuildProperties;
    private readonly FileBasedProgramInfo? _fileBasedProgram;
    private readonly WorkspaceOperationGate _gate = new();

    // The writer section (RunWriterAsync): one refactoring at a time reads the solution,
    // computes its change and commits it. Taken inside an operation lease, never the other
    // way round, and never waited for by a file update.
    private readonly SemaphoreSlim _writerLock = new(1, 1);

    // The running writer section (a fresh object per section; null when none runs), and the
    // section each async flow belongs to. A commit or UpdateSolution is accepted only from the
    // flow of the running section, so neither a caller outside one nor work an earlier section
    // left behind can race it.
    private object? _currentWriterSection;
    private readonly AsyncLocal<object?> _writerSectionOfFlow = new();

    // The snapshot the running writer section builds on: the solution when it started, moved
    // on by its own commits and UpdateSolution calls; null when no section runs. Touched only
    // by the section holding _writerLock.
    private Solution? _writerBase;

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

    /// <summary>
    /// Whether a <see cref="GeneratorLoadIssues"/> entry is about the project named
    /// <paramref name="projectName"/>: the messages name their project ("... for project 'X'").
    /// </summary>
    public static bool IssueConcernsProject(string issue, string projectName) =>
        issue.Contains($"project '{projectName}'", StringComparison.Ordinal);

    /// <summary>
    /// Diagnostics the workspace reported while loading (and while re-evaluating projects
    /// since). Empty for workspaces not loaded through MSBuild.
    /// </summary>
    public IReadOnlyCollection<WorkspaceDiagnostic> LoadDiagnostics { get; }

    internal WorkspaceContext(
        Microsoft.CodeAnalysis.Workspace workspace,
        Solution solution,
        string loadedPath,
        IFileWriter? fileWriter = null,
        IReadOnlyList<string>? generatorLoadIssues = null,
        IDisposable? analyzerAssemblyLoader = null,
        IReadOnlyDictionary<string, string>? msbuildProperties = null,
        FileBasedProgramInfo? fileBasedProgram = null,
        IReadOnlyCollection<WorkspaceDiagnostic>? loadDiagnostics = null)
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
        LoadDiagnostics = loadDiagnostics ?? Array.Empty<WorkspaceDiagnostic>();
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
    /// Gets a document by its file path. The path may be spelled any equivalent way: both
    /// sides are normalized and compared like <c>PathResolver.PathsEqual</c>, so 8.3
    /// short names, mixed separators, <c>..</c> segments and (on Windows) letter case match.
    /// </summary>
    /// <param name="filePath">Absolute path to the file.</param>
    /// <returns>Document if found, null otherwise.</returns>
    public Document? GetDocumentByPath(string filePath)
    {
        var normalizedPath = PathResolver.NormalizePath(filePath);
        return _solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => !string.IsNullOrEmpty(d.FilePath)
                && PathResolver.PathComparer.Equals(PathResolver.NormalizePath(d.FilePath), normalizedPath));
    }

    /// <summary>
    /// Updates the solution with a new snapshot. Only inside <see cref="RunWriterAsync{T}"/>,
    /// with a snapshot derived from <see cref="Solution"/> as read inside that section.
    /// </summary>
    /// <param name="newSolution">New solution snapshot.</param>
    /// <exception cref="InvalidOperationException">Called outside a writer section.</exception>
    /// <exception cref="RefactoringException">
    /// <see cref="ErrorCodes.WorkspaceBusy"/>: the solution is no longer the one the section
    /// started from (or last set).
    /// </exception>
    public void UpdateSolution(Solution newSolution)
    {
        EnsureWriterBaseIsCurrent(nameof(UpdateSolution));
        _solution = newSolution;
        _writerBase = newSolution;
    }

    /// <summary>
    /// Runs <paramref name="body"/> as the workspace's only writer: refactorings run their
    /// whole read-compute-commit here, so none builds on a snapshot another one is about to
    /// replace, or commits over another one's commit. Queries need not; they read immutable
    /// snapshots. <see cref="CommitChangesAsync(Solution, string?, CancellationToken)"/> and
    /// <see cref="UpdateSolution"/> only work inside a section.
    /// </summary>
    /// <remarks>
    /// A cached context's caller holds an operation lease while it waits here, which keeps
    /// file updates out until every waiting writer is done; so the body must never wait for
    /// a file update (<see cref="SyncDocumentFromDiskAsync"/> and
    /// <see cref="ReconcileProjectDocumentsAsync"/> refuse to run inside a section) or for
    /// another operation. Not re-entrant. The section ends, and the lock is released, when the
    /// body completes, throws or is cancelled.
    /// </remarks>
    public async Task<T> RunWriterAsync<T>(
        Func<CancellationToken, Task<T>> body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        ThrowIfDisposed();
        if (FlowRunsWriterSection())
            throw new InvalidOperationException(
                "This flow is already inside the workspace's writer section, which is not re-entrant.");

        await _writerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var section = new object();
        try
        {
            ThrowIfDisposed();
            _writerBase = _solution;
            Volatile.Write(ref _currentWriterSection, section);
            // Flows into the body (and whatever it starts); this method's caller keeps its own
            // value, since an async method restores the caller's context when it returns.
            _writerSectionOfFlow.Value = section;
            return await body(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _currentWriterSection, null);
            _writerBase = null;
            _writerLock.Release();
        }
    }

    /// <inheritdoc cref="RunWriterAsync{T}"/>
    public Task RunWriterAsync(
        Func<CancellationToken, Task> body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return RunWriterAsync<bool>(
            async ct =>
            {
                await body(ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    /// <summary>
    /// Fails unless the calling flow runs a writer section whose snapshot is still
    /// <see cref="Solution"/>. Inside a section nothing else replaces the solution, so a
    /// mismatch means a mutation bypassed the section; committing a change computed against
    /// the section's snapshot would then silently revert it.
    /// </summary>
    private void EnsureWriterBaseIsCurrent(string action)
    {
        if (!FlowRunsWriterSection() || _writerBase is null)
            throw new InvalidOperationException(
                $"{action} must run inside the workspace's writer section (RunWriterAsync).");

        if (!ReferenceEquals(_writerBase, _solution))
            throw new RefactoringException(
                ErrorCodes.WorkspaceBusy,
                "The workspace changed while this refactoring was computed, so it was not applied " +
                "(applying it would have reverted that change). Nothing was written; run it again.");
    }

    /// <summary>Whether the calling flow belongs to the writer section that is running now.</summary>
    private bool FlowRunsWriterSection()
    {
        var section = _writerSectionOfFlow.Value;
        return section != null && ReferenceEquals(section, Volatile.Read(ref _currentWriterSection));
    }

    /// <summary>
    /// A file update waits until no operation holds a lease, and a writer section runs inside
    /// one: waiting for an update from inside a section would never end.
    /// </summary>
    private void ThrowIfInWriterSection()
    {
        if (FlowRunsWriterSection())
            throw new InvalidOperationException(
                "A filesystem-driven update cannot run inside the workspace's writer section; it would wait for the section's own operation lease.");
    }

    /// <summary>
    /// For a standalone "file-based program" workspace, decides whether an external edit to
    /// its entry <c>.cs</c> file must trigger a full reload instead of an in-place text
    /// update. A change to the file's <c>#:</c> directives can alter the resolved project
    /// (references, SDK, properties), so the materialized project has to be regenerated; a
    /// code-only edit can still be applied incrementally. Always false for ordinary
    /// project/solution-backed workspaces.
    /// </summary>
    internal bool ShouldReloadOnSourceChange(string filePath, SourceText newText)
    {
        if (_fileBasedProgram is null) return false;
        if (!string.Equals(
                PathResolver.NormalizePath(filePath),
                _fileBasedProgram.EntryPath,
                StringComparison.OrdinalIgnoreCase))
            return false;

        var newSignature = FileBasedProgramProject.ComputeDirectiveSignature(newText.ToString());
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
    /// Completes once a filesystem-driven update (<see cref="SyncDocumentFromDiskAsync"/> or
    /// <see cref="ReconcileProjectDocumentsAsync"/>) is waiting for, or holding, its exclusive
    /// lease. For tests that order an update against an operation deterministically.
    /// </summary>
    internal Task WaitUntilFileUpdateQueuedAsync(CancellationToken cancellationToken)
        => _gate.WaitUntilFileUpdateQueuedAsync(cancellationToken);

    /// <summary>
    /// Brings the documents at <paramref name="filePath"/> in line with the file on disk.
    /// Called by the cache in response to filesystem change events.
    /// </summary>
    /// <returns>
    /// What the file and the solution looked like while the lease was held; see
    /// <see cref="DiskSyncOutcome"/>. The caller uses <see cref="DiskSyncOutcome.NotTracked"/>
    /// to tell "edit to a known file" apart from "a brand-new file appeared" (which needs a
    /// project re-evaluation so MSBuild can place it).
    /// </returns>
    /// <remarks>
    /// The file is decoded as the compiler would (see <see cref="SourceFileEncoding.Decode"/>),
    /// a file without byte order mark that is not UTF-8 in the code page its text was read in
    /// before. Text that only changed its encoding (a file converted from windows-1252 to
    /// UTF-8 without BOM) is taken over too, so the next commit writes it in the new one.
    /// The file is read only once the exclusive file-update lease is held. An operation writes
    /// its files and updates <see cref="Solution"/> within its own lease, so while this lease
    /// is held no operation is active and the disk holds every edit committed so far: the text
    /// applied here is never older than the solution's. Reading the file before waiting for the
    /// lease let an operation commit a newer edit in between, which the older text then
    /// overwrote — and the next operation wrote that older text back to disk. A document whose
    /// text already matches the disk is left alone, so its compilation stays cached. Once
    /// queued, the gate prioritises this update over newly arriving operations.
    /// </remarks>
    internal async Task<DiskSyncOutcome> SyncDocumentFromDiskAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfInWriterSection();

        await _gate.EnterFileUpdateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            var bytes = await TryReadFileAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (bytes == null)
                return DiskSyncOutcome.Missing;

            var normalized = PathResolver.NormalizePath(filePath);
            var sol = _solution;
            var tracked = sol.Projects
                .SelectMany(p => p.Documents)
                .Where(d => d.FilePath != null &&
                    PathResolver.PathComparer.Equals(PathResolver.NormalizePath(d.FilePath), normalized))
                .ToList();

            // A file that is not UTF-8 is read in the code page it was read in before (which
            // may be the project's), not in whatever the system's is.
            var readBefore = tracked.Count > 0
                ? (await tracked[0].GetTextAsync(cancellationToken).ConfigureAwait(false)).Encoding
                : null;
            var text = SourceFileEncoding.Decode(bytes, fallbackEncoding: readBefore);

            // A file-based program whose #: directives changed has to be rebuilt; leave the
            // solution as it is for the caller to invalidate.
            if (ShouldReloadOnSourceChange(filePath, text))
                return DiskSyncOutcome.ReloadRequired;

            if (tracked.Count == 0)
                return DiskSyncOutcome.NotTracked;

            var changed = false;
            foreach (var doc in tracked)
            {
                var current = await doc.GetTextAsync(cancellationToken).ConfigureAwait(false);
                if (current.ContentEquals(text) && !IsReencoded(current.Encoding, text.Encoding, bytes))
                    continue;

                sol = sol.WithDocumentText(doc.Id, text, PreservationMode.PreserveIdentity);
                changed = true;
            }

            if (!changed)
                return DiskSyncOutcome.Unchanged;

            _solution = sol;
            return DiskSyncOutcome.Updated;
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
    /// comparatively slow); only the snapshot edit is gated. Operations may create, move or
    /// delete files between the two, so the edit decides against the disk as it is under the
    /// lease: it adds only files it can read and removes only files that are gone. Throws if
    /// the evaluation fails, so the caller can fall back to a full reload.
    /// </remarks>
    internal async Task<bool> ReconcileProjectDocumentsAsync(
        string projectFilePath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfInWriterSection();

        // Evaluate outside the gate — an MSBuild evaluation must not block operations.
        var freshInfos = await EvaluateProjectAsync(projectFilePath, cancellationToken);
        if (freshInfos is not { Count: > 0 })
            return false;

        var normalizedProjectPath = PathResolver.NormalizePath(projectFilePath);

        await _gate.EnterFileUpdateAsync(cancellationToken);
        try
        {
            if (_disposed) return false;
            var sol = _solution;

            // Existing in-memory projects for this csproj. A multi-targeted project shares
            // its file path across one entry per target framework, matched 1:1 below.
            var candidates = sol.Projects
                .Where(p => p.FilePath != null &&
                    PathResolver.PathComparer.Equals(PathResolver.NormalizePath(p.FilePath), normalizedProjectPath))
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

                var freshDocs = new Dictionary<string, DocumentInfo>(PathResolver.PathComparer);
                foreach (var d in fresh.Documents)
                    if (d.FilePath != null) freshDocs[PathResolver.NormalizePath(d.FilePath)] = d;

                var existingDocs = new Dictionary<string, Document>(PathResolver.PathComparer);
                foreach (var d in existing.Documents)
                    if (d.FilePath != null) existingDocs[PathResolver.NormalizePath(d.FilePath)] = d;

                // Remove documents that are no longer part of the evaluated project, once their
                // file is gone from disk (checked now, under the lease). The evaluation ran
                // before the lease was taken, so a file an operation created or moved in between
                // — writing the file and the solution within its own lease — is missing from it
                // although the project compiles it. Removing it dropped it from the solution, for
                // good once its own watcher event had already found it tracked. Within a cache entry
                // the project files don't change (an edit to one invalidates the entry), so
                // membership only changes as files appear and disappear: a tracked file that
                // still exists is still a member.
                var toRemove = existingDocs
                    .Where(kv => !freshDocs.ContainsKey(kv.Key) && !File.Exists(kv.Value.FilePath))
                    .Select(kv => kv.Value.Id)
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
                    var bytes = await TryReadFileAsync(info.FilePath!, cancellationToken);
                    if (bytes == null) continue; // vanished again mid-reconcile; a later event settles it

                    // Decoded as MSBuild's loader would: in the project's code page if it sets one.
                    var text = SourceFileEncoding.Decode(
                        bytes, projectEncoding: (info.TextLoader as FileTextLoader)?.DefaultEncoding);
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
    /// The files MSBuild compiles in every target framework of <paramref name="projectFilePath"/>
    /// (normalized, compared with <c>PathResolver.PathComparer</c>), judged by
    /// re-evaluating the project as it is on disk now; null when this workspace cannot be
    /// re-evaluated (see <see cref="EvaluateProjectAsync"/>). Takes no lock, so an operation can
    /// call it while it holds its lease.
    /// </summary>
    internal async Task<IReadOnlySet<string>?> GetFilesCompiledByProjectAsync(
        string projectFilePath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var infos = await EvaluateProjectAsync(projectFilePath, cancellationToken);
        if (infos == null)
            return null;

        HashSet<string>? compiled = null;
        foreach (var info in infos)
        {
            var files = info.Documents
                .Select(d => d.FilePath)
                .OfType<string>()
                .Select(PathResolver.NormalizePath)
                .ToHashSet(PathResolver.PathComparer);
            if (compiled == null)
                compiled = files;
            else
                compiled.IntersectWith(files);
        }

        return compiled ?? new HashSet<string>(PathResolver.PathComparer);
    }

    /// <summary>
    /// Re-evaluates <paramref name="projectFilePath"/> with MSBuild (the design-time load the
    /// workspace itself used) and returns its project infos, one per target framework. Returns
    /// null when the workspace is not MSBuild-backed or the project file is not on disk.
    /// </summary>
    private async Task<IReadOnlyList<ProjectInfo>?> EvaluateProjectAsync(
        string projectFilePath,
        CancellationToken cancellationToken)
    {
        // Only an MSBuild-backed workspace can be re-evaluated. The framework-only fallback for
        // a standalone .cs file lives in an AdhocWorkspace with no project on disk, so there is
        // nothing to reconcile — its single document is kept current via ApplyExternalTextChangeAsync.
        if (_workspace is not MSBuildWorkspace)
            return null;

        // A standalone file-based program's wrapper .csproj is materialized only transiently and
        // deleted right after load; it has a fixed document set and no longer exists on disk, so
        // it can't (and needn't) be re-evaluated. Referenced real projects still reconcile normally.
        if (!File.Exists(projectFilePath))
            return null;

        var normalizedProjectPath = PathResolver.NormalizePath(projectFilePath);

        // No ProjectMap: it would mark this project "already known" and short-circuit the
        // load. LoadMetadataForReferencedProjects stays false (the default), so referenced
        // projects surface only as metadata and the result is just this project's info(s).
        var loader = new MSBuildProjectLoader(_workspace, _msbuildProperties);
        var evaluated = await loader.LoadProjectInfoAsync(
            projectFilePath, cancellationToken: cancellationToken);

        // A load surfaces referenced projects too; keep only the project we re-evaluated.
        return evaluated
            .Where(i => i.FilePath != null &&
                PathResolver.PathComparer.Equals(PathResolver.NormalizePath(i.FilePath), normalizedProjectPath))
            .ToList();
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
        var result = new HashSet<string>(PathResolver.PathComparer);
        string? deepestProjectFile = null;
        var deepestDirLen = -1;

        foreach (var project in _solution.Projects)
        {
            if (project.FilePath == null) continue;
            var projFile = PathResolver.NormalizePath(project.FilePath);

            foreach (var doc in project.Documents)
            {
                if (doc.FilePath != null &&
                    PathResolver.PathComparer.Equals(PathResolver.NormalizePath(doc.FilePath), normalized))
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

    /// <summary>
    /// A changed encoding of text whose content stayed the same: the file was converted, e.g.
    /// from windows-1252 to UTF-8 without BOM. Bytes that are all ASCII read the same in
    /// either, so they keep the encoding the text has (which may be the project's code page).
    /// An unknown encoding stays unknown: the commit then decides from the file.
    /// </summary>
    private static bool IsReencoded(Encoding? current, Encoding? decoded, byte[] bytes) =>
        current != null && decoded != null &&
        (current.CodePage != decoded.CodePage || current.Preamble.Length != decoded.Preamble.Length) &&
        bytes.AsSpan().IndexOfAnyInRange((byte)0x80, (byte)0xFF) >= 0;

    private static async Task<byte[]?> TryReadFileAsync(string filePath, CancellationToken ct)
    {
        const int maxAttempts = 4;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var bytes = new byte[fs.Length];
                await fs.ReadExactlyAsync(bytes, ct);
                return bytes;
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
        if (PathResolver.PathComparer.Equals(candidate, ancestor))
            return true;

        var sep = Path.DirectorySeparatorChar;
        var prefix = ancestor.Length > 0 && ancestor[^1] == sep ? ancestor : ancestor + sep;
        return candidate.StartsWith(prefix, PathResolver.PathComparison);
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
    /// Only inside <see cref="RunWriterAsync{T}"/>, which serializes commits; see the other
    /// overload. Files are written sequentially to avoid file locking issues.
    /// </remarks>
    public Task<CommitResult> CommitChangesAsync(
        Solution newSolution,
        CancellationToken cancellationToken = default) =>
        CommitChangesAsync(newSolution, newFilesEncodingFrom: null, cancellationToken);

    /// <summary>
    /// Commits all pending changes to the filesystem. A file is written in the encoding its
    /// text was decoded with (see <see cref="SourceFileEncoding.ForRewrite"/>), so the bytes
    /// of the text a change leaves alone stay as they were — a windows-1252 file stays
    /// windows-1252. A file the commit creates takes the encoding of an existing file: a
    /// document moved to a new path that of its old path, an added document that of
    /// <paramref name="newFilesEncodingFrom"/> (e.g. the file a type was split off from).
    /// Text a file's code page cannot represent is saved as UTF-8 with BOM instead, which
    /// <see cref="CommitResult.Warnings"/> reports. Encodings are read before anything is
    /// written or deleted; a file whose bytes are not valid in its own encoding fails the
    /// commit then, with nothing written.
    /// </summary>
    /// <param name="newSolution">
    /// Solution with changes to commit, derived from <see cref="Solution"/> as read inside the
    /// running writer section.
    /// </param>
    /// <param name="newFilesEncodingFrom">Existing file whose encoding added documents take.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// The change is everything that differs between <paramref name="newSolution"/> and the
    /// current solution, so it must be computed from the current solution: built on an older
    /// snapshot, it would write that snapshot's text over every edit committed since. Hence
    /// commits run only inside <see cref="RunWriterAsync{T}"/>, and one is refused when the
    /// solution is no longer the snapshot its section builds on.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Called outside a writer section.</exception>
    /// <exception cref="RefactoringException">
    /// <see cref="ErrorCodes.WorkspaceBusy"/>: the solution changed since the section started
    /// (or last committed); nothing was written.
    /// </exception>
    public async Task<CommitResult> CommitChangesAsync(
        Solution newSolution,
        string? newFilesEncodingFrom,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureWriterBaseIsCurrent(nameof(CommitChangesAsync));

        var filesModified = new List<string>();
        var filesCreated = new List<string>();
        var filesDeleted = new List<string>();
        var warnings = new List<string>();
        var writing = false;

        try
        {
            State = WorkspaceState.Operating;

            var changes = newSolution.GetChanges(_solution);

            // Collect all file operations first — their content and encoding included, so
            // every encoding is read before anything is written or deleted — then execute
            // them sequentially. This prevents interleaved writes to the same file from
            // different documents.
            var fileOperations = new List<(string FilePath, Func<Task> Operation, string Category)>();
            var written = new List<(DocumentId Id, Encoding Encoding)>();

            Encoding? sourceEncoding = null;
            var sourceEncodingRead = false;

            foreach (var projectChanges in changes.GetProjectChanges())
            {
                // Handle added documents. A file one replaces keeps its encoding; a new one
                // takes that of newFilesEncodingFrom, else UTF-8 with BOM.
                foreach (var docId in projectChanges.GetAddedDocuments())
                {
                    var doc = newSolution.GetDocument(docId);
                    if (doc?.FilePath == null) continue;

                    var filePath = doc.FilePath;
                    var content = (await doc.GetTextAsync(cancellationToken)).ToString();
                    if (!sourceEncodingRead && newFilesEncodingFrom != null)
                    {
                        sourceEncoding = await GetEncodingToKeepAsync(newFilesEncodingFrom, cancellationToken);
                        sourceEncodingRead = true;
                    }

                    var encoding = Representable(
                        filePath,
                        content,
                        GetEncodingOfReplacedFile(filePath) ?? sourceEncoding ?? SourceFileEncoding.Utf8WithBom,
                        warnings);
                    fileOperations.Add((filePath, () => _fileWriter.WriteAsync(filePath, content, encoding, cancellationToken), "created"));
                    written.Add((docId, encoding));
                    filesCreated.Add(filePath);
                }

                // Handle changed documents: each is written in the encoding its text was
                // decoded with, so the bytes of the text it did not change stay as they were.
                // One repointed at a new path (a renamed file) is written there as a new file,
                // in the encoding of the file at its old path.
                foreach (var docId in projectChanges.GetChangedDocuments())
                {
                    var doc = newSolution.GetDocument(docId);
                    if (doc?.FilePath == null) continue;

                    var filePath = doc.FilePath;
                    var text = await doc.GetTextAsync(cancellationToken);
                    var content = text.ToString();
                    var oldDoc = _solution.GetDocument(docId);
                    var decodedWith = (oldDoc == null ? null : (await oldDoc.GetTextAsync(cancellationToken)).Encoding)
                        ?? text.Encoding;
                    var oldPath = oldDoc?.FilePath;
                    var encodingFrom = oldPath != null && !PathResolver.PathsEqual(oldPath, filePath) ? oldPath : filePath;

                    var encoding = Representable(
                        filePath,
                        content,
                        _fileWriter.GetEncoding(encodingFrom, decodedWith) ?? decodedWith ?? SourceFileEncoding.Utf8WithBom,
                        warnings);
                    fileOperations.Add((filePath, () => _fileWriter.WriteAsync(filePath, content, encoding, cancellationToken), "modified"));
                    written.Add((docId, encoding));
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
            writing = true;
            foreach (var (_, operation, _) in fileOperations)
            {
                await operation();
            }

            var committed = await LabelEncodingsAsync(newSolution, written);
            _solution = committed;
            _writerBase = committed;
            State = WorkspaceState.Ready;

            return new CommitResult
            {
                Success = true,
                FilesModified = filesModified,
                FilesCreated = filesCreated,
                FilesDeleted = filesDeleted,
                Warnings = warnings
            };
        }
        catch (Exception ex)
        {
            // Failing before the first write (on a file whose bytes are not valid in its
            // encoding, say) leaves disk and solution as they were.
            State = writing ? WorkspaceState.Error : WorkspaceState.Ready;
            return new CommitResult
            {
                Success = false,
                FilesModified = filesModified,
                FilesCreated = filesCreated,
                FilesDeleted = filesDeleted,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// The encoding <paramref name="filePath"/> is kept in when rewritten, judged from its
    /// bytes and the encoding its document's text was decoded with; null when it does not exist.
    /// </summary>
    private async Task<Encoding?> GetEncodingToKeepAsync(string filePath, CancellationToken cancellationToken)
    {
        var document = GetDocumentByPath(filePath);
        var decodedWith = document == null ? null : (await document.GetTextAsync(cancellationToken)).Encoding;
        return _fileWriter.GetEncoding(filePath, decodedWith);
    }

    /// <summary>
    /// The encoding of an existing file an added document replaces, if any. Its text is
    /// replaced as a whole, so bytes that are invalid in its encoding don't stop the write.
    /// </summary>
    private Encoding? GetEncodingOfReplacedFile(string filePath)
    {
        try
        {
            return _fileWriter.GetEncoding(filePath);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="encoding"/> when it can represent <paramref name="content"/>; otherwise
    /// UTF-8 with BOM, which can, and a warning. Writing it in the code page would turn the
    /// characters it lacks into <c>?</c> or look-alikes.
    /// </summary>
    private static Encoding Representable(string filePath, string content, Encoding encoding, List<string> warnings)
    {
        var unencodable = SourceFileEncoding.FindUnencodable(content, encoding);
        if (unencodable == null)
            return encoding;

        warnings.Add(
            $"'{filePath}' was saved as UTF-8 with BOM instead of {SourceFileEncoding.Describe(encoding)}, " +
            $"which cannot represent {unencodable}.");
        return SourceFileEncoding.Utf8WithBom;
    }

    /// <summary>
    /// Labels the text of each written document with the encoding its file was written in,
    /// where it is not labelled so already (an operation built the text from a string, or the
    /// file was converted to UTF-8). The next commit writes the file in the encoding its text
    /// names, as a code page cannot be told from a file's bytes.
    /// </summary>
    /// <remarks>
    /// The files are written by now, so this runs to the end without a cancellation token:
    /// the solution must describe them.
    /// </remarks>
    private static async Task<Solution> LabelEncodingsAsync(
        Solution solution, List<(DocumentId Id, Encoding Encoding)> written)
    {
        foreach (var (id, encoding) in written)
        {
            var document = solution.GetDocument(id);
            if (document == null)
                continue;

            var text = await document.GetTextAsync(CancellationToken.None);
            if (text.Encoding != null &&
                text.Encoding.CodePage == encoding.CodePage &&
                text.Encoding.Preamble.Length == encoding.Preamble.Length)
            {
                continue;
            }

            solution = solution.WithDocumentText(
                id, SourceText.From(text.ToString(), encoding, text.ChecksumAlgorithm), PreservationMode.PreserveIdentity);
        }

        return solution;
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
        // _writerLock is left to the GC: it never allocates a wait handle, and a section still
        // running (on an uncached context disposed under it) must be able to release it.
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
/// Outcome of <see cref="WorkspaceContext.SyncDocumentFromDiskAsync"/>.
/// </summary>
internal enum DiskSyncOutcome
{
    /// <summary>The file is tracked and differed from the solution, which now holds its text.</summary>
    Updated,

    /// <summary>The file is tracked and the solution already held its text; nothing changed.</summary>
    Unchanged,

    /// <summary>The file is on disk, but no document in the solution has its path.</summary>
    NotTracked,

    /// <summary>The file is absent (or stayed unreadable); the solution is untouched.</summary>
    Missing,

    /// <summary>
    /// A file-based program's <c>#:</c> directives changed, so the workspace has to be rebuilt;
    /// the solution is untouched.
    /// </summary>
    ReloadRequired,
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
    /// What the user should know about a successful commit: e.g. a file saved as UTF-8
    /// because its code page cannot represent the new text.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Error message if commit failed.
    /// </summary>
    public string? Error { get; init; }
}
