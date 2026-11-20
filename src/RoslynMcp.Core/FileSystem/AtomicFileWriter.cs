using System.Text;

namespace RoslynMcp.Core.FileSystem;

/// <summary>
/// Writes files atomically: the content goes to a temp file next to the target, which then
/// replaces the target in one step. Prevents partial writes on crash or cancellation, and a
/// reader finds the old file or the new one, never none.
/// </summary>
/// <remarks>
/// <para>
/// The replace is <see cref="File.Move(string, string, bool)"/> with overwrite: on Windows
/// <c>MoveFileEx</c> with <c>MOVEFILE_REPLACE_EXISTING</c>, on Unix <c>rename(2)</c>, both atomic
/// for readers. Deleting the target first and then moving the temp file in left a moment without
/// the file, in which another RoslynMcp server, an editor or a file watcher reading it found it
/// gone. On Windows the replace fails while anyone else has the target open (even sharing
/// delete), so a failed replace is tried again a few times over about 0.4 s: another process's
/// file watcher or editor holds it for milliseconds.
/// </para>
/// <para>
/// Content is encoded strictly: a character the encoding cannot represent fails the write
/// instead of turning into <c>?</c> or a best-fit look-alike (windows-1252 would write 'ā'
/// as 'a'). Choosing an encoding that can represent the content is the caller's job; see
/// <see cref="SourceFileEncoding"/>.
/// </para>
/// </remarks>
public sealed class AtomicFileWriter : IFileWriter
{
    // The pauses before the second and later attempts to replace a file someone holds open.
    private static readonly int[] ReplaceRetryDelaysMs = [10, 25, 50, 100, 200];

    /// <summary>For tests: called with the failure each time replacing a file is tried again.</summary>
    internal Action<Exception>? ReplaceRetried { get; set; }

    /// <inheritdoc />
    public Task WriteAsync(string filePath, string content, CancellationToken cancellationToken = default) =>
        WriteAsync(filePath, content, GetEncoding(filePath) ?? SourceFileEncoding.Utf8WithBom, cancellationToken);

    /// <inheritdoc />
    public async Task WriteAsync(string filePath, string content, Encoding encoding, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(encoding);

        var bytes = Encode(content, encoding);

        EnsureDirectoryExists(filePath);

        // Generate temp file in same directory for atomic rename
        var directory = Path.GetDirectoryName(filePath)!;
        var tempFileName = $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp";
        var tempFilePath = Path.Combine(directory, tempFileName);

        try
        {
            // Write to temp file
            await File.WriteAllBytesAsync(tempFilePath, bytes, cancellationToken);

            // Replace the target with it (same directory, so same file system)
            await ReplaceAsync(tempFilePath, filePath, cancellationToken);
        }
        catch
        {
            // Clean up temp file on any failure
            TryDeleteFile(tempFilePath);
            throw;
        }
    }

    /// <summary>
    /// Moves <paramref name="tempFilePath"/> over <paramref name="filePath"/> in one step, trying
    /// again while someone else holds the target open (see the class remarks).
    /// </summary>
    private async Task ReplaceAsync(string tempFilePath, string filePath, CancellationToken cancellationToken)
    {
        // A replace keeps the name the file has on disk, so a new spelling of it on a
        // case-insensitive file system (Foo.cs written as foo.cs, a type renamed only in case)
        // is applied by deleting the file first, as all writes once did; only then is it briefly
        // absent.
        var respelled = DiffersOnDiskInCaseOnly(filePath);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (respelled)
                {
                    File.Delete(filePath);
                    File.Move(tempFilePath, filePath);
                }
                else
                {
                    File.Move(tempFilePath, filePath, overwrite: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < ReplaceRetryDelaysMs.Length)
            {
                ReplaceRetried?.Invoke(ex);
                await Task.Delay(ReplaceRetryDelaysMs[attempt], cancellationToken);
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="filePath"/> exists on disk under a name that differs from its own
    /// only in letter case (a case-insensitive file system).
    /// </summary>
    private static bool DiffersOnDiskInCaseOnly(string filePath)
    {
        var name = Path.GetFileName(filePath);
        try
        {
            var onDisk = new DirectoryInfo(Path.GetDirectoryName(filePath)!).EnumerateFiles(name).FirstOrDefault()?.Name;
            return onDisk != null &&
                !string.Equals(onDisk, name, StringComparison.Ordinal) &&
                string.Equals(onDisk, name, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Delete(string filePath)
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    /// <inheritdoc />
    public void EnsureDirectoryExists(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <inheritdoc />
    public Encoding? GetEncoding(string filePath, Encoding? decodedWith = null)
    {
        byte[] bytes;
        try
        {
            using var stream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        return SourceFileEncoding.ForRewrite(bytes, decodedWith, filePath);
    }

    private static byte[] Encode(string content, Encoding encoding)
    {
        var strict = encoding;
        if (encoding.EncoderFallback is not EncoderExceptionFallback)
        {
            strict = (Encoding)encoding.Clone();
            strict.EncoderFallback = EncoderFallback.ExceptionFallback;
        }

        var preamble = encoding.Preamble;
        var bytes = new byte[preamble.Length + strict.GetByteCount(content)];
        preamble.CopyTo(bytes);
        strict.GetBytes(content, bytes.AsSpan(preamble.Length));
        return bytes;
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
