namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the move_type_to_namespace tool.
/// </summary>
public sealed class MoveTypeToNamespaceParams
{
    /// <summary>
    /// Absolute path to the source file containing the type.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the type to move (simple name or fully qualified).
    /// </summary>
    public required string SymbolName { get; init; }

    /// <summary>
    /// 1-based line number where symbol is declared (for disambiguation).
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// Target namespace (e.g., MyApp.Services).
    /// </summary>
    public required string TargetNamespace { get; init; }

    /// <summary>
    /// Also move the file into the folder that matches the target namespace relative to the
    /// project's root namespace, when that is safe; otherwise the file stays and the result
    /// reports <c>PARTIAL_SUCCESS</c> with the reason. Default: false.
    /// </summary>
    public bool UpdateFileLocation { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
