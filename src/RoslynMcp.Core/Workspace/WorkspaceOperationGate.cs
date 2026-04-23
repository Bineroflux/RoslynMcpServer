namespace RoslynMcp.Core.Workspace;

/// <summary>
/// Coordinates workspace operations with filesystem-driven updates.
/// </summary>
/// <remarks>
/// Two kinds of access are tracked:
/// <list type="bullet">
/// <item>
///   <term>Operation</term>
///   <description>Shared lease taken for the lifetime of any MCP call. Multiple
///   operations may run concurrently.</description>
/// </item>
/// <item>
///   <term>File update</term>
///   <description>Exclusive lease taken when applying an external text change
///   surfaced by the file watcher. A file update cannot start while any
///   operation is active, and no new operation may start while a file update
///   is either pending or active. The gate is "unfair" in favour of file
///   updates: once one is queued, the next idle moment hands control to it
///   before any waiting operation.</description>
/// </item>
/// </list>
/// </remarks>
internal sealed class WorkspaceOperationGate
{
    private readonly object _sync = new();
    private int _activeOperations;
    private int _pendingFileUpdates;
    private bool _fileUpdateActive;

    // Latched event: every state change replaces this with a fresh TCS and
    // completes the old one. Waiters observe the old task; on completion they
    // re-check the condition under the lock and either proceed or wait again.
    private TaskCompletionSource _stateChanged =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Waits until no file update is pending or active, then records an
    /// operation as active. Must be paired with <see cref="ExitOperation"/>.
    /// </summary>
    public async Task EnterOperationAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_sync)
            {
                if (!_fileUpdateActive && _pendingFileUpdates == 0)
                {
                    _activeOperations++;
                    return;
                }
                wait = _stateChanged.Task;
            }
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Releases an operation lease previously acquired via
    /// <see cref="EnterOperationAsync"/>.
    /// </summary>
    public void ExitOperation()
    {
        lock (_sync)
        {
            if (_activeOperations <= 0)
                throw new InvalidOperationException(
                    "ExitOperation called without a matching EnterOperation.");
            _activeOperations--;
            SignalStateChange();
        }
    }

    /// <summary>
    /// Waits until no operation is active and no other file update is in
    /// progress, then takes the exclusive file-update lease. Must be paired
    /// with <see cref="ExitFileUpdate"/>.
    /// </summary>
    /// <remarks>
    /// The pending count is incremented synchronously on entry so that the
    /// first opportunity to block new operations is before this method even
    /// awaits — ensuring the "unfair" priority promise.
    /// </remarks>
    public async Task EnterFileUpdateAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _pendingFileUpdates++;
            SignalStateChange();
        }

        try
        {
            while (true)
            {
                Task wait;
                lock (_sync)
                {
                    if (_activeOperations == 0 && !_fileUpdateActive)
                    {
                        _pendingFileUpdates--;
                        _fileUpdateActive = true;
                        SignalStateChange();
                        return;
                    }
                    wait = _stateChanged.Task;
                }
                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            lock (_sync)
            {
                _pendingFileUpdates--;
                SignalStateChange();
            }
            throw;
        }
    }

    /// <summary>
    /// Releases a file-update lease previously acquired via
    /// <see cref="EnterFileUpdateAsync"/>.
    /// </summary>
    public void ExitFileUpdate()
    {
        lock (_sync)
        {
            if (!_fileUpdateActive)
                throw new InvalidOperationException(
                    "ExitFileUpdate called without a matching EnterFileUpdate.");
            _fileUpdateActive = false;
            SignalStateChange();
        }
    }

    private void SignalStateChange()
    {
        // Called under _sync. RunContinuationsAsynchronously ensures continuations
        // do not execute inline while we still hold the lock.
        var previous = _stateChanged;
        _stateChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }
}
