using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoslynMcp.Core.Arguments;

/// <summary>
/// An argument key that the object it appears in does not take.
/// </summary>
/// <param name="Path">
/// Where the object is: empty for the arguments themselves, <c>parameters[0]</c> for the first
/// entry of the <c>parameters</c> array.
/// </param>
/// <param name="Key">The key as given.</param>
/// <param name="Suggestion">The valid key it probably means, or null when none is close.</param>
/// <param name="ValidKeys">The keys the object takes, in schema order.</param>
public sealed record UnknownArgument(string Path, string Key, string? Suggestion, IReadOnlyList<string> ValidKeys)
{
    /// <summary>The key with its <see cref="Path"/>: <c>maxResults</c>, <c>parameters[0].name</c>.</summary>
    public string FullPath => Path.Length == 0 ? Key : $"{Path}.{Key}";
}

/// <summary>
/// A value its schema does not allow.
/// </summary>
/// <param name="Path">The key with its place: <c>line</c>, <c>parameters[0].newPosition</c>.</param>
/// <param name="Problem">What is wrong, to follow the path: <c>must be an integer, not "ten"</c>.</param>
public sealed record InvalidArgument(string Path, string Problem);

/// <summary>
/// Checks tool arguments against the subset of JSON Schema the tools declare: an object's
/// <c>properties</c>, whether it allows <c>additionalProperties</c>, an array's <c>items</c>,
/// and a value's <c>type</c>, <c>enum</c>, <c>minimum</c> and <c>maximum</c>. The server checks
/// the arguments of every call against the tool's input schema, the CLI checks its options
/// against a schema built from the tool's parameter type.
/// </summary>
public static class ArgumentSchema
{
    /// <summary>
    /// The keys of <paramref name="arguments"/> and of the objects nested in it that their
    /// schema does not declare, where the schema says <c>"additionalProperties": false</c>.
    /// Values the schema says nothing about are not looked into.
    /// </summary>
    /// <param name="schema">The schema of <paramref name="arguments"/>.</param>
    /// <param name="arguments">The arguments of one call.</param>
    /// <param name="ignoreCase">Whether a key matches a property in another case.</param>
    public static IReadOnlyList<UnknownArgument> FindUnknownArguments(JsonElement schema, JsonElement arguments, bool ignoreCase = false)
    {
        var found = new List<UnknownArgument>();
        Walk(schema, arguments, "", ignoreCase, found);
        return found;
    }

    /// <summary>
    /// The values in <paramref name="arguments"/> that their schema does not allow: of another
    /// type (an integer must be a whole number in the 32-bit range, or the 64-bit range where the
    /// schema says <c>"format": "int64"</c>), not one of the schema's <c>enum</c> values (compared
    /// ignoring case, as the tools read them), or outside its <c>minimum</c> / <c>maximum</c>.
    /// A property that is <c>null</c> counts as absent (see <see cref="WithoutNulls"/>); an array
    /// item that is <c>null</c> does not. Keys the schema does not declare are not looked at.
    /// </summary>
    /// <param name="schema">The schema of <paramref name="arguments"/>.</param>
    /// <param name="arguments">The arguments of one call, or one value of them.</param>
    /// <param name="ignoreCase">Whether a key matches a property in another case.</param>
    public static IReadOnlyList<InvalidArgument> FindInvalidValues(JsonElement schema, JsonElement arguments, bool ignoreCase = false)
    {
        var found = new List<InvalidArgument>();
        CheckValue(schema, arguments, "", ignoreCase, found);
        return found;
    }

