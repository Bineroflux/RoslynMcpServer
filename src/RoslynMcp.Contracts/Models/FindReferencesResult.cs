namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Result of a find references query.
/// </summary>
public sealed class FindReferencesResult
{
    /// <summary>
    /// Name of the symbol that was searched.
    /// </summary>
    public required string SymbolName { get; init; }

    /// <summary>
    /// Fully qualified name of the symbol.
    /// </summary>
    public required string FullyQualifiedName { get; init; }

    /// <summary>
    /// All reference locations found.
    /// </summary>
    public required IReadOnlyList<ReferenceLocationInfo> References { get; init; }

    /// <summary>
    /// Total number of references found (may exceed References.Count if truncated).
    /// </summary>
    public required int TotalCount { get; init; }

    /// <summary>
    /// Whether the result was truncated due to maxResults.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>
    /// True when the symbol was resolved from Roslyn's error-tolerant candidate list
    /// rather than as a definitive match. This occurs when the source file contains
    /// compile errors and the semantic model is incomplete. References are best-effort.
    /// </summary>
    public bool SymbolIsCandidate { get; init; }

    /// <summary>
    /// Fully qualified names of all candidate symbols that were searched when
    /// <see cref="SymbolIsCandidate"/> is true. References are the union of all candidates.
    /// Empty when the symbol was resolved definitively.
    /// </summary>
    public IReadOnlyList<string> CandidateFullyQualifiedNames { get; init; } = [];

    /// <summary>
    /// Non-null when the caller-supplied line/column did not point at the requested
    /// symbol but the server recovered by finding a unique identifier with the
    /// expected name on that line. Describes the position actually used.
    /// </summary>
    public SymbolLocationOverride? LocationOverride { get; init; }
}
