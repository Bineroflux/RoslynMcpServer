using RoslynMcp.Core.Workspace;
using RoslynMcp.Server;
using RoslynMcp.Server.Logging;

// Parse CLI args / env for transport selection.
// Defaults to stdio (backwards compatible).
// Enable HTTP via:
//   --http [port]              (port defaults to 7337 if omitted)
//   ROSLYN_MCP_HTTP=1          (optionally ROSLYN_MCP_HTTP_PORT=<port>)
var (useHttp, httpPort) = ParseTransportArgs(args);

// Log startup
FileLogger.Log($"RoslynMcp Server starting. Log file: {FileLogger.LogFilePath}");
FileLogger.Log($"Process ID: {Environment.ProcessId}");
FileLogger.Log($"Working directory: {Environment.CurrentDirectory}");
FileLogger.Log($".NET Version: {Environment.Version}");
FileLogger.Log(useHttp ? $"Transport: HTTP (port {httpPort})" : "Transport: stdio");

// Wire up logging callbacks for MSBuildWorkspaceProvider
MSBuildWorkspaceProvider.LogCallback = FileLogger.Log;
MSBuildWorkspaceProvider.LogErrorCallback = FileLogger.LogError;

// Create cancellation token for graceful shutdown
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    FileLogger.Log("Received cancel signal, initiating graceful shutdown...");
    cts.Cancel();
};

// Create workspace provider
FileLogger.Log("Creating MSBuildWorkspaceProvider...");
using var workspaceProvider = new MSBuildWorkspaceProvider();
FileLogger.Log("MSBuildWorkspaceProvider created successfully.");

// Build the shared request dispatcher (tool registry + method switch).
var dispatcher = new McpRequestDispatcher(workspaceProvider);

try
{
    if (useHttp)
    {
        FileLogger.Log("Creating HttpMcpServerHost...");
        await using var server = new HttpMcpServerHost(dispatcher, httpPort);
        FileLogger.Log("HttpMcpServerHost created, starting listener...");
        await server.RunAsync(cts.Token);
        FileLogger.Log("HTTP server loop ended normally.");
    }
    else
    {
        FileLogger.Log("Creating McpServerHost (stdio)...");
        await using var server = new McpServerHost(dispatcher);
        FileLogger.Log("McpServerHost created, starting message loop...");
        await server.RunAsync(cts.Token);
        FileLogger.Log("stdio server loop ended normally.");
    }
}
catch (Exception ex)
{
    FileLogger.LogError("Server loop failed with exception", ex);
    throw;
}
finally
{
    FileLogger.Log("RoslynMcp Server shutting down.");
}

static (bool UseHttp, int Port) ParseTransportArgs(string[] args)
{
    const int defaultPort = 7337;

    // CLI flag takes precedence.
    for (var i = 0; i < args.Length; i++)
    {
        if (!string.Equals(args[i], "--http", StringComparison.OrdinalIgnoreCase)) continue;

        // Optional port argument follows.
        if (i + 1 < args.Length && int.TryParse(args[i + 1], out var cliPort) && cliPort is > 0 and <= 65535)
            return (true, cliPort);

        return (true, defaultPort);
    }

    // Env vars as a fallback so users can set this without editing launch configs.
    var envFlag = Environment.GetEnvironmentVariable("ROSLYN_MCP_HTTP");
    if (!string.IsNullOrEmpty(envFlag) &&
        (envFlag == "1" || envFlag.Equals("true", StringComparison.OrdinalIgnoreCase)))
    {
        var envPort = Environment.GetEnvironmentVariable("ROSLYN_MCP_HTTP_PORT");
        if (int.TryParse(envPort, out var p) && p is > 0 and <= 65535)
            return (true, p);
        return (true, defaultPort);
    }

    return (false, 0);
}
