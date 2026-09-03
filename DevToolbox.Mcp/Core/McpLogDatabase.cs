namespace DevToolbox.Mcp.Core;

/// <summary>
/// Where this server process keeps its scratch SQLite database, and how the previous ones get
/// cleaned up.
/// <para>
/// The UI's <c>LogDatabase</c> uses one fixed file and deletes it at startup. Its own docstring
/// says why that is safe — <em>"a second copy of the application can no longer be running — see
/// the single-instance guard in Program.Main"</em> — and an MCP server process is exactly the
/// second process that guard assumes cannot exist. So neither half of that design is reused:
/// </para>
/// <list type="bullet">
/// <item><b>Never the UI's file.</b> Sharing it would mean the dev launching DevToolbox destroys
/// every live agent session's tables mid-query, and there is nothing this side could do about it.</item>
/// <item><b>One file per process.</b> stdio is a single-session transport, so a file named for the
/// process is a file owned by one session. Growth is bounded by the session — in practice by a
/// single ingest, since a prepare recreates its table rather than appending.</item>
/// </list>
/// <para>
/// The subfolder is load-bearing rather than tidiness: <see cref="Sweep"/> globs for its own files,
/// and keeping them out of <c>%LOCALAPPDATA%\DevToolbox</c> makes it <em>impossible</em> for that
/// glob to reach the UI's <c>logs.db</c>, rather than merely careful not to.
/// </para>
/// <para>
/// <b>Ownership is proved by a lock file, not by the database.</b> The obvious design — try to
/// delete, and treat failure as "someone is using it" — does not work here, and the reason is worth
/// recording because it is not obvious: the storage layer opens and closes a connection per
/// operation, so a perfectly live session's <c>.db</c> is unlocked most of the time. A sweep
/// relying on that would delete a sibling session's database between two of its own queries. So
/// each process holds an exclusive handle on a <c>.lock</c> beside its database for its whole
/// lifetime, and the sweep tests THAT. A lock that can be opened is a process that is gone.
/// </para>
/// </summary>
internal static class McpLogDatabase
{
    internal const string FolderName = "mcp";
    internal const string FilePrefix = "logs.";
    internal const string FileExtension = ".db";
    internal const string LockExtension = ".lock";

    internal static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DevToolbox",
        FolderName);

    /// <summary>
    /// This process's database file name. The pid identifies the owner; the timestamp defeats pid
    /// recycling, so two runs never collide even if Windows reissues the number.
    /// </summary>
    internal static string FileNameFor(int processId, DateTime startedUtc) =>
        $"{FilePrefix}{processId}.{startedUtc:yyyyMMddHHmmssfff}{FileExtension}";

    /// <summary>
    /// Takes the exclusive handle that marks <paramref name="databasePath"/> as owned, and returns
    /// it. <b>Keep it for the life of the process</b> — disposing it early makes this session's
    /// database look abandoned to the next sweep, and it will be deleted underneath a live query.
    /// </summary>
    internal static FileStream AcquireOwnership(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        return new FileStream(
            databasePath + LockExtension,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.DeleteOnClose);
    }

    /// <summary>
    /// Deletes databases whose owning process is gone, and reports the bytes reclaimed.
    /// <para>
    /// A database is considered abandoned when its lock file can be opened exclusively, or when it
    /// has no lock file at all — the latter covering anything left by a build of this server from
    /// before the lock existed. <paramref name="keep"/> is skipped by name so a caller cannot sweep
    /// away its own.
    /// </para>
    /// </summary>
    internal static long Sweep(string? folder = null, string? keep = null)
    {
        folder ??= Folder;
        if (!Directory.Exists(folder)) return 0;

        List<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(folder, $"{FilePrefix}*{FileExtension}").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        var reclaimed = 0L;

        foreach (var db in candidates)
        {
            if (keep is not null && string.Equals(Path.GetFileName(db), keep, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!IsAbandoned(db)) continue;

            reclaimed += Delete(db);
        }

        return reclaimed;
    }

    /// <summary>
    /// Whether the owner of <paramref name="databasePath"/> is gone. Opening its lock exclusively
    /// is the test: a live owner is holding that handle and this fails.
    /// </summary>
    private static bool IsAbandoned(string databasePath)
    {
        var lockPath = databasePath + LockExtension;
        if (!File.Exists(lockPath)) return true;

        try
        {
            // Not DeleteOnClose: this handle is a probe, and the real delete happens below where it
            // can be reported. Closing it immediately keeps the window shut.
            using var _ = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes one database and everything that belongs to it, returning the bytes reclaimed.
    /// <para>
    /// The <c>-wal</c> and <c>-shm</c> companions are part of the database: deleting the <c>.db</c>
    /// and leaving a <c>-wal</c> behind is how you get a file SQLite will not open. The <c>.db</c>
    /// goes last, so a deletion interrupted halfway leaves a set the next sweep still recognizes
    /// rather than orphans nothing matches.
    /// </para>
    /// </summary>
    internal static long Delete(string databasePath)
    {
        var reclaimed = 0L;

        foreach (var file in new[]
                 {
                     databasePath + "-wal",
                     databasePath + "-shm",
                     databasePath + LockExtension,
                     databasePath
                 })
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
                // Still held, or not ours. A database that cannot be removed is a disk-space
                // problem; refusing to carry on over one would be worse.
            }
        }

        return reclaimed;
    }
}
