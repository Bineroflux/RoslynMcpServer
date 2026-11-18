namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the get_type_hierarchy query.
/// </summary>
public sealed class GetTypeHierarchyParams
{
    /// <summary>
    /// Absolute path to the source file containing the type.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the type symbol.
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
    /// Direction: Ancestors, Descendants, or Both. Default: Both.
    /// </summary>
    public string? Direction { get; init; }

    /// <summary>
    /// Whether derived types include indirect ones. Default: true; false returns only the
    /// types deriving directly from the type (for an interface: the interfaces and types
    /// that list it themselves).
    /// </summary>
    public bool? Transitive { get; init; }

    /// <summary>
    /// Maximum number of derived types to return.
    /// </summary>
    public int? MaxResults { get; init; }
}
