using System.Text;

namespace RoslynMcp.Core.FileSystem;

/// <summary>
/// Writes files atomically using temp-then-rename pattern.
/// Prevents partial writes on crash or cancellation.
/// </summary>
/// <remarks>
/// Content is encoded strictly: a character the encoding cannot represent fails the write
/// instead of turning into <c>?</c> or a best-fit look-alike (windows-1252 would write 'ā'
/// as 'a'). Choosing an encoding that can represent the content is the caller's job; see
/// <see cref="SourceFileEncoding"/>.
/// </remarks>
public sealed class AtomicFileWriter : IFileWriter
{
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

            // Atomic rename (same filesystem)
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            File.Move(tempFilePath, filePath);
        }
        catch
        {
            // Clean up temp file on any failure
            TryDeleteFile(tempFilePath);
            throw;
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
