namespace DevToolbox.Mcp.Core;

/// <summary>
/// What may be passed as a <c>logFile</c> argument: a bare file name, and only a bare file name.
/// <para>
/// This is the argument-side half of <see cref="LocalPathPolicy"/>, and it exists because that
/// half was missing. The location policy bounds which directories the server may read; nothing
/// bounded the name an agent supplies to be searched inside them. The value reaches the
/// filesystem as part of a search pattern —
/// <c>new DirectoryInfo(loc.Path).EnumerateFiles($"{logFile}*{template.Extension}")</c> — and a
/// .NET search pattern is permitted to contain directory separators, so the pattern is combined
/// with the location path and <c>..\</c> segments walk the search straight out of it.
/// </para>
/// <para>
/// Measured before the fix: <c>..\..\Windows\Temp\x</c> and <c>.</c> both succeeded and were
/// issued a handle. They returned no rows, but that was the name-prefix match finding nothing in
/// the directory it had landed in — not a refusal. Any local file matching
/// <c>&lt;remainder&gt;*&lt;extension&gt;</c> outside every admitted location was reachable, and
/// template parsing is positional and lenient, so unmatched content is placed in the overflow
/// columns rather than rejected. The readable set was therefore wider than
/// <c>list_locations</c> reports, which is the one thing that list exists to promise.
/// </para>
/// <para>
/// The absolute and UNC spellings were refused before this policy, but only by accident: they
/// reached a <see cref="Path.Combine(string, string)"/> deep in the BCL, which threw
/// <c>"Second path fragment must not be a drive or UNC name. (Parameter 'expression')"</c> —
/// a foreign message passed to the caller verbatim by the <c>ArgumentException</c> arm of
/// <c>ToolErrors</c>, which is written on the assumption that every such exception is one we
/// authored. A refusal resting on BCL incidentals is fragile even where its outcome is right, and
/// it teaches the caller nothing. Refusing here means the decision is made deliberately, in one
/// place, with a message that names the argument and says what to do instead.
/// </para>
/// </summary>
internal static class LogFileNamePolicy
{
    /// <summary>Why a log file name was refused, in words meant for the caller.</summary>
    internal const string ReasonBlank =
        "A log file name is required. Call list_log_files to see what is available.";

    internal const string ReasonPathShape =
        "Refused: a log file name, not a path. This server searches its configured locations " +
        "(see list_locations) and a name may not contain a directory separator, a drive, a " +
        "wildcard or a relative segment. Call list_log_files and pass a name it reports.";

    /// <summary>
    /// Null when the name is acceptable; otherwise the reason it is not.
    /// <para>
    /// Deliberately does NOT test whether any file matches. A name that is well formed but
    /// present nowhere is an empty result, which <c>prepare_table</c> already reports as zero
    /// rows — a different fact from a name this server will not search on, and collapsing the two
    /// would make a typo look like a policy decision. That is the same distinction
    /// <see cref="LocalPathPolicy.Refuse"/> draws by not testing directory existence.
    /// </para>
    /// </summary>
    internal static string? Refuse(string? logFile)
    {
        if (string.IsNullOrWhiteSpace(logFile))
            return ReasonBlank;

        // Covers every escape in one check, because on Windows the invalid set includes both
        // separators, the drive colon, and the wildcards: \ / : * ? " < > | and the control
        // characters. Separators are what make traversal possible; the colon is what makes
        // "C:name" drive-relative; and the wildcards matter because this argument is already
        // used as a prefix with the server appending its own '*', so a caller-supplied one only
        // widens a match in ways the caller cannot see reported.
        if (logFile.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return ReasonPathShape;

        // A name of only dots carries no separator and so survives the check above, while still
        // naming a directory rather than a file. Refused for the same reason as the rest even
        // though "." resolves to the location itself: a caller who sends it has not passed a log
        // file name, and being told so is more useful than an empty result.
        if (logFile.Trim('.').Length == 0)
            return ReasonPathShape;

        return null;
    }

    internal static bool IsAcceptable(string? logFile) => Refuse(logFile) is null;
}
