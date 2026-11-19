using System.Text;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp.Core.FileSystem;

/// <summary>
/// How source files are decoded and encoded again, so that a refactoring leaves the bytes of
/// the text it did not edit as they were.
/// </summary>
/// <remarks>
/// <para>
/// Decoding follows the C# compiler (Roslyn's <c>EncodedStringText</c>, which the MSBuild
/// workspace loads files with): a byte order mark decides; without one, the project's code
/// page if it sets one (<c>&lt;CodePage&gt;</c>), else UTF-8 when the bytes are valid UTF-8,
/// else the system's ANSI code page. A file that is not UTF-8 — a windows-1252 file with
/// umlauts, say — is therefore read in its code page, not as UTF-8 with replacement
/// characters.
/// </para>
/// <para>
/// A file is written back in the encoding its text was decoded with, so text read from it
/// round-trips byte for byte; see <see cref="ForRewrite"/>. Characters the encoding cannot
/// represent are never replaced: <see cref="FindUnencodable"/> lets the writer choose UTF-8 instead.
/// </para>
/// </remarks>
public static class SourceFileEncoding
{
    /// <summary>
    /// UTF-8 with a byte order mark: what converted files are written in, and new files when
    /// neither an <c>.editorconfig</c> nor the project's other files say otherwise.
    /// </summary>
    public static Encoding Utf8WithBom { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    /// <summary>UTF-8 without a byte order mark.</summary>
    public static Encoding Utf8WithoutBom { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // The encodings a byte order mark identifies, longest preamble first: the UTF-32 LE mark
    // (FF FE 00 00) starts with the UTF-16 LE one (FF FE).
    private static readonly Encoding[] ByteOrderMarkEncodings =
    [
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        new UTF32Encoding(bigEndian: false, byteOrderMark: true),
        new UTF32Encoding(bigEndian: true, byteOrderMark: true),
        new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
        new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
    ];

    private static readonly Lazy<Encoding> LazyFallbackCodePage = new(CreateFallbackCodePage);

    static SourceFileEncoding()
    {
        // Code pages such as windows-1252 are available only through this provider on .NET.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// The code page a file without byte order mark that is not valid UTF-8 is read in: the
    /// system's ANSI code page, as the compiler uses, or windows-1252 where that is UTF-8
    /// (which could not read such a file) or unavailable, or else Latin-1. Each of them
    /// decodes any byte sequence.
    /// </summary>
    public static Encoding FallbackCodePage => LazyFallbackCodePage.Value;

    /// <summary>
    /// Decodes a source file. A byte order mark decides the encoding; without one,
    /// <paramref name="projectEncoding"/> (the project's <c>CodePage</c>) when given, else
    /// UTF-8 when the bytes are valid UTF-8, else <paramref name="fallbackEncoding"/> or
    /// <see cref="FallbackCodePage"/>. The text's <see cref="SourceText.Encoding"/> is the
    /// encoding used, with a preamble exactly when the file has a byte order mark.
    /// </summary>
    /// <param name="bytes">The file's content.</param>
    /// <param name="projectEncoding">The encoding the project declares for its files, if any.</param>
    /// <param name="fallbackEncoding">
    /// The code page to use for a file without byte order mark that is not valid UTF-8, e.g.
    /// the one the file was read in before; ignored unless it is a code page (see
    /// <see cref="IsCodePage"/>).
    /// </param>
    /// <remarks>
    /// Bytes that are invalid for the chosen encoding (a broken file) are decoded with
    /// replacement characters, as the compiler does; <see cref="ForRewrite"/> then refuses to
    /// write the file back.
    /// </remarks>
    public static SourceText Decode(byte[] bytes, Encoding? projectEncoding = null, Encoding? fallbackEncoding = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        Encoding encoding;
        var start = 0;
        if (DetectByteOrderMark(bytes) is { } byteOrderMark)
        {
            encoding = byteOrderMark;
            start = byteOrderMark.Preamble.Length;
        }
        else if (projectEncoding != null)
        {
            encoding = WithoutPreamble(projectEncoding);
        }
        else if (DecodesStrictly(bytes, StrictUtf8))
        {
            encoding = Utf8WithoutBom;
        }
        else
        {
            encoding = fallbackEncoding != null && IsCodePage(fallbackEncoding)
                ? fallbackEncoding
                : FallbackCodePage;
        }

        var text = encoding.GetString(bytes, start, bytes.Length - start);
        return SourceText.From(text, encoding, SourceHashAlgorithm.Sha256);
    }

    /// <summary>
    /// The encoding to write an existing file in so that the text it was decoded to keeps its
    /// bytes: the file's byte order mark when it has one; else the code page
    /// <paramref name="decodedWith"/> names; else UTF-8 without BOM when the text was decoded
    /// as UTF-8 or the bytes are valid UTF-8; else <see cref="FallbackCodePage"/>.
    /// </summary>
    /// <param name="bytes">The file's current content.</param>
    /// <param name="decodedWith">
    /// The encoding the text that replaces the file was decoded from it with
    /// (<see cref="SourceText.Encoding"/>), if known.
    /// </param>
    /// <param name="filePath">The file, for the error message.</param>
    /// <returns>An encoding that writes its preamble exactly when the file has one.</returns>
    /// <exception cref="InvalidDataException">
    /// The bytes are not valid in that encoding. Its text was then decoded with replacement
    /// characters, and writing it would replace those bytes for good.
    /// </exception>
    public static Encoding ForRewrite(byte[] bytes, Encoding? decodedWith, string filePath)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        Encoding encoding;
        var start = 0;
        if (DetectByteOrderMark(bytes) is { } byteOrderMark)
        {
            encoding = byteOrderMark;
            start = byteOrderMark.Preamble.Length;
        }
        else if (decodedWith != null && IsCodePage(decodedWith))
        {
            encoding = WithoutPreamble(decodedWith);
        }
        else if (decodedWith?.CodePage == Encoding.UTF8.CodePage || DecodesStrictly(bytes, StrictUtf8))
        {
            encoding = Utf8WithoutBom;
        }
        else
        {
            encoding = FallbackCodePage;
        }

        if (!DecodesStrictly(bytes.AsSpan(start), encoding))
        {
            throw new InvalidDataException(
                $"'{filePath}' is not valid {Describe(encoding)}, so it was read with replacement characters; " +
                "writing it back would replace the bytes that are invalid for good. Fix or convert the file's encoding first.");
        }

        return encoding;
    }

    /// <summary>
    /// The first character of <paramref name="content"/> that <paramref name="encoding"/>
    /// cannot write as it is — it would become <c>?</c> or a best-fit look-alike — described
    /// as <c>'→' (U+2192)</c>; null when it can write them all. The Unicode encodings can
    /// write any text.
    /// </summary>
    public static string? FindUnencodable(string content, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(encoding);

        if (!IsCodePage(encoding))
            return null;

        var strict = encoding.EncoderFallback is EncoderExceptionFallback
            ? encoding
            : CloneWith(encoding, EncoderFallback.ExceptionFallback, encoding.DecoderFallback);
        try
        {
            strict.GetByteCount(content);
            return null;
        }
        catch (EncoderFallbackException ex)
        {
            var (character, codePoint) = ex.IsUnknownSurrogate()
                ? (new string([ex.CharUnknownHigh, ex.CharUnknownLow]), char.ConvertToUtf32(ex.CharUnknownHigh, ex.CharUnknownLow))
                : (ex.CharUnknown.ToString(), ex.CharUnknown);
            return $"'{character}' (U+{codePoint:X4})";
        }
    }

    /// <summary>
    /// The encoding an <c>.editorconfig</c> <c>charset</c> value names: <c>utf-8</c> (without
    /// BOM), <c>utf-8-bom</c>, <c>latin1</c> (ISO-8859-1), <c>utf-16le</c> and <c>utf-16be</c>
    /// (with BOM, without which no compiler could tell them); null for anything else, such as
    /// <c>unset</c>. Case and surrounding spaces don't matter.
    /// </summary>
    public static Encoding? FromEditorConfigCharset(string? charset) =>
        charset?.Trim().ToLowerInvariant() switch
        {
            "utf-8" => Utf8WithoutBom,
            "utf-8-bom" => Utf8WithBom,
            "latin1" => Encoding.Latin1,
            "utf-16le" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            "utf-16be" => new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
            _ => null,
        };

    /// <summary>
    /// The encoding the byte order mark at the start of <paramref name="bytes"/> identifies,
    /// with a preamble; null without one.
    /// </summary>
    public static Encoding? DetectByteOrderMark(ReadOnlySpan<byte> bytes)
    {
        foreach (var encoding in ByteOrderMarkEncodings)
        {
            if (bytes.StartsWith(encoding.Preamble))
                return encoding;
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="encoding"/> is a code page — windows-1252, Shift-JIS and the
    /// like — rather than a Unicode encoding. A code page is not detectable from a file's
    /// bytes, so the one a file was read in is remembered rather than guessed again.
    /// </summary>
    public static bool IsCodePage(Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        return encoding.CodePage is not (1200 or 1201 or 12000 or 12001 or 65000 or 65001);
    }

    /// <summary>A name for <paramref name="encoding"/> in messages: "windows-1252", "UTF-8 with BOM".</summary>
    public static string Describe(Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);

        var name = encoding.CodePage switch
        {
            65001 => "UTF-8",
            1200 => "UTF-16 LE",
            1201 => "UTF-16 BE",
            12000 => "UTF-32 LE",
            12001 => "UTF-32 BE",
            _ => encoding.WebName,
        };

        if (IsCodePage(encoding))
            return name;
        return encoding.Preamble.Length > 0 ? $"{name} with BOM" : $"{name} without BOM";
    }

    private static Encoding WithoutPreamble(Encoding encoding) => encoding.CodePage switch
    {
        // Only the UTF encodings have preambles; keep the caller's instance otherwise.
        65001 => Utf8WithoutBom,
        1200 => new UnicodeEncoding(bigEndian: false, byteOrderMark: false),
        1201 => new UnicodeEncoding(bigEndian: true, byteOrderMark: false),
        12000 => new UTF32Encoding(bigEndian: false, byteOrderMark: false),
        12001 => new UTF32Encoding(bigEndian: true, byteOrderMark: false),
        _ => encoding,
    };

    private static bool DecodesStrictly(ReadOnlySpan<byte> bytes, Encoding encoding)
    {
        var strict = encoding.DecoderFallback is DecoderExceptionFallback
            ? encoding
            : CloneWith(encoding, encoding.EncoderFallback, DecoderFallback.ExceptionFallback);
        try
        {
            strict.GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static Encoding CloneWith(Encoding encoding, EncoderFallback encoderFallback, DecoderFallback decoderFallback)
    {
        var clone = (Encoding)encoding.Clone();
        clone.EncoderFallback = encoderFallback;
        clone.DecoderFallback = decoderFallback;
        return clone;
    }

    private static Encoding CreateFallbackCodePage()
    {
        try
        {
            // With the code-pages provider registered, code page 0 is the system's ANSI code
            // page — what the compiler falls back to. Where that is UTF-8 it cannot read a
            // file that just failed to decode as UTF-8, so take windows-1252, like the compiler
            // does where no ANSI code page exists.
            var ansi = Encoding.GetEncoding(0);
            return ansi.CodePage == Encoding.UTF8.CodePage ? Encoding.GetEncoding(1252) : ansi;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return Encoding.Latin1;
        }
    }
}
