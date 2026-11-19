namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for finding types and members that nothing references.
/// </summary>
public sealed class FindUnusedSymbolsParams
{
    /// <summary>
    /// Absolute path to a project file of the solution. Only symbols declared in that project
    /// (every target framework of it) are reported; references are still searched wherever the
    /// symbols are visible.
    /// </summary>
    public string? ProjectPath { get; init; }

    /// <summary>
    /// Absolute path to a source file. Only symbols declared in it (a partial type when any of
    /// its declarations is there) are reported.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// Only symbols of this kind: Class, Struct, Interface, Enum, Record, Delegate, Method,
    /// Property, Field, Event or Constant.
    /// </summary>
    public string? KindFilter { get; init; }

    /// <summary>
    /// Also report symbols visible outside their assembly (public, or protected in a public
    /// type that can be derived from). Default: false.
    /// </summary>
    public bool? IncludePublic { get; init; }

    /// <summary>
    /// Also report symbols that carry attributes (test methods, serialized or interop members),
    /// and fields of types laid out for interop or serialization. Default: false.
    /// </summary>
    public bool? IncludeAttributed { get; init; }

    /// <summary>
    /// Also report fields and auto-properties that are assigned but never read, with the reason
    /// <c>write-only</c>. Default: true.
    /// </summary>
    public bool? IncludeWriteOnly { get; init; }

    /// <summary>
    /// Maximum number of symbols to return. Default: 100.
    /// </summary>
    public int? MaxResults { get; init; }
}
