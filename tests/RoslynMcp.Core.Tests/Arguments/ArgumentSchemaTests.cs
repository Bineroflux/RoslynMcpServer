using System.Text.Json;
using RoslynMcp.Core.Arguments;
using Xunit;

namespace RoslynMcp.Core.Tests.Arguments;

public sealed class ArgumentSchemaTests
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "sourceFile": { "type": "string" },
            "maxResults": { "type": "integer" },
            "parameters": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": { "originalName": { "type": "string" }, "name": { "type": "string" } },
                "additionalProperties": false
              }
            },
            "open": { "type": "object", "properties": { "a": { "type": "string" } } }
          },
          "additionalProperties": false
        }
        """).RootElement;

    [Fact]
    public void KnownKeys_AreFine()
    {
        var unknown = Find("""{"sourceFile": "a", "maxResults": 2, "parameters": [{"originalName": "x", "name": "y"}]}""");

        Assert.Empty(unknown);
    }

    [Fact]
    public void UnknownKeys_AreFound_AtEveryLevel_WithSuggestions()
    {
        var unknown = Find("""{"MaxResults": 2, "foo": 1, "parameters": [{"name": "y"}, {"orignalName": "x", "nam": "y"}]}""");

        Assert.Equal(
            [
                ("", "MaxResults", "maxResults"),
                ("", "foo", null),
                ("parameters[1]", "orignalName", "originalName"),
                ("parameters[1]", "nam", "name")
            ],
            unknown.Select(u => (u.Path, u.Key, u.Suggestion)));
        Assert.Equal("parameters[1].orignalName", unknown[2].FullPath);
        Assert.Equal(["originalName", "name"], unknown[2].ValidKeys);
    }

    [Fact]
    public void IgnoringCase_AcceptsKeysInAnotherCase()
    {
        var unknown = Find("""{"MaxResults": 2, "Parameters": [{"OriginalName": "x"}]}""", ignoreCase: true);

        Assert.Empty(unknown);
    }

    [Fact]
    public void ObjectsThatAllowOtherKeys_AreNotChecked()
    {
        Assert.Empty(Find("""{"open": {"b": 1}}"""));
    }

    [Theory]
    [InlineData("max_results", "maxResults")]
    [InlineData("MAX-RESULTS", "maxResults")]
    [InlineData("sourcefile", "sourceFile")]
    [InlineData("sourceFiles", "sourceFile")]
    [InlineData("maxResult", "maxResults")]
    [InlineData("filePath", null)]
    [InlineData("x", null)]
    public void SuggestKey_FindsTheKeyMeant(string key, string? expected)
    {
        Assert.Equal(expected, ArgumentSchema.SuggestKey(key, ["sourceFile", "maxResults", "line", "column"]));
    }

    [Fact]
    public void SuggestKey_SuggestsNothing_WhenTwoKeysAreEquallyClose()
    {
        Assert.Null(ArgumentSchema.SuggestKey("lin", ["line", "link"]));
    }

    [Fact]
    public void Values_AreCheckedByType_WithInt64WhereTheSchemaSaysSo()
    {
        var schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "small": { "type": "integer" },
                "big": { "type": "integer", "format": "int64" },
                "ratio": { "type": "number" },
                "names": { "type": "array", "items": { "type": "string" } }
              }
            }
            """).RootElement;

        var invalid = ArgumentSchema.FindInvalidValues(schema, JsonDocument.Parse("""
            {"small": 5000000000, "big": 5000000000, "ratio": 0.5, "names": ["a", 1, null], "other": "x"}
            """).RootElement);

        Assert.Equal(
            [
                ("small", "must be a whole number from -2147483648 to 2147483647, not 5000000000"),
                ("names[1]", "must be a string, not 1"),
                ("names[2]", "must be a string, not null")
            ],
            invalid.Select(i => (i.Path, i.Problem)));
    }

    [Fact]
    public void WithoutNulls_RemovesNullPropertiesAtEveryLevel_ButNotArrayItems()
    {
        var value = JsonDocument.Parse("""{"a": null, "b": [{"c": null, "d": 1}, null], "e": {"f": null}}""").RootElement;

        Assert.Equal("""{"b":[{"d":1},null],"e":{}}""", ArgumentSchema.WithoutNulls(value).GetRawText());
    }

    private static IReadOnlyList<UnknownArgument> Find(string arguments, bool ignoreCase = false) =>
        ArgumentSchema.FindUnknownArguments(Schema, JsonDocument.Parse(arguments).RootElement, ignoreCase);
}
