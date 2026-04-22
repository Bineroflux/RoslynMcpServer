namespace RoslynMcp.Core.Workspace;

/// <summary>
/// Flows per-request workspace timing metadata through the async call stack.
/// </summary>
/// <remarks>
/// Set by <see cref="MSBuildWorkspaceProvider"/> after <c>CreateContextAsync</c> returns.
/// Read by query/refactoring operation bases to populate the result's
/// <c>WorkspaceLoadMs</c> and <c>TotalExecutionTimeMs</c> fields without changing
/// tool-layer signatures.
/// </remarks>
public static class WorkspaceTimingContext
{
    private static readonly AsyncLocal<long> _lastLoadMs = new();

    /// <summary>
    /// Time (ms) spent acquiring the workspace for the current request.
    /// Zero on a cache hit.
    /// </summary>
    public static long LastLoadMs
    {
        get => _lastLoadMs.Value;
        internal set => _lastLoadMs.Value = value;
    }
}
