using System.Text.Json;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Arguments;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// Checks the arguments of a tool call against the tool's input schema before the tool runs.
/// A tool deserializes its arguments leniently, so a key it does not know (a typo, or
/// <c>MaxResults</c> for <c>maxResults</c>) would silently be dropped and the option take its
/// default; such a call is refused instead.
/// </summary>
public static class ToolArguments
{
    /// <summary>
    /// The error result for arguments the schema does not allow, or null when they are fine.
    /// Missing arguments are left to the tool, which reports them as before.
    /// </summary>
    /// <param name="toolName">The tool's name, for the message.</param>
    /// <param name="schema">The tool's input schema.</param>
    /// <param name="arguments">The call's arguments, null when the call has none.</param>
    public static ToolResult? Check(string toolName, JsonElement schema, JsonElement? arguments)
    {
        if (arguments is not { } value || value.ValueKind == JsonValueKind.Null)
            return null;

        if (value.ValueKind != JsonValueKind.Object)
        {
            return Error(
                ErrorCodes.InvalidParameterValue,
                $"The arguments of {toolName} must be a JSON object, not {value.ValueKind.ToString().ToLowerInvariant()}.",
                details: null);
        }

        var unknown = ArgumentSchema.FindUnknownArguments(schema, value);
        if (unknown.Count == 0)
            return null;

        return Error(
            ErrorCodes.UnknownParameter,
            DescribeUnknown(toolName, unknown),
            new Dictionary<string, object>
            {
                ["unknownArguments"] = unknown.Select(u => u.FullPath).ToList(),
                ["validArguments"] = ArgumentSchema.PropertyNames(schema)
            });
    }

    /// <summary>
    /// One sentence per object with unknown keys: the keys, what each probably means, and the
    /// keys the object takes.
    /// </summary>
    internal static string DescribeUnknown(string toolName, IReadOnlyList<UnknownArgument> unknown)
    {
        var sentences = unknown.GroupBy(u => u.Path).Select(group =>
        {
            var keys = Join(group.Select(u => u.Suggestion == null ? $"'{u.Key}'" : $"'{u.Key}' (did you mean '{u.Suggestion}'?)").ToList());
            var valid = string.Join(", ", group.First().ValidKeys);
            var plural = group.Count() > 1;
            return group.Key.Length == 0
                ? $"Unknown argument{(plural ? "s" : "")} {keys}. {toolName} takes: {valid}."
                : $"Unknown key{(plural ? "s" : "")} {keys} in {group.Key}, which takes: {valid}.";
        }).ToList();

        if (unknown.Any(u => u.Suggestion != null && string.Equals(u.Key, u.Suggestion, StringComparison.OrdinalIgnoreCase)))
            sentences.Add("Argument names are case-sensitive.");

        return string.Join(" ", sentences);
    }

    private static string Join(IReadOnlyList<string> items) =>
        items.Count == 1 ? items[0] : $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}";

    private static ToolResult Error(string code, string message, Dictionary<string, object>? details)
    {
        var error = RefactoringError.Create(code, message, details);
        return ToolResult.Error(JsonSerializer.Serialize(new { success = false, error }, ToolJsonOptions.Default));
    }
}
