namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for finding implementations of an interface or abstract member.
/// </summary>
public sealed class FindImplementationsParams
{
    /// <summary>
    /// Absolute path to the source file containing the symbol.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the interface, abstract class, or virtual member.
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
    /// Maximum number of implementations to return.
    /// </summary>
    public int? MaxResults { get; init; }

    /// <summary>
    /// Whether implementations through a base class or a derived interface are included.
    /// Default: true; false returns only types that list the interface themselves (and, for
    /// an interface member, their implementations of it).
    /// </summary>
    public bool? Transitive { get; init; }
}
