using RoslynMcp.Core.FileSystem;

namespace RoslynMcp.Core.Workspace;

/// <summary>
/// The writer section shared by workspace contexts that may hold the same files, and the record
/// of the files each section wrote. A <see cref="WorkspaceCache"/> shares one among all the
/// contexts it loads: a <c>.slnx</c> and a <c>.csproj</c> of one tree, a <c>.sln</c> and a
/// <c>.slnx</c>, or an invalidated workspace and its reloaded successor while a call still uses
/// the old one. A context created on its own has one of its own.
/// </summary>
/// <remarks>
/// <para>
/// Refactorings of these contexts run one at a time (<see cref="RunSectionAsync{T}"/>), so none
/// computes on files another one is writing. That alone is not enough: a context learns of
/// another context's write only from its file watcher, which may not have run yet, and a
/// refactoring computed on the older text would write it back over that edit. So a section first
/// takes over from disk every file written since its context last looked
/// (<see cref="GetWrittenSince"/>), without waiting for any file watcher.
/// </para>
/// <para>
/// One section for all of them rather than one per file: which files a refactoring writes is
/// known only once it has computed its change, and refactorings of unrelated workspaces at the
/// same time in one server are rare. Queries are not affected; they never enter a section.
/// </para>
/// </remarks>
internal sealed class WorkspaceWriteCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    // The running section (a fresh object per section; null when none runs), and the section each
    // async flow belongs to. A flow is in the running section only if its value is that object, so
    // work an earlier section left behind is not.
    private object? _currentSection;
    private readonly AsyncLocal<object?> _sectionOfFlow = new();

    // Incremented once per recorded write, by the running section; read anywhere.
    private long _sequence;

    // Per file written (normalized path): the sequence number of its latest write, and of the
    // latest write that created, deleted or moved it. Touched only by the running section, which
    // holds _gate. Grows with the files written while the server runs (one entry per file).
    private readonly Dictionary<string, (long Written, long Structural)> _written = new(PathResolver.PathComparer);

    /// <summary>
    /// The sequence number of the latest recorded write; a context that has taken over every
    /// write up to it needs nothing from disk.
    /// </summary>
    public long Sequence => Interlocked.Read(ref _sequence);

    /// <summary>Whether the calling flow belongs to the section that is running now.</summary>
    public bool FlowRunsSection()
    {
        var section = _sectionOfFlow.Value;
        return section != null && ReferenceEquals(section, Volatile.Read(ref _currentSection));
    }

    /// <summary>
    /// Runs <paramref name="body"/> as the only section of every context sharing this
    /// coordinator; the section ends, and the next one may start, when the body completes, throws
    /// or is cancelled. Not re-entrant, also not through another context: a flow inside a section
    /// that started another would wait for itself.
    /// </summary>
    /// <exception cref="InvalidOperationException">The calling flow already runs a section.</exception>
    public async Task<T> RunSectionAsync<T>(
        Func<CancellationToken, Task<T>> body,
        CancellationToken cancellationToken)
    {
        if (FlowRunsSection())
            throw new InvalidOperationException(
                "This flow is already inside a workspace writer section, which is not re-entrant.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var section = new object();
        try
        {
            Volatile.Write(ref _currentSection, section);
            // Flows into the body (and whatever it starts); this method's caller keeps its own
            // value, since an async method restores the caller's context when it returns.
            _sectionOfFlow.Value = section;
            return await body(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _currentSection, null);
            _gate.Release();
        }
    }

    /// <summary>
    /// Records that the running section wrote <paramref name="files"/> (or is about to): written
    /// in place, or <c>Structural</c> when the file was created, deleted or moved to or from that
    /// path, which may change what a project compiles.
    /// </summary>
    /// <exception cref="InvalidOperationException">The calling flow does not run the section.</exception>
    public void RecordWrites(IReadOnlyCollection<(string Path, bool Structural)> files)
    {
        EnsureFlowRunsSection(nameof(RecordWrites));
        if (files.Count == 0)
            return;

        var sequence = Interlocked.Increment(ref _sequence);
        foreach (var (path, structural) in files)
        {
            var key = PathResolver.NormalizePath(path);
            var structuralSequence = structural
                ? sequence
                : _written.TryGetValue(key, out var previous) ? previous.Structural : 0;
            _written[key] = (sequence, structuralSequence);
        }
    }

    /// <summary>
    /// The files written after <paramref name="seen"/> (normalized paths, each once, with whether
    /// a write since then created, deleted or moved it), and the sequence number they run
    /// through. Nothing is listed when nothing was written since, without looking at the record.
    /// </summary>
    /// <exception cref="InvalidOperationException">The calling flow does not run the section.</exception>
    public (long Through, IReadOnlyList<(string Path, bool Structural)> Files) GetWrittenSince(long seen)
    {
        EnsureFlowRunsSection(nameof(GetWrittenSince));
        var through = Sequence;
        if (seen > through)
            throw new InvalidOperationException(
                $"A context has seen write {seen}, but only {through} were recorded.");
        if (seen == through)
            return (through, []);

        var files = new List<(string Path, bool Structural)>();
        foreach (var (path, (written, structural)) in _written)
        {
            if (written > seen)
                files.Add((path, structural > seen));
        }

        return (through, files);
    }

    /// <summary>
    /// The record is changed and read only by the running section, which holds the gate; a call
    /// from elsewhere would race it.
    /// </summary>
    private void EnsureFlowRunsSection(string action)
    {
        if (!FlowRunsSection())
            throw new InvalidOperationException($"{action} must run inside the workspace writer section.");
    }
}
