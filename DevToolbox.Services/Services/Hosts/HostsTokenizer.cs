namespace DevToolbox.Services.Services.Hosts;

/// <summary>
/// Lexical helpers for a single hosts-file line: splitting it into tokens the way the legacy
/// Toolbox did, deciding whether it is enabled, and commenting or uncommenting it.
/// <para>
/// The tokenizer is a deliberate port of the legacy regex <c>/(\#|\S*)/g</c> with empty
/// matches dropped, because that regex defines what "this line is switched off" has meant in
/// the team's hosts files for years. Its one surprising property is that <c>#</c> is only ever
/// its own token when it <em>starts</em> a run: <c>#10.0.0.1</c> tokenizes as
/// <c>["#", "10.0.0.1"]</c>, while <c>abc#def</c> is the single token <c>abc#def</c>. Files in
/// the wild rely on this — they carry both <c>#10.55.160.58</c> and <c># 200.0.2.143</c>.
/// </para>
/// </summary>
public static class HostsTokenizer
{
    /// <summary>The marker that disables a line.</summary>
    public const char CommentChar = '#';

    /// <summary>What <see cref="Comment"/> prepends. Matches the legacy writer, space included.</summary>
    public const string CommentPrefix = "# ";

    /// <summary>
    /// Splits the line into tokens: each leading <c>#</c> of a run is its own token, and every
    /// other maximal run of non-whitespace is one token. Whitespace is dropped.
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var tokens = new List<string>();
        var index = 0;

        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                index++;
                continue;
            }

            if (text[index] == CommentChar)
            {
                tokens.Add("#");
                index++;
                continue;
            }

            var start = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index])) index++;
            tokens.Add(text[start..index]);
        }

        return tokens;
    }

    /// <summary>Whether the line carries anything at all besides whitespace.</summary>
    public static bool HasContent(string text)
    {
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c)) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the line is in effect — it has content and does not start with a comment
    /// marker. Blank lines are not active; nor are they inactive, they are simply not content.
    /// </summary>
    public static bool IsActive(string text) => HasContent(text) && !StartsCommented(text);

    /// <summary>Whether the first non-whitespace character is a comment marker.</summary>
    public static bool StartsCommented(string text)
    {
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            return c == CommentChar;
        }

        return false;
    }

    /// <summary>
    /// The line with every leading comment marker and the whitespace around them removed —
    /// what the line would say if it were switched on.
    /// <para>
    /// This is the canonical form both <see cref="Comment"/> and <see cref="Uncomment"/> leave
    /// untouched, which is what lets <c>HostsInvariantChecker</c> assert that a switch changed
    /// only a line's comment markers and never its content.
    /// </para>
    /// </summary>
    public static string StripComment(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var index = 0;
        while (index < text.Length && (char.IsWhiteSpace(text[index]) || text[index] == CommentChar)) index++;

        return index >= text.Length ? string.Empty : text[index..];
    }

    /// <summary>
    /// Disables the line by prefixing a comment marker. A line that is already commented, or
    /// that has no content, is returned unchanged so markers cannot accumulate.
    /// </summary>
    public static string Comment(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return IsActive(text) ? CommentPrefix + text : text;
    }

    /// <summary>
    /// Enables the line by removing its leading comment markers. A line that is already
    /// enabled, or that has nothing but markers, is returned unchanged.
    /// </summary>
    public static string Uncomment(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!StartsCommented(text)) return text;

        var content = StripComment(text);
        return content.Length == 0 ? text : content;
    }
}
