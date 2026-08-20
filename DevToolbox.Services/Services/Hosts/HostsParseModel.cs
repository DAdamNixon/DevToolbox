using System.Net;
using DevToolbox.Services.Models.Hosts;

namespace DevToolbox.Services.Services.Hosts;

/// <summary>
/// Everything worth knowing about one line, worked out once.
/// <para>
/// The state machine, the risk analyzer and the entry builder all need the same answers —
/// where the directive starts, whether the line is switched on, what address it names — and
/// computing them three times invites the three passes to disagree. They read this instead.
/// </para>
/// </summary>
/// <param name="DirectiveIndex">Index of the dialect prefix, or -1 when the line has no directive.</param>
/// <param name="IsSectionDirective">
/// The directive stands alone and opens a scope, rather than tagging this one line. True when
/// nothing but whitespace precedes it.
/// </param>
/// <param name="ContentPart">
/// The part of the line that is host-file content: everything before the directive, or the
/// whole line when there is none.
/// </param>
internal sealed record HostsLineFacts(
    HostsLine Line,
    int DirectiveIndex,
    bool IsSectionDirective,
    string ContentPart,
    bool HasContent,
    bool IsActive,
    string StrippedContent,
    bool IsParked,
    IPAddress? Address,
    IReadOnlyList<string> Hostnames,
    string? TrailingText)
{
    public int Number => Line.Number;

    public bool HasDirective => DirectiveIndex >= 0;

    public bool IsInlineDirective => HasDirective && !IsSectionDirective;

    /// <summary>Nothing but whitespace on the line, and no directive.</summary>
    public bool IsBlank => !HasDirective && !HasContent;

    /// <summary>
    /// A usable address-to-hostnames mapping, whether or not it is switched on. A commented-out
    /// entry still counts — that is the normal state of an option that is switched off. Prose
    /// comments do not, which is what lets the risk analyzer tell a real body apart from a
    /// foreign block that drifted into scope.
    /// </summary>
    public bool IsEntry => Address is not null && Hostnames.Count > 0;

    // Escaped rather than written literally: both are invisible in an editor, and a stray
    // copy-paste of one is impossible to spot in a diff.
    private const char ByteOrderMark = '\uFEFF';
    private const char ZeroWidthSpace = '\u200B';

    /// <summary>Characters that may precede a directive and still leave it opening a scope.</summary>
    private static bool IsIgnorableBefore(char c) =>
        char.IsWhiteSpace(c) || c is ByteOrderMark or ZeroWidthSpace;

    public static HostsLineFacts From(HostsLine line, HostsDialect dialect)
    {
        var text = line.Text;
        var directiveIndex = text.IndexOf(dialect.Prefix, StringComparison.Ordinal);

        // A directive opens a scope when nothing but whitespace precedes it, rather than when it
        // sits literally at index 0. That distinction is the whole of the byte-order-mark bug:
        // the legacy tool tested for index 0, so a file starting with a BOM had its very first
        // group classified as a per-line tag and dropped on the floor. Tolerating a zero-width
        // space and leading indentation costs nothing and removes the entire class of problem.
        var isSection = directiveIndex >= 0;
        for (var i = 0; i < directiveIndex; i++)
        {
            if (IsIgnorableBefore(text[i])) continue;
            isSection = false;
            break;
        }

        var contentPart = directiveIndex >= 0 ? text[..directiveIndex] : text;
        var hasContent = HostsTokenizer.HasContent(contentPart);
        var stripped = HostsTokenizer.StripComment(contentPart);
        var (address, hostnames, trailing) = ParseEntry(stripped);

        return new HostsLineFacts(
            Line: line,
            DirectiveIndex: directiveIndex,
            IsSectionDirective: isSection,
            ContentPart: contentPart,
            HasContent: hasContent,
            IsActive: hasContent && !HostsTokenizer.StartsCommented(contentPart),
            StrippedContent: stripped,
            IsParked: hasContent && dialect.IsParked(stripped),
            Address: address,
            Hostnames: hostnames,
            TrailingText: trailing);
    }

    /// <summary>
    /// Splits <c>203.0.113.9  db01.example.com db01  # note</c> into its address, its hostnames
    /// and anything trailing.
    /// <para>
    /// Text in parentheses is treated as trailing rather than as more hostnames, because that is
    /// how hosts files in the wild annotate a line — and the hosts format has no such notion, so
    /// those words would silently become additional hostnames the moment the line was enabled.
    /// Reporting it beats honouring it.
    /// </para>
    /// </summary>
    private static (IPAddress? Address, IReadOnlyList<string> Hostnames, string? Trailing) ParseEntry(string content)
    {
        if (content.Length == 0) return (null, Array.Empty<string>(), null);

        var commentIndex = content.IndexOf(HostsTokenizer.CommentChar);
        var body = commentIndex >= 0 ? content[..commentIndex] : content;

        var tokens = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || !IPAddress.TryParse(tokens[0], out var address))
        {
            return (null, Array.Empty<string>(), null);
        }

        var names = new List<string>();
        string? trailing = null;

        for (var i = 1; i < tokens.Length; i++)
        {
            if (tokens[i].StartsWith('('))
            {
                trailing = string.Join(' ', tokens[i..]);
                break;
            }

            names.Add(tokens[i]);
        }

        return (address, names, trailing);
    }
}

/// <summary>Mutable option while the scope pass is running. Frozen into a <see cref="HostsOption"/> afterwards.</summary>
internal sealed class HostsOptionDraft
{
    public required string Group { get; init; }
    public required string Name { get; init; }
    public HostsSeverityLevel Severity { get; set; }

    /// <summary>
    /// Every line carrying a scope-opening directive for this option. Usually one; a name repeated
    /// later in the file adds another, and renaming has to rewrite all of them.
    /// </summary>
    public List<int> DirectiveLines { get; } = [];

    /// <summary>Content lines inside the option's scope, before the risk analyzer partitions them.</summary>
    public List<int> BodyLines { get; } = [];

    /// <summary>Lines tagged individually by a trailing directive.</summary>
    public List<int> InlineLines { get; } = [];

    /// <summary>Lines parked by a marker. Never toggled, never counted.</summary>
    public List<int> ParkedLines { get; } = [];

    /// <summary>Filled by the risk analyzer: the body lines the option unambiguously owns.</summary>
    public List<int> OwnedLines { get; } = [];

    /// <summary>Filled by the risk analyzer: body lines quarantined as probably not the option's.</summary>
    public List<int> SuspectLines { get; } = [];
}

/// <summary>Mutable group while the scope pass is running.</summary>
internal sealed class HostsGroupDraft
{
    public required string Name { get; init; }

    /// <inheritdoc cref="HostsOptionDraft.DirectiveLines"/>
    public List<int> DirectiveLines { get; } = [];

    public int EndLine { get; set; }
    public HostsScopeEnd EndKind { get; set; }
    public List<HostsOptionDraft> Options { get; } = [];
    public Dictionary<string, HostsOptionDraft> ByName { get; } = new(StringComparer.Ordinal);
}
