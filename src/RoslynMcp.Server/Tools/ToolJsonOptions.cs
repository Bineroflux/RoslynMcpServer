using System.Text.Encodings.Web;
using System.Text.Json;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// Shared serializer options for tool results: camelCase, tab-indented, and
/// non-ASCII/HTML-sensitive characters (umlauts, &lt;, &gt;, &amp;) emitted
/// verbatim instead of \uXXXX escapes. Safe here because tool output is plain
/// UTF-8 text content, never embedded in HTML.
/// </summary>
public static class ToolJsonOptions
{
    /// <summary>
    /// The options instance shared by all tool handlers.
    /// </summary>
    public static JsonSerializerOptions Default { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        IndentCharacter = '\t',
        IndentSize = 1,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
