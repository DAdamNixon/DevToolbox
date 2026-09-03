namespace DevToolbox.Services.Services;

/// <summary>
/// Where the log database lives, and the fact that it is scratch.
/// <para>
/// The Log Viewer ingests whatever a search matched into SQLite and rebuilds its table on the next
/// search, so nothing in there is ever read back across sessions — but nothing deleted it either.
/// On the machine this was found on it had reached **19.5 GB**, which would have followed the
/// application onto every other machine that ran a search and eventually filled someone's disk.
/// </para>
/// <para>
/// So it is thrown away at startup and made again empty. That is a decision about what this data
/// *is*, not a workaround: search results are worth keeping for as long as you are looking at them
/// and not one moment longer, and a retention policy for something nobody reads twice would be
/// machinery in place of an answer.
/// </para>
/// <para>
/// Deleting is safe at startup specifically because a second copy of the application can no longer
/// be running — see the single-instance guard in <c>Program.Main</c>. Without it this would risk
/// pulling the file out from under a live search in another instance.
/// </para>
/// </summary>
public static class LogDatabase
{
    public const string FileName = "logs.db";

    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DevToolbox",
        FileName);

    /// <summary>
    /// Deletes the database so the next search starts from an empty one.
    /// </summary>
    /// <param name="path">The database. Defaults to <see cref="Path"/>.</param>
    /// <returns>
    /// Bytes reclaimed. Zero when there was nothing to delete, and also when deletion failed — a
    /// stale database is a disk-space problem, and refusing to start over one would be worse.
    /// </returns>
    public static long Reset(string? path = null)
    {
        path ??= Path;
        var reclaimed = 0L;

        // The write-ahead log and its shared-memory index are part of the database. Leaving a -wal
        // behind after deleting the .db is how you get a file SQLite will not open.
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            try
            {
                if (!File.Exists(file)) continue;

                var size = new FileInfo(file).Length;
                File.Delete(file);
                reclaimed += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked or not ours. The application still starts; the file gets another chance to
                // go on the next launch.
            }
        }

        return reclaimed;
    }
}
