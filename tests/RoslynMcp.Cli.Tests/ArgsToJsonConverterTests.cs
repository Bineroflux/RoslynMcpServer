using System.Text.Json;
using RoslynMcp.Cli;
using RoslynMcp.Contracts.Models;
using Xunit;

namespace RoslynMcp.Cli.Tests;

public class ArgsToJsonConverterTests
{
    private static readonly JsonElement RenameSchema = ParamsSchema.For(typeof(RenameSymbolParams));

    [Fact]
    public void EmptyDictionary_ReturnsEmptyJsonObject()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>(), RenameSchema);
        Assert.Equal("{}", json);
    }

    [Fact]
    public void StringValue_SerializedAsJsonString()
    {
        var dict = new Dictionary<string, string> { ["source-file"] = @"C:\Code\Foo.cs" };
        var json = ArgsToJsonConverter.Convert(dict, RenameSchema);
        var doc = JsonDocument.Parse(json);
        Assert.Equal(@"C:\Code\Foo.cs", doc.RootElement.GetProperty("sourceFile").GetString());
    }

    [Fact]
    public void JsonArrayOrObject_IsPassedThrough()
    {
        // List parameters (extract-base-class --members, change-signature --parameters) could
        // not be given at all: the value always became a JSON string.
        var members = ArgsToJsonConverter.Convert(
            new Dictionary<string, string> { ["members"] = "[\"Print\", \"Other\"]" },
            ParamsSchema.For(typeof(ExtractBaseClassParams)));
        var parameters = ArgsToJsonConverter.Convert(
            new Dictionary<string, string> { ["parameters"] = "[{\"name\": \"x\", \"type\": \"int\"}]", ["method-name"] = "[not json" },
            ParamsSchema.For(typeof(ChangeSignatureParams)));

        Assert.Equal(["Print", "Other"], JsonDocument.Parse(members).RootElement.GetProperty("members").EnumerateArray().Select(e => e.GetString()));
        var doc = JsonDocument.Parse(parameters);
        Assert.Equal("int", doc.RootElement.GetProperty("parameters")[0].GetProperty("type").GetString());
        Assert.Equal("[not json", doc.RootElement.GetProperty("methodName").GetString());
    }

    [Fact]
    public void NumericValue_SerializedAsJsonNumber()
    {
        var dict = new Dictionary<string, string> { ["line"] = "42" };
        var json = ArgsToJsonConverter.Convert(dict, RenameSchema);
        var doc = JsonDocument.Parse(json);
        Assert.Equal(42, doc.RootElement.GetProperty("line").GetInt32());
    }

    [Fact]
    public void BooleanTrue_SerializedAsJsonBool()
    {
        var dict = new Dictionary<string, string> { ["preview"] = "true" };
        var json = ArgsToJsonConverter.Convert(dict, RenameSchema);
        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("preview").GetBoolean());
    }

    [Fact]
    public void BooleanFalse_SerializedAsJsonBool()
    {
        var dict = new Dictionary<string, string> { ["preview"] = "false" };
        var json = ArgsToJsonConverter.Convert(dict, RenameSchema);
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
        var json = ArgsToJsonConverter.Convert(dict, RenameSchema);
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
        var json = ArgsToJsonConverter.Convert(dict, RenameSchema);
        var doc = JsonDocument.Parse(json);
        Assert.Equal(@"C:\Code\Foo.cs", doc.RootElement.GetProperty("sourceFile").GetString());
    }

    [Fact]
    public void OptionInAnotherCase_IsTheToolsOption()
    {
        // The CLI reads parameters case-insensitively, so --Source-File still reaches sourceFile.
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string> { ["Source-File"] = @"C:\Code\Foo.cs" }, RenameSchema);

        Assert.Equal(@"C:\Code\Foo.cs", JsonDocument.Parse(json).RootElement.GetProperty("sourceFile").GetString());
    }

    [Fact]
    public void UnknownOptions_AreRefused_WithTheClosestOptionAndTheValidOnes()
    {
        // A mistyped option used to be dropped silently, so the tool ran with its default.
        var ex = Assert.Throws<CliOptionException>(() => ArgsToJsonConverter.Convert(
            new Dictionary<string, string> { ["new-nam"] = "Baz", ["colour"] = "red", ["line"] = "3" },
            RenameSchema));

        Assert.StartsWith("Unknown options '--new-nam' (did you mean '--new-name'?), '--colour'.", ex.Message);
        Assert.Contains("Valid options: --source-file, ", ex.Message);
        Assert.Contains("--new-name", ex.Message.Split("Valid options:")[1]);
        Assert.Contains("--rename-overloads", ex.Message);
    }

    [Fact]
    public void UnknownKeyInAJsonValue_IsRefused_WithItsPlace()
    {
        var ex = Assert.Throws<CliOptionException>(() => ArgsToJsonConverter.Convert(
            new Dictionary<string, string>
            {
                ["parameters"] = """[{"originalName": "a", "name": "b"}, {"orignalName": "c", "Name": "d"}]"""
            },
            ParamsSchema.For(typeof(ChangeSignatureParams))));

        Assert.Equal(
            "Unknown key 'orignalName' (did you mean 'originalName'?) in --parameters[1], " +
            "which takes: originalName, name, type, defaultValue, newPosition, remove.",
            ex.Message);
    }
}
