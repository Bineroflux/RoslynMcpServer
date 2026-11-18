using System.Text.Json;
using RoslynMcp.Server.Tools;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// The find_unused_symbols tool definition and argument handling.
/// </summary>
public sealed class FindUnusedSymbolsToolTests
{
    private readonly FindUnusedSymbolsTool _tool = new(null!);

    [Fact]
    public void Definition_NamesTheToolAndOnlyRequiresTheSolution()
    {
        var root = JsonDocument.Parse(JsonSerializer.Serialize(_tool.InputSchema)).RootElement;

        Assert.Equal("find_unused_symbols", _tool.Name);
        Assert.Contains("candidates", _tool.Description, StringComparison.Ordinal);
        Assert.Contains("reflection", _tool.Description, StringComparison.Ordinal);
        Assert.Equal(["solutionPath"], root.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
    }

    [Theory]
    [InlineData("projectPath", "string")]
    [InlineData("sourceFile", "string")]
    [InlineData("kindFilter", "string")]
    [InlineData("includePublic", "boolean")]
    [InlineData("includeAttributed", "boolean")]
    [InlineData("maxResults", "integer")]
    public void Definition_HasOptionalArguments(string name, string type)
    {
        SchemaAssert.HasOptionalProperty(_tool, name, type);
    }

    [Fact]
    public async Task NullArguments_AreAnError()
    {
        var result = await _tool.ExecuteAsync(null, TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("Arguments required", result.Content.FirstOrDefault()?.Text);
    }
}
