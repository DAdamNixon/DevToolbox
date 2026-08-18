using System.Text;
using DevToolbox.Services.Services.Hosts;

namespace DevToolbox.Tests.Hosts;

/// <summary>
/// The byte-faithfulness of the codec. Everything else in the feature is built on the guarantee
/// asserted here, so it is checked against every sample rather than a representative one.
/// </summary>
public class HostsDocumentCodecTests
{
    [Theory]
    [MemberData(nameof(Samples))]
    public void Compose_of_an_unmutated_document_is_byte_identical(string sample)
    {
        var original = HostsSamples.BytesOf(sample);

        var composed = HostsDocumentCodec.Compose(HostsSamples.Load(sample));

        Assert.Equal(original, composed);
    }

    public static TheoryData<string> Samples => HostsSamples.All;

    [Fact]
    public void A_byte_order_mark_is_preserved_and_kept_out_of_the_first_line()
    {
        var document = HostsSamples.Load(HostsSamples.CrlfBom);

        Assert.True(document.HasByteOrderMark);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, document.Preamble);

        // The mark must not leak into the text, or every check against the first line — including
        // whether it opens a group — is off by one character.
        Assert.Equal("##key:Local Sites", document.Lines[0].Text);
        Assert.DoesNotContain((char)0xFEFF, document.ToText());
    }

    [Fact]
    public void A_file_without_a_byte_order_mark_does_not_gain_one()
    {
        var document = HostsSamples.Load(HostsSamples.LfNoBom);

        Assert.False(document.HasByteOrderMark);
        Assert.Empty(document.Preamble);
        Assert.Equal(HostsSamples.BytesOf(HostsSamples.LfNoBom), HostsDocumentCodec.Compose(document));
    }

    [Fact]
    public void Every_line_keeps_its_own_terminator()
    {
        var document = HostsSamples.Load(HostsSamples.CrlfBom);

        Assert.All(document.Lines, line => Assert.Equal("\r\n", line.NewLine));
        Assert.Equal("\r\n", document.DefaultNewLine);
    }

    [Fact]
    public void Mixed_terminators_survive_line_by_line()
    {
        var document = HostsSamples.Load(HostsSamples.MixedEndings);

        Assert.Contains(document.Lines, line => line.NewLine == "\r\n");
        Assert.Contains(document.Lines, line => line.NewLine == "\n");
        Assert.Equal(HostsSamples.BytesOf(HostsSamples.MixedEndings), HostsDocumentCodec.Compose(document));
    }

    [Fact]
    public void A_final_line_without_a_terminator_does_not_gain_one()
    {
        var document = HostsSamples.Load(HostsSamples.NoTrailingNewLine);

        Assert.Equal(string.Empty, document.Lines[^1].NewLine);
        Assert.Equal(HostsSamples.BytesOf(HostsSamples.NoTrailingNewLine), HostsDocumentCodec.Compose(document));
    }

    [Fact]
    public void A_trailing_terminator_does_not_produce_a_phantom_final_line()
    {
        var document = HostsSamples.Load(HostsSamples.CrlfBom);

        Assert.Equal(100, document.Lines.Count);
        Assert.Equal("127.0.0.1        metrics.example.com", document.Lines[^1].Text);
        Assert.Equal("\r\n", document.Lines[^1].NewLine);
    }

    [Fact]
    public void An_empty_file_has_no_lines_and_stays_empty()
    {
        var document = HostsSamples.Load(HostsSamples.Empty);

        Assert.Empty(document.Lines);
        Assert.Empty(HostsDocumentCodec.Compose(document));
    }

    [Fact]
    public void Bytes_that_are_not_valid_utf8_fall_back_to_latin1_and_survive()
    {
        var document = HostsSamples.Load(HostsSamples.Latin1);

        // Spelled from the code point rather than typed, so the test does not itself depend on how
        // this source file happens to be encoded.
        var expected = "caf" + (char)0xE9 + ".example.com";

        Assert.True(document.DecodedWithFallbackEncoding);
        Assert.Contains(expected, document.ToText());
        Assert.Equal(HostsSamples.BytesOf(HostsSamples.Latin1), HostsDocumentCodec.Compose(document));
    }

    [Fact]
    public void Valid_utf8_without_a_mark_is_not_treated_as_latin1()
    {
        Assert.False(HostsSamples.Load(HostsSamples.LfNoBom).DecodedWithFallbackEncoding);
    }

    /// <summary>
    /// The raw editor's failure mode. A browser hands back a <c>&lt;textarea&gt;</c>'s value with
    /// every terminator flattened to LF, so saving a CRLF file would silently convert the whole
    /// thing — the same defect the legacy tool had, one layer up.
    /// </summary>
    [Fact]
    public void FromText_restores_the_files_newline_style_after_a_textarea_flattens_it()
    {
        var document = HostsSamples.Load(HostsSamples.CrlfBom);
        var flattened = document.ToText().Replace("\r\n", "\n");

        var rebuilt = HostsDocumentCodec.FromText(document, flattened);

        Assert.All(rebuilt.Lines, line => Assert.Equal("\r\n", line.NewLine));
        Assert.Equal(HostsSamples.BytesOf(HostsSamples.CrlfBom), HostsDocumentCodec.Compose(rebuilt));
    }

    [Fact]
    public void FromText_keeps_the_byte_order_mark_and_encoding()
    {
        var document = HostsSamples.Load(HostsSamples.CrlfBom);

        var rebuilt = HostsDocumentCodec.FromText(document, document.ToText());

        Assert.True(rebuilt.HasByteOrderMark);
        Assert.Equal(document.Encoding.CodePage, rebuilt.Encoding.CodePage);
    }

    [Fact]
    public void FromText_renumbers_lines_from_one()
    {
        var document = HostsSamples.Load(HostsSamples.LfNoBom);

        var rebuilt = HostsDocumentCodec.FromText(document, "one\ntwo\nthree\n");

        Assert.Equal([1, 2, 3], rebuilt.Lines.Select(line => line.Number));
    }

    [Fact]
    public void The_hash_identifies_the_bytes_that_were_read()
    {
        var document = HostsSamples.Load(HostsSamples.CrlfBom);
        var expected = HostsDocumentCodec.Compose(document);

        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(expected)).ToLowerInvariant(),
            document.Sha256);
    }

    [Fact]
    public void Composing_uses_the_stored_encoding_rather_than_reencoding_as_utf8()
    {
        var document = HostsSamples.Load(HostsSamples.Latin1);

        // Latin-1, not UTF-8: an 'e-acute' is one byte here and two in UTF-8, so a codec that
        // silently normalised the encoding would change the file's length.
        Assert.Equal(Encoding.Latin1.CodePage, document.Encoding.CodePage);
        Assert.Equal(HostsSamples.BytesOf(HostsSamples.Latin1).Length, HostsDocumentCodec.Compose(document).Length);
    }
}
