using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace RoslynMcp.Server.Tests;

/// <summary>
/// End-to-end tests that run the real server executable over stdio, the way an MCP client
/// launches it. <see cref="Process"/> redirects stdio through anonymous synchronous pipes,
/// the same kind many clients (e.g. Python's <c>subprocess</c>) hand the server.
/// </summary>
public sealed class StdioServerProcessTests
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task StandaloneFile_Loads_WithoutChildProcessesBlockingOnTheProtocolPipe()
    {
        // Loading a standalone .cs file spawns child processes (dotnet run-api, git, dotnet
        // restore) while the server has a read pending on its stdin. A child that inherited
        // that handle deadlocked: Git for Windows blocked at startup probing the synchronous
        // pipe, and the call failed only when the 5-minute load timeout fired.
        var ct = TestContext.Current.CancellationToken;
        var file = Path.Combine(Path.GetTempPath(), $"RoslynMcp.StdioE2E-{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(file, "Console.WriteLine(\"hello\");\n", ct);
        try
        {
            await using var server = ServerProcess.Start();
            await server.RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "e2e-test", ["version"] = "1.0" },
            }, ct);
            await server.NotifyAsync("notifications/initialized", ct);

            var response = await server.RequestAsync("tools/call", new JsonObject
            {
                ["name"] = "get_diagnostics",
                ["arguments"] = new JsonObject { ["solutionPath"] = file },
            }, ct);

            var text = response["result"]?["content"]?[0]?["text"]?.GetValue<string>();
            Assert.NotNull(text);
            Assert.True(JsonNode.Parse(text)?["success"]?.GetValue<bool>(), text);
        }
        finally
        {
            try { File.Delete(file); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>The server built next to this test assembly, driven over its stdio.</summary>
    private sealed class ServerProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private int _nextId;

        private ServerProcess(Process process) => _process = process;

        public static ServerProcess Start()
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "RoslynMcp.Server.dll"));

            var process = Process.Start(psi) ?? throw new InvalidOperationException("Server did not start.");
            process.ErrorDataReceived += (_, _) => { }; // drain so a full stderr pipe can't block it
            process.BeginErrorReadLine();
            return new ServerProcess(process);
        }

        public async Task NotifyAsync(string method, CancellationToken ct)
            => await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method }, ct);

        public async Task<JsonNode> RequestAsync(string method, JsonObject @params, CancellationToken ct)
        {
            var id = ++_nextId;
            await SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = @params,
            }, ct);

            var deadline = DateTime.UtcNow + ResponseTimeout;
            while (true)
            {
                var remaining = deadline - DateTime.UtcNow;
                string? line;
                try
                {
                    // WaitAsync, not the token: a blocked pipe read may ignore cancellation.
                    line = await _process.StandardOutput.ReadLineAsync(ct).AsTask()
                        .WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, ct);
                }
                catch (TimeoutException)
                {
                    Assert.Fail($"No response to '{method}' within {ResponseTimeout.TotalSeconds:0} s.");
                    throw;
                }

                if (line is null) Assert.Fail($"Server exited before answering '{method}'.");
                if (string.IsNullOrWhiteSpace(line)) continue;

                var message = JsonNode.Parse(line);
                if (message?["id"] is JsonValue value && value.TryGetValue<int>(out var responseId) && responseId == id)
                    return message;
            }
        }

        private async Task SendAsync(JsonObject message, CancellationToken ct)
        {
            await _process.StandardInput.WriteLineAsync(message.ToJsonString().AsMemory(), ct);
            await _process.StandardInput.FlushAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _process.StandardInput.Close(); // end of stream: the server shuts down
                using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await _process.WaitForExitAsync(exit.Token);
            }
            catch
            {
                try { _process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            }
            finally
            {
                _process.Dispose();
            }
        }
    }
}
