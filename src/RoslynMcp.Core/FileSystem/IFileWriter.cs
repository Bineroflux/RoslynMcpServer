using System.Text;

namespace RoslynMcp.Core.FileSystem;

/// <summary>
/// Abstraction for file write operations.
/// </summary>
public interface IFileWriter
{
    /// <summary>
    /// Writes content to a file atomically. An existing file keeps its encoding (see
    /// <see cref="GetEncoding"/>); a new file is written as UTF-8 with BOM.
    /// </summary>
    /// <param name="filePath">Absolute path to the file.</param>
    /// <param name="content">Content to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="EncoderFallbackException">The file's encoding cannot represent the content.</exception>
    Task WriteAsync(string filePath, string content, CancellationToken cancellationToken = default) =>
        WriteAsync(filePath, content, GetEncoding(filePath) ?? SourceFileEncoding.Utf8WithBom, cancellationToken);

    /// <summary>
    /// Writes content to a file atomically in exactly <paramref name="encoding"/>: its
    /// preamble first when it has one, then the content.
    /// </summary>
    /// <param name="filePath">Absolute path to the file.</param>
    /// <param name="content">Content to write.</param>
    /// <param name="encoding">Encoding to write the file in.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="EncoderFallbackException">
    /// <paramref name="encoding"/> cannot represent the content; nothing is written. A
    /// character is never replaced by <c>?</c> or a look-alike.
    /// </exception>
    Task WriteAsync(string filePath, string content, Encoding encoding, CancellationToken cancellationToken = default);

    /// <summary>
    /// The encoding to rewrite an existing file in so that the text it was decoded to keeps
    /// its bytes (see <see cref="SourceFileEncoding.ForRewrite"/>), or null when the file
    /// does not exist or the writer does not track encodings.
    /// </summary>
    /// <param name="filePath">Absolute path to the file.</param>
    /// <param name="decodedWith">
    /// The encoding the text replacing the file was decoded from it with, if known. It tells
    /// which code page a file without byte order mark is in.
    /// </param>
    /// <exception cref="InvalidDataException">
    /// The file's bytes are not valid in its encoding, so rewriting it would change bytes the
    /// text does not represent.
    /// </exception>
    Encoding? GetEncoding(string filePath, Encoding? decodedWith = null) => null;

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
