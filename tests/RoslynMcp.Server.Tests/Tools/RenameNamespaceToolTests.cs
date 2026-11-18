using System.Text.Json;
using RoslynMcp.Server.Tools;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// The rename_namespace tool definition and argument handling.
/// </summary>
public sealed class RenameNamespaceToolTests
{
    private readonly RenameNamespaceTool _tool = new(null!);

    [Fact]
    public void Definition_NamesTheToolAndItsRequiredArguments()
    {
        var root = JsonDocument.Parse(JsonSerializer.Serialize(_tool.InputSchema)).RootElement;

        Assert.Equal("rename_namespace", _tool.Name);
        Assert.Contains("updateFolders", _tool.Description, StringComparison.Ordinal);
        Assert.Equal(
            ["solutionPath", "oldNamespace", "newNamespace"],
            root.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
    }

    [Theory]
    [InlineData("updateFolders", "boolean")]
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
