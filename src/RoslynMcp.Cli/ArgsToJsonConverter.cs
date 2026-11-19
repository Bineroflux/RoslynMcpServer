using System.Globalization;
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
    /// - The value is converted by the parameter's type in the schema, never by how it looks:
    ///   a string parameter stays a string (<c>--query 42</c>, <c>--query true</c>); an integer
    ///   parameter takes an integer and a boolean one <c>true</c> or <c>false</c>
    ///   (case-insensitive), anything else is refused
    /// - A list parameter takes a JSON array (<c>--members '["A","B"]'</c>), passed on without
    ///   the properties that are <c>null</c>; the keys and values of the objects in it must be
    ///   ones the tool takes there
    /// - A path parameter (<see cref="ArgumentSchema.IsPathProperty"/>) that is relative is
    ///   resolved against <paramref name="baseDirectory"/>, the way a command line names files;
    ///   the tools take only absolute paths
    /// </remarks>
    /// <param name="options">The tool options, kebab-case keys.</param>
    /// <param name="schema">The tool's schema (<see cref="ParamsSchema.For"/>).</param>
    /// <param name="baseDirectory">The directory relative paths are resolved against; null for the current directory.</param>
    /// <exception cref="CliOptionException">
    /// An option, or a key inside a JSON value, the tool does not take, or a value its parameter cannot take.
    /// </exception>
    public static string Convert(Dictionary<string, string> options, JsonElement schema, string? baseDirectory = null)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
        var unknownOptions = new List<string>();
        var unknownKeys = new List<UnknownArgument>();
        var invalidValues = new List<string>();

        writer.WriteStartObject();

        foreach (var (kebabKey, value) in options)
        {
            if (ArgumentSchema.FindProperty(schema, KebabToCamel(kebabKey), ignoreCase: true) is not { } property)
            {
                unknownOptions.Add(kebabKey);
                continue;
            }

            var (camelKey, propertySchema) = property;

            switch (ArgumentSchema.TypeOf(propertySchema))
            {
                case "integer" when TryParseInteger(value, propertySchema, out var number):
                    writer.WriteNumber(camelKey, number);
                    break;

                case "integer":
                    invalidValues.Add($"--{kebabKey} takes an integer, not '{value}'.");
                    break;

                case "boolean" when bool.TryParse(value, out var flag):
                    writer.WriteBoolean(camelKey, flag);
                    break;

                case "boolean":
                    invalidValues.Add($"--{kebabKey} takes true or false, not '{value}'.");
                    break;

                case "array" or "object" when TryParseJson(value) is { } json:
                    using (json)
                    {
                        if (json.RootElement.ValueKind.ToString().ToLowerInvariant() != ArgumentSchema.TypeOf(propertySchema))
                        {
                            invalidValues.Add($"--{kebabKey} takes a JSON {ArgumentSchema.TypeOf(propertySchema)}, not '{value}'.");
                            break;
                        }

                        unknownKeys.AddRange(ArgumentSchema.FindUnknownArguments(propertySchema, json.RootElement, ignoreCase: true)
                            .Select(u => u with { Path = OptionPath(kebabKey, u.Path) }));
                        invalidValues.AddRange(ArgumentSchema.FindInvalidValues(propertySchema, json.RootElement, ignoreCase: true)
                            .Select(i => $"{OptionPath(kebabKey, i.Path)} {i.Problem}."));
                        writer.WritePropertyName(camelKey);
                        ArgumentSchema.WithoutNulls(json.RootElement).WriteTo(writer);
                    }

                    break;

                case "array":
                    invalidValues.Add($"--{kebabKey} takes a JSON array, such as '[\"A\", \"B\"]', not '{value}'.");
                    break;

                case "object":
                    invalidValues.Add($"--{kebabKey} takes a JSON object, not '{value}'.");
                    break;

                case "string" when ArgumentSchema.IsPathProperty(camelKey, propertySchema):
                    writer.WriteString(camelKey, ResolvePath(value, baseDirectory));
                    break;

                default:
                    writer.WriteString(camelKey, value);
                    break;
            }
        }

        writer.WriteEndObject();
        writer.Flush();

        if (unknownOptions.Count > 0 || unknownKeys.Count > 0 || invalidValues.Count > 0)
        {
            var sentences = invalidValues.ToList();
            if (unknownOptions.Count > 0 || unknownKeys.Count > 0)
                sentences.Insert(0, DescribeUnknown(schema, unknownOptions, unknownKeys));
            throw new CliOptionException(string.Join(" ", sentences));
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// <paramref name="path"/> resolved against <paramref name="baseDirectory"/> (or the current
    /// directory) unless it is already absolute; an empty value stays empty.
    /// </summary>
    public static string ResolvePath(string path, string? baseDirectory = null) =>
        string.IsNullOrWhiteSpace(path) || Path.IsPathFullyQualified(path)
            ? path
            : Path.GetFullPath(path, baseDirectory ?? Environment.CurrentDirectory);

    /// <summary>
    /// Parses an integer in the range of the parameter: 64-bit where the schema says
    /// <c>"format": "int64"</c>, 32-bit otherwise.
    /// </summary>
    private static bool TryParseInteger(string value, JsonElement schema, out long number)
    {
        var isInt64 = schema.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String && format.GetString() == "int64";
        if (isInt64)
            return long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number);

        var parsed = int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var int32);
        number = int32;
        return parsed;
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
