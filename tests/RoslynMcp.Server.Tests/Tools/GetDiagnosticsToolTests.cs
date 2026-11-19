using System.Text.Json;
using RoslynMcp.Contracts.Models;
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
    [InlineData("includeAnalyzers", "boolean")]
    public void GetDefinition_HasFilteringParameters(string name, string type)
    {
        SchemaAssert.HasOptionalProperty(_tool, name, type);
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

    [Fact]
    public void GetDefinition_SaysAnalyzersRunOnlyWhenAskedAndAreSlow()
    {
        var properties = JsonDocument.Parse(JsonSerializer.Serialize(_tool.InputSchema)).RootElement.GetProperty("properties");
        var includeAnalyzers = properties.GetProperty("includeAnalyzers").GetProperty("description").GetString()!;

        Assert.Contains("only with includeAnalyzers", _tool.Description);
        Assert.Contains("slower", _tool.Description);
        Assert.Contains("SLOW", includeAnalyzers);
        Assert.Contains("EnforceCodeStyleInBuild", includeAnalyzers);
        Assert.Contains("default: false", includeAnalyzers);
        Assert.Contains("listed first and no filter drops them", includeAnalyzers);
        Assert.Contains("includeAnalyzers", properties.GetProperty("diagnosticIds").GetProperty("description").GetString());
    }

    [Fact]
    public void TargetFrameworks_IsOmittedFromTheJsonUnlessSet()
    {
        var plain = new DiagnosticInfo { Id = "CS0168", Message = "m", Severity = "Warning" };
        var multiTargeted = new DiagnosticInfo { Id = "CS0168", Message = "m", Severity = "Warning", TargetFrameworks = ["net8.0", "net9.0"] };

        var plainJson = JsonSerializer.Serialize(plain, ToolJsonOptions.Default);
        var multiJson = JsonDocument.Parse(JsonSerializer.Serialize(multiTargeted, ToolJsonOptions.Default)).RootElement;

        Assert.DoesNotContain("targetFrameworks", plainJson);
        Assert.Equal(["net8.0", "net9.0"], multiJson.GetProperty("targetFrameworks").EnumerateArray().Select(e => e.GetString()));
    }

    private static string GetResultText(ToolResult result)
    {
        return result.Content.FirstOrDefault()?.Text ?? string.Empty;
    }
}
