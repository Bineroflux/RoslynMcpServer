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

    // Warnings of this execution's commits, added to its result's own.
    private readonly List<string> _commitWarnings = [];

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
                ct => ExecuteCoreAsync(operationId, @params, ct), cancellationToken);
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
    /// encoding of <paramref name="newFilesEncodingFrom"/> (see
    /// <see cref="WorkspaceContext.CommitChangesAsync(Solution, string?, CancellationToken)"/>).
    /// The commit's warnings are added to the operation's result.
    /// </summary>
    /// <param name="newSolution">Solution with changes.</param>
    /// <param name="newFilesEncodingFrom">Existing file whose encoding added documents take.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Commit result.</returns>
    protected async Task<CommitResult> CommitChangesAsync(
        Solution newSolution, string? newFilesEncodingFrom, CancellationToken cancellationToken)
    {
        var result = await Context.CommitChangesAsync(newSolution, newFilesEncodingFrom, cancellationToken);
        if (!result.Success)
        {
            throw new RefactoringException(
                ErrorCodes.FilesystemError,
                $"Failed to write files: {result.Error}");
        }

        _commitWarnings.AddRange(result.Warnings);
        return result;
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
        // Preview results intentionally don't carry timing (and commit nothing).
        if (result.Preview)
            return result;

        var opMs = result.ExecutionTimeMs > 0 ? result.ExecutionTimeMs : elapsedMs;
        return new RefactoringResult
        {
            Success = result.Success,
            OperationId = result.OperationId,
            Preview = result.Preview,
            Changes = result.Changes,
            Symbol = result.Symbol,
            ReferencesUpdated = result.ReferencesUpdated,
            UsingDirectivesAdded = result.UsingDirectivesAdded,
            UsingDirectivesRemoved = result.UsingDirectivesRemoved,
            ExecutionTimeMs = opMs,
            WorkspaceLoadMs = workspaceLoadMs,
            TotalExecutionTimeMs = opMs + workspaceLoadMs,
            Error = result.Error,
            PendingChanges = result.PendingChanges,
            Warnings = MergeWarnings(result.Warnings, commitWarnings)
        };
    }
}
