using RoslynMcp.Core.FileSystem;
using Xunit;

namespace RoslynMcp.Core.Tests.FileSystem;

public class AtomicFileWriterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AtomicFileWriter _writer;

    public AtomicFileWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"AtomicFileWriterTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _writer = new AtomicFileWriter();
    }

    [Fact]
    public async Task WriteAsync_CreatesNewFile()
    {
        // Arrange
        var filePath = Path.Combine(_tempDir, "test.cs");
        var content = "public class Test { }";

        // Act
        await _writer.WriteAsync(filePath, content, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(File.Exists(filePath));
        Assert.Equal(content, await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteAsync_OverwritesExistingFile()
    {
        // Arrange
        var filePath = Path.Combine(_tempDir, "existing.cs");
        await File.WriteAllTextAsync(filePath, "old content", TestContext.Current.CancellationToken);
        var newContent = "new content";

        // Act
        await _writer.WriteAsync(filePath, newContent, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(newContent, await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteAsync_ExistingFileWithoutBom_StaysWithoutBom()
    {
        // Overwriting used to always write Encoding.UTF8, adding EF BB BF to every file
        // a refactoring touched.
        var filePath = Path.Combine(_tempDir, "nobom.cs");
        await File.WriteAllBytesAsync(filePath, "class A { }\n"u8.ToArray(), TestContext.Current.CancellationToken);

        await _writer.WriteAsync(filePath, "class B { } // ü\n", TestContext.Current.CancellationToken);

        var bytes = await File.ReadAllBytesAsync(filePath, TestContext.Current.CancellationToken);
        Assert.Equal("class B { } // ü\n"u8.ToArray(), bytes);
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    [InlineData("utf-16BE")]
    public async Task WriteAsync_ExistingFileWithBom_KeepsItsEncoding(string encodingName)
    {
        var encoding = System.Text.Encoding.GetEncoding(encodingName);
        var filePath = Path.Combine(_tempDir, "bom.cs");
        await File.WriteAllTextAsync(filePath, "class A { }", encoding, TestContext.Current.CancellationToken);

        await _writer.WriteAsync(filePath, "class B { }", TestContext.Current.CancellationToken);

        var bytes = await File.ReadAllBytesAsync(filePath, TestContext.Current.CancellationToken);
        Assert.Equal([.. encoding.GetPreamble(), .. encoding.GetBytes("class B { }")], bytes);
    }

    [Fact]
    public async Task WriteAsync_NewFile_WritesUtf8WithBom()
    {
        var filePath = Path.Combine(_tempDir, "new.cs");

        await _writer.WriteAsync(filePath, "class A { }", TestContext.Current.CancellationToken);

        var bytes = await File.ReadAllBytesAsync(filePath, TestContext.Current.CancellationToken);
        Assert.Equal([0xEF, 0xBB, 0xBF, .. "class A { }"u8.ToArray()], bytes);
    }

    [Fact]
    public async Task WriteAsync_NewFile_UsesTheGivenEncoding()
    {
        var filePath = Path.Combine(_tempDir, "renamed.cs");

        await _writer.WriteAsync(filePath, "class A { }", new System.Text.UTF8Encoding(false), TestContext.Current.CancellationToken);

        Assert.Equal("class A { }"u8.ToArray(), await File.ReadAllBytesAsync(filePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteAsync_ExistingFile_KeepsItsOwnEncodingOverTheGivenOne()
    {
        var filePath = Path.Combine(_tempDir, "existing.cs");
        await File.WriteAllBytesAsync(filePath, "class A { }"u8.ToArray(), TestContext.Current.CancellationToken);

        await _writer.WriteAsync(filePath, "class B { }", System.Text.Encoding.UTF8, TestContext.Current.CancellationToken);

        Assert.Equal("class B { }"u8.ToArray(), await File.ReadAllBytesAsync(filePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void GetEncoding_DescribesAnExistingFileOnly()
    {
        var withBom = Path.Combine(_tempDir, "bom.cs");
        var withoutBom = Path.Combine(_tempDir, "plain.cs");
        File.WriteAllText(withBom, "class A { }", new System.Text.UTF8Encoding(true));
        File.WriteAllText(withoutBom, "class A { }", new System.Text.UTF8Encoding(false));

        Assert.NotEmpty(_writer.GetEncoding(withBom)!.GetPreamble());
        Assert.Empty(_writer.GetEncoding(withoutBom)!.GetPreamble());
        Assert.Null(_writer.GetEncoding(Path.Combine(_tempDir, "missing.cs")));
    }

    [Fact]
    public async Task WriteAsync_CreatesDirectoryIfNotExists()
    {
        // Arrange
        var filePath = Path.Combine(_tempDir, "subdir", "nested", "test.cs");
        var content = "test content";

        // Act
        await _writer.WriteAsync(filePath, content, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public void Delete_RemovesExistingFile()
    {
        // Arrange
        var filePath = Path.Combine(_tempDir, "todelete.cs");
        File.WriteAllText(filePath, "content");

        // Act
        _writer.Delete(filePath);

        // Assert
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void Delete_DoesNothingIfFileNotExists()
    {
        // Arrange
        var filePath = Path.Combine(_tempDir, "nonexistent.cs");

        // Act & Assert (should not throw)
        _writer.Delete(filePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
