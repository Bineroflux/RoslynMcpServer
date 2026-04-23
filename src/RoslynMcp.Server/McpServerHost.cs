using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Logging;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server;

/// <summary>
/// stdio-based MCP server host. Reads JSON-RPC lines from stdin, dispatches them
/// concurrently via an <see cref="McpRequestDispatcher"/>, and serializes responses
/// back onto stdout through a write lock so messages never interleave.
/// </summary>
public sealed class McpServerHost : IAsyncDisposable
{
    private readonly StdioTransport _transport;
    private readonly McpRequestDispatcher _dispatcher;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// Creates a new stdio MCP server host.
    /// </summary>
    public McpServerHost(IWorkspaceProvider workspaceProvider)
        : this(new McpRequestDispatcher(workspaceProvider))
    {
    }

    /// <summary>
    /// Creates a new stdio MCP server host that shares a pre-built dispatcher.
    /// </summary>
    public McpServerHost(McpRequestDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _transport = new StdioTransport();
    }

    /// <summary>
    /// Runs the MCP server message loop.
    /// Requests are dispatched concurrently; responses are serialized on stdout via a write lock.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        FileLogger.Log("McpServerHost.RunAsync starting message loop...");
        await LogAsync("Roslyn MCP Server starting...");

        var inflight = new HashSet<Task>();
        var inflightLock = new object();

        while (!cancellationToken.IsCancellationRequested)
        {
            McpRequest? request;
            try
            {
                request = await _transport.ReadMessageAsync(cancellationToken);
                if (request == null)
                {
                    FileLogger.Log("Transport stream closed, exiting message loop.");
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                FileLogger.Log("Operation cancelled, exiting message loop.");
                break;
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("Failed to parse MCP message"))
            {
                // JSON parse error — respond with JSON-RPC -32700 and keep looping.
                // ID is null because we couldn't parse the request to get an ID.
                FileLogger.LogError("JSON parse error", ex);
                await LogAsync($"JSON parse error: {ex.Message}");
                var errorResponse = McpResponse.Failure(null, -32700,
                    $"Parse error: {ex.InnerException?.Message ?? ex.Message}");
                await WriteMessageSerializedAsync(errorResponse, cancellationToken);
                continue;
            }
            catch (Exception ex)
            {
                FileLogger.LogError("Error reading request", ex);
                await LogAsync($"Error reading request: {ex.Message}");
                continue;
            }

            FileLogger.Log($"Received request: method={request.Method}, id={request.Id}");

            // Dispatch the handler on the thread pool so long-running tool calls do
            // not block reading the next request. Writes back to stdout are
            // serialized by _writeLock so JSON messages never interleave.
            var captured = request;
            var task = Task.Run(async () =>
            {
                try
                {
                    var response = await _dispatcher.DispatchAsync(captured, cancellationToken);
                    if (captured.Id != null)
                    {
                        await WriteMessageSerializedAsync(response, cancellationToken);
                    }
                }
                catch (OperationCanceledException) { /* shutdown */ }
                catch (Exception ex)
                {
                    FileLogger.LogError($"Unhandled error dispatching request id={captured.Id}", ex);
                    try
                    {
                        if (captured.Id != null)
                        {
                            var errorResponse = McpResponse.Failure(
                                captured.Id, -32603, $"Internal error: {ex.Message}");
                            await WriteMessageSerializedAsync(errorResponse, cancellationToken);
                        }
                    }
                    catch { /* swallow secondary errors */ }
                }
            }, cancellationToken);

            lock (inflightLock) inflight.Add(task);
            _ = task.ContinueWith(t =>
            {
                lock (inflightLock) inflight.Remove(t);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        // Let in-flight handlers finish (best effort) before shutdown.
        Task[] remaining;
        lock (inflightLock) remaining = inflight.ToArray();
        if (remaining.Length > 0)
        {
            FileLogger.Log($"Awaiting {remaining.Length} in-flight request(s) before shutdown...");
            try { await Task.WhenAll(remaining); }
            catch { /* individual failures already logged */ }
        }

        FileLogger.Log("McpServerHost.RunAsync message loop ended.");
        await LogAsync("Roslyn MCP Server shutting down...");
    }

    private async Task WriteMessageSerializedAsync(McpResponse response, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _transport.WriteMessageAsync(response, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task LogAsync(string message)
    {
        // Serialize log notifications on the same lock that gates response writes;
        // otherwise their JSON could interleave with in-flight responses.
        await _writeLock.WaitAsync();
        try
        {
            await _transport.WriteNotificationAsync("notifications/message", new
            {
                level = "info",
                logger = "roslyn-mcp",
                data = message
            });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _transport.Dispose();
        _writeLock.Dispose();
        await Task.CompletedTask;
    }
}
