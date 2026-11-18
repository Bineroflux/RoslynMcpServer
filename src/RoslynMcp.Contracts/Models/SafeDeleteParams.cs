namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the safe_delete tool.
/// </summary>
public sealed class SafeDeleteParams
{
    /// <summary>
    /// Absolute path to a source file declaring or using the symbol.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the type or member to delete.
    /// </summary>
    public required string SymbolName { get; init; }

    /// <summary>
    /// 1-based line of the symbol, when several in the file have the name.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column of the symbol on that line.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Return the computed changes without applying them. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
