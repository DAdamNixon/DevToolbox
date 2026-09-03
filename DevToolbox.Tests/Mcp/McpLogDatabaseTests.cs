using DevToolbox.Mcp.Core;

namespace DevToolbox.Tests.Mcp;

/// <summary>
/// The per-process scratch database and its sweep.
/// <para>
/// This is the correctness half of the "MCP does not share the UI's database" decision, and the
/// interesting tests are the two negatives: a sweep must not delete a <b>live</b> sibling session's
/// database, and it must not be able to reach the UI's <c>logs.db</c> at all. Both were observed
/// red against the first implementation, which decided liveness by trying to delete the database
/// itself — that passes the happy path and silently deletes a live session's file, because the
/// storage layer opens and closes a connection per operation and the .db is unlocked most of the
/// time.
/// </para>
/// </summary>
public sealed class McpLogDatabaseTests
{
    private static string MakeDatabase(string folder, string name, long bytes = 16)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void The_file_name_carries_the_process_and_a_timestamp()
    {
        var name = McpLogDatabase.FileNameFor(4321, new DateTime(2026, 9, 2, 22, 15, 4, 987, DateTimeKind.Utc));

        Assert.StartsWith("logs.4321.", name);
        Assert.EndsWith(".db", name);

        // The timestamp is not decoration: pids are recycled, so without it two runs could collide
        // on a name and the second would adopt the first's file.
        Assert.Contains("20260902221504987", name);
        Assert.NotEqual(
            McpLogDatabase.FileNameFor(4321, new DateTime(2026, 9, 2, 22, 15, 4, 988, DateTimeKind.Utc)),
            name);
    }

    [Fact]
    public void A_database_with_no_live_owner_is_swept()
    {
        using var temp = new TempDirectory("mcp-sweep");
        var db = MakeDatabase(temp.Path, "logs.999.20260101000000000.db", bytes: 64);

        var reclaimed = McpLogDatabase.Sweep(temp.Path);

        Assert.False(File.Exists(db));
        Assert.Equal(64, reclaimed);
    }

    [Fact]
    public void A_database_whose_owner_still_holds_its_lock_is_left_alone()
    {
        // THE test. A live sibling session is exactly the case a naive "try to delete it" sweep
        // gets wrong, and getting it wrong means deleting a table another agent is querying.
        using var temp = new TempDirectory("mcp-sweep-live");
        var db = MakeDatabase(temp.Path, "logs.1000.20260101000000000.db");

        using (var _ = McpLogDatabase.AcquireOwnership(db))
        {
            var reclaimed = McpLogDatabase.Sweep(temp.Path);

            Assert.True(File.Exists(db), "a live owner's database was deleted by the sweep");
            Assert.Equal(0, reclaimed);
        }

        // Ownership released — the same sweep now collects it, which proves the previous assertion
        // was about the lock and not about the sweep being broken.
        McpLogDatabase.Sweep(temp.Path);
        Assert.False(File.Exists(db));
    }

    [Fact]
    public void Releasing_ownership_removes_the_lock_file()
    {
        using var temp = new TempDirectory("mcp-lock");
        var db = Path.Combine(temp.Path, "logs.1001.20260101000000000.db");

        var stream = McpLogDatabase.AcquireOwnership(db);
        var lockPath = db + McpLogDatabase.LockExtension;
        Assert.True(File.Exists(lockPath));

        stream.Dispose();

        // DeleteOnClose: a crash-free exit leaves nothing behind for the next sweep to consider.
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public void The_caller_can_keep_its_own_database_out_of_the_sweep()
    {
        using var temp = new TempDirectory("mcp-keep");
        var mine = MakeDatabase(temp.Path, "logs.2000.20260101000000000.db");
        var theirs = MakeDatabase(temp.Path, "logs.2001.20260101000000000.db");

        McpLogDatabase.Sweep(temp.Path, keep: Path.GetFileName(mine));

        Assert.True(File.Exists(mine));
        Assert.False(File.Exists(theirs));
    }

    [Fact]
    public void The_sweep_cannot_reach_the_uis_database()
    {
        // The \mcp\ subfolder is the whole mechanism. The UI's logs.db sits one level up, and the
        // sweep's glob is rooted in the subfolder, so reaching it is impossible rather than merely
        // avoided. Red if the folder were shared and the glob widened to logs*.db.
        using var temp = new TempDirectory("mcp-isolation");

        var devToolboxFolder = Path.Combine(temp.Path, "DevToolbox");
        var mcpFolder = Path.Combine(devToolboxFolder, McpLogDatabase.FolderName);
        Directory.CreateDirectory(mcpFolder);

        var uiDatabase = Path.Combine(devToolboxFolder, "logs.db");
        File.WriteAllBytes(uiDatabase, new byte[128]);
        MakeDatabase(mcpFolder, "logs.3000.20260101000000000.db");

        McpLogDatabase.Sweep(mcpFolder);

        Assert.True(File.Exists(uiDatabase), "the sweep deleted the DevToolbox UI's own log database");
    }

    [Fact]
    public void Delete_removes_the_wal_and_shm_companions_too()
    {
        // Deleting the .db and leaving a -wal behind is how you get a file SQLite will not open.
        using var temp = new TempDirectory("mcp-companions");
        var db = MakeDatabase(temp.Path, "logs.4000.20260101000000000.db", bytes: 10);
        File.WriteAllBytes(db + "-wal", new byte[20]);
        File.WriteAllBytes(db + "-shm", new byte[30]);

        var reclaimed = McpLogDatabase.Delete(db);

        Assert.False(File.Exists(db));
        Assert.False(File.Exists(db + "-wal"));
        Assert.False(File.Exists(db + "-shm"));
        Assert.Equal(60, reclaimed);
    }

    [Fact]
    public void Sweeping_a_folder_that_does_not_exist_is_not_an_error()
    {
        // It runs at startup before anything has created the folder.
        Assert.Equal(0, McpLogDatabase.Sweep(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
    }
}
