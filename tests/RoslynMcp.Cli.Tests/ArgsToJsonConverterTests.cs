using System.Text.Json;
using RoslynMcp.Cli;
using Xunit;

namespace RoslynMcp.Cli.Tests;

public class ArgsToJsonConverterTests
{
    [Fact]
    public void EmptyDictionary_ReturnsEmptyJsonObject()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>());
        Assert.Equal("{}", json);
    }

    [Fact]
    public void StringValue_SerializedAsJsonString()
    {
        var dict = new Dictionary<string, string> { ["source-file"] = "Foo.cs" };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("Foo.cs", doc.RootElement.GetProperty("sourceFile").GetString());
    }

    [Fact]
    public void JsonArrayOrObject_IsPassedThrough()
    {
        // List parameters (extract-base-class --members, change-signature --parameters) could
        // not be given at all: the value always became a JSON string.
        var dict = new Dictionary<string, string>
        {
            ["members"] = "[\"Print\", \"Other\"]",
            ["parameters"] = "[{\"name\": \"x\", \"type\": \"int\"}]",
            ["new-name"] = "[not json"
        };
        var doc = JsonDocument.Parse(ArgsToJsonConverter.Convert(dict));
        Assert.Equal(["Print", "Other"], doc.RootElement.GetProperty("members").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("int", doc.RootElement.GetProperty("parameters")[0].GetProperty("type").GetString());
        Assert.Equal("[not json", doc.RootElement.GetProperty("newName").GetString());
    }

    [Fact]
    public void NumericValue_SerializedAsJsonNumber()
    {
        var dict = new Dictionary<string, string> { ["line"] = "42" };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal(42, doc.RootElement.GetProperty("line").GetInt32());
    }

    [Fact]
    public void BooleanTrue_SerializedAsJsonBool()
    {
        var dict = new Dictionary<string, string> { ["preview"] = "true" };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("preview").GetBoolean());
    }

    [Fact]
    public void BooleanFalse_SerializedAsJsonBool()
    {
        var dict = new Dictionary<string, string> { ["preview"] = "false" };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("preview").GetBoolean());
    }

    [Fact]
    public void MultipleOptions_AllConverted()
    {
        var dict = new Dictionary<string, string>
        {
            ["source-file"] = "C:/Code/Foo.cs",
            ["symbol-name"] = "Bar",
            ["line"] = "10",
            ["preview"] = "true"
        };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);

        Assert.Equal("C:/Code/Foo.cs", doc.RootElement.GetProperty("sourceFile").GetString());
        Assert.Equal("Bar", doc.RootElement.GetProperty("symbolName").GetString());
        Assert.Equal(10, doc.RootElement.GetProperty("line").GetInt32());
        Assert.True(doc.RootElement.GetProperty("preview").GetBoolean());
    }

    [Theory]
    [InlineData("source-file", "sourceFile")]
    [InlineData("line", "line")]
    [InlineData("symbol-name", "symbolName")]
    [InlineData("severity-filter", "severityFilter")]
    [InlineData("new-name", "newName")]
    [InlineData("all-files", "allFiles")]
    [InlineData("a-b-c-d", "aBCD")]
    public void KebabToCamel_ConvertsCorrectly(string kebab, string expected)
    {
        Assert.Equal(expected, ArgsToJsonConverter.KebabToCamel(kebab));
    }

    [Fact]
    public void KebabToCamel_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", ArgsToJsonConverter.KebabToCamel(""));
    }

    [Fact]
    public void PathWithBackslash_PreservedAsString()
    {
        var dict = new Dictionary<string, string> { ["source-file"] = @"C:\Code\Foo.cs" };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal(@"C:\Code\Foo.cs", doc.RootElement.GetProperty("sourceFile").GetString());
    }

    [Fact]
    public void LargeNumericValue_SerializedAsJsonNumber()
    {
        // 3_000_000_000 exceeds int.MaxValue (2_147_483_647) but fits in long
        var dict = new Dictionary<string, string> { ["big-value"] = "3000000000" };
        var json = ArgsToJsonConverter.Convert(dict);
        var doc = JsonDocument.Parse(json);
        Assert.Equal(3_000_000_000L, doc.RootElement.GetProperty("bigValue").GetInt64());
    }
}
