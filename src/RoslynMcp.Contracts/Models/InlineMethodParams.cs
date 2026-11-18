namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the inline_method tool.
/// </summary>
public sealed class InlineMethodParams
{
    /// <summary>
    /// Absolute path to the source file declaring the method.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the method to inline.
    /// </summary>
    public required string MethodName { get; init; }

    /// <summary>
    /// 1-based line of the method's declaration, when the file declares several methods of that name.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// Absolute path of the file holding the one call to inline. Default: <see cref="SourceFile"/>
    /// when <see cref="CallLine"/> is given.
    /// </summary>
    public string? CallFile { get; init; }

    /// <summary>
    /// 1-based line of the one call to inline. Without it, every call is inlined.
    /// </summary>
    public int? CallLine { get; init; }

    /// <summary>
    /// 1-based column of the method name in the call, when the line has several calls.
    /// </summary>
    public int? CallColumn { get; init; }

    /// <summary>
    /// Delete the method once nothing references it any more. Default: true when every call is
    /// inlined, false when one call is picked.
    /// </summary>
    public bool? DeleteMethod { get; init; }

    /// <summary>
    /// Return the computed changes without applying them. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
