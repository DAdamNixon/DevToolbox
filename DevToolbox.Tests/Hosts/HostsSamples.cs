using DevToolbox.Services.Models.Hosts;
using DevToolbox.Services.Services.Hosts;

namespace DevToolbox.Tests.Hosts;

/// <summary>
/// Access to the sample hosts files in <c>Samples\</c>.
/// <para>
/// The samples are read as bytes and compared as bytes. They deliberately carry awkward
/// properties real files have — a byte-order mark, CRLF, tabs, a missing closing directive, an
/// invalid UTF-8 byte, no trailing newline — because those are what the parser has to survive.
/// Addresses are RFC 5737 and names RFC 2606 throughout: the structure is what matters, and no
/// real infrastructure belongs in a repository.
/// </para>
/// </summary>
internal static class HostsSamples
{
    /// <summary>Structurally a real in-use file: BOM, CRLF, five groups, no closing directive, an orphan region.</summary>
    public const string CrlfBom = "crlf-bom.hosts";

    /// <summary>No BOM, LF only, closed correctly.</summary>
    public const string LfNoBom = "lf-nobom.hosts";

    /// <summary>Tabs, the Microsoft preamble, a group tagged entirely by inline directives, and a closing directive.</summary>
    public const string TabsInlineClear = "tabs-inline-clear.hosts";

    /// <summary>Alternate addresses parked behind a second marker.</summary>
    public const string Parked = "parked.hosts";

    /// <summary>An option directive before any group directive.</summary>
    public const string OptionBeforeGroup = "option-before-group.hosts";

    /// <summary>Directives preceded by whitespace, which must still open a scope.</summary>
    public const string IndentedDirectives = "indented-directives.hosts";

    public const string MixedEndings = "mixed-endings.hosts";
    public const string NoTrailingNewLine = "no-trailing-newline.hosts";
    public const string Empty = "empty.hosts";

    /// <summary>Contains a byte that is valid Latin-1 and invalid UTF-8.</summary>
    public const string Latin1 = "latin1.hosts";

    /// <summary>Uses a completely different annotation dialect.</summary>
    public const string AltDialect = "alt-dialect.hosts";

    private static string Directory => Path.Combine(AppContext.BaseDirectory, "Samples");

    public static string PathOf(string name) => Path.Combine(Directory, name);

    public static byte[] BytesOf(string name) => File.ReadAllBytes(PathOf(name));

    public static HostsDocument Load(string name) => HostsDocumentCodec.Read(PathOf(name));

    public static (HostsDocument Document, HostsMap Map) Parse(string name, HostsDialect? dialect = null)
    {
        var document = Load(name);
        return (document, HostsAnnotationParser.Parse(document, dialect));
    }

    /// <summary>
    /// The dialect <see cref="AltDialect"/> is written in. Nothing about it overlaps the default,
    /// so a parser that has quietly hard-coded the default tokens fails every test using it.
    /// </summary>
    public static HostsDialect AlternateDialect { get; } = new()
    {
        Prefix = "#@",
        GroupVerb = "group",
        OptionVerb = "env",
        ClearVerb = "reset",
        FlagSeparator = ":",
        SeverityFlags = new Dictionary<string, HostsSeverityLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["prod"] = HostsSeverityLevel.Danger,
            ["staging"] = HostsSeverityLevel.Caution,
        },
    };

    /// <summary>
    /// A document built from text rather than from a file, for cases too small to deserve a
    /// fixture. UTF-8 with no byte-order mark, so the text is the bytes.
    /// </summary>
    public static (HostsDocument Document, HostsMap Map) ParseText(string text, HostsDialect? dialect = null)
    {
        var document = HostsDocumentCodec.FromBytes(
            "in-memory", System.Text.Encoding.UTF8.GetBytes(text), DateTime.UnixEpoch);

        return (document, HostsAnnotationParser.Parse(document, dialect));
    }

    /// <summary>Every sample, for the round-trip theory.</summary>
    public static TheoryData<string> All =>
    [
        CrlfBom, LfNoBom, TabsInlineClear, Parked, OptionBeforeGroup,
        IndentedDirectives, MixedEndings, NoTrailingNewLine, Empty, Latin1, AltDialect,
    ];
}
