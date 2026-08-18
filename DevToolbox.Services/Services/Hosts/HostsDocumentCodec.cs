using System.Text;
using DevToolbox.Services.Models.Hosts;

namespace DevToolbox.Services.Services.Hosts;

/// <summary>
/// Turns hosts-file bytes into a <see cref="HostsDocument"/> and back again without changing a
/// byte that was not deliberately edited.
/// <para>
/// <see cref="Compose"/> of an unmutated <see cref="Read"/> is byte-identical to the source.
/// Everything else in this feature depends on that, so it is asserted directly in the tests
/// against every sample file rather than assumed.
/// </para>
/// </summary>
public static class HostsDocumentCodec
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Utf16LeBom = [0xFF, 0xFE];
    private static readonly byte[] Utf16BeBom = [0xFE, 0xFF];

    /// <summary>
    /// Windows writes hosts files with CRLF, so that is the terminator given to inserted lines
    /// when a file offers no evidence of its own — an empty or single-line file.
    /// </summary>
    private const string WindowsNewLine = "\r\n";

    /// <summary>
    /// Reads and parses the file.
    /// <para>
    /// Opened <see cref="FileShare.ReadWrite"/> deliberately: an editor holding the hosts file
    /// open must not make the tab fail to load.
    /// </para>
    /// </summary>
    /// <exception cref="IOException"/>
    /// <exception cref="UnauthorizedAccessException"/>
    public static HostsDocument Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var info = new FileInfo(path);
        byte[] bytes;

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }

        return FromBytes(path, bytes, info.LastWriteTimeUtc);
    }

    /// <summary>Parses bytes already in hand — the path is recorded but never read.</summary>
    public static HostsDocument FromBytes(string path, byte[] bytes, DateTime lastWriteTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var detected = DetectEncoding(bytes);
        var text = detected.ForReading.GetString(bytes, detected.Preamble.Length, bytes.Length - detected.Preamble.Length);
        var lines = SplitLines(text);

        return new HostsDocument
        {
            Path = path,
            Encoding = detected.ForWriting,
            Preamble = detected.Preamble,
            DefaultNewLine = PredominantNewLine(lines),
            Lines = lines,
            Sha256 = HostsDocument.HashOf(bytes),
            LastWriteTimeUtc = lastWriteTimeUtc,
            Length = bytes.Length,
            DecodedWithFallbackEncoding = detected.UsedFallback,
        };
    }

    /// <summary>
    /// The bytes to write. Byte-order mark first, verbatim, then the lines with their own
    /// terminators.
    /// </summary>
    public static byte[] Compose(HostsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var body = document.Encoding.GetBytes(document.ToText());
        if (document.Preamble.Count == 0) return body;

        var result = new byte[document.Preamble.Count + body.Length];
        for (var i = 0; i < document.Preamble.Count; i++) result[i] = document.Preamble[i];
        body.CopyTo(result, document.Preamble.Count);

        return result;
    }

    /// <summary>
    /// Rebuilds a document from edited text, keeping the original's encoding, byte-order mark
    /// and newline style.
    /// <para>
    /// This exists for the raw editor, and it is the one place the CRLF problem comes back:
    /// browsers normalise a <c>&lt;textarea&gt;</c>'s value to bare LF, so text arriving from
    /// the UI has lost the file's terminators entirely. Any line whose terminator was
    /// flattened to LF gets <see cref="HostsDocument.DefaultNewLine"/> instead, which restores
    /// CRLF on a CRLF file. Text that genuinely still carries CRLF is left as it is.
    /// </para>
    /// </summary>
    public static HostsDocument FromText(HostsDocument template, string text)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(text);

        var lines = SplitLines(text)
            .Select(line => line.NewLine == "\n" ? line with { NewLine = template.DefaultNewLine } : line)
            .ToArray();

        return template.WithLines(lines);
    }

    /// <summary>
    /// Splits text into lines, giving each one the terminator that actually followed it.
    /// A trailing terminator does not produce a phantom empty final line, and a final line
    /// without one gets <see cref="string.Empty"/> so it stays unterminated on write.
    /// </summary>
    internal static List<HostsLine> SplitLines(string text)
    {
        var lines = new List<HostsLine>();
        var start = 0;
        var number = 1;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;

            // A lone CR that is not part of a CRLF pair stays in the line's text, so even a
            // classic-Mac file survives a round trip.
            var pairedWithCr = i > start && text[i - 1] == '\r';
            var end = pairedWithCr ? i - 1 : i;

            lines.Add(new HostsLine(number++, text[start..end], pairedWithCr ? "\r\n" : "\n"));
            start = i + 1;
        }

        if (start < text.Length) lines.Add(new HostsLine(number, text[start..], string.Empty));

        return lines;
    }

    /// <summary>The newline style used by most of the file's terminated lines.</summary>
    private static string PredominantNewLine(IReadOnlyList<HostsLine> lines)
    {
        var crlf = 0;
        var lf = 0;

        foreach (var line in lines)
        {
            if (line.NewLine == "\r\n") crlf++;
            else if (line.NewLine == "\n") lf++;
        }

        if (crlf == 0 && lf == 0) return WindowsNewLine;
        return crlf >= lf ? "\r\n" : "\n";
    }

    /// <summary>
    /// What <see cref="DetectEncoding"/> worked out about the bytes.
    /// <para>
    /// Reading and writing use different instances of the same encoding on purpose. The reader
    /// is lenient or strict as the detection requires; the writer always throws rather than
    /// substituting a replacement character, because a silent <c>?</c> in a hosts file is a
    /// wrong hostname. Neither ever emits a preamble — <see cref="Preamble"/> is written
    /// verbatim instead, so a file without a byte-order mark never acquires one.
    /// </para>
    /// </summary>
    private readonly record struct DetectedEncoding(
        Encoding ForReading,
        Encoding ForWriting,
        byte[] Preamble,
        bool UsedFallback);

    /// <summary>
    /// Identifies the encoding, preferring a byte-order mark and falling back to Latin-1 when
    /// the bytes are not valid UTF-8.
    /// <para>
    /// Latin-1 matters because it maps every byte to exactly one character and back, so a hosts
    /// file saved in a legacy code page round-trips intact. Decoding it as UTF-8 with the
    /// default fallback would replace each bad byte with U+FFFD and quietly rewrite the file
    /// the first time it was saved.
    /// </para>
    /// </summary>
    private static DetectedEncoding DetectEncoding(byte[] bytes)
    {
        if (StartsWith(bytes, Utf8Bom))
        {
            return new DetectedEncoding(new UTF8Encoding(false), StrictUtf8(), Utf8Bom, false);
        }

        // UTF-16 LE's mark is a prefix of UTF-32 LE's, but a hosts file is never UTF-32 and
        // guessing wrong would mangle it, so only the two-byte forms are recognised.
        if (StartsWith(bytes, Utf16LeBom))
        {
            return new DetectedEncoding(
                new UnicodeEncoding(bigEndian: false, byteOrderMark: false),
                new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true),
                Utf16LeBom,
                false);
        }

        if (StartsWith(bytes, Utf16BeBom))
        {
            return new DetectedEncoding(
                new UnicodeEncoding(bigEndian: true, byteOrderMark: false),
                new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true),
                Utf16BeBom,
                false);
        }

        try
        {
            _ = StrictUtf8().GetString(bytes);
            return new DetectedEncoding(new UTF8Encoding(false), StrictUtf8(), [], false);
        }
        catch (DecoderFallbackException)
        {
            var strictLatin1 = Encoding.GetEncoding(
                Encoding.Latin1.CodePage,
                new EncoderExceptionFallback(),
                new DecoderExceptionFallback());

            return new DetectedEncoding(Encoding.Latin1, strictLatin1, [], true);
        }
    }

    private static UTF8Encoding StrictUtf8() => new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static bool StartsWith(byte[] bytes, byte[] prefix)
    {
        if (bytes.Length < prefix.Length) return false;

        for (var i = 0; i < prefix.Length; i++)
        {
            if (bytes[i] != prefix[i]) return false;
        }

        return true;
    }
}
