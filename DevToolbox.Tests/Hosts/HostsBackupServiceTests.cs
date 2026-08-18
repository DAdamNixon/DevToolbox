using System.Text;
using DevToolbox.Services.Models.Hosts;
using DevToolbox.Services.Services.Hosts;

namespace DevToolbox.Tests.Hosts;

/// <summary>
/// Backups are what make the non-atomic write path recoverable and an unwanted switch undoable, so
/// what matters here is that a backup is exactly the bytes it was given and that pruning never eats
/// the newest ones.
/// </summary>
public sealed class HostsBackupServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "DevToolbox.Tests", "backups-" + Guid.NewGuid().ToString("N"));

    private readonly HostsBackupService _backups;

    public HostsBackupServiceTests()
    {
        _backups = new HostsBackupService(_directory);
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task A_backup_holds_exactly_the_bytes_it_was_given()
    {
        var contents = HostsSamples.BytesOf(HostsSamples.CrlfBom);

        var backup = await _backups.CreateAsync(contents, HostsChangeReasonKind.Switch);

        Assert.Equal(contents.Length, backup.SizeBytes);
        Assert.Equal(contents, await _backups.ReadAsync(backup));
        Assert.Equal(contents, await File.ReadAllBytesAsync(backup.FilePath));
    }

    [Fact]
    public async Task The_reason_survives_a_round_trip_through_the_file_name()
    {
        foreach (var reason in Enum.GetValues<HostsChangeReasonKind>())
        {
            var created = await _backups.CreateAsync(Bytes(reason.ToString()), reason);
            var found = await _backups.FindAsync(created.Id);

            Assert.NotNull(found);
            Assert.Equal(reason, found!.Reason);
        }
    }

    [Fact]
    public async Task Backups_are_listed_newest_first()
    {
        var first = await _backups.CreateAsync(Bytes("one"), HostsChangeReasonKind.Switch);
        await Task.Delay(5);
        var second = await _backups.CreateAsync(Bytes("two"), HostsChangeReasonKind.Switch);

        var listed = await _backups.ListAsync();

        Assert.Equal(second.Id, listed[0].Id);
        Assert.Contains(listed, backup => backup.Id == first.Id);
    }

    [Fact]
    public async Task Listing_a_folder_that_does_not_exist_yet_is_empty_rather_than_an_error()
    {
        Assert.Empty(await new HostsBackupService(Path.Combine(_directory, "never-created")).ListAsync());
    }

    [Fact]
    public async Task Files_that_are_not_backups_are_ignored()
    {
        await _backups.CreateAsync(Bytes("real"), HostsChangeReasonKind.Switch);

        // The folder belongs to the user and may hold anything.
        await File.WriteAllTextAsync(Path.Combine(_directory, "notes.txt"), "mine");
        await File.WriteAllTextAsync(Path.Combine(_directory, "hosts-nonsense.txt"), "mine");

        Assert.Single(await _backups.ListAsync());
    }

    [Fact]
    public async Task Pruning_keeps_the_newest_and_deletes_the_rest()
    {
        for (var i = 0; i < 5; i++)
        {
            await _backups.CreateAsync(Bytes($"copy {i}"), HostsChangeReasonKind.Switch);
            await Task.Delay(5);
        }

        var before = await _backups.ListAsync();
        await _backups.PruneAsync(2);
        var after = await _backups.ListAsync();

        Assert.Equal(5, before.Count);
        Assert.Equal(2, after.Count);
        Assert.Equal(before.Take(2).Select(b => b.Id), after.Select(b => b.Id));
    }

    [Fact]
    public async Task A_retention_of_zero_keeps_everything()
    {
        await _backups.CreateAsync(Bytes("one"), HostsChangeReasonKind.Switch);
        await Task.Delay(5);
        await _backups.CreateAsync(Bytes("two"), HostsChangeReasonKind.Switch);

        await _backups.PruneAsync(0);

        Assert.Equal(2, (await _backups.ListAsync()).Count);
    }

    [Fact]
    public async Task An_unknown_backup_id_is_null_rather_than_an_error()
    {
        Assert.Null(await _backups.FindAsync("hosts-20200101-000000000-switch.txt"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a test run over.
        }
    }
}
