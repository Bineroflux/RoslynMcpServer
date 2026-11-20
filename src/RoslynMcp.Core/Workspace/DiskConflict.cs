namespace RoslynMcp.Core.Workspace;

/// <summary>
/// A file a commit was about to write, delete or create that is no longer on disk what the
/// snapshot the refactoring was computed on has for it: another program (an IDE saving the file,
/// another RoslynMcp server) wrote, deleted or created it after the workspace read it. Writing the
/// change would have reverted that, so the commit wrote nothing (see
/// <see cref="CommitResult.Conflicts"/>).
/// </summary>
/// <param name="FilePath">The file.</param>
/// <param name="Kind">What is different on disk.</param>
public sealed record DiskConflict(string FilePath, DiskConflictKind Kind)
{
    /// <summary>What is different, e.g. <c>'C:\src\A.cs' changed on disk</c>.</summary>
    public string Description => Kind switch
    {
        DiskConflictKind.Changed => $"'{FilePath}' changed on disk",
        DiskConflictKind.Deleted => $"'{FilePath}' is gone from disk",
        _ => $"'{FilePath}' exists on disk but not in the workspace",
    };

    /// <summary>
    /// The warning of a refactoring that found this conflict at its commit, took the file over
    /// and was computed again.
    /// </summary>
    internal string RecomputedWarning => Kind switch
    {
        DiskConflictKind.Changed =>
            $"'{FilePath}' changed on disk while the refactoring ran (another program wrote it); it was computed again on the new text.",
        DiskConflictKind.Deleted =>
            $"'{FilePath}' was deleted on disk while the refactoring ran (another program deleted it); it was computed again without it.",
        _ =>
            $"'{FilePath}' was created on disk while the refactoring ran (another program created it); it was computed again with it.",
    };

    /// <summary>The warning of a preview computed on a snapshot that is behind the disk for this file.</summary>
    internal string PreviewWarning =>
        $"The workspace is behind the disk for '{FilePath}' (another program {Kind switch
        {
            DiskConflictKind.Changed => "changed",
            DiskConflictKind.Deleted => "deleted",
            _ => "created",
        }} it): this preview was computed without that; applying it re-reads the file and computes the change again.";

    /// <summary>The <see cref="Description"/>s of <paramref name="conflicts"/>, joined.</summary>
    internal static string Describe(IEnumerable<DiskConflict> conflicts) =>
        string.Join("; ", conflicts.Select(c => c.Description));
}

/// <summary>What a <see cref="DiskConflict"/> found different on disk.</summary>
public enum DiskConflictKind
{
    /// <summary>The file's text is not the text the snapshot has for it.</summary>
    Changed,

    /// <summary>The snapshot has documents at the path, but the file is gone.</summary>
    Deleted,

    /// <summary>A file exists at a path where the snapshot has none, which the commit would create.</summary>
    Created,
}
