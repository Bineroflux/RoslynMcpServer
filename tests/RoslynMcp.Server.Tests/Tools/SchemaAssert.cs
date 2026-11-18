using System.Text.Json;
using RoslynMcp.Server.Tools;
using Xunit;

namespace RoslynMcp.Server.Tests.Tools;

/// <summary>
/// Assertions on a tool's input schema.
/// </summary>
internal static class SchemaAssert
{
    /// <summary>
    /// The schema declares <paramref name="name"/> as an optional property of
    /// <paramref name="type"/> with a description; an array's items are strings.
    /// </summary>
    public static void HasOptionalProperty(IToolHandler tool, string name, string type)
    {
        var root = JsonDocument.Parse(JsonSerializer.Serialize(tool.InputSchema)).RootElement;

        var property = root.GetProperty("properties").GetProperty(name);
        Assert.Equal(type, property.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(property.GetProperty("description").GetString()));
        if (type == "array")
            Assert.Equal("string", property.GetProperty("items").GetProperty("type").GetString());

        var required = root.GetProperty("required").EnumerateArray().Select(e => e.GetString());
        Assert.DoesNotContain(name, required);
    }
}
