using System.Text.RegularExpressions;
using DevToolbox.Services.Models;

namespace DevToolbox.Mcp.Core;

/// <summary>
/// Which log names exist in a location — with a fallback for the very common case of a location
/// that has no <c>namePattern</c>.
/// <para>
/// <c>DbLogService.DiscoverLogFileNamesAsync</c> skips any location without a pattern, which is
/// right for the UI: the Log File box still accepts free text, so a dropdown that cannot be filled
/// costs nothing. An agent has no free text to fall back on — an empty list reads as "there are no
/// logs here", and it would be wrong. On this workstation the local location is exactly that case.
/// </para>
/// <para>
/// So when a pattern is configured it is used and the method is reported as <c>pattern</c>; when
/// none is, files are grouped by stripping a trailing date-like stamp and the method is reported as
/// <c>heuristic</c>. <b>Which one ran is always in the result</b>, because a grouping that came
/// from a guess and one that came from configuration are different kinds of fact and the caller is
/// entitled to tell them apart.
/// </para>
/// <para>
/// The heuristic is deliberately generic — a trailing run of digits after a dot — rather than the
/// site's own <c>yyyyMMdd</c> convention. Encoding EE's naming here would put EE-specific
/// knowledge in a codebase whose stated constraint is that none lives in it.
/// </para>
/// </summary>
internal static class LogNameDiscovery
{
    internal const string MethodPattern = "pattern";
    internal const string MethodHeuristic = "heuristic";

    /// <summary>
    /// A trailing <c>.</c> plus six or more digits, immediately before the extension. Six is the
    /// floor because <c>yyyyMM</c> is the shortest date stamp in real use; fewer digits than that
    /// is far more likely to be part of a name.
    /// </summary>
    private static readonly Regex TrailingDateStamp = new(@"\.\d{6,}$", RegexOptions.Compiled);

    internal static (List<DiscoveredName> Names, string Method) Discover(
        LogLocation location,
        string extension,
        CancellationToken cancellationToken = default)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var method = string.IsNullOrWhiteSpace(location.NamePattern) ? MethodHeuristic : MethodPattern;

        Regex? configured = null;
        if (method == MethodPattern)
        {
            try
            {
                configured = new Regex(location.NamePattern!, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            }
            catch (ArgumentException)
            {
                // A bad pattern in hand-edited YAML must not make the location unreadable. Fall
                // back rather than return nothing, and say so.
                method = MethodHeuristic;
            }
        }

        try
        {
            if (!Directory.Exists(location.Path))
                return (new List<DiscoveredName>(), method);

            foreach (var path in Directory.EnumerateFiles(location.Path, $"*{extension}"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileName = Path.GetFileName(path);
                string? name;

                if (configured is not null && method == MethodPattern)
                {
                    var match = configured.Match(fileName);
                    if (!match.Success) continue;

                    var group = match.Groups["name"];
                    if (!group.Success || group.Value.Length == 0) continue;
                    name = group.Value;
                }
                else
                {
                    // Strip the extension, then a trailing date stamp if there is one. A file with
                    // no stamp keeps its whole stem, which is the honest answer for it.
                    name = TrailingDateStamp.Replace(Path.GetFileNameWithoutExtension(fileName), string.Empty);
                    if (name.Length == 0) continue;
                }

                counts.TryGetValue(name, out var running);
                counts[name] = running + 1;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Whatever was counted still stands, same as the UI's walk.
        }

        var names = counts
            .Select(kv => new DiscoveredName(kv.Key, kv.Value))
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (names, method);
    }
}
