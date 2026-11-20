using System.Diagnostics;
using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Core.Refactoring.Base;

/// <summary>
/// Base class for refactoring operations providing common infrastructure.
/// </summary>
/// <typeparam name="TParams">Parameter type for this operation.</typeparam>
public abstract class RefactoringOperationBase<TParams> : IRefactoringOperation<TParams>
{
    /// <summary>
    /// The workspace context for this operation.
    /// </summary>
    protected WorkspaceContext Context { get; }

    /// <summary>
    /// Type symbol resolver for this workspace.
    /// </summary>
    protected TypeSymbolResolver TypeResolver { get; }

    /// <summary>
    /// Reference tracker for finding symbol usages.
    /// </summary>
    protected ReferenceTracker ReferenceTracker { get; }

    // Warnings of this execution's commits (or of the commit a preview stands for) and those
    // reported with AddWarnings, added to its result's own.
    private readonly List<string> _commitWarnings = [];

    // The commits the running attempt of ExecuteCoreAsync has made. One that finds files changed
    // on disk is computed again from the start only while this is 0: nothing is written then.
    private int _commitsMade;

    /// <summary>
    /// Adds warnings to this execution's result, preview or not, e.g. what laying out code
    /// moved into another file reported (<see cref="Utilities.LineEndings.KeptLiteralsWarning"/>).
    /// </summary>
    protected void AddWarnings(IEnumerable<string> warnings) => _commitWarnings.AddRange(warnings);

    /// <summary>
    /// Creates a new refactoring operation.
    /// </summary>
    /// <param name="context">Workspace context.</param>
    protected RefactoringOperationBase(WorkspaceContext context)
    {
        Context = context;
        TypeResolver = context.CreateSymbolResolver();
        ReferenceTracker = context.CreateReferenceTracker();
    }

