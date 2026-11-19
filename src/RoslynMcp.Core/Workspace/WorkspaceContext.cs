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
/// When obtained from <see cref="WorkspaceCache"/>, each caller gets a handle of its own
/// on the cached workspace: its <see cref="Dispose"/> releases that caller's lease, once,
/// and the cache owns the actual workspace lifetime. A disposed handle throws
/// <see cref="ObjectDisposedException"/> when used. Concurrent read access to
/// <see cref="Solution"/> is safe. Mutations are serialized two ways: refactorings run as
/// the single writer (<see cref="RunWriterAsync{T}"/>), and file-watcher updates take the
/// gate's exclusive file-update lease, which no operation holds at the same time.
/// </remarks>
public sealed class WorkspaceContext : IDisposable
{
    // Everything this context gives access to. Shared by all handles of a cached workspace
    // (CreateLeaseHandle), so it is the same workspace whichever handle a caller holds.
    private readonly Session _s;

    // The operation lease this handle holds on a cached workspace; null for the context a
    // loader created, which owns the session.
    private readonly CacheLease? _lease;

    /// <summary>
    /// Current solution snapshot.
    /// </summary>
    /// <exception cref="ObjectDisposedException">This handle on a cached workspace was disposed.</exception>
    public Solution Solution
    {
        get
        {
            ThrowIfReleased();
            return _s.Solution;
        }
    }

    /// <summary>
    /// The underlying Roslyn workspace.
    /// </summary>
    /// <exception cref="ObjectDisposedException">This handle on a cached workspace was disposed.</exception>
    public Microsoft.CodeAnalysis.Workspace Workspace
    {
        get
        {
            ThrowIfReleased();
            return _s.Workspace;
        }
    }

    /// <summary>
    /// Path to the loaded solution or project.
    /// </summary>
    public string LoadedPath => _s.LoadedPath;

    /// <summary>
    /// Current workspace state.
    /// </summary>
    public WorkspaceState State
    {
        get => _s.State;
        private set => _s.State = value;
    }

    /// <summary>
    /// Issues encountered while loading the workspace that affect downstream
    /// analysis but are not themselves fatal — most notably source generators
    /// (e.g. the Razor source generator) that failed to load and were stripped
    /// from analyzer references. Tools may surface these as synthetic info-level
    /// diagnostics so callers can tell apart "no diagnostics" from "the
    /// generator that would have produced them never ran".
    /// </summary>
    public IReadOnlyList<string> GeneratorLoadIssues => _s.GeneratorLoadIssues;

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
    public IReadOnlyCollection<WorkspaceDiagnostic> LoadDiagnostics => _s.LoadDiagnostics;

