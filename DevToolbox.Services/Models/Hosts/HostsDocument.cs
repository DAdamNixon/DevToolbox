using System.Security.Cryptography;
using System.Text;

namespace DevToolbox.Services.Models.Hosts;

/// <summary>
/// One line of a hosts file, carrying its own terminator.
/// <para>
/// The terminator travels with the line rather than being normalised away, because that is
/// what makes a file with mixed endings — or a CRLF file read by something that prefers LF —
/// survive a round trip untouched. The legacy tool read with <c>replace(/\r/g,'')</c> and
/// wrote with <c>join('\n')</c>, silently converting every CRLF hosts file it edited.
/// </para>
/// </summary>
/// <param name="Number">1-based line number, matching what an editor shows.</param>
/// <param name="Text">The line without its terminator.</param>
/// <param name="NewLine"><c>"\r\n"</c>, <c>"\n"</c>, or <c>""</c> for a final line with no terminator.</param>
public sealed record HostsLine(int Number, string Text, string NewLine)
{
    /// <summary>The same line with different content, keeping its number and terminator.</summary>
    public HostsLine WithText(string text) => this with { Text = text };
}

/// <summary>
/// A hosts file parsed into lines, with everything needed to write it back byte-for-byte.
/// <para>
/// <see cref="HostsDocumentCodec.Compose"/> of an unmutated document equals the bytes it was
/// read from. That invariant is the feature's foundation: a bug in the parser or the mutator
/// can then only decline to act, never corrupt a hosts file.
/// </para>
/// </summary>
public sealed class HostsDocument
{
    /// <summary>Where this was read from. Informational; the codec does not reread it.</summary>
    public required string Path { get; init; }

    /// <summary>
    /// The encoding the bytes were decoded with. Paired with <see cref="Preamble"/> rather
    /// than relying on the encoding's own BOM behaviour, since <c>GetBytes</c> never emits a
    /// preamble and <c>Encoding.UTF8</c> claims one whether the file had it or not.
    /// </summary>
    public required Encoding Encoding { get; init; }

    /// <summary>
    /// The exact byte-order-mark bytes found at the start of the file, or empty when there
    /// were none. Written back verbatim.
    /// </summary>
    public required IReadOnlyList<byte> Preamble { get; init; }

    /// <summary>Whether the file began with a byte-order mark.</summary>
    public bool HasByteOrderMark => Preamble.Count > 0;

    /// <summary>
    /// The terminator to give lines this application inserts — the file's most frequent
    /// existing one, so a repair does not introduce a stray ending.
    /// </summary>
    public required string DefaultNewLine { get; init; }

    public required IReadOnlyList<HostsLine> Lines { get; init; }

    /// <summary>Hash of the bytes this was read from. The precondition for a safe write.</summary>
    public required string Sha256 { get; init; }

    public DateTime LastWriteTimeUtc { get; init; }

    /// <summary>How many bytes the source was. Used together with the timestamp to spot external edits cheaply.</summary>
    public long Length { get; init; }

    /// <summary>
    /// The bytes were not valid UTF-8 and were read as Latin-1 to keep them intact. Reported as
    /// an anomaly so the developer knows why an unusual character looks the way it does.
    /// </summary>
    public bool DecodedWithFallbackEncoding { get; init; }

    /// <summary>
    /// The same document with different lines, renumbered from 1.
    /// <para>
    /// <see cref="Sha256"/>, <see cref="LastWriteTimeUtc"/> and <see cref="Length"/> are
    /// carried over unchanged: they describe the bytes on disk that this document started
    /// from, which is exactly what a write needs as its precondition.
    /// </para>
    /// </summary>
    public HostsDocument WithLines(IEnumerable<HostsLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var renumbered = lines
            .Select((line, index) => line.Number == index + 1 ? line : line with { Number = index + 1 })
            .ToArray();

        return new HostsDocument
        {
            Path = Path,
            Encoding = Encoding,
            Preamble = Preamble,
            DefaultNewLine = DefaultNewLine,
            Lines = renumbered,
            Sha256 = Sha256,
            LastWriteTimeUtc = LastWriteTimeUtc,
            Length = Length,
            DecodedWithFallbackEncoding = DecodedWithFallbackEncoding,
        };
    }

    /// <summary>The whole file as text, terminators included, without the byte-order mark.</summary>
    public string ToText()
    {
        var builder = new StringBuilder(checked((int)Math.Min(Length + 64, int.MaxValue)));
        foreach (var line in Lines)
        {
            builder.Append(line.Text).Append(line.NewLine);
        }

        return builder.ToString();
    }

    /// <summary>Lowercase hex SHA-256, the form used everywhere a hash is compared or logged.</summary>
    public static string HashOf(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
