using System.Text.RegularExpressions;

namespace DevToolbox.Mcp.Core;

/// <summary>
/// The only sanctioned route from an exception to a string a caller can see.
/// <para>
/// The DB2 server's version of this exists because a driver failure can quote a connection string
/// containing a plaintext password. Nothing here holds a credential — but exception text in this
/// process routinely carries <b>filesystem paths</b>, and those paths run through
/// <c>%LOCALAPPDATA%</c>, which contains the Windows account name. That is not a catastrophe; it is
/// also not something to hand to a caller by accident on every I/O error, so it is removed.
/// </para>
/// <para>
/// Reads <c>ex.Message</c> and <c>ex.GetType().Name</c> and nothing else — never
/// <c>StackTrace</c>, <c>Data</c>, <c>InnerException</c> or <c>ToString()</c>, each of which
/// widens what a message can contain without widening what it explains.
/// </para>
/// </summary>
internal static class SafeError
{
    internal const string Redacted = "<path>";

    /// <summary>
    /// Any Windows absolute path — drive-rooted or UNC — plus everything to the end of the run.
    /// Deliberately greedy about what counts as a path character so a partially quoted path does
    /// not survive as a fragment.
    /// </summary>
    private static readonly Regex PathLike = new(
        @"(?:[A-Za-z]:[\\/]|\\\\)[^\s""'<>|]*",
        RegexOptions.Compiled);

    internal static string Describe(Exception ex)
    {
        var message = Scrub(ex.Message);

        // The type name is kept: "IOException" versus "SqliteException" is the difference between
        // "the file moved" and "the query was wrong", and neither name reveals anything.
        return string.IsNullOrWhiteSpace(message)
            ? ex.GetType().Name
            : $"{ex.GetType().Name}: {message}";
    }

    internal static string Scrub(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : PathLike.Replace(text, Redacted);
}
