namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Result of a get_document_outline query.
/// </summary>
public sealed class GetDocumentOutlineResult
{
    /// <summary>
    /// File path that was analyzed.
    /// </summary>
    public required string File { get; init; }

    /// <summary>
    /// Top-level outline entries.
    /// </summary>
    public required IReadOnlyList<OutlineEntry> Entries { get; init; }

    /// <summary>
    /// Total count of entries in the outline after the filters, the containing namespaces and
    /// types kept for them included (may exceed the entries returned if truncated).
    /// </summary>
    public required int TotalCount { get; init; }

    /// <summary>
    /// Whether the outline was truncated due to maxResults.
    /// </summary>
    public bool Truncated { get; init; }
}

/// <summary>
/// A single entry in the document outline.
/// </summary>
public sealed class OutlineEntry
{
    /// <summary>
    /// Symbol name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Symbol kind (Class, Method, Property, etc.).
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// 1-based line number.
    /// </summary>
    public required int Line { get; init; }

    /// <summary>
    /// 1-based column number.
    /// </summary>
    public required int Column { get; init; }

    /// <summary>
    /// Accessibility modifier (public, private, etc.).
    /// </summary>
    public string? Accessibility { get; init; }

    /// <summary>
    /// Return type or type annotation (for methods, properties, fields).
    /// </summary>
    public string? ReturnType { get; init; }

    /// <summary>
    /// Child entries (methods inside class, etc.).
    /// </summary>
    public IReadOnlyList<OutlineEntry>? Children { get; init; }
}
