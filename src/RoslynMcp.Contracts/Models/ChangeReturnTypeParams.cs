namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the change_return_type tool.
/// </summary>
public sealed class ChangeReturnTypeParams
{
    /// <summary>
    /// Absolute path to the source file declaring the method, property or indexer.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the method or property; <c>this</c> for an indexer.
    /// </summary>
    public required string MemberName { get; init; }

    /// <summary>
    /// Line of the declaration (1-based), to pick one of several with the name.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// The new type as C# source text (e.g. <c>long</c>, <c>Task&lt;string?&gt;</c>, <c>void</c>),
    /// resolved where the member is declared.
    /// </summary>
    public required string NewType { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
