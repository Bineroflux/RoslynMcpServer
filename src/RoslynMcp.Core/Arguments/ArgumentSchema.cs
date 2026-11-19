using System.Text.Json;

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
/// Checks tool arguments against the subset of JSON Schema the tools declare: an object's
/// <c>properties</c>, whether it allows <c>additionalProperties</c>, and an array's
/// <c>items</c>. The server checks the arguments of every call against the tool's input schema,
/// the CLI checks its options against a schema built from the tool's parameter type.
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
