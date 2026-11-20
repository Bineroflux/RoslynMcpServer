using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Refactoring;

namespace RoslynMcp.Core.Workspace;

/// <summary>
/// The lock every RoslynMcp process of the user shares for writing source files — each server
/// (one per Claude session) and the CLI: an exclusive lock on a lock file. A commit holds it while
/// it checks the files it writes against the disk and writes them (see
/// <see cref="WorkspaceContext.CommitChangesAsync(Microsoft.CodeAnalysis.Solution, string?, CancellationToken)"/>),
/// which takes milliseconds, so no other RoslynMcp process can write a file between one's check
/// and its write.
/// </summary>
/// <remarks>
/// <para>
/// One lock for all workspaces rather than one per repository: it is held only for the check and
/// the writes, never while a refactoring is computed, so commits of unrelated repositories wait
/// for each other for milliseconds at most; and one lock cannot be split wrongly — a file reached
/// through two roots (a <c>.slnx</c> and a <c>.csproj</c>, a linked file, a junction) is under it
/// either way.
/// </para>
/// <para>
/// The operating system releases the lock when the process ends, also when it crashes or is
/// killed: on Windows the file handle opened without sharing closes; on Unix .NET takes an
/// advisory <c>flock</c> for <see cref="FileShare.None"/>, which every .NET process honours (unless
/// file locking is switched off with <c>DOTNET_SYSTEM_IO_DISABLEFILELOCKING</c>). The file is
/// opened for reading only, so a lock file another user created can still be locked.
/// </para>
/// <para>
/// Editors and other programs don't take this lock. An editor saving a file within those
/// milliseconds can still be overwritten; one saving it at any other time is found by the check.
/// </para>
/// </remarks>
internal sealed class CrossProcessWriteLock
{
    /// <summary>The lock file all RoslynMcp processes of the user share: <c>%TEMP%/RoslynMcp/writers.lock</c>.</summary>
    public static string DefaultPath { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RoslynMcp", "writers.lock");

    /// <summary>How long a commit waits for another process's commit before it gives up.</summary>
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(30);

    // The longest pause between two attempts to take the lock; it is held for milliseconds.
    private const int MaxRetryDelayMs = 50;

    /// <summary>Creates the lock on <paramref name="path"/> (default: <see cref="DefaultPath"/>).</summary>
    /// <param name="path">The lock file; its directory is created when missing.</param>
    /// <param name="timeout">How long <see cref="AcquireAsync"/> waits (default: <see cref="DefaultTimeout"/>).</param>
    public CrossProcessWriteLock(string? path = null, TimeSpan? timeout = null)
    {
        Path = path ?? DefaultPath;
        Timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>The lock file.</summary>
    public string Path { get; }

    /// <summary>How long <see cref="AcquireAsync"/> waits for the lock.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>For tests: awaited right after the lock is taken, before the holder's work starts.</summary>
    internal Func<CancellationToken, Task>? Acquired { get; set; }

    /// <summary>For tests: called right before the lock is released.</summary>
    internal Action? Releasing { get; set; }

    /// <summary>For tests: called each time an attempt finds the lock held by someone else.</summary>
    internal Action? Contended { get; set; }

    /// <summary>
    /// Takes the lock, retrying with short pauses while another process (or another lock on the
    /// same file in this one) holds it. Dispose the result to release it, and don't await
    /// anything slow but the writes while holding it.
    /// </summary>
    /// <exception cref="RefactoringException">
    /// <see cref="ErrorCodes.WorkspaceBusy"/>: the lock could not be taken within <see cref="Timeout"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled while waiting.</exception>
    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        var waited = Stopwatch.StartNew();
        var delayMs = 1;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Exception failure;
            try
            {
                var held = new Held(this, Open());
                if (Acquired is { } acquired)
                {
                    try
                    {
                        await acquired(cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        held.Dispose();
                        throw;
                    }
                }

                return held;
            }
            catch (IOException ex)
            {
                // Held elsewhere: a sharing violation on Windows, EWOULDBLOCK from flock on Unix.
                failure = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                // Transient on Windows (e.g. a pending delete); a lasting one ends in the timeout.
                failure = ex;
            }

            Contended?.Invoke();
            if (waited.Elapsed >= Timeout)
            {
                throw new RefactoringException(
                    ErrorCodes.WorkspaceBusy,
                    $"Another RoslynMcp process kept the lock for writing files ('{Path}') for {Timeout.TotalSeconds:0.#} s " +
                    $"({failure.Message}). Nothing was written; run it again.");
            }

            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            delayMs = Math.Min(delayMs * 2, MaxRetryDelayMs);
        }
    }

    /// <summary>Opens the lock file without sharing, creating it and its directory when missing.</summary>
    private SafeFileHandle Open()
    {
        try
        {
            return File.OpenHandle(Path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.None);
        }
        catch (DirectoryNotFoundException)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            return File.OpenHandle(Path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.None);
        }
    }

    /// <summary>The held lock; disposing it releases the lock, once.</summary>
    private sealed class Held(CrossProcessWriteLock owner, SafeFileHandle handle) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            try
            {
                owner.Releasing?.Invoke();
            }
            finally
            {
                handle.Dispose();
            }
        }
    }
}