    /// <summary>
    /// Executes the refactoring with standard error handling and timing.
    /// </summary>
    public async Task<RefactoringResult> ExecuteAsync(TParams @params, CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid();
        var workspaceLoadMs = WorkspaceTimingContext.LastLoadMs;
        var stopwatch = Stopwatch.StartNew();
        _commitWarnings.Clear();

        try
        {
            ValidateParams(@params);

            // Everything that reads the solution — and the commit and file moves that follow —
            // runs as the workspace's single writer, so a concurrent refactoring can neither
            // build on a snapshot this one replaces nor commit over it. Previews take the writer
            // too: they compute against the snapshot a commit would build on.
            var result = await Context.RunWriterAsync(
                ct => ExecuteOnDiskStateAsync(operationId, @params, ct), cancellationToken);
            stopwatch.Stop();
            return WithTiming(result, stopwatch.ElapsedMilliseconds, workspaceLoadMs, _commitWarnings);
        }
        catch (RefactoringException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new RefactoringException(ErrorCodes.Timeout, "Operation was cancelled.");
        }
        catch (Exception ex)
        {
            throw new RefactoringException(
                ErrorCodes.RoslynError,
                $"Unexpected error: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// How often a refactoring is computed at most when its commit keeps finding files changed
    /// on disk: the first attempt and two recomputations. Two servers refactoring one file back
    /// to back can each write it while the other computes, more than once in a row.
    /// </summary>
    internal const int MaxAttempts = 3;

    /// <summary>
    /// Runs <see cref="ExecuteCoreAsync"/> inside the writer section. When its commit finds a
    /// file it would write, delete or create changed on disk — another program (an IDE, another
    /// RoslynMcp server) wrote it after the workspace read it — the commit has written nothing:
    /// the files are taken over from disk and the refactoring is computed again from the start
    /// on them, up to <see cref="MaxAttempts"/> times in all, and the result's warnings name each
    /// file and attempt. That is safe because an operation computes its change from
    /// <see cref="Context"/>'s solution and touches the disk only in and after its commit. If the
    /// last attempt's commit finds files changed again, the refactoring fails with
    /// <see cref="ErrorCodes.WorkspaceBusy"/>, nothing written.
    /// </summary>
    private async Task<RefactoringResult> ExecuteOnDiskStateAsync(
        Guid operationId, TParams @params, CancellationToken cancellationToken)
    {
        var recomputed = new List<string>();
        for (var attempt = 1; ; attempt++)
        {
            IReadOnlyList<DiskConflict> conflicts;
            _commitsMade = 0;
            try
            {
                var result = await ExecuteCoreAsync(operationId, @params, cancellationToken);
                _commitWarnings.InsertRange(0, recomputed);
                return result;
            }
            catch (DiskConflictException changed)
            {
                conflicts = changed.Conflicts;
            }

            if (attempt == MaxAttempts)
            {
                throw new RefactoringException(
                    ErrorCodes.WorkspaceBusy,
                    $"Files changed on disk while the refactoring ran, each of the {MaxAttempts} times it was computed " +
                    $"(another program is writing them): {DiskConflict.Describe(conflicts)}. " +
                    "Nothing was written; run it again once the other program is done.");
            }

            await Context.TakeOverDiskConflictsAsync(conflicts, cancellationToken);

            // This attempt's warnings come again with the next one.
            _commitWarnings.Clear();
            recomputed.AddRange(conflicts.Select(c => c.RecomputedWarning(attempt + 1, MaxAttempts)));
        }
    }

    /// <summary>
    /// Unwinds an execution whose commit found files changed on disk (and wrote nothing) to
    /// <see cref="ExecuteOnDiskStateAsync"/>, which computes it again.
    /// </summary>
    private sealed class DiskConflictException(IReadOnlyList<DiskConflict> conflicts)
        : Exception("Files changed on disk since the refactoring read them; nothing was written.")
    {
        public IReadOnlyList<DiskConflict> Conflicts { get; } = conflicts;
    }

    /// <summary>
    /// Validates the operation parameters. Override to add custom validation.
    /// </summary>
    /// <param name="params">Parameters to validate.</param>
    protected abstract void ValidateParams(TParams @params);

    /// <summary>
    /// Executes the core operation logic.
    /// </summary>
    /// <param name="operationId">Unique operation identifier.</param>
    /// <param name="params">Operation parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Refactoring result.</returns>
    protected abstract Task<RefactoringResult> ExecuteCoreAsync(
        Guid operationId,
        TParams @params,
        CancellationToken cancellationToken);

    /// <summary>
    /// Commits solution changes to the filesystem. The commit's warnings (e.g. a file saved
    /// as UTF-8 because its code page cannot represent the new text) are added to the
    /// operation's result.
    /// </summary>
    /// <param name="newSolution">Solution with changes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Commit result.</returns>
    protected Task<CommitResult> CommitChangesAsync(Solution newSolution, CancellationToken cancellationToken) =>
        CommitChangesAsync(newSolution, newFilesEncodingFrom: null, cancellationToken);

    /// <summary>
    /// Commits solution changes to the filesystem; a file the commit creates takes the
    /// encoding of <paramref name="newFilesEncodingFrom"/> unless an <c>.editorconfig</c> names
    /// one (see <see cref="WorkspaceContext.CommitChangesAsync(Solution, string?, CancellationToken)"/>).
    /// The commit's warnings are added to the operation's result. When the commit finds files
    /// changed on disk since the workspace read them, it writes nothing and this does not return:
    /// the operation is computed again on them (see <see cref="ExecuteOnDiskStateAsync"/>), so
    /// nothing may be written to disk before the commit.
    /// </summary>
    /// <param name="newSolution">Solution with changes.</param>
    /// <param name="newFilesEncodingFrom">File the content of added documents comes from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Commit result.</returns>
    protected async Task<CommitResult> CommitChangesAsync(
        Solution newSolution, string? newFilesEncodingFrom, CancellationToken cancellationToken)
    {
        var result = await Context.CommitChangesAsync(newSolution, newFilesEncodingFrom, cancellationToken);
        if (result.Conflicts.Count > 0)
        {
            // Nothing of this commit was written. An earlier one of the same execution was, so
            // the execution cannot simply run again.
            if (_commitsMade > 0)
            {
                throw new RefactoringException(
                    ErrorCodes.WorkspaceBusy,
                    $"Files changed on disk while the refactoring was being written (another program wrote them): " +
                    $"{DiskConflict.Describe(result.Conflicts)}. Its earlier part was written, the rest was not.");
            }

            throw new DiskConflictException(result.Conflicts);
        }

        if (!result.Success)
        {
            throw new RefactoringException(
                ErrorCodes.FilesystemError,
                $"Failed to write files: {result.Error}");
        }

        _commitsMade++;
        _commitWarnings.AddRange(result.Warnings);
        return result;
    }

    /// <summary>
    /// The preview's counterpart of <see cref="CommitChangesAsync(Solution, CancellationToken)"/>:
    /// adds to the preview's result what committing <paramref name="newSolution"/> would warn
    /// about (a file that would be saved as UTF-8 because its code page cannot represent the
    /// new text) or fail on (a file whose bytes are not valid in its encoding, which a commit
    /// refuses to rewrite), without writing anything. Every preview calls it with the solution
    /// its commit would write.
    /// </summary>
    /// <param name="newSolution">Solution with the changes the commit would write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    protected Task PreviewCommitAsync(Solution newSolution, CancellationToken cancellationToken) =>
        PreviewCommitAsync(newSolution, newFilesEncodingFrom: null, cancellationToken);

    /// <inheritdoc cref="PreviewCommitAsync(Solution, CancellationToken)"/>
    /// <param name="newSolution">Solution with the changes the commit would write.</param>
    /// <param name="newFilesEncodingFrom">As for <see cref="CommitChangesAsync(Solution, string?, CancellationToken)"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    protected async Task PreviewCommitAsync(
        Solution newSolution, string? newFilesEncodingFrom, CancellationToken cancellationToken)
    {
        _commitWarnings.AddRange(await Context.GetCommitWarningsAsync(newSolution, newFilesEncodingFrom, cancellationToken));
    }

    /// <summary>
    /// <paramref name="warnings"/> followed by the <paramref name="additional"/> ones it does
    /// not contain yet; null when there are none.
    /// </summary>
    internal static IReadOnlyList<string>? MergeWarnings(IReadOnlyList<string>? warnings, IEnumerable<string> additional)
    {
        var merged = new List<string>(warnings ?? []);
        foreach (var warning in additional)
        {
            if (!merged.Contains(warning))
                merged.Add(warning);
        }

        return merged.Count > 0 ? merged : warnings;
    }

    /// <summary>
    /// Gets a document by file path, throwing if not found.
    /// </summary>
    /// <param name="filePath">Absolute file path.</param>
    /// <returns>The document.</returns>
    protected Document GetDocumentOrThrow(string filePath)
    {
        var doc = Context.GetDocumentByPath(filePath);
        if (doc == null)
        {
            throw new RefactoringException(
                ErrorCodes.SourceNotInWorkspace,
                $"File not found in workspace: {filePath}");
        }
        return doc;
    }

    private static RefactoringResult WithTiming(
        RefactoringResult result, long elapsedMs, long workspaceLoadMs, IReadOnlyList<string> commitWarnings)
    {
        // Preview results intentionally don't carry timing (and commit nothing), but they carry
        // what the commit would warn about (PreviewCommitAsync).
        if (result.Preview)
        {
            return commitWarnings.Count == 0
                ? result
                : Copy(result, result.ExecutionTimeMs, result.WorkspaceLoadMs, result.TotalExecutionTimeMs, commitWarnings);
        }

        var opMs = result.ExecutionTimeMs > 0 ? result.ExecutionTimeMs : elapsedMs;
        return Copy(result, opMs, workspaceLoadMs, opMs + workspaceLoadMs, commitWarnings);
    }

    private static RefactoringResult Copy(
        RefactoringResult result, long executionMs, long workspaceLoadMs, long totalMs, IReadOnlyList<string> commitWarnings) =>
        new()
        {
            Success = result.Success,
            OperationId = result.OperationId,
            Preview = result.Preview,
            Changes = result.Changes,
            Symbol = result.Symbol,
            ReferencesUpdated = result.ReferencesUpdated,
            UsingDirectivesAdded = result.UsingDirectivesAdded,
            UsingDirectivesRemoved = result.UsingDirectivesRemoved,
            ExecutionTimeMs = executionMs,
            WorkspaceLoadMs = workspaceLoadMs,
            TotalExecutionTimeMs = totalMs,
            Error = result.Error,
            PendingChanges = result.PendingChanges,
            Warnings = MergeWarnings(result.Warnings, commitWarnings)
        };
}
