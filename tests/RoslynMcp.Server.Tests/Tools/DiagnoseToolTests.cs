using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// diagnose listed three hard-coded capabilities and ignored <c>verbose</c>; the server now
/// reports every registered tool and adds details only on request.
/// </summary>
public sealed class DiagnoseToolTests
{
    [Fact]
    public async Task Capabilities_AreTheRegisteredTools()
    {
        var dispatcher = new McpRequestDispatcher(new FakeProvider());

        var list = await dispatcher.DispatchAsync(new McpRequest { Id = 1, Method = "tools/list" }, TestContext.Current.CancellationToken);
        var registered = JsonSerializer.SerializeToElement(list.Result).GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToList();

        var result = await CallDiagnoseAsync(dispatcher, "{}");

        Assert.Equal(44, registered.Count);
        Assert.Equal(registered, result.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("""{"verbose": false}""", false)]
    [InlineData("""{"verbose": true}""", true)]
    public async Task Details_OnlyWithVerbose(string arguments, bool expectDetails)
    {
        var result = await CallDiagnoseAsync(new McpRequestDispatcher(new FakeProvider()), arguments);

        Assert.Equal(expectDetails, result.TryGetProperty("details", out var details));
        if (expectDetails)
        {
            Assert.Equal("Only one.", details.GetProperty("selectionReason").GetString());
            Assert.True(details.GetProperty("msBuildInstances")[0].GetProperty("selected").GetBoolean());
        }

        var components = result.GetProperty("components");
        Assert.Equal("18.12.2", components.GetProperty("msBuildVersion").GetString());
        Assert.Equal("11.0.100", components.GetProperty("dotnetSdkVersion").GetString());
        Assert.Equal("11.0.0", components.GetProperty("dotnetRuntimeVersion").GetString());
    }

    private static async Task<JsonElement> CallDiagnoseAsync(McpRequestDispatcher dispatcher, string arguments)
    {
        var request = new McpRequest
        {
            Id = 2,
            Method = "tools/call",
            Params = JsonDocument.Parse($$"""{"name": "diagnose", "arguments": {{arguments}}}""").RootElement
        };

        var response = await dispatcher.DispatchAsync(request, TestContext.Current.CancellationToken);

        var toolResult = Assert.IsType<ToolResult>(response.Result);
        Assert.False(toolResult.IsError);
        return JsonDocument.Parse(toolResult.Content[0].Text!).RootElement;
    }

    private sealed class FakeProvider : IWorkspaceProvider
    {
        public Task<WorkspaceContext> CreateContextAsync(string projectOrSolutionPath, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No workspace in this test.");

        public EnvironmentDiagnostics CheckEnvironment() => new()
        {
            MsBuildFound = true,
            MsBuildVersion = "18.12.2",
            MsBuildPath = "sdk/11.0.100",
            DotnetSdkVersion = "11.0.100",
            DotnetRuntimeVersion = "11.0.0",
            Instances = [new MsBuildInstanceInfo { Name = ".NET SDK", Version = "11.0.100", MsBuildPath = "sdk/11.0.100", DiscoveryType = "DotNetSdk", Selected = true }],
            SelectionReason = "Only one."
        };
    }
}