    /// <summary>
    /// <paramref name="value"/> without the object properties that are <c>null</c>, at every
    /// level: a client may send <c>null</c> for an optional argument it means to leave out, and
    /// the tools read a missing argument as its default.
    /// </summary>
    public static JsonElement WithoutNulls(JsonElement value)
    {
        if (!ContainsNullProperty(value))
            return value;

        var node = JsonNode.Parse(value.GetRawText());
        RemoveNullProperties(node);
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>
    /// The property <paramref name="key"/> of an object schema, with its name as the schema
    /// spells it, or null when the schema declares no such property.
    /// </summary>
    public static (string Name, JsonElement Schema)? FindProperty(JsonElement schema, string key, bool ignoreCase = false)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in properties.EnumerateObject())
        {
            if (string.Equals(property.Name, key, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return (property.Name, property.Value);
        }

        return null;
    }

    /// <summary>The names of the properties an object schema declares, in schema order.</summary>
    public static IReadOnlyList<string> PropertyNames(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object &&
        schema.TryGetProperty("properties", out var properties) &&
        properties.ValueKind == JsonValueKind.Object
            ? properties.EnumerateObject().Select(p => p.Name).ToList()
            : [];

    /// <summary>The <c>type</c> a schema declares (<c>string</c>, <c>integer</c>, ...), or null.</summary>
    public static string? TypeOf(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object &&
        schema.TryGetProperty("type", out var type) &&
        type.ValueKind == JsonValueKind.String
            ? type.GetString()
            : null;

    /// <summary>
    /// Whether a property takes a file or directory path: a string whose name ends in
    /// <c>File</c> or <c>Path</c> (<c>sourceFile</c>, <c>targetFile</c>, <c>callerFile</c>,
    /// <c>projectPath</c>, <c>solutionPath</c>). Every tool names its path parameters that way
    /// and describes them as absolute paths; a test keeps the two in step.
    /// </summary>
    /// <param name="name">The property name, camelCase.</param>
    /// <param name="schema">The property's schema.</param>
    public static bool IsPathProperty(string name, JsonElement schema) =>
        TypeOf(schema) == "string" &&
        (name.EndsWith("File", StringComparison.Ordinal) || name.EndsWith("Path", StringComparison.Ordinal));

    /// <summary>
    /// The valid key that <paramref name="key"/> probably means: the one it equals ignoring case
    /// and the separators <c>_</c> and <c>-</c> (<c>MaxResults</c>, <c>max_results</c>), or else
    /// the closest one at an edit distance of at most 2 (and less than half the key's length).
    /// Null when there is no such key, or two are equally close.
    /// </summary>
    public static string? SuggestKey(string key, IEnumerable<string> validKeys)
    {
        var candidates = validKeys.ToList();
        var normalized = Normalize(key);
        var sameLetters = candidates.Where(c => Normalize(c) == normalized).ToList();
        if (sameLetters.Count == 1)
            return sameLetters[0];

        var maxDistance = Math.Min(2, (normalized.Length - 1) / 2);
        var closest = candidates
            .Select(c => (Key: c, Distance: EditDistance(normalized, Normalize(c))))
            .Where(c => c.Distance <= maxDistance)
            .OrderBy(c => c.Distance)
            .ToList();

        return closest.Count == 1 || (closest.Count > 1 && closest[0].Distance < closest[1].Distance)
            ? closest[0].Key
            : null;
    }

    private static void CheckValue(JsonElement schema, JsonElement value, string path, bool ignoreCase, List<InvalidArgument> found)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return;

        var type = TypeOf(schema);
        switch (type)
        {
            case "string" when value.ValueKind == JsonValueKind.String:
                CheckEnum(schema, value.GetString()!, path, found);
                return;

            case "integer" when value.ValueKind == JsonValueKind.Number:
                var isInt64 = schema.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String &&
                    format.GetString() == "int64";
                if (!value.TryGetInt64(out var number) || (!isInt64 && number is < int.MinValue or > int.MaxValue))
                {
                    var range = isInt64 ? "a whole number" : $"a whole number from {int.MinValue} to {int.MaxValue}";
                    found.Add(new InvalidArgument(path, $"must be {range}, not {Describe(value)}"));
                    return;
                }

                if (schema.TryGetProperty("minimum", out var minimum) && minimum.TryGetInt64(out var min) && number < min)
                    found.Add(new InvalidArgument(path, $"must be at least {min}, not {number}"));
                else if (schema.TryGetProperty("maximum", out var maximum) && maximum.TryGetInt64(out var max) && number > max)
                    found.Add(new InvalidArgument(path, $"must be at most {max}, not {number}"));
                return;

            case "number" when value.ValueKind == JsonValueKind.Number:
                return;

            case "boolean" when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                return;

            case "array" when value.ValueKind == JsonValueKind.Array:
                if (schema.TryGetProperty("items", out var items))
                {
                    var index = 0;
                    foreach (var item in value.EnumerateArray())
                    {
                        var itemPath = $"{path}[{index++}]";
                        if (item.ValueKind == JsonValueKind.Null && TypeOf(items) is { } itemType)
                            found.Add(new InvalidArgument(itemPath, $"must be {Expected(itemType)}, not null"));
                        else
                            CheckValue(items, item, itemPath, ignoreCase, found);
                    }
                }

                return;

            case "object" or null when value.ValueKind == JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Null &&
                        FindProperty(schema, property.Name, ignoreCase) is { } declared)
                    {
                        CheckValue(declared.Schema, property.Value, path.Length == 0 ? property.Name : $"{path}.{property.Name}", ignoreCase, found);
                    }
                }

                return;

            case null:
                return;

            default:
                found.Add(new InvalidArgument(path, $"must be {Expected(type)}, not {Describe(value)}"));
                return;
        }
    }