    internal WorkspaceContext(
        Microsoft.CodeAnalysis.Workspace workspace,
        Solution solution,
        string loadedPath,
        IFileWriter? fileWriter = null,
        IReadOnlyList<string>? generatorLoadIssues = null,
        IDisposable? analyzerAssemblyLoader = null,
        IReadOnlyDictionary<string, string>? msbuildProperties = null,
        FileBasedProgramInfo? fileBasedProgram = null,
        IReadOnlyCollection<WorkspaceDiagnostic>? loadDiagnostics = null,
        IReadOnlyDictionary<ProjectId, Encoding>? declaredEncodings = null)
    {
        _s = new Session(
            workspace,
            solution,
            loadedPath,
            fileWriter ?? new AtomicFileWriter(),
            generatorLoadIssues ?? Array.Empty<string>(),
            analyzerAssemblyLoader,
            msbuildProperties?.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase)
                ?? ImmutableDictionary<string, string>.Empty,
            fileBasedProgram,
            loadDiagnostics ?? Array.Empty<WorkspaceDiagnostic>(),
            declaredEncodings?.ToImmutableDictionary() ?? ImmutableDictionary<ProjectId, Encoding>.Empty);
    }

    /// <summary>A handle on the session of a cached workspace, holding one operation lease.</summary>
    private WorkspaceContext(Session session, CacheLease lease)
    {
        _s = session;
        _lease = lease;
    }

    /// <summary>
    /// The state of one workspace, shared by every handle on it. Its fields are guarded as
    /// documented on <see cref="WorkspaceContext"/>'s members that use them: the solution and
    /// the writer fields by the writer section and the file-update lease, the rest is set
    /// once.
    /// </summary>
    private sealed class Session(
        Microsoft.CodeAnalysis.Workspace workspace,
        Solution solution,
        string loadedPath,
        IFileWriter fileWriter,
        IReadOnlyList<string> generatorLoadIssues,
        IDisposable? analyzerAssemblyLoader,
        ImmutableDictionary<string, string> msbuildProperties,
        FileBasedProgramInfo? fileBasedProgram,
        IReadOnlyCollection<WorkspaceDiagnostic> loadDiagnostics,
        ImmutableDictionary<ProjectId, Encoding> declaredEncodings)
    {
        public readonly Microsoft.CodeAnalysis.Workspace Workspace = workspace;

        // The encoding each project declares for its files without byte order mark (MSBuild's
        // CodePage), as captured at load (ProjectFileConventions.InferDeclaredEncodingsAsync);
        // a project that declares none is missing. Projects don't change within a workspace.
        public readonly ImmutableDictionary<ProjectId, Encoding> DeclaredEncodings = declaredEncodings;
        public readonly string LoadedPath = loadedPath;
        public readonly IFileWriter FileWriter = fileWriter;
        public readonly IReadOnlyList<string> GeneratorLoadIssues = generatorLoadIssues;
        public readonly IReadOnlyCollection<WorkspaceDiagnostic> LoadDiagnostics = loadDiagnostics;
        public readonly IDisposable? AnalyzerAssemblyLoader = analyzerAssemblyLoader;
        public readonly IReadOnlyList<AnalyzerFileStamp> AnalyzerStamps = CaptureMutableAnalyzerStamps(solution);
        public readonly ImmutableDictionary<string, string> MsBuildProperties = msbuildProperties;
        public readonly FileBasedProgramInfo? FileBasedProgram = fileBasedProgram;
        public readonly WorkspaceOperationGate Gate = new();

        // The writer section (RunWriterAsync): one refactoring at a time reads the solution,
        // computes its change and commits it, across every context sharing these writers (a
        // cache shares its own among all its contexts; ShareWriters). Taken inside an operation
        // lease, never the other way round, and never waited for by a file update. Replaced only
        // before the context is handed to anyone.
        public WorkspaceWriteCoordinator Writers = new();

        // The sequence number of Writers up to which this session holds every recorded write:
        // its own, and those of the other contexts, taken over from disk when a section starts.
        // Touched only by this session's running section (and before the context is shared).
        public long WritesSeen;

        // The snapshot the running writer section builds on: the solution when it started, moved
        // on by its own commits and UpdateSolution calls; null when no section of this session
        // runs. A commit or UpdateSolution is accepted only from the flow of the running section
        // while it is set, so neither a caller outside one, nor work an earlier section left
        // behind, nor a section of another context can race it. Touched only by that section.
        public Solution? WriterBase;

        public Solution Solution = solution;
        public WorkspaceState State = WorkspaceState.Ready;
        public bool Disposed;
        public bool CacheOwned;
    }

    /// <summary>
    /// One operation lease on a cached workspace, released at most once however often the
    /// handle holding it is disposed: a second release would end a lease another caller holds.
    /// </summary>
    private sealed class CacheLease(WorkspaceOperationGate gate, Action onReleased)
    {
        private int _released;

        public bool IsReleased => Volatile.Read(ref _released) != 0;

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            // The operation lease is exited first so any queued file update can start
            // immediately; then the cache updates its ref count and last-access time.
            gate.ExitOperation();
            onReleased();
        }
    }

    /// <summary>
    /// Creates a type symbol resolver for this workspace.
    /// </summary>
    public TypeSymbolResolver CreateSymbolResolver()
    {
        ThrowIfReleased();
        return new(this);
    }

    /// <summary>
    /// Creates a general-purpose symbol resolver that can find any symbol by position or name.
    /// </summary>
    public SymbolResolver CreateGeneralSymbolResolver()
    {
        ThrowIfReleased();
        return new(this);
    }

    /// <summary>
    /// Creates a reference tracker for this workspace.
    /// </summary>
    public ReferenceTracker CreateReferenceTracker()
    {
        ThrowIfReleased();
        return new(this);
    }

    /// <summary>
    /// Gets a document by its file path. The path may be spelled any equivalent way: both
    /// sides are normalized and compared like <c>PathResolver.PathsEqual</c>, so 8.3
    /// short names, mixed separators, <c>..</c> segments and (on Windows) letter case match.
    /// </summary>
    /// <param name="filePath">Absolute path to the file.</param>
    /// <returns>Document if found, null otherwise.</returns>
    public Document? GetDocumentByPath(string filePath)
    {
        ThrowIfReleased();
        var normalizedPath = PathResolver.NormalizePath(filePath);
        return _s.Solution.Projects
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
        ThrowIfDisposed();
        EnsureWriterBaseIsCurrent(nameof(UpdateSolution));

        // Moves the section made on disk before repointing documents (a file moved to the folder
        // of its new namespace) are news for the other contexts sharing the writers.
        _s.Writers.RecordWrites(GetMovedFiles(_s.Solution, newSolution));
        _s.Solution = newSolution;
        _s.WriterBase = newSolution;
    }

    /// <summary>
    /// Runs <paramref name="body"/> as the workspace's only writer: refactorings run their
    /// whole read-compute-commit here, so none builds on a snapshot another one is about to
    /// replace, or commits over another one's commit. Queries need not; they read immutable
    /// snapshots. <see cref="CommitChangesAsync(Solution, string?, CancellationToken)"/> and
    /// <see cref="UpdateSolution"/> only work inside a section.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The section is shared with every context over possibly the same files (all contexts of
    /// one <see cref="WorkspaceCache"/>, e.g. a <c>.slnx</c> and a <c>.csproj</c> of one tree),
    /// so their refactorings run one after another too. Before the body runs, the section takes
    /// over from disk every file such a context wrote since this one last looked: its text, and
    /// for a file created, deleted or moved, the project's evaluated documents (see
    /// <see cref="WorkspaceWriteCoordinator"/>). The body thus never computes on text another
    /// context has replaced, though this context's file watcher may not have caught up yet. When
    /// nothing was written elsewhere, that costs nothing.
    /// </para>
    /// <para>
    /// A cached context's caller holds an operation lease while it waits here, which keeps
    /// file updates out until every waiting writer is done; so the body must never wait for
    /// a file update (<see cref="SyncDocumentFromDiskAsync"/> and
    /// <see cref="ReconcileProjectDocumentsAsync"/> refuse to run inside a section) or for
    /// another operation. Not re-entrant, also not through another context. The section ends,
    /// and the lock is released, when the body completes, throws or is cancelled.
    /// </para>
    /// </remarks>
    /// <exception cref="RefactoringException">
    /// <see cref="ErrorCodes.WorkspaceBusy"/>: another context created or deleted files, and
    /// MSBuild could not re-evaluate a project of this one to take that over; the body did not run.
    /// </exception>
    public async Task<T> RunWriterAsync<T>(
        Func<CancellationToken, Task<T>> body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        ThrowIfDisposed();

        var writers = _s.Writers;
        return await writers.RunSectionAsync(
            async ct =>
            {
                ThrowIfDisposed();
                await TakeOverOtherWritesAsync(writers, ct).ConfigureAwait(false);

                _s.WriterBase = _s.Solution;
                try
                {
                    var result = await body(ct).ConfigureAwait(false);

                    // The section's own writes are in the solution now. (After a failure they
                    // may not all be, so the next section takes them over from disk too.)
                    _s.WritesSeen = writers.Sequence;
                    return result;
                }
                finally
                {
                    _s.WriterBase = null;
                }
            },
            cancellationToken).ConfigureAwait(false);
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
        // WriterBase is set only while a section of this session runs, and only one section of
        // the shared writers runs at a time: a flow of the running section with WriterBase set
        // is a flow of this session's section.
        if (!_s.Writers.FlowRunsSection() || _s.WriterBase is null)
            throw new InvalidOperationException(
                $"{action} must run inside the workspace's writer section (RunWriterAsync).");

        if (!ReferenceEquals(_s.WriterBase, _s.Solution))
            throw new RefactoringException(
                ErrorCodes.WorkspaceBusy,
                "The workspace changed while this refactoring was computed, so it was not applied " +
                "(applying it would have reverted that change). Nothing was written; run it again.");
    }

    /// <summary>
    /// A file update waits until no operation holds a lease, and a writer section runs inside
    /// one: waiting for an update from inside a section would never end. Nor may a section of
    /// another context sharing the writers wait for one, whose operation may be waiting for
    /// that section.
    /// </summary>
    private void ThrowIfInWriterSection()
    {
        if (_s.Writers.FlowRunsSection())
            throw new InvalidOperationException(
                "A filesystem-driven update cannot run inside the workspace's writer section; it would wait for the section's own operation lease.");
    }

    /// <summary>
    /// Shares <paramref name="writers"/> with the other contexts of a cache: their refactorings
    /// then run one at a time with this context's, and this one takes over what they write (see
    /// <see cref="RunWriterAsync{T}"/>). <paramref name="writesSeen"/> is the
    /// <see cref="WorkspaceWriteCoordinator.Sequence"/> read before this workspace was loaded:
    /// every write after it may be missing from what the load read, and is taken over when the
    /// first section starts. Only before the context is handed to anyone.
    /// </summary>
    internal void ShareWriters(WorkspaceWriteCoordinator writers, long writesSeen)
    {
        ArgumentNullException.ThrowIfNull(writers);
        if (_lease != null || _s.WriterBase != null)
            throw new InvalidOperationException("Writers can be shared only before the context is in use.");

        _s.Writers = writers;
        _s.WritesSeen = writesSeen;
    }

    /// <summary>
    /// Brings the solution in line with the disk for every file another context sharing
    /// <paramref name="writers"/> wrote since this session last looked: a tracked file gets the
    /// text on disk; a file created, deleted or moved has its project re-evaluated with MSBuild,
    /// whose documents are then added and removed as <see cref="ReconcileProjectDocumentsAsync"/>
    /// does. Runs at the start of a section: the section's lock keeps every other writer out, and
    /// a cached context's operation lease keeps its file updates out, so the disk read here is
    /// the disk the section computes on. Changes nothing (and keeps <see cref="Session.WritesSeen"/>)
    /// when it fails.
    /// </summary>
    private async Task TakeOverOtherWritesAsync(WorkspaceWriteCoordinator writers, CancellationToken cancellationToken)
    {
        var (through, written) = writers.GetWrittenSince(_s.WritesSeen);
        if (written.Count == 0)
        {
            _s.WritesSeen = through;
            return;
        }

        var sol = _s.Solution;
        var documentsByPath = IndexDocumentsByPath(sol);
        var toEvaluate = new List<string>();
        foreach (var (path, structural) in written)
        {
            documentsByPath.TryGetValue(path, out var tracked);
            var bytes = await TryReadFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (bytes == null)
            {
                // Deleted or moved away: the project decides whether the document goes.
                if (tracked != null)
                    toEvaluate.Add(path);
                continue;
            }

            if (tracked == null)
            {
                // Created or moved here: the project decides whether it compiles the file. A file
                // the project left out before and that was only edited stays out.
                if (structural)
                    toEvaluate.Add(path);
                continue;
            }

            var text = await DecodeFromDiskAsync(tracked, bytes, cancellationToken).ConfigureAwait(false);
            sol = await WithDiskTextAsync(sol, tracked, text, bytes, cancellationToken).ConfigureAwait(false);
        }

        var projectFiles = toEvaluate
            .SelectMany(path => FindProjectFilesForPath(sol, path))
            .Distinct(PathResolver.PathComparer);
        foreach (var projectFile in projectFiles)
        {
            try
            {
                var evaluated = await EvaluateProjectAsync(projectFile, cancellationToken).ConfigureAwait(false);
                if (evaluated is { Count: > 0 })
                {
                    sol = await WithEvaluatedDocumentsAsync(sol, projectFile, evaluated, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new RefactoringException(
                    ErrorCodes.WorkspaceBusy,
                    $"A refactoring through another workspace over the same files created or deleted files of " +
                    $"'{projectFile}', and MSBuild could not re-evaluate it to take that over ({ex.Message}). " +
                    "Nothing was written; run it again.",
                    ex);
            }
        }

        _s.Solution = sol;
        _s.WritesSeen = through;
    }

    /// <summary>
    /// The files a solution change moves, adds or removes documents at (old and new paths), as
    /// structural writes for <see cref="WorkspaceWriteCoordinator.RecordWrites"/>. Text changes
    /// are left out: <see cref="UpdateSolution"/> writes no text to disk.
    /// </summary>
    private static List<(string Path, bool Structural)> GetMovedFiles(Solution before, Solution after)
    {
        var files = new List<(string Path, bool Structural)>();
        foreach (var projectChanges in after.GetChanges(before).GetProjectChanges())
        {
            foreach (var id in projectChanges.GetAddedDocuments())
                AddPath(after.GetDocument(id)?.FilePath);
            foreach (var id in projectChanges.GetRemovedDocuments())
                AddPath(before.GetDocument(id)?.FilePath);
            foreach (var id in projectChanges.GetChangedDocuments())
            {
                var oldPath = before.GetDocument(id)?.FilePath;
                var newPath = after.GetDocument(id)?.FilePath;
                if (!PathResolver.PathsEqual(oldPath, newPath))
                {
                    AddPath(oldPath);
                    AddPath(newPath);
                }
            }
        }

        return files;

        void AddPath(string? path)
        {
            if (!string.IsNullOrEmpty(path))
                files.Add((path, true));
        }
    }

    /// <summary>The documents of <paramref name="solution"/> by their normalized file path.</summary>
    private static Dictionary<string, List<Document>> IndexDocumentsByPath(Solution solution)
    {
        var index = new Dictionary<string, List<Document>>(PathResolver.PathComparer);
        foreach (var document in solution.Projects.SelectMany(p => p.Documents))
        {
            if (string.IsNullOrEmpty(document.FilePath))
                continue;

            var path = PathResolver.NormalizePath(document.FilePath);
            if (!index.TryGetValue(path, out var documents))
                index[path] = documents = [];
            documents.Add(document);
        }

        return index;
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
        if (_s.FileBasedProgram is null) return false;
        if (!string.Equals(
                PathResolver.NormalizePath(filePath),
                _s.FileBasedProgram.EntryPath,
                StringComparison.OrdinalIgnoreCase))
            return false;

        var newSignature = FileBasedProgramProject.ComputeDirectiveSignature(newText.ToString());
        return !string.Equals(newSignature, _s.FileBasedProgram.DirectiveSignature, StringComparison.Ordinal);
    }

    /// <summary>
    /// Marks this context as owned by a cache. After this call, the cache hands out
    /// <see cref="CreateLeaseHandle"/>s instead of this context, this context's
    /// <see cref="Dispose"/> does nothing, and the cache disposes the workspace via
    /// <see cref="DisposeOwned"/>.
    /// </summary>
    internal void MarkCacheOwned()
    {
        if (_lease != null)
            throw new InvalidOperationException("A lease handle cannot be made cache-owned; its session already is.");
        _s.CacheOwned = true;
    }

    /// <summary>
    /// A handle on this cache-owned workspace for one caller, holding the operation lease the
    /// caller has just taken (<see cref="EnterOperationAsync"/>). Disposing the handle exits
    /// that lease and then calls <paramref name="onReleased"/> — once, however often it is
    /// disposed, so a caller that disposes twice cannot end another caller's lease.
    /// </summary>
    internal WorkspaceContext CreateLeaseHandle(Action onReleased)
    {
        if (!_s.CacheOwned)
            throw new InvalidOperationException("Only a cache-owned workspace hands out lease handles.");
        return new WorkspaceContext(_s, new CacheLease(_s.Gate, onReleased));
    }

    /// <summary>
    /// Takes a shared operation lease. All MCP tool invocations run while
    /// holding a lease, so filesystem-driven updates can't mutate the solution
    /// under them. Multiple leases may be held simultaneously; a lease handle
    /// (<see cref="CreateLeaseHandle"/>) exits its lease when it is disposed.
    /// </summary>
    internal Task EnterOperationAsync(CancellationToken cancellationToken)
        => _s.Gate.EnterOperationAsync(cancellationToken);

    /// <summary>
    /// Releases an operation lease previously obtained via
    /// <see cref="EnterOperationAsync"/>.
    /// </summary>
    internal void ExitOperation() => _s.Gate.ExitOperation();

    /// <summary>
    /// Completes once a filesystem-driven update (<see cref="SyncDocumentFromDiskAsync"/> or
    /// <see cref="ReconcileProjectDocumentsAsync"/>) is waiting for, or holding, its exclusive
    /// lease. For tests that order an update against an operation deterministically.
    /// </summary>
    internal Task WaitUntilFileUpdateQueuedAsync(CancellationToken cancellationToken)
        => _s.Gate.WaitUntilFileUpdateQueuedAsync(cancellationToken);

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
        // A file update takes no operation lease, so a released handle may start one too.
        ObjectDisposedException.ThrowIf(_s.Disposed, this);
        ThrowIfInWriterSection();

        await _s.Gate.EnterFileUpdateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A handle may be released by now (an update needs no lease); the workspace must still be there.
            ObjectDisposedException.ThrowIf(_s.Disposed, this);

            var bytes = await TryReadFileAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (bytes == null)
                return DiskSyncOutcome.Missing;

            var normalized = PathResolver.NormalizePath(filePath);
            var sol = _s.Solution;
            var tracked = sol.Projects
                .SelectMany(p => p.Documents)
                .Where(d => d.FilePath != null &&
                    PathResolver.PathComparer.Equals(PathResolver.NormalizePath(d.FilePath), normalized))
                .ToList();

            var text = await DecodeFromDiskAsync(tracked, bytes, cancellationToken).ConfigureAwait(false);

            // A file-based program whose #: directives changed has to be rebuilt; leave the
            // solution as it is for the caller to invalidate.
            if (ShouldReloadOnSourceChange(filePath, text))
                return DiskSyncOutcome.ReloadRequired;

            if (tracked.Count == 0)
                return DiskSyncOutcome.NotTracked;

            var updated = await WithDiskTextAsync(sol, tracked, text, bytes, cancellationToken).ConfigureAwait(false);
            if (ReferenceEquals(updated, sol))
                return DiskSyncOutcome.Unchanged;

            _s.Solution = updated;
            return DiskSyncOutcome.Updated;
        }
        finally
        {
            _s.Gate.ExitFileUpdate();
        }
    }

    /// <summary>
    /// How much earlier than the start of a load a file's last-write time may lie and still count
    /// as "changed during the load" for <see cref="SyncFilesChangedSinceAsync"/>: file systems
    /// that store times coarsely (FAT: 2 s) or a clock that differs from the file server's. A
    /// file changed shortly before the load is read once more and found unchanged.
    /// </summary>
    private static readonly TimeSpan LoadClockTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Brings the documents whose file was written at or after <paramref name="sinceUtc"/>, the
    /// start of this workspace's load, in line with the disk; called once the file watchers run.
    /// A file changed after MSBuild (or the compilation) read it, but before the watchers
    /// started, raises no event, so without this the workspace kept the older text until the
    /// file changed again, and the next refactoring wrote it back. Takes the file-update lease;
    /// each recent file is read and applied as <see cref="SyncDocumentFromDiskAsync"/> does,
    /// files not written since the load are only stat'ed.
    /// </summary>
    /// <returns>
    /// The tracked files that are gone (whose projects need a re-evaluation, as for a deletion the
    /// watcher reports), and whether a file-based program's <c>#:</c> directives changed (the
    /// workspace must be rebuilt; that file's text is left as it was).
    /// </returns>
    /// <remarks>
    /// The last-write time decides, so an edit that sets an older time (a copy that keeps the
    /// source's time) is not seen, nor is a file created during the load that MSBuild's
    /// evaluation missed.
    /// </remarks>
    internal async Task<ChangedDuringLoad> SyncFilesChangedSinceAsync(
        DateTime sinceUtc,
        CancellationToken cancellationToken = default)
    {
        // A file update takes no operation lease, so a released handle may start one too.
        ObjectDisposedException.ThrowIf(_s.Disposed, this);
        ThrowIfInWriterSection();

        await _s.Gate.EnterFileUpdateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A handle may be released by now (an update needs no lease); the workspace must still be there.
            ObjectDisposedException.ThrowIf(_s.Disposed, this);

            var threshold = sinceUtc - LoadClockTolerance;
            var sol = _s.Solution;
            var missing = new List<string>();
            var updated = 0;
            var reloadRequired = false;
            foreach (var (path, tracked) in IndexDocumentsByPath(sol))
            {
                var file = new FileInfo(path);
                if (file.Exists && file.LastWriteTimeUtc < threshold)
                    continue;

                var bytes = file.Exists ? await TryReadFileAsync(path, cancellationToken).ConfigureAwait(false) : null;
                if (bytes == null)
                {
                    missing.Add(path);
                    continue;
                }

                var text = await DecodeFromDiskAsync(tracked, bytes, cancellationToken).ConfigureAwait(false);
                if (ShouldReloadOnSourceChange(path, text))
                {
                    reloadRequired = true;
                    continue;
                }

                var next = await WithDiskTextAsync(sol, tracked, text, bytes, cancellationToken).ConfigureAwait(false);
                if (!ReferenceEquals(next, sol))
                {
                    sol = next;
                    updated++;
                }
            }

            _s.Solution = sol;
            return new ChangedDuringLoad(updated, missing, reloadRequired);
        }
        finally
        {
            _s.Gate.ExitFileUpdate();
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
        // A file update takes no operation lease, so a released handle may start one too.
        ObjectDisposedException.ThrowIf(_s.Disposed, this);
        ThrowIfInWriterSection();

        // Evaluate outside the gate — an MSBuild evaluation must not block operations.
        var freshInfos = await EvaluateProjectAsync(projectFilePath, cancellationToken);
        if (freshInfos is not { Count: > 0 })
            return false;

        await _s.Gate.EnterFileUpdateAsync(cancellationToken);
        try
        {
            if (_s.Disposed) return false;
            var sol = _s.Solution;
            var updated = await WithEvaluatedDocumentsAsync(sol, projectFilePath, freshInfos, cancellationToken);
            if (ReferenceEquals(updated, sol))
                return false;

            _s.Solution = updated;
            return true;
        }
        finally
        {
            _s.Gate.ExitFileUpdate();
        }
    }

    /// <summary>
    /// <paramref name="solution"/> with the documents of <paramref name="projectFilePath"/>
    /// brought in line with <paramref name="freshInfos"/>, its re-evaluation (one per target
    /// framework): documents the evaluation newly compiles are added, and documents it no longer
    /// compiles are removed once their file is gone from disk. The same instance when nothing
    /// changes. The caller holds what keeps the other writers out (the file-update lease, or the
    /// writer section), so the disk checked here is the disk the result is applied to.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A re-evaluated project does not map to exactly one loaded project (e.g. a target framework
    /// was added or removed); the caller falls back to a full reload.
    /// </exception>
    private static async Task<Solution> WithEvaluatedDocumentsAsync(
        Solution solution,
        string projectFilePath,
        IReadOnlyList<ProjectInfo> freshInfos,
        CancellationToken cancellationToken)
    {
        var normalizedProjectPath = PathResolver.NormalizePath(projectFilePath);
        var sol = solution;

        // Existing in-memory projects for this csproj. A multi-targeted project shares
        // its file path across one entry per target framework, matched 1:1 below.
        var candidates = sol.Projects
            .Where(p => p.FilePath != null &&
                PathResolver.PathComparer.Equals(PathResolver.NormalizePath(p.FilePath), normalizedProjectPath))
            .ToList();

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
            // file is gone from disk (checked now, while the other writers are kept out). The
            // evaluation may have run before that, so a file an operation created or moved in
            // between — writing the file and the solution within its own lease — is missing from
            // it although the project compiles it. Removing it dropped it from the solution, for
            // good once its own watcher event had already found it tracked. Within a cache entry
            // the project files don't change (an edit to one invalidates the entry), so
            // membership only changes as files appear and disappear: a tracked file that
            // still exists is still a member.
            var toRemove = existingDocs
                .Where(kv => !freshDocs.ContainsKey(kv.Key) && !File.Exists(kv.Value.FilePath))
                .Select(kv => kv.Value.Id)
                .ToImmutableArray();
            if (toRemove.Length > 0)
                sol = sol.RemoveDocuments(toRemove);

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
            }
        }

        return sol;
    }

    /// <summary>
    /// The text of a file read as <paramref name="bytes"/>, decoded as the compiler would (see
    /// <see cref="SourceFileEncoding.Decode"/>). A file without byte order mark is read in the
    /// encoding its project declares (<c>CodePage</c>, see <see cref="Session.DeclaredEncodings"/>)
    /// even when its bytes are valid UTF-8, as the compiler and MSBuild's loader read it; in a
    /// project that declares none, a file that is not UTF-8 is read in the code page the
    /// documents at its path were read in before, not in whatever the system's is.
    /// </summary>
    private async Task<SourceText> DecodeFromDiskAsync(
        IReadOnlyList<Document> tracked, byte[] bytes, CancellationToken cancellationToken)
    {
        if (tracked.Count == 0)
            return SourceFileEncoding.Decode(bytes);

        var readBefore = (await tracked[0].GetTextAsync(cancellationToken).ConfigureAwait(false)).Encoding;
        return SourceFileEncoding.Decode(
            bytes,
            projectEncoding: _s.DeclaredEncodings.GetValueOrDefault(tracked[0].Project.Id),
            fallbackEncoding: readBefore);
    }

    /// <summary>
    /// <paramref name="solution"/> with <paramref name="text"/>, the file's text on disk, as the
    /// text of every document in <paramref name="tracked"/> that differs from it (or that only
    /// changed its encoding); the same instance when none does, so compilations stay cached.
    /// </summary>
    private static async Task<Solution> WithDiskTextAsync(
        Solution solution,
        IReadOnlyList<Document> tracked,
        SourceText text,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        foreach (var doc in tracked)
        {
            var current = await doc.GetTextAsync(cancellationToken).ConfigureAwait(false);
            if (current.ContentEquals(text) && !IsReencoded(current.Encoding, text.Encoding, bytes))
                continue;

            solution = solution.WithDocumentText(doc.Id, text, PreservationMode.PreserveIdentity);
        }

        return solution;
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
        if (_s.Workspace is not MSBuildWorkspace)
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
        var loader = new MSBuildProjectLoader(_s.Workspace, _s.MsBuildProperties);
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
    internal IReadOnlyCollection<string> FindProjectFilesForPath(string filePath) =>
        FindProjectFilesForPath(_s.Solution, filePath);

    /// <inheritdoc cref="FindProjectFilesForPath(string)"/>
    private static IReadOnlyCollection<string> FindProjectFilesForPath(Solution solution, string filePath)
    {
        var normalized = PathResolver.NormalizePath(filePath);
        var result = new HashSet<string>(PathResolver.PathComparer);
        string? deepestProjectFile = null;
        var deepestDirLen = -1;

        foreach (var project in solution.Projects)
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

        foreach (var project in _s.Solution.Projects)
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
    internal bool AnalyzerReferencesChangedOnDisk() => StampsChanged(_s.AnalyzerStamps);

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
    /// windows-1252. A document moved to a new path keeps the encoding of its old file. An
    /// added document takes the <c>.editorconfig</c> <c>charset</c> of its path, else the
    /// encoding of <paramref name="newFilesEncodingFrom"/> (e.g. the file a type was split off
    /// from), else the encoding most of its project's files are in, else UTF-8 with BOM (see
    /// <see cref="NewFileEncodings"/>).
    /// Text a file's code page cannot represent is saved as UTF-8 with BOM instead, which
    /// <see cref="CommitResult.Warnings"/> reports. Encodings are read before anything is
    /// written or deleted; a file whose bytes are not valid in its own encoding fails the
    /// commit then, with nothing written.
    /// </summary>
    /// <param name="newSolution">
    /// Solution with changes to commit, derived from <see cref="Solution"/> as read inside the
    /// running writer section.
    /// </param>
    /// <param name="newFilesEncodingFrom">
    /// Existing file whose encoding added documents take unless an <c>.editorconfig</c> names one.
    /// </param>
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

        CommitPlan? plan = null;
        var writing = false;

        try
        {
            State = WorkspaceState.Operating;

            // Everything is decided first — contents and encodings, so every encoding is read
            // before anything is written or deleted — then the files are written one after
            // another, which prevents interleaved writes to the same file from different
            // documents.
            plan = await PlanCommitAsync(newSolution, newFilesEncodingFrom, preview: false, cancellationToken);

            // Recorded first, so a commit that fails halfway is taken over as far as it got.
            _s.Writers.RecordWrites(plan.Files);
            writing = true;
            foreach (var operation in plan.Operations)
            {
                if (operation.Content == null)
                    _s.FileWriter.Delete(operation.FilePath);
                else
                    await _s.FileWriter.WriteAsync(operation.FilePath, operation.Content, operation.Encoding!, cancellationToken);
            }

            var committed = await LabelEncodingsAsync(newSolution, plan.Written);
            _s.Solution = committed;
            _s.WriterBase = committed;
            State = WorkspaceState.Ready;

            return new CommitResult
            {
                Success = true,
                FilesModified = plan.FilesModified,
                FilesCreated = plan.FilesCreated,
                FilesDeleted = plan.FilesDeleted,
                Warnings = plan.Warnings
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
                FilesModified = plan?.FilesModified ?? [],
                FilesCreated = plan?.FilesCreated ?? [],
                FilesDeleted = plan?.FilesDeleted ?? [],
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// What committing <paramref name="newSolution"/> would report, decided as
    /// <see cref="CommitChangesAsync(Solution, string?, CancellationToken)"/> decides it but
    /// without writing anything, for a preview: the files that would be saved as UTF-8 with
    /// BOM because their code page cannot represent the new text, and the reason the commit
    /// would fail with <see cref="ErrorCodes.FilesystemError"/> before writing anything (a file
    /// whose bytes are not valid in its own encoding). Empty when the commit would report
    /// nothing. Only inside <see cref="RunWriterAsync{T}"/>, like a commit.
    /// </summary>
    /// <param name="newSolution">
    /// Solution with the changes a commit would write, derived from <see cref="Solution"/> as
    /// read inside the running writer section.
    /// </param>
    /// <param name="newFilesEncodingFrom">As for the commit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Called outside a writer section.</exception>
    /// <exception cref="RefactoringException">
    /// <see cref="ErrorCodes.WorkspaceBusy"/>: the solution changed since the section started.
    /// </exception>
    public async Task<IReadOnlyList<string>> GetCommitWarningsAsync(
        Solution newSolution,
        string? newFilesEncodingFrom = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureWriterBaseIsCurrent(nameof(GetCommitWarningsAsync));

        try
        {
            var plan = await PlanCommitAsync(newSolution, newFilesEncodingFrom, preview: true, cancellationToken);
            return plan.Warnings;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [$"Applying this would fail with FilesystemError ({ErrorCodes.FilesystemError}) and write nothing: {ex.Message}"];
        }
    }

    /// <summary>
    /// The file writes and deletions of a commit, sorted by path, with what the commit reports.
    /// </summary>
    private sealed class CommitPlan
    {
        /// <summary>The files to write (<c>Content</c> in <c>Encoding</c>) or, without content, delete.</summary>
        public List<(string FilePath, string? Content, Encoding? Encoding)> Operations { get; } = [];

        /// <summary>The documents written, with the encoding their file is written in.</summary>
        public List<(DocumentId Id, Encoding Encoding)> Written { get; } = [];

        /// <summary>
        /// What the other contexts sharing the writers take over (<see cref="RunWriterAsync{T}"/>);
        /// a file created, deleted or moved may change what a project compiles.
        /// </summary>
        public List<(string Path, bool Structural)> Files { get; } = [];

        public List<string> FilesModified { get; } = [];

        public List<string> FilesCreated { get; } = [];

        public List<string> FilesDeleted { get; } = [];

        public List<string> Warnings { get; } = [];
    }

    /// <summary>
    /// Decides what committing <paramref name="newSolution"/> writes: every changed, added and
    /// removed document's file, its content and its encoding (see
    /// <see cref="CommitChangesAsync(Solution, string?, CancellationToken)"/>), reading the
    /// files' current bytes but writing nothing. <paramref name="preview"/> words the warnings
    /// for a preview.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// A file to rewrite has bytes that are not valid in its encoding.
    /// </exception>
    private async Task<CommitPlan> PlanCommitAsync(
        Solution newSolution, string? newFilesEncodingFrom, bool preview, CancellationToken cancellationToken)
    {
        var plan = new CommitPlan();
        var changes = newSolution.GetChanges(_s.Solution);
        var newFileEncodings = new NewFileEncodings(this, newFilesEncodingFrom);

        foreach (var projectChanges in changes.GetProjectChanges())
        {
            // Added documents. A file one replaces keeps its encoding; a new one is written as
            // NewFileEncodings decides.
            foreach (var docId in projectChanges.GetAddedDocuments())
            {
                var doc = newSolution.GetDocument(docId);
                if (doc?.FilePath == null) continue;

                var filePath = doc.FilePath;
                var content = (await doc.GetTextAsync(cancellationToken)).ToString();
                var encoding = Representable(
                    filePath,
                    content,
                    GetEncodingOfReplacedFile(filePath) ?? await newFileEncodings.GetAsync(doc, cancellationToken),
                    plan.Warnings,
                    preview);
                plan.Operations.Add((filePath, content, encoding));
                plan.Written.Add((docId, encoding));
                plan.Files.Add((filePath, true));
                plan.FilesCreated.Add(filePath);
            }

            // Changed documents: each is written in the encoding its text was decoded with, so
            // the bytes of the text it did not change stay as they were. One repointed at a new
            // path (a renamed file) is written there as a new file, in the encoding of the file
            // at its old path.
            foreach (var docId in projectChanges.GetChangedDocuments())
            {
                var doc = newSolution.GetDocument(docId);
                if (doc?.FilePath == null) continue;

                var filePath = doc.FilePath;
                var text = await doc.GetTextAsync(cancellationToken);
                var content = text.ToString();
                var oldDoc = _s.Solution.GetDocument(docId);
                var decodedWith = (oldDoc == null ? null : (await oldDoc.GetTextAsync(cancellationToken)).Encoding)
                    ?? text.Encoding;
                var oldPath = oldDoc?.FilePath;
                var moved = oldPath != null && !PathResolver.PathsEqual(oldPath, filePath);
                var encodingFrom = moved ? oldPath! : filePath;

                var encoding = Representable(
                    filePath,
                    content,
                    _s.FileWriter.GetEncoding(encodingFrom, decodedWith) ?? decodedWith ?? SourceFileEncoding.Utf8WithBom,
                    plan.Warnings,
                    preview);
                plan.Operations.Add((filePath, content, encoding));
                plan.Written.Add((docId, encoding));
                plan.Files.Add((filePath, moved));
                if (moved)
                    plan.Files.Add((oldPath!, true)); // the operation deletes the old file afterwards

                plan.FilesModified.Add(filePath);
            }

            // Removed documents.
            foreach (var docId in projectChanges.GetRemovedDocuments())
            {
                var doc = _s.Solution.GetDocument(docId);
                if (doc?.FilePath == null) continue;

                plan.Operations.Add((doc.FilePath, null, null));
                plan.Files.Add((doc.FilePath, true));
                plan.FilesDeleted.Add(doc.FilePath);
            }
        }

        // Sorted by file path for a consistent order, which also prevents deadlocks with
        // external file locks.
        plan.Operations.Sort((a, b) => string.Compare(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase));
        return plan;
    }

    /// <summary>
    /// The encoding <paramref name="filePath"/> is kept in when rewritten, judged from its
    /// bytes and the encoding its document's text was decoded with; null when it does not exist.
    /// </summary>
    private async Task<Encoding?> GetEncodingToKeepAsync(string filePath, CancellationToken cancellationToken)
    {
        var document = GetDocumentByPath(filePath);
        var decodedWith = document == null ? null : (await document.GetTextAsync(cancellationToken)).Encoding;
        return _s.FileWriter.GetEncoding(filePath, decodedWith);
    }

    /// <summary>
    /// The encoding of each file a commit creates (an added document; one that replaces an
    /// existing file keeps that file's encoding instead), decided in this order:
    /// <list type="number">
    /// <item>the <c>.editorconfig</c> <c>charset</c> that applies to its path, as an IDE does;</item>
    /// <item>the encoding of the file its content comes from (the commit's
    /// <c>newFilesEncodingFrom</c>, e.g. the file a type was split off from);</item>
    /// <item>the encoding most of its project's files were read in (see
    /// <see cref="ProjectFileConventions.GetPrevailingEncodingAsync"/>);</item>
    /// <item>UTF-8 with BOM.</item>
    /// </list>
    /// A renamed or moved file is no new file: it keeps its own encoding, like any file a
    /// refactoring edits. Each source of an answer is read once per commit, before anything is
    /// written.
    /// </summary>
    private sealed class NewFileEncodings(WorkspaceContext context, string? contentFrom)
    {
        private readonly Dictionary<ProjectId, Encoding?> _prevailing = [];
        private Encoding? _contentFromEncoding;
        private bool _contentFromRead;

        public async Task<Encoding> GetAsync(Document document, CancellationToken cancellationToken)
        {
            if (await ProjectFileConventions.GetEditorConfigEncodingAsync(document, cancellationToken) is { } configured)
                return configured;

            if (contentFrom != null)
            {
                if (!_contentFromRead)
                {
                    _contentFromEncoding = await context.GetEncodingToKeepAsync(contentFrom, cancellationToken);
                    _contentFromRead = true;
                }

                if (_contentFromEncoding != null)
                    return _contentFromEncoding;
            }

            // The project as it was: the files the commit adds have no recorded encoding yet.
            var projectId = document.Project.Id;
            if (!_prevailing.TryGetValue(projectId, out var prevailing))
            {
                var project = context._s.Solution.GetProject(projectId);
                prevailing = project == null
                    ? null
                    : await ProjectFileConventions.GetPrevailingEncodingAsync(project, cancellationToken);
                _prevailing[projectId] = prevailing;
            }

            return prevailing ?? SourceFileEncoding.Utf8WithBom;
        }
    }

    /// <summary>
    /// The encoding of an existing file an added document replaces, if any. Its text is
    /// replaced as a whole, so bytes that are invalid in its encoding don't stop the write.
    /// </summary>
    private Encoding? GetEncodingOfReplacedFile(string filePath)
    {
        try
        {
            return _s.FileWriter.GetEncoding(filePath);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="encoding"/> when it can represent <paramref name="content"/>; otherwise
    /// UTF-8 with BOM, which can, and a warning (worded for a preview when
    /// <paramref name="preview"/> is set). Writing it in the code page would turn the
    /// characters it lacks into <c>?</c> or look-alikes.
    /// </summary>
    private static Encoding Representable(
        string filePath, string content, Encoding encoding, List<string> warnings, bool preview)
    {
        var unencodable = SourceFileEncoding.FindUnencodable(content, encoding);
        if (unencodable == null)
            return encoding;

        warnings.Add(
            $"'{filePath}' {(preview ? "would be" : "was")} saved as UTF-8 with BOM instead of " +
            $"{SourceFileEncoding.Describe(encoding)}, which cannot represent {unencodable}.");
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
        if (_lease != null)
        {
            // A caller's handle on a cached workspace: release its lease (once). The cache
            // owns the workspace's lifetime.
            _lease.Release();
            return;
        }

        // The cache's own reference to a workspace it owns: the cache disposes it.
        if (_s.CacheOwned)
            return;

        DisposeOwned();
    }

    /// <summary>
    /// Performs the actual workspace disposal. For cache-owned contexts, this is
    /// called by the cache on eviction; otherwise it's invoked directly from
    /// <see cref="Dispose"/>.
    /// </summary>
    internal void DisposeOwned()
    {
        if (_s.Disposed) return;
        _s.Disposed = true;
        State = WorkspaceState.Disposed;
        // _s.WriterLock is left to the GC: it never allocates a wait handle, and a section still
        // running (on an uncached context disposed under it) must be able to release it.
        _s.Workspace.Dispose();

        // Dispose after the workspace so it has already released its references to
        // the shadow-copied analyzer assemblies; this unloads the dedicated load
        // context and prunes the temp copies.
        _s.AnalyzerAssemblyLoader?.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_s.Disposed, this);
        ThrowIfReleased();
    }

    /// <summary>
    /// A handle on a cached workspace works only while it holds its lease: once disposed, file
    /// updates may change the solution under it and the workspace may be torn down, so using it
    /// is a bug that must not run silently. <see cref="State"/> and the load-time information
    /// (<see cref="LoadedPath"/>, <see cref="GeneratorLoadIssues"/>, <see cref="LoadDiagnostics"/>)
    /// stay readable, and a file update (<see cref="SyncDocumentFromDiskAsync"/>,
    /// <see cref="ReconcileProjectDocumentsAsync"/>), which never runs under an operation lease,
    /// may still be started through it.
    /// </summary>
    private void ThrowIfReleased()
    {
        if (_lease is { IsReleased: true })
            throw new ObjectDisposedException(
                nameof(WorkspaceContext),
                "This workspace context was disposed, which released its lease on the cached workspace; " +
                "get a new one from the workspace provider.");
    }
}

/// <summary>
/// Outcome of <see cref="WorkspaceContext.SyncFilesChangedSinceAsync"/>.
/// </summary>
/// <param name="Updated">How many files' text changed in the solution.</param>
/// <param name="Missing">Tracked files (normalized paths) that are gone from disk; the solution still has them.</param>
/// <param name="ReloadRequired">A file-based program's <c>#:</c> directives changed, so the workspace must be rebuilt.</param>
internal sealed record ChangedDuringLoad(int Updated, IReadOnlyList<string> Missing, bool ReloadRequired);

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
