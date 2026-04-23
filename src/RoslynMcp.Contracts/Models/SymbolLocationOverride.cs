namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Describes a server-side recovery that was applied when the caller's
/// supplied line/column did not point directly at the requested symbol
/// but the line contained exactly one identifier token matching the
/// requested symbol name.
/// </summary>
public sealed class SymbolLocationOverride
{
    /// <summary>
    /// The 1-based line the caller supplied.
    /// </summary>
    public required int RequestedLine { get; init; }

    /// <summary>
    /// The 1-based column the caller supplied, if any.
    /// </summary>
    public int? RequestedColumn { get; init; }

    /// <summary>
    /// The 1-based line actually used to resolve the symbol.
    /// </summary>
    public required int ResolvedLine { get; init; }

    /// <summary>
    /// The 1-based column actually used to resolve the symbol.
    /// </summary>
    public required int ResolvedColumn { get; init; }

    /// <summary>
    /// Human-readable explanation of the recovery.
    /// </summary>
    public required string Reason { get; init; }
}
