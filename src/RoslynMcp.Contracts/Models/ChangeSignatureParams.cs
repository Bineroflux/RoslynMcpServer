namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the change_signature tool.
/// </summary>
public sealed class ChangeSignatureParams
{
    /// <summary>
    /// Absolute path to the source file containing the method.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the method to modify.
    /// </summary>
    public required string MethodName { get; init; }

    /// <summary>
    /// Line number for disambiguation if multiple methods have the same name (1-based).
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// The changes to the parameter list, one entry per changed or added parameter. Parameters
    /// that are not listed stay, in their relative order.
    /// </summary>
    public required IReadOnlyList<ParameterChange> Parameters { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

/// <summary>
/// One entry of change_signature's <c>parameters</c>: a change to an existing parameter (with
/// <see cref="OriginalName"/>) or a new parameter (without). A field that is not given leaves
/// that part of an existing parameter as it is.
/// </summary>
public sealed class ParameterChange
{
    /// <summary>
    /// The name of the parameter to change, as the method declares it; null to add a parameter.
    /// </summary>
    public string? OriginalName { get; init; }

    /// <summary>
    /// The new name (a rename), or the name of a new parameter (required then). Null keeps the name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// The new type, or the type of a new parameter (required then). Null keeps the type.
    /// </summary>
    public string? Type { get; init; }

    /// <summary>
    /// The default value as a C# expression. For an existing parameter: null keeps its default,
    /// an empty string removes it (calls that left the argument out then pass the old default). A new parameter with a default value is left out of the
    /// existing calls; one without gets a <c>default /* TODO */</c> placeholder in them.
    /// </summary>
    public string? DefaultValue { get; init; }

    /// <summary>
    /// The 0-based position in the new parameter list. Without it an existing parameter keeps
    /// its place among the parameters that do not move, and a new one goes at the end.
    /// </summary>
    public int? NewPosition { get; init; }

    /// <summary>
    /// If true, removes the parameter named by <see cref="OriginalName"/>; the other fields must
    /// then be absent or equal to what the parameter has.
    /// </summary>
    public bool Remove { get; init; }
}
