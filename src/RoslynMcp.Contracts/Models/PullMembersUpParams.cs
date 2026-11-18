namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the pull_members_up tool.
/// </summary>
public sealed class PullMembersUpParams
{
    /// <summary>
    /// Absolute path to the source file declaring the type.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the type whose members move up.
    /// </summary>
    public required string TypeName { get; init; }

    /// <summary>
    /// Names of the members to move: fields, methods (every overload), properties, events, and
    /// indexers as <c>this</c>.
    /// </summary>
    public required IReadOnlyList<string> Members { get; init; }

    /// <summary>
    /// 1-based line of the type's declaration, when the file declares several types of that name.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// The type to move them into: the direct base class (the default) or an interface the type
    /// implements, by simple or qualified name.
    /// </summary>
    public string? TargetType { get; init; }

    /// <summary>
    /// For a base class: declare methods, properties, events and indexers there as abstract and keep
    /// the implementations as overrides. Default: false.
    /// </summary>
    public bool MakeAbstract { get; init; }

    /// <summary>
    /// Return the computed changes without applying them. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
