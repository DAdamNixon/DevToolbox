namespace DevToolbox.UI.Services;

/// <summary>
/// Hands back the row objects already on screen wherever a fresh page still holds the same row.
/// <para>
/// The grid keys what the user has done to a row — expanded it, opened its menu — on the row
/// object itself, and every query builds new ones. So without this, the page a load finishes on
/// arrives as all-new rows and every expansion quietly closes, at exactly the moment the user was
/// reading them. Rows are matched by content, and each old row is handed out at most once: a page
/// can hold identical rows (two copies of a line, a SQL projection of one column), and sharing one
/// object between them would expand them all together.
/// </para>
/// </summary>
internal static class LogRowReuse
{
    /// <summary>
    /// <paramref name="fresh"/>, with each row replaced by an unused equal row from
    /// <paramref name="current"/> where there is one. When every row matches, in order, and nothing
    /// was added or removed, returns <paramref name="current"/> itself — the page then sees no change
    /// at all and does not re-measure its columns.
    /// </summary>
    internal static List<Dictionary<string, string>> Reuse(
        List<Dictionary<string, string>> current,
        List<Dictionary<string, string>> fresh)
    {
        if (current.Count == 0 || fresh.Count == 0) return fresh;

        var byContent = new Dictionary<string, Queue<Dictionary<string, string>>>(StringComparer.Ordinal);
        foreach (var row in current)
        {
            var key = ContentKey(row);
            if (!byContent.TryGetValue(key, out var queue))
                byContent[key] = queue = new Queue<Dictionary<string, string>>();
            queue.Enqueue(row);
        }

        var result = new List<Dictionary<string, string>>(fresh.Count);
        foreach (var row in fresh)
        {
            result.Add(byContent.TryGetValue(ContentKey(row), out var queue) && queue.Count > 0
                ? queue.Dequeue()
                : row);
        }

        if (result.Count == current.Count)
        {
            var same = true;
            for (var i = 0; i < result.Count && same; i++)
                same = ReferenceEquals(result[i], current[i]);
            if (same) return current;
        }

        return result;
    }

    /// <summary>Column names and values, with separators no log line contains.</summary>
    private static string ContentKey(Dictionary<string, string> row) =>
        string.Join('\u001f', row.Select(kv => kv.Key + '\u001e' + kv.Value));
}
