using System.Text;
using RoslynMcp.Core.FileSystem;
using Xunit;

namespace RoslynMcp.Core.Tests.FileSystem;

public class SourceFileEncodingTests
{
    private static readonly Encoding Windows1252 = GetWindows1252();

    [Fact]
    public void Decode_Utf8WithoutBom_IsUtf8WithoutPreamble()
    {
        var text = SourceFileEncoding.Decode("// Größe\n"u8.ToArray());

        Assert.Equal("// Größe\n", text.ToString());
        Assert.Equal(65001, text.Encoding!.CodePage);
        Assert.Empty(text.Encoding.GetPreamble());
    }

    [Fact]
    public void Decode_Windows1252_KeepsTheUmlauts()
    {
        // Was decoded as UTF-8: every umlaut became U+FFFD, and a commit wrote that back.
        var text = SourceFileEncoding.Decode(Windows1252.GetBytes("// Größe\n"), fallbackEncoding: Windows1252);

        Assert.Equal("// Größe\n", text.ToString());
        Assert.Equal(1252, text.Encoding!.CodePage);
    }

    [Fact]
    public void Decode_NotUtf8_WithoutAKnownCodePage_UsesTheFallbackCodePage()
    {
        var bytes = Windows1252.GetBytes("// Größe\n");

        var text = SourceFileEncoding.Decode(bytes, fallbackEncoding: new UTF8Encoding(false));

        Assert.Same(SourceFileEncoding.FallbackCodePage, text.Encoding);
        Assert.Equal(SourceFileEncoding.FallbackCodePage.GetString(bytes), text.ToString());
        Assert.DoesNotContain('�', text.ToString());
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    [InlineData("utf-16BE")]
    [InlineData("utf-32")]
    public void Decode_ByteOrderMark_Decides(string name)
    {
        var encoding = Encoding.GetEncoding(name);
        byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes("// Größe → x\n")];

        var text = SourceFileEncoding.Decode(bytes, projectEncoding: Windows1252);

        Assert.Equal("// Größe → x\n", text.ToString());
        Assert.Equal(encoding.CodePage, text.Encoding!.CodePage);
        Assert.Equal(encoding.GetPreamble(), text.Encoding.GetPreamble());
    }

    [Fact]
    public void Decode_ProjectCodePage_WinsOverUtf8Detection()
    {
        // As the compiler does with <CodePage>: these bytes are "ä" in UTF-8 but "Ã¤" in windows-1252.
        var text = SourceFileEncoding.Decode([0xC3, 0xA4], projectEncoding: Windows1252);

        Assert.Equal("Ã¤", text.ToString());
        Assert.Equal(1252, text.Encoding!.CodePage);
    }

    [Fact]
    public void ForRewrite_Windows1252File_RoundTripsItsBytes()
    {
        var bytes = Windows1252.GetBytes("// Größe\nclass Maß { }\n");
        var text = SourceFileEncoding.Decode(bytes, fallbackEncoding: Windows1252);

        var encoding = SourceFileEncoding.ForRewrite(bytes, text.Encoding, "Legacy.cs");

        Assert.Equal(1252, encoding.CodePage);
        Assert.Equal(bytes, encoding.GetBytes(text.ToString()));
    }

    [Fact]
    public void ForRewrite_ByteOrderMark_IsKept()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. "class A { }"u8.ToArray()];

        var encoding = SourceFileEncoding.ForRewrite(bytes, decodedWith: Windows1252, "A.cs");

        Assert.Equal(65001, encoding.CodePage);
        Assert.Equal([0xEF, 0xBB, 0xBF], encoding.GetPreamble());
    }

    [Fact]
    public void ForRewrite_WithoutKnownEncoding_JudgesTheBytes()
    {
        var utf8 = SourceFileEncoding.ForRewrite("// Größe\n"u8.ToArray(), decodedWith: null, "A.cs");
        var codePage = SourceFileEncoding.ForRewrite(Windows1252.GetBytes("// Größe\n"), decodedWith: null, "B.cs");

        Assert.Equal(65001, utf8.CodePage);
        Assert.Empty(utf8.GetPreamble());
        Assert.Same(SourceFileEncoding.FallbackCodePage, codePage);
    }

    [Fact]
    public void ForRewrite_TextReadWithReplacementCharacters_Throws()
    {
        // Read as UTF-8 although it is not (what a UTF-8 system code page does): the text holds
        // U+FFFD where the umlauts were, and writing it would make that permanent.
        var bytes = Windows1252.GetBytes("// Größe\n");

        var ex = Assert.Throws<InvalidDataException>(
            () => SourceFileEncoding.ForRewrite(bytes, decodedWith: new UTF8Encoding(false), "Legacy.cs"));

        Assert.Contains("Legacy.cs", ex.Message);
        Assert.Contains("UTF-8 without BOM", ex.Message);
    }

    [Fact]
    public void FindUnencodable_NamesTheFirstCharacterACodePageLacks()
    {
        Assert.Null(SourceFileEncoding.FindUnencodable("Größe", Windows1252));
        Assert.Equal("'→' (U+2192)", SourceFileEncoding.FindUnencodable("Größe → ā", Windows1252));
        Assert.Equal("'😀' (U+1F600)", SourceFileEncoding.FindUnencodable("x 😀", Windows1252));
        Assert.Null(SourceFileEncoding.FindUnencodable("→ 😀", SourceFileEncoding.Utf8WithoutBom));
    }

    [Fact]
    public void Describe_NamesCodePagesAndByteOrderMarks()
    {
        Assert.Equal("windows-1252", SourceFileEncoding.Describe(Windows1252));
        Assert.Equal("UTF-8 with BOM", SourceFileEncoding.Describe(SourceFileEncoding.Utf8WithBom));
        Assert.Equal("UTF-8 without BOM", SourceFileEncoding.Describe(SourceFileEncoding.Utf8WithoutBom));
        Assert.Equal("UTF-16 LE with BOM", SourceFileEncoding.Describe(Encoding.Unicode));
    }

    private static Encoding GetWindows1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }
}
