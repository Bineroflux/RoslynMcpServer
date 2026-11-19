using System.Text.Json;
using RoslynMcp.Cli;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Arguments;
using Xunit;

namespace RoslynMcp.Cli.Tests;

public class ArgsToJsonConverterTests
{
    private static readonly JsonElement RenameSchema = ParamsSchema.For(typeof(RenameSymbolParams));

    /// <summary>An absolute path on any platform.</summary>
    private static readonly string FooFile = Path.Combine(Path.GetTempPath(), "Code", "Foo.cs");

    [Fact]
    public void EmptyDictionary_ReturnsEmptyJsonObject()
    {
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string>(), RenameSchema);
        Assert.Equal("{}", json);
    }

    [Fact]
    public void StringValue_SerializedAsJsonString()
    {
        var dict = new Dictionary<string, string> { ["symbol-name"] = "Foo" };
        var json = ArgsToJsonConverter.Convert(dict, RenameSchema);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("Foo", doc.RootElement.GetProperty("symbolName").GetString());
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
            ["source-file"] = FooFile,
            ["symbol-name"] = "Bar",
            ["line"] = "10",
            ["preview"] = "true"
        };
        var json = ArgsToJsonConverter.Convert(dict, RenameSchema);
        var doc = JsonDocument.Parse(json);

        Assert.Equal(FooFile, doc.RootElement.GetProperty("sourceFile").GetString());
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
    public void AbsolutePath_IsPassedAsItIs()
    {
        var dict = new Dictionary<string, string> { ["source-file"] = FooFile };
        var json = ArgsToJsonConverter.Convert(dict, RenameSchema, baseDirectory: Path.Combine(Path.GetTempPath(), "elsewhere"));
        var doc = JsonDocument.Parse(json);
        Assert.Equal(FooFile, doc.RootElement.GetProperty("sourceFile").GetString());
    }

    [Fact]
    public void RelativePaths_AreResolvedAgainstTheBaseDirectory()
    {
        // The help's own example, --source-file Foo.cs, was refused by the operations, which
        // take only absolute paths; only the solution path was resolved.
        var baseDirectory = Path.Combine(Path.GetTempPath(), "repo", "src");
        var json = ArgsToJsonConverter.Convert(
            new Dictionary<string, string>
            {
                ["source-file"] = "Foo.cs",
                ["reference-file"] = Path.Combine("..", "tests", "FooTests.cs"),
                ["project-path"] = Path.Combine("App", "App.csproj"),
                ["symbol-name"] = "Foo.cs"
            },
            ParamsSchema.For(typeof(FindReferencesParams)),
            baseDirectory);

        var doc = JsonDocument.Parse(json).RootElement;
        Assert.Equal(Path.Combine(baseDirectory, "Foo.cs"), doc.GetProperty("sourceFile").GetString());
        Assert.Equal(Path.Combine(Path.GetTempPath(), "repo", "tests", "FooTests.cs"), doc.GetProperty("referenceFile").GetString());
        Assert.Equal(Path.Combine(baseDirectory, "App", "App.csproj"), doc.GetProperty("projectPath").GetString());
        Assert.Equal("Foo.cs", doc.GetProperty("symbolName").GetString());
    }

    [Fact]
    public void PathOptions_AreTheToolsFileAndPathParameters()
    {
        // The CLI resolves the string parameters named *File or *Path; these are all of them.
        var paths = ToolRegistry.BuildDefault().GetAllTools()
            .SelectMany(t =>
            {
                var schema = ParamsSchema.For(t.ParamsType);
                return ArgumentSchema.PropertyNames(schema)
                    .Where(n => ArgumentSchema.IsPathProperty(n, schema.GetProperty("properties").GetProperty(n)));
            })
            .Distinct()
            .Order(StringComparer.Ordinal);

        Assert.Equal(["callFile", "callerFile", "projectPath", "referenceFile", "solutionPath", "sourceFile", "targetFile"], paths);
    }

    [Fact]
    public void OptionInAnotherCase_IsTheToolsOption()
    {
        // The CLI reads parameters case-insensitively, so --Source-File still reaches sourceFile.
        var json = ArgsToJsonConverter.Convert(new Dictionary<string, string> { ["Source-File"] = FooFile }, RenameSchema);

        Assert.Equal(FooFile, JsonDocument.Parse(json).RootElement.GetProperty("sourceFile").GetString());
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

    [Theory]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("[Foo]")]
    [InlineData("-1")]
    public void StringParameter_StaysAString_HoweverItLooks(string query)
    {
        // search-symbols --query 42 failed: the value became a JSON number, which a string
        // parameter cannot take; [Foo] became a JSON array.
        var json = ArgsToJsonConverter.Convert(
            new Dictionary<string, string> { ["query"] = query, ["max-results"] = "7", ["exact-match"] = "TRUE" },
            ParamsSchema.For(typeof(SearchSymbolsParams)));

        var @params = JsonSerializer.Deserialize<SearchSymbolsParams>(json, JsonOptions)!;
        Assert.Equal(query, @params.Query);
        Assert.Equal(7, @params.MaxResults);
        Assert.True(@params.ExactMatch);
    }

    [Theory]
    [InlineData("line", "abc", "--line takes an integer, not 'abc'.")]
    [InlineData("line", "3000000000", "--line takes an integer, not '3000000000'.")]
    [InlineData("line", "1.5", "--line takes an integer, not '1.5'.")]
    [InlineData("preview", "yes", "--preview takes true or false, not 'yes'.")]
    public void InvalidValues_AreRefused(string option, string value, string message)
    {
        var ex = Assert.Throws<CliOptionException>(() => ArgsToJsonConverter.Convert(
            new Dictionary<string, string> { [option] = value }, RenameSchema));

        Assert.Equal(message, ex.Message);
    }

    [Theory]
    [InlineData("Print", "--members takes a JSON array, such as '[\"A\", \"B\"]', not 'Print'.")]
    [InlineData("{\"a\": 1}", "--members takes a JSON array, not '{\"a\": 1}'.")]
    public void ListParameter_TakesAJsonArray(string value, string message)
    {
        var ex = Assert.Throws<CliOptionException>(() => ArgsToJsonConverter.Convert(
            new Dictionary<string, string> { ["members"] = value }, ParamsSchema.For(typeof(ExtractBaseClassParams))));

        Assert.Equal(message, ex.Message);
    }

    [Fact]
    public void UnknownOptionsAndInvalidValues_AreReportedTogether()
    {
        var ex = Assert.Throws<CliOptionException>(() => ArgsToJsonConverter.Convert(
            new Dictionary<string, string> { ["line"] = "x", ["colour"] = "red" }, RenameSchema));

        Assert.StartsWith("Unknown option '--colour'. Valid options: ", ex.Message);
        Assert.EndsWith(" --line takes an integer, not 'x'.", ex.Message);
    }

    // The options the CLI's registry deserializes params with.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}
