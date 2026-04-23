using System.Net;
using System.Text;
using System.Text.Json;
using RoslynMcp.Server.Logging;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server;

/// <summary>
/// HTTP-based MCP server host using <see cref="HttpListener"/>.
/// </summary>
/// <remarks>
/// Implements the MCP "Streamable HTTP" transport in its simplest form: clients
/// POST a JSON-RPC request to <c>/mcp</c>, the server returns the JSON-RPC response
/// with <c>Content-Type: application/json</c>. Server-initiated notifications are
/// not delivered (no persistent channel); log messages go to the file logger only.
///
/// Binds to <c>http://127.0.0.1:{port}/</c> — no HTTPS, no auth. Intended for
/// local-only use; do not expose externally.
/// </remarks>
public sealed class HttpMcpServerHost : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly McpRequestDispatcher _dispatcher;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly int _port;
    private bool _disposed;

    /// <summary>
    /// Creates a new HTTP MCP host. The server does not start until <see cref="RunAsync"/> is called.
    /// </summary>
    public HttpMcpServerHost(McpRequestDispatcher dispatcher, int port)
    {
        _dispatcher = dispatcher;
        _port = port;
        _listener = new HttpListener();
        // 127.0.0.1 (not "localhost") avoids the DNS round-trip some clients add,
        // and matches the common MCP HTTP client default.
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <summary>
    /// Starts the HTTP listener and serves requests until <paramref name="cancellationToken"/> fires.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _listener.Start();
        FileLogger.Log($"HttpMcpServerHost listening on http://127.0.0.1:{_port}/mcp");

        // Register a cancellation hook that closes the listener so the blocking
        // GetContextAsync call unblocks immediately.
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try { _listener.Stop(); } catch { /* already stopped */ }
        });

        var inflight = new HashSet<Task>();
        var inflightLock = new object();

        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                break; // listener stopped during shutdown
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                FileLogger.LogError("HttpListener.GetContextAsync failed", ex);
                continue;
            }

            // Dispatch each request on the thread pool so long-running tool calls
            // don't block the accept loop.
            var captured = context;
            var task = Task.Run(() => HandleContextAsync(captured, cancellationToken), cancellationToken);

            lock (inflightLock) inflight.Add(task);
            _ = task.ContinueWith(t =>
            {
                lock (inflightLock) inflight.Remove(t);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        Task[] remaining;
        lock (inflightLock) remaining = inflight.ToArray();
        if (remaining.Length > 0)
        {
            FileLogger.Log($"Awaiting {remaining.Length} in-flight HTTP request(s) before shutdown...");
            try { await Task.WhenAll(remaining); }
            catch { /* individual failures already logged */ }
        }

        FileLogger.Log("HttpMcpServerHost.RunAsync loop ended.");
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;

            // Health probe — lets humans verify the server is up.
            if (request.HttpMethod == "GET" &&
                (request.Url?.AbsolutePath == "/" || request.Url?.AbsolutePath == "/health"))
            {
                await WritePlainAsync(response, 200, "text/plain", "roslyn-mcp OK");
                return;
            }

            if (request.HttpMethod != "POST" || request.Url?.AbsolutePath != "/mcp")
            {
                await WritePlainAsync(response, 404, "text/plain", "Not Found. POST JSON-RPC to /mcp.");
                return;
            }


            McpRequest? mcpRequest;
            try
            {
                var encoding = request.ContentEncoding ?? Encoding.UTF8;
                if (encoding is UTF8Encoding)
                {
                    mcpRequest = JsonSerializer.Deserialize<McpRequest>(request.InputStream, _jsonOptions);
                }
                else
                {
                    using var reader = new StreamReader(request.InputStream, encoding);
                    var body = await reader.ReadToEndAsync(cancellationToken);
                    mcpRequest = JsonSerializer.Deserialize<McpRequest>(body, _jsonOptions);
                }
            }
            catch (JsonException ex)
            {
                var err = McpResponse.Failure(null, -32700, $"Parse error: {ex.Message}");
                await WriteJsonRpcAsync(response, err);
                return;
            }

            if (mcpRequest == null)
            {
                var err = McpResponse.Failure(null, -32600, "Invalid request");
                await WriteJsonRpcAsync(response, err);
                return;
            }

            FileLogger.Log($"[HTTP] Received request: method={mcpRequest.Method}, id={mcpRequest.Id}");

            var mcpResponse = await _dispatcher.DispatchAsync(mcpRequest, cancellationToken);

            // Notifications (no id) don't expect a response body.
            if (mcpRequest.Id == null)
            {
                response.StatusCode = 202; // Accepted
                response.Close();
                return;
            }

            await WriteJsonRpcAsync(response, mcpResponse);
        }
        catch (OperationCanceledException)
        {
            // shutdown — best effort close
            try { context.Response.Abort(); } catch { }
        }
        catch (Exception ex)
        {
            FileLogger.LogError("Unhandled error in HTTP request handler", ex);
            try
            {
                var err = McpResponse.Failure(null, -32603, $"Internal error: {ex.Message}");
                await WriteJsonRpcAsync(context.Response, err);
            }
            catch { /* swallow secondary errors */ }
        }
    }

    private async Task WriteJsonRpcAsync(HttpListenerResponse response, McpResponse payload)
    {
        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        response.StatusCode = 200;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private static async Task WritePlainAsync(HttpListenerResponse response, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = status;
        response.ContentType = contentType + "; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        try { _listener.Close(); } catch { /* already closed */ }
        return ValueTask.CompletedTask;
    }
}
