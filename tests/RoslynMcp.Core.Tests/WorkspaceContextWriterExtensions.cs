using Microsoft.CodeAnalysis;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Tests;

/// <summary>
/// Changes a test's workspace the way a refactoring does: in a writer section of its own,
/// from the solution as read inside that section.
/// </summary>
internal static class WorkspaceContextWriterExtensions
{
    /// <summary>Commits <paramref name="change"/> applied to the current solution.</summary>
    public static Task<CommitResult> CommitInWriterAsync(
        this WorkspaceContext context, Func<Solution, Solution> change, CancellationToken cancellationToken = default) =>
        context.RunWriterAsync(ct => context.CommitChangesAsync(change(context.Solution), ct), cancellationToken);

    /// <summary>Replaces the solution (no files are written) with <paramref name="change"/> applied to it.</summary>
    public static Task UpdateSolutionInWriterAsync(
        this WorkspaceContext context, Func<Solution, Solution> change, CancellationToken cancellationToken = default) =>
        context.RunWriterAsync(
            _ =>
            {
                context.UpdateSolution(change(context.Solution));
                return Task.CompletedTask;
            },
            cancellationToken);
}
