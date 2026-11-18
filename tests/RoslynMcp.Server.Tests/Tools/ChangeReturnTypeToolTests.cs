using System.Text.Json;
using RoslynMcp.Server.Tools;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// The change_return_type tool definition and argument handling.
/// </summary>
public sealed class ChangeReturnTypeToolTests
{
    private readonly ChangeReturnTypeTool _tool = new(null!);

    [Fact]
    public void Definition_NamesTheToolAndItsRequiredArguments()
    {
        var root = JsonDocument.Parse(JsonSerializer.Serialize(_tool.InputSchema)).RootElement;

        Assert.Equal("change_return_type", _tool.Name);
        Assert.False(string.IsNullOrWhiteSpace(_tool.Description));
        Assert.Equal(
            ["solutionPath", "sourceFile", "memberName", "newType"],
            root.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
    }

    [Theory]
    [InlineData("line", "integer")]
    [InlineData("preview", "boolean")]
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
