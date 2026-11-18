using System.Text;

namespace RoslynMcp.Core.FileSystem;

/// <summary>
/// Writes files atomically using temp-then-rename pattern.
/// Prevents partial writes on crash or cancellation.
/// </summary>
/// <remarks>
/// An existing file keeps its encoding: a file without a byte order mark is rewritten
/// without one, and a UTF-8 or UTF-16 BOM is kept. New files are written as UTF-8 with BOM.
/// </remarks>
public sealed class AtomicFileWriter : IFileWriter
{
    /// <inheritdoc />
    public async Task WriteAsync(string filePath, string content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        EnsureDirectoryExists(filePath);

        // Generate temp file in same directory for atomic rename
        var directory = Path.GetDirectoryName(filePath)!;
        var tempFileName = $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp";
        var tempFilePath = Path.Combine(directory, tempFileName);

        try
        {
            // Write to temp file
            await File.WriteAllTextAsync(tempFilePath, content, GetEncoding(filePath), cancellationToken);

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

    /// <summary>
    /// The encoding to write <paramref name="filePath"/> with, derived from the byte order
    /// mark of the file it replaces (UTF-8 without BOM when it has none).
    /// </summary>
    private static Encoding GetEncoding(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return Encoding.UTF8;
        }

        Span<byte> preamble = stackalloc byte[3];
        int read;
        using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            read = stream.ReadAtLeast(preamble, preamble.Length, throwOnEndOfStream: false);
        }

        preamble = preamble[..read];
        if (preamble.StartsWith(Encoding.UTF8.Preamble))
        {
            return Encoding.UTF8;
        }

        if (preamble.StartsWith(Encoding.Unicode.Preamble))
        {
            return Encoding.Unicode;
        }

        if (preamble.StartsWith(Encoding.BigEndianUnicode.Preamble))
        {
            return Encoding.BigEndianUnicode;
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
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
