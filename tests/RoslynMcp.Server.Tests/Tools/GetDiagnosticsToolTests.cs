using System.Text.Json;
using RoslynMcp.Server.Tools;
using RoslynMcp.Server.Transport;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// Unit tests for GetDiagnosticsTool.
/// </summary>
public class GetDiagnosticsToolTests
{
    private readonly GetDiagnosticsTool _tool;

    public GetDiagnosticsToolTests()
    {
        _tool = new GetDiagnosticsTool(null!);
    }

    [Fact]
    public void GetDefinition_ReturnsCorrectName()
    {
        Assert.Equal("get_diagnostics", _tool.Name);
    }

    [Fact]
    public void GetDefinition_ReturnsNonEmptyDescription()
    {
        Assert.NotNull(_tool.Description);
        Assert.NotEmpty(_tool.Description);
    }

    [Fact]
    public void GetDefinition_ReturnsCorrectSchema()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("object", root.GetProperty("type").GetString());
        Assert.True(root.TryGetProperty("properties", out _));
        Assert.True(root.TryGetProperty("required", out _));
    }

    [Fact]
    public void GetDefinition_HasRequiredField_SolutionPath()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var required = doc.RootElement.GetProperty("required");

        var requiredFields = new List<string>();
        foreach (var item in required.EnumerateArray())
            requiredFields.Add(item.GetString()!);

        Assert.Contains("solutionPath", requiredFields);
    }

    [Fact]
    public void GetDefinition_HasProperties_ForAllParameters()
    {
        var schema = _tool.InputSchema;
        var json = JsonSerializer.Serialize(schema);
        var doc = JsonDocument.Parse(json);
        var properties = doc.RootElement.GetProperty("properties");

        Assert.True(properties.TryGetProperty("solutionPath", out _));
        Assert.True(properties.TryGetProperty("sourceFile", out _));
        Assert.True(properties.TryGetProperty("severityFilter", out _));
    }

    [Theory]
    [InlineData("projectPath", "string")]
    [InlineData("diagnosticIds", "array")]
    [InlineData("excludeDiagnosticIds", "array")]
    [InlineData("messageFilter", "string")]
    [InlineData("maxResults", "integer")]
    public void GetDefinition_HasFilteringParameters(string name, string type)
    {
        var json = JsonSerializer.Serialize(_tool.InputSchema);
        var properties = JsonDocument.Parse(json).RootElement.GetProperty("properties");

        var property = properties.GetProperty(name);
        Assert.Equal(type, property.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(property.GetProperty("description").GetString()));
        if (type == "array")
            Assert.Equal("string", property.GetProperty("items").GetProperty("type").GetString());
    }

    [Fact]
    public void GetDefinition_FilteringParametersAreOptional()
    {
        var json = JsonSerializer.Serialize(_tool.InputSchema);
        var required = JsonDocument.Parse(json).RootElement.GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Equal(["solutionPath"], required);
    }

    [Fact]
    public async Task ExecuteAsync_NullArguments_ReturnsError()
    {
        var result = await _tool.ExecuteAsync(null, TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Contains("Arguments required", GetResultText(result));
    }

    [Fact]
    public async Task ExecuteAsync_EmptyArguments_ReturnsError()
    {
        var args = JsonDocument.Parse("{}").RootElement;
        var result = await _tool.ExecuteAsync(args, TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
    }

    private static string GetResultText(ToolResult result)
    {
        return result.Content.FirstOrDefault()?.Text ?? string.Empty;
    }
}
