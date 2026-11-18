namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the find_callers query.
/// </summary>
public sealed class FindCallersParams
{
    /// <summary>
    /// Absolute path to the source file containing the symbol.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the symbol to find callers for.
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
    /// Maximum number of callers to return.
    /// </summary>
    public int? MaxResults { get; init; }

    /// <summary>
    /// Absolute path to a source file. Only call sites located in it.
    /// </summary>
    public string? CallerFile { get; init; }

    /// <summary>
    /// Absolute path to a project file of the solution. Only call sites located in that
    /// project (every target framework of it).
    /// </summary>
    public string? ProjectPath { get; init; }
}
