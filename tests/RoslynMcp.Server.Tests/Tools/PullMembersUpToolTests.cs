using System.Text.Json;
using RoslynMcp.Server.Tools;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// The pull_members_up tool definition and argument handling.
/// </summary>
public sealed class PullMembersUpToolTests
{
    private readonly PullMembersUpTool _tool = new(null!);

    [Fact]
    public void Definition_NamesTheToolAndItsRequiredArguments()
    {
        var root = JsonDocument.Parse(JsonSerializer.Serialize(_tool.InputSchema)).RootElement;

        Assert.Equal("pull_members_up", _tool.Name);
        Assert.Contains("makeAbstract", _tool.Description, StringComparison.Ordinal);
        Assert.Equal(
            ["solutionPath", "sourceFile", "typeName", "members"],
            root.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("string", root.GetProperty("properties").GetProperty("members").GetProperty("items").GetProperty("type").GetString());
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
    }

    [Theory]
    [InlineData("line", "integer")]
    [InlineData("targetType", "string")]
    [InlineData("targetFile", "string")]
    [InlineData("makeAbstract", "boolean")]
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
