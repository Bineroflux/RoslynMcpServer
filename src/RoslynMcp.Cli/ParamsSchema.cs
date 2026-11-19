using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace RoslynMcp.Cli;

/// <summary>
/// The input schema of a CLI tool, built from its parameter type the way the CLI's
/// deserializer reads it: one property per settable property (camelCase), typed
/// <c>string</c>, <c>integer</c>, <c>boolean</c>, <c>array</c> (with its <c>items</c>) or
/// <c>object</c>, and no other properties allowed. It is the CLI's counterpart of the MCP
/// tools' input schemas, which the CLI does not have.
/// </summary>
public static class ParamsSchema
{
    private static readonly ConcurrentDictionary<Type, JsonElement> Cache = new();

    /// <summary>The schema of <paramref name="paramsType"/>.</summary>
    public static JsonElement For(Type paramsType) =>
        Cache.GetOrAdd(paramsType, type => JsonSerializer.SerializeToElement(Build(type, [])));

    private static JsonObject Build(Type type, HashSet<Type> visiting)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(string) || underlying.IsEnum)
            return new JsonObject { ["type"] = "string" };

        if (underlying == typeof(int) || underlying == typeof(short))
            return new JsonObject { ["type"] = "integer" };

        if (underlying == typeof(long))
            return new JsonObject { ["type"] = "integer", ["format"] = "int64" };

        if (underlying == typeof(bool))
            return new JsonObject { ["type"] = "boolean" };

        if (ElementType(underlying) is { } element)
            return new JsonObject { ["type"] = "array", ["items"] = Build(element, visiting) };

        if (!underlying.IsClass || !visiting.Add(underlying))
            return new JsonObject();

        var properties = new JsonObject();
        foreach (var property in ToolRegistry.JsonOptions.GetTypeInfo(underlying).Properties)
        {
            if (property.Set != null)
                properties[property.Name] = Build(property.PropertyType, visiting);
        }

        visiting.Remove(underlying);
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false
        };
    }

    private static Type? ElementType(Type type)
    {
        if (type.IsArray)
            return type.GetElementType();

        return type.IsGenericType && type.GetGenericArguments() is [var argument] &&
            typeof(IEnumerable<>).MakeGenericType(argument).IsAssignableFrom(type)
            ? argument
            : null;
    }
}
