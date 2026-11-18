namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the rename_namespace tool.
/// </summary>
public sealed class RenameNamespaceParams
{
    /// <summary>
    /// The namespace to rename, fully qualified (e.g. <c>App.Old</c>); its sub-namespaces are
    /// renamed with it.
    /// </summary>
    public required string OldNamespace { get; init; }

    /// <summary>
    /// The new fully qualified name (e.g. <c>App.New</c> or <c>Company.App.New</c>).
    /// </summary>
    public required string NewNamespace { get; init; }

    /// <summary>
    /// Also move the files of the renamed namespaces that sit in the folder of their old namespace
    /// into the folder of the new one, where the project's default globbing makes that safe; other
    /// files stay where they are. Default: false.
    /// </summary>
    public bool UpdateFolders { get; init; }

    /// <summary>
    /// Return the computed changes without applying them. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}
