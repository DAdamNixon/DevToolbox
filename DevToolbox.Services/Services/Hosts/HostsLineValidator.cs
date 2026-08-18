using System.Net;
using DevToolbox.Services.Models.Hosts;

namespace DevToolbox.Services.Services.Hosts;

/// <summary>
/// Checks what somebody typed before any of it reaches the file, and composes the line it becomes.
/// <para>
/// The rules divide into two kinds. Some are the hosts format's — an address that parses, at least
/// one name, labels of a legal length. The rest exist so that what gets written parses back as what
/// was meant: a name carrying the dialect's own punctuation would be read as a directive or as a
/// severity flag, and the file would then describe something nobody asked for.
/// </para>
/// </summary>
public static class HostsLineValidator
{
    /// <summary>Longest a fully-qualified name may be, per RFC 1035.</summary>
    public const int MaxHostnameLength = 253;

    /// <summary>Longest one dot-separated label may be, per RFC 1035.</summary>
    public const int MaxLabelLength = 63;

    /// <summary>Column the hostnames start in, so authored lines line up with each other.</summary>
    private const int AddressColumnWidth = 15;

    /// <summary>Names as the developer separated them: any run of whitespace.</summary>
    public static string[] SplitHostnames(string? hostnames) =>
        (hostnames ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Everything wrong with an entry, in the order a form would show it. Empty means usable.</summary>
    public static IReadOnlyList<string> ValidateEntry(NewHostsEntry entry, HostsDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(dialect);

        var problems = new List<string>();

        ValidateAddress(entry.Address, problems);
        ValidateHostnames(entry.Hostnames, problems);

        if (entry.Comment is { } comment && comment.Length > 0)
        {
            if (HasLineBreak(comment)) problems.Add("The note cannot span more than one line.");

            // An inline directive is a real feature of the format, so this is not nonsense — it is
            // just never what somebody typing a note meant, and it would silently re-scope the line.
            if (comment.Contains(dialect.Prefix, StringComparison.Ordinal))
            {
                problems.Add($"The note cannot contain '{dialect.Prefix}', which would turn it into a directive.");
            }
        }

        return problems;
    }

    private static void ValidateAddress(string? address, List<string> problems)
    {
        var trimmed = (address ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            problems.Add("An address is required.");
            return;
        }

        if (!IPAddress.TryParse(trimmed, out var parsed))
        {
            problems.Add($"'{trimmed}' is not an IP address.");
            return;
        }

        // TryParse is more generous than anybody expects — "1" is accepted and means 0.0.0.1, and
        // "10.1.2.03" is accepted and means 10.1.2.3. Both would go into the file looking like one
        // thing and resolve as another, so only the canonical spelling is allowed through.
        if (!string.Equals(parsed.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"'{trimmed}' means {parsed}. Write it that way to avoid the ambiguity.");
        }
    }

    private static void ValidateHostnames(string? hostnames, List<string> problems)
    {
        var names = SplitHostnames(hostnames);

        if (names.Length == 0)
        {
            problems.Add("At least one hostname is required.");
            return;
        }

        foreach (var name in names)
        {
            var problem = HostnameProblem(name);
            if (problem is not null) problems.Add(problem);
        }
    }

    /// <summary>What is wrong with one hostname, or null when nothing is.</summary>
    public static string? HostnameProblem(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (name.Length == 0) return "A hostname cannot be empty.";
        if (name.Length > MaxHostnameLength) return $"'{Shorten(name)}' is longer than {MaxHostnameLength} characters.";
        if (name.StartsWith('.') || name.EndsWith('.')) return $"'{name}' cannot start or end with a dot.";

        foreach (var label in name.Split('.'))
        {
            if (label.Length == 0) return $"'{name}' has an empty part between two dots.";
            if (label.Length > MaxLabelLength) return $"'{name}' has a part longer than {MaxLabelLength} characters.";
            if (label.StartsWith('-') || label.EndsWith('-')) return $"'{name}' has a part starting or ending with a hyphen.";

            foreach (var c in label)
            {
                // Underscores are not strictly legal in a hostname but are common in hosts files and
                // Windows honours them, so rejecting one would fail a line that demonstrably works.
                if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
                {
                    return $"'{name}' contains '{c}', which is not allowed in a hostname.";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a group or option may be called this. <paramref name="noun"/> names the thing in the
    /// message, e.g. <c>"group"</c>.
    /// </summary>
    public static IReadOnlyList<string> ValidateName(string? name, HostsDialect dialect, string noun)
    {
        ArgumentNullException.ThrowIfNull(dialect);

        var problems = new List<string>();
        var trimmed = (name ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            problems.Add($"The {noun} needs a name.");
            return problems;
        }

        if (HasLineBreak(trimmed)) problems.Add($"A {noun} name cannot span more than one line.");

        if (trimmed.Contains(dialect.Prefix, StringComparison.Ordinal))
        {
            problems.Add($"A {noun} name cannot contain '{dialect.Prefix}', which starts a directive.");
        }

        // The parser can cope with a name containing the separator, but only by guessing whether the
        // last part is a severity flag — so '{Name}:warn' would come back as just '{Name}'. Reading
        // such a file is worth supporting; writing one is not.
        if (trimmed.Contains(dialect.FlagSeparator, StringComparison.Ordinal))
        {
            problems.Add($"A {noun} name cannot contain '{dialect.FlagSeparator}', because that separates "
                         + "the name from its severity flag.");
        }

        return problems;
    }

    /// <summary>Everything wrong with a whole group, names and entries together.</summary>
    public static IReadOnlyList<string> ValidateGroup(NewHostsGroup group, HostsDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(dialect);

        var problems = new List<string>(ValidateName(group.Name, dialect, "group"));

        if (group.Options.Count == 0)
        {
            problems.Add("A group needs at least one option — there would be nothing to switch between.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var option in group.Options)
        {
            problems.AddRange(ValidateOption(option, dialect));

            if (!seen.Add(option.Name.Trim()))
            {
                problems.Add($"'{option.Name.Trim()}' appears twice. Options in a group need distinct names.");
            }
        }

        return problems;
    }

    /// <summary>Everything wrong with one option and the entries it would start with.</summary>
    public static IReadOnlyList<string> ValidateOption(NewHostsOption option, HostsDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(option);
        ArgumentNullException.ThrowIfNull(dialect);

        var problems = new List<string>(ValidateName(option.Name, dialect, "option"));

        foreach (var entry in option.EntryList)
        {
            problems.AddRange(ValidateEntry(entry, dialect));
        }

        return problems;
    }

    /// <summary>
    /// The entry as one line of hosts-file content, enabled. Callers that want it switched off pass
    /// the result through <see cref="HostsTokenizer.Comment"/>, so the marker is applied by the same
    /// code that applies it everywhere else and the invariant checker's view of it stays true.
    /// </summary>
    public static string ComposeEntry(NewHostsEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var address = entry.Address.Trim();
        var names = string.Join(' ', SplitHostnames(entry.Hostnames));
        var line = address.PadRight(AddressColumnWidth) + ' ' + names;

        var comment = entry.Comment?.Trim();

        return string.IsNullOrEmpty(comment) ? line : line + "  " + HostsTokenizer.CommentChar + ' ' + comment;
    }

    /// <summary>
    /// Pulls an existing line apart into the fields an editor shows — the inverse of
    /// <see cref="ComposeEntry"/>.
    /// <para>
    /// Comment markers are stripped first, so a switched-off entry is just as editable as a live
    /// one. Anything after a <c>#</c> is the note. Text in brackets after the hostnames is folded
    /// into the note as well: the hosts format has no comment there, so those words would become
    /// extra hostnames the moment the line was enabled — editing a line through this dialog quietly
    /// repairs that.
    /// </para>
    /// </summary>
    /// <returns>The decomposed entry, or <c>null</c> when the line is not one.</returns>
    public static NewHostsEntry? DecomposeEntry(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var content = HostsTokenizer.StripComment(text);
        if (content.Length == 0) return null;

        var marker = content.IndexOf(HostsTokenizer.CommentChar);
        var body = marker >= 0 ? content[..marker] : content;
        var note = marker >= 0 ? content[(marker + 1)..].Trim() : string.Empty;

        var tokens = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        // The first token has to be an address, or this is prose rather than an entry. Without this
        // a comment like "# Added by Docker Desktop" decomposes into an address of "Added" and the
        // editor offers to correct it.
        if (tokens.Length < 2 || !IPAddress.TryParse(tokens[0], out _)) return null;

        var names = new List<string>();
        var bracketed = new List<string>();

        for (var index = 1; index < tokens.Length; index++)
        {
            if (bracketed.Count > 0 || tokens[index].StartsWith('(')) bracketed.Add(tokens[index]);
            else names.Add(tokens[index]);
        }

        if (names.Count == 0) return null;

        if (bracketed.Count > 0)
        {
            var trailing = string.Join(' ', bracketed);
            note = note.Length == 0 ? trailing : trailing + " " + note;
        }

        return new NewHostsEntry(tokens[0], string.Join(' ', names), note.Length == 0 ? null : note);
    }

    private static bool HasLineBreak(string text) => text.Contains('\n') || text.Contains('\r');

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..40] + "…";
}
