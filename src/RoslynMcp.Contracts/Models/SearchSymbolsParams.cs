namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for searching symbols by name pattern across the solution.
/// </summary>
public sealed class SearchSymbolsParams
{
    /// <summary>
    /// Name pattern to search for. Supports substring matching.
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// Optional filter by symbol kind (e.g., "Class", "Method", "Property", "Namespace").
    /// </summary>
    public string? KindFilter { get; init; }

    /// <summary>
    /// Maximum number of results to return.
    /// </summary>
    public int? MaxResults { get; init; }

    /// <summary>
    /// Whether the symbol name must equal the query instead of containing it. Default: false.
    /// </summary>
    public bool? ExactMatch { get; init; }

    /// <summary>
    /// Whether the query is matched case-sensitively. Default: false.
    /// </summary>
    public bool? CaseSensitive { get; init; }

    /// <summary>
    /// Absolute path to a project file of the solution. Only that project (every target
    /// framework of it) is searched.
    /// </summary>
    public string? ProjectPath { get; init; }

    /// <summary>
    /// Only symbols declared in this namespace or one nested in it, matched by whole segments
    /// and case-insensitively: <c>App.Core</c> matches <c>App.Core.X</c> but not <c>App.CoreUtils</c>.
    /// </summary>
    public string? NamespaceFilter { get; init; }

    /// <summary>
    /// Absolute path to a source file. Only symbols declared in it (for a partial type, any
    /// of its declarations).
    /// </summary>
    public string? SourceFile { get; init; }
}
