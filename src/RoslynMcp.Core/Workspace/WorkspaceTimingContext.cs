using System.Runtime.CompilerServices;

namespace RoslynMcp.Core.Workspace;

/// <summary>
/// Flows per-request workspace timing metadata through the async call stack.
/// </summary>
/// <remarks>
/// Uses a shared mutable box stored in an <see cref="AsyncLocal{T}"/> so that
/// downstream callees (the workspace provider, for instance) can record the load
/// time and callers (query / refactoring bases) observe it after awaiting.
///
/// Plain <c>AsyncLocal&lt;long&gt;</c> does not work here because a callee's
/// <c>AsyncLocal.Value = x</c> creates a new execution context that does not
/// flow back up to the caller's continuation. Mutating the contents of a shared
/// reference already stored in the AsyncLocal sidesteps that: the AsyncLocal's
/// value is never reassigned after scope creation, so no EC split occurs.
///
/// The scope is created at the call boundary (the MCP dispatcher for server
/// requests, the CLI entry point for one-shot invocations). If no scope is
/// active (test code hitting the workspace provider directly, for example),
/// reads return 0 and <see cref="RecordLoadMs"/> is a no-op.
/// </remarks>
public static class WorkspaceTimingContext
{
    private static readonly AsyncLocal<StrongBox<long>?> _box = new();

    /// <summary>
    /// Time (ms) spent acquiring the workspace for the current request, or 0
    /// if no scope is active or no load has been recorded yet.
    /// </summary>
    public static long LastLoadMs => _box.Value?.Value ?? 0;

    /// <summary>
    /// Called by the workspace provider to publish the load time for the
    /// current request. Silently ignored if no scope is active.
    /// </summary>
    internal static void RecordLoadMs(long ms)
    {
        var box = _box.Value;
        if (box != null) box.Value = ms;
    }

    /// <summary>
    /// Opens a new timing scope. Dispose to close it.
    /// </summary>
    /// <remarks>
    /// Must be called synchronously before any <c>await</c> that will in turn
    /// call <see cref="RecordLoadMs"/>. Scopes do not nest meaningfully — opening
    /// an inner scope shadows the outer box for the duration of the inner scope.
    /// </remarks>
    public static IDisposable BeginScope()
    {
        var previous = _box.Value;
        _box.Value = new StrongBox<long>(0);
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly StrongBox<long>? _previous;
        private bool _disposed;

        public Scope(StrongBox<long>? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _box.Value = _previous;
        }
    }
}
