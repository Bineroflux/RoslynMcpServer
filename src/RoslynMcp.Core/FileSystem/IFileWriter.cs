using System.Text;

namespace RoslynMcp.Core.FileSystem;

/// <summary>
/// Abstraction for file write operations.
/// </summary>
public interface IFileWriter
{
    /// <summary>
    /// Writes content to a file atomically.
    /// </summary>
    /// <param name="filePath">Absolute path to the file.</param>
    /// <param name="content">Content to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WriteAsync(string filePath, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes content to a file atomically. An existing file keeps its own encoding; a new
    /// file is written in <paramref name="newFileEncoding"/> (e.g. that of the file it
    /// replaces under another name), or the writer's default when that is null.
    /// </summary>
    /// <param name="filePath">Absolute path to the file.</param>
    /// <param name="content">Content to write.</param>
    /// <param name="newFileEncoding">Encoding for a file that does not exist yet.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WriteAsync(string filePath, string content, Encoding? newFileEncoding, CancellationToken cancellationToken = default) =>
        WriteAsync(filePath, content, cancellationToken);

    /// <summary>
    /// The encoding an existing file is written in, as the writer would keep it, or null when
    /// the file does not exist or the writer does not track encodings.
    /// </summary>
    /// <param name="filePath">Absolute path to the file.</param>
    Encoding? GetEncoding(string filePath) => null;

    /// <summary>
    /// Deletes a file if it exists.
    /// </summary>
    /// <param name="filePath">Absolute path to the file.</param>
    void Delete(string filePath);

    /// <summary>
    /// Ensures the directory for a file path exists.
    /// </summary>
    /// <param name="filePath">Absolute path to a file.</param>
    void EnsureDirectoryExists(string filePath);
}
