namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for finding all references to a symbol.
/// </summary>
public sealed class FindReferencesParams
{
    /// <summary>
    /// Absolute path to the source file containing the symbol.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the symbol to find references for.
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
    /// Maximum number of references to return.
    /// </summary>
    public int? MaxResults { get; init; }

    /// <summary>
    /// Whether the symbol's declarations are included. Default: true; false returns usages only.
    /// </summary>
    public bool? IncludeDeclaration { get; init; }

    /// <summary>
    /// Absolute path to a source file. Only references located in it.
    /// </summary>
    public string? ReferenceFile { get; init; }

    /// <summary>
    /// Absolute path to a project file of the solution. Only references located in that
    /// project (every target framework of it).
    /// </summary>
    public string? ProjectPath { get; init; }
}
