using System.Text.Json;
using RoslynMcp.Core.Arguments;

namespace RoslynMcp.Cli;

/// <summary>
/// Converts CLI option dictionaries (kebab-case keys) to JSON strings (camelCase keys)
/// suitable for deserializing into params DTOs.
/// </summary>
public static class ArgsToJsonConverter
{
    /// <summary>
    /// Convert a dictionary of kebab-case CLI options to a camelCase JSON string, checked
    /// against the tool's schema (see <see cref="ParamsSchema"/>).
    /// </summary>
    /// <remarks>
    /// Rules:
    /// - --kebab-case → camelCase key, which the tool must take (case does not matter)
    /// - Numeric string values → JSON numbers
    /// - "true"/"false" (case-insensitive) → JSON booleans
    /// - A JSON array or object (<c>--members '["A","B"]'</c>) → that JSON, for list parameters;
    ///   the keys of the objects in it must be keys the tool takes there
    /// - Everything else → JSON strings
    /// </remarks>
    /// <exception cref="CliOptionException">An option, or a key inside a JSON value, the tool does not take.</exception>
    public static string Convert(Dictionary<string, string> options, JsonElement schema)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
        var unknownOptions = new List<string>();
        var unknownKeys = new List<UnknownArgument>();

        writer.WriteStartObject();

        foreach (var (kebabKey, value) in options)
        {
            if (ArgumentSchema.FindProperty(schema, KebabToCamel(kebabKey), ignoreCase: true) is not { } property)
            {
                unknownOptions.Add(kebabKey);
                continue;
            }

            var (camelKey, propertySchema) = property;

            if (bool.TryParse(value, out var boolVal))
            {
                writer.WriteBoolean(camelKey, boolVal);
            }
            else if (long.TryParse(value, out var longVal))
            {
                writer.WriteNumber(camelKey, longVal);
            }
            else if (TryParseJson(value) is { } json)
            {
                using (json)
                {
                    unknownKeys.AddRange(ArgumentSchema.FindUnknownArguments(propertySchema, json.RootElement, ignoreCase: true)
                        .Select(u => u with { Path = OptionPath(kebabKey, u.Path) }));
                    writer.WritePropertyName(camelKey);
                    json.RootElement.WriteTo(writer);
                }
            }
            else
            {
                writer.WriteString(camelKey, value);
            }
        }

        writer.WriteEndObject();
        writer.Flush();

        if (unknownOptions.Count > 0 || unknownKeys.Count > 0)
            throw new CliOptionException(DescribeUnknown(schema, unknownOptions, unknownKeys));

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// The option a key inside a JSON value belongs to: <c>--parameters[0]</c>.
    /// </summary>
    private static string OptionPath(string kebabKey, string path) =>
        path.Length == 0 || path.StartsWith('[') ? $"--{kebabKey}{path}" : $"--{kebabKey}.{path}";

    private static string DescribeUnknown(JsonElement schema, List<string> unknownOptions, List<UnknownArgument> unknownKeys)
    {
        var sentences = new List<string>();
        if (unknownOptions.Count > 0)
        {
            var valid = ArgumentSchema.PropertyNames(schema);
            var options = unknownOptions.Select(option =>
                ArgumentSchema.SuggestKey(KebabToCamel(option), valid) is { } suggestion
                    ? $"'--{option}' (did you mean '--{HelpGenerator.PascalToKebab(suggestion)}'?)"
                    : $"'--{option}'");
            sentences.Add(
                $"Unknown option{(unknownOptions.Count > 1 ? "s" : "")} {string.Join(", ", options)}. " +
                $"Valid options: {string.Join(", ", valid.Select(v => "--" + HelpGenerator.PascalToKebab(v)))}.");
        }

        foreach (var group in unknownKeys.GroupBy(u => u.Path))
        {
            var keys = group.Select(u => u.Suggestion == null ? $"'{u.Key}'" : $"'{u.Key}' (did you mean '{u.Suggestion}'?)");
            sentences.Add(
                $"Unknown key{(group.Count() > 1 ? "s" : "")} {string.Join(", ", keys)} in {group.Key}, " +
                $"which takes: {string.Join(", ", group.First().ValidKeys)}.");
        }

        return string.Join(" ", sentences);
    }

    private static JsonDocument? TryParseJson(string value)
    {
        if (!value.StartsWith('[') && !value.StartsWith('{'))
            return null;

        try
        {
            return JsonDocument.Parse(value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Convert a kebab-case string to camelCase.
    /// </summary>
    /// <example>"source-file" → "sourceFile", "line" → "line"</example>
    public static string KebabToCamel(string kebab)
    {
        if (string.IsNullOrEmpty(kebab))
            return kebab;

        var parts = kebab.Split('-');
        if (parts.Length == 1)
            return parts[0].ToLowerInvariant();

        // First segment is lowercase, subsequent segments are title-cased
        var result = parts[0].ToLowerInvariant();
        for (int i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
            {
                result += char.ToUpperInvariant(parts[i][0]) + parts[i][1..].ToLowerInvariant();
            }
        }

        return result;
    }
}
