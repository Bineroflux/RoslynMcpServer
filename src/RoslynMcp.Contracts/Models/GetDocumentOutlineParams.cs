namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the get_document_outline query.
/// </summary>
public sealed class GetDocumentOutlineParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Maximum nesting depth of the declarations to include; namespaces don't count.
    /// 1 = top-level types only, 2 = plus their members, and so on. If null, all.
    /// </summary>
    public int? MaxDepth { get; init; }

    /// <summary>
    /// Only entries of this symbol kind (e.g. "Method"; constructors count as Method, enum
    /// members as Constant), kept with their containing namespaces and types.
    /// </summary>
    public string? KindFilter { get; init; }

    /// <summary>
    /// Only entries whose name contains this case-insensitive substring, kept with their
    /// containing namespaces and types.
    /// </summary>
    public string? NameFilter { get; init; }

    /// <summary>
    /// Maximum number of entries to return, counted depth-first with parents before children.
    /// </summary>
    public int? MaxResults { get; init; }
}
