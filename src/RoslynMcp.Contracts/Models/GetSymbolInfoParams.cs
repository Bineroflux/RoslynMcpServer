namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for getting detailed information about a symbol.
/// </summary>
public sealed class GetSymbolInfoParams
{
    /// <summary>
    /// Absolute path to the source file containing the symbol.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the symbol to examine.
    /// </summary>
    public string? SymbolName { get; init; }

    /// <summary>
    /// 1-based line number for position-based resolution.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column number for position-based resolution.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// For a type: only members of this symbol kind (e.g. "Method", "Property").
    /// </summary>
    public string? KindFilter { get; init; }

    /// <summary>
    /// For a type: only members whose name contains this case-insensitive substring.
    /// </summary>
    public string? NameFilter { get; init; }

    /// <summary>
    /// For a type: only members with exactly this declared accessibility: public, internal,
    /// protected, private, protected internal or private protected.
    /// </summary>
    public string? AccessibilityFilter { get; init; }

    /// <summary>
    /// For a type: maximum number of members to return.
    /// </summary>
    public int? MaxResults { get; init; }
}
