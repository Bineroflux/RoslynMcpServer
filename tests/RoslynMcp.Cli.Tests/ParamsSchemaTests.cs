using System.Reflection;
using System.Text.Json;
using RoslynMcp.Cli;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Arguments;
using Xunit;

namespace RoslynMcp.Cli.Tests;

/// <summary>
/// The CLI checks its options against a schema built from each tool's parameter type, so the
/// schema has to offer every option the help lists.
/// </summary>
public class ParamsSchemaTests
{
    [Fact]
    public void EveryTool_TakesExactlyTheOptionsItsHelpLists()
    {
        foreach (var tool in ToolRegistry.BuildDefault().GetAllTools())
        {
            var helpOptions = tool.ParamsType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => HelpGenerator.PascalToKebab(p.Name))
                .Order(StringComparer.Ordinal);
            var schemaOptions = ArgumentSchema.PropertyNames(ParamsSchema.For(tool.ParamsType))
                .Select(HelpGenerator.PascalToKebab)
                .Order(StringComparer.Ordinal);

            Assert.True(helpOptions.SequenceEqual(schemaOptions), $"{tool.Name}: help {string.Join(", ", helpOptions)}; schema {string.Join(", ", schemaOptions)}");
        }
    }

    [Fact]
    public void Types_FollowTheParameterTypes_AndListsOfObjectsAreClosed()
    {
        var schema = ParamsSchema.For(typeof(ChangeSignatureParams));

        Assert.Equal("string", Type("sourceFile"));
        Assert.Equal("integer", Type("line"));
        Assert.Equal("boolean", Type("preview"));
        Assert.Equal("array", Type("parameters"));
        var items = schema.GetProperty("properties").GetProperty("parameters").GetProperty("items");
        Assert.Equal(["originalName", "name", "type", "defaultValue", "newPosition", "remove"], ArgumentSchema.PropertyNames(items));
        Assert.Equal(JsonValueKind.False, items.GetProperty("additionalProperties").ValueKind);
        Assert.Equal(JsonValueKind.False, schema.GetProperty("additionalProperties").ValueKind);

        string? Type(string name) => ArgumentSchema.TypeOf(schema.GetProperty("properties").GetProperty(name));
    }
}