    private static void CheckEnum(JsonElement schema, string value, string path, List<InvalidArgument> found)
    {
        if (!schema.TryGetProperty("enum", out var values) || values.ValueKind != JsonValueKind.Array)
            return;

        var allowed = values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
        if (!allowed.Contains(value, StringComparer.OrdinalIgnoreCase))
            found.Add(new InvalidArgument(path, $"must be one of {string.Join(", ", allowed)}, not {JsonSerializer.Serialize(value)}"));
    }

    private static string Expected(string type) => type switch
    {
        "string" => "a string",
        "integer" => "an integer",
        "number" => "a number",
        "boolean" => "true or false",
        "array" => "an array",
        "object" => "an object",
        _ => type
    };

    /// <summary>A JSON value as a message shows it: <c>the string "ten"</c>, <c>1.5</c>, <c>an array</c>.</summary>
    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => $"the string {JsonSerializer.Serialize(Shorten(value.GetString()!))}",
        JsonValueKind.Number => Shorten(value.GetRawText()),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array => "an array",
        JsonValueKind.Object => "an object",
        _ => "null"
    };

    private static string Shorten(string text) => text.Length <= 60 ? text : text[..57] + "...";

    private static bool ContainsNullProperty(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.Null || ContainsNullProperty(p.Value)),
        JsonValueKind.Array => value.EnumerateArray().Any(ContainsNullProperty),
        _ => false
    };

    private static void RemoveNullProperties(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Where(p => p.Value == null).Select(p => p.Key).ToList())
                    obj.Remove(name);
                foreach (var (_, child) in obj)
                    RemoveNullProperties(child);
                break;

            case JsonArray array:
                foreach (var child in array)
                    RemoveNullProperties(child);
                break;
        }
    }

    private static void Walk(JsonElement schema, JsonElement value, string path, bool ignoreCase, List<UnknownArgument> found)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            if (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("items", out var items))
            {
                var index = 0;
                foreach (var item in value.EnumerateArray())
                    Walk(items, item, $"{path}[{index++}]", ignoreCase, found);
            }

            return;
        }

        if (value.ValueKind != JsonValueKind.Object || schema.ValueKind != JsonValueKind.Object)
            return;

        var closed = schema.TryGetProperty("additionalProperties", out var additional) &&
            additional.ValueKind == JsonValueKind.False;
        var validKeys = PropertyNames(schema);

        foreach (var property in value.EnumerateObject())
        {
            if (FindProperty(schema, property.Name, ignoreCase) is { } declared)
            {
                Walk(declared.Schema, property.Value, path.Length == 0 ? property.Name : $"{path}.{property.Name}", ignoreCase, found);
            }
            else if (closed)
            {
                found.Add(new UnknownArgument(path, property.Name, SuggestKey(property.Name, validKeys), validKeys));
            }
        }
    }

    private static string Normalize(string key) =>
        new(key.Where(c => c != '_' && c != '-').Select(char.ToLowerInvariant).ToArray());

    /// <summary>The Levenshtein distance between two strings.</summary>
    internal static int EditDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(substitution, Math.Min(previous[j] + 1, current[j - 1] + 1));
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
