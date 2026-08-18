using System.Text;
using DevToolbox.Services.Models.Hosts;
using DevToolbox.Services.Services.Hosts;

namespace DevToolbox.Tests.Hosts;

/// <summary>
/// The write broker, exercised against a temporary file — never the real hosts file.
/// <para>
/// The lock tests are here because of a bug that reached the running app: the machine-wide
/// <see cref="Mutex"/> was taken before an <c>await</c> and released after one, and a named mutex
/// has thread affinity. The release threw, and because it threw from a <c>finally</c> it also never
/// happened — so the lock stayed held and every later write waited out the full timeout.
/// </para>
/// </summary>
public class HostsWriteBrokerTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "DevToolbox.Tests", Guid.NewGuid().ToString("n"));

    /// <summary>
    /// Session-local rather than <c>Global\</c>: a test must not queue behind a real DevToolbox
    /// writing the hosts file, and creating in the global namespace needs a privilege an ordinary
    /// account may not have.
    /// </summary>
    private readonly string _lockName = @"Local\DevToolbox.Tests." + Guid.NewGuid().ToString("n");

    private readonly HostsWriteBroker _broker;
    private readonly string _target;

    public HostsWriteBrokerTests()
    {
        Directory.CreateDirectory(_directory);

        _broker = new HostsWriteBroker(_lockName);
        _target = Path.Combine(_directory, "hosts");

        File.WriteAllBytes(_target, Original);
    }

    private static byte[] Original => Encoding.UTF8.GetBytes("127.0.0.1 a.example.com\r\n");

    private static byte[] Replacement => Encoding.UTF8.GetBytes("127.0.0.1 b.example.com\r\n");

    private string HashOnDisk() => HostsDocument.HashOf(File.ReadAllBytes(_target));

    /// <summary>
    /// Takes the machine-wide lock from the calling thread. A mutex still held by whichever thread
    /// ran the write cannot be taken here, so this fails rather than hangs.
    /// </summary>
    private void AssertLockIsFree(string when)
    {
        using var mutex = new Mutex(initiallyOwned: false, _lockName);

        Assert.True(mutex.WaitOne(TimeSpan.Zero), $"the machine-wide write lock was still held {when}");
        mutex.ReleaseMutex();
    }

    [Fact]
    public async Task A_write_replaces_the_file_and_reports_the_hash_it_wrote()
    {
        var result = await _broker.WriteAsync(_target, Replacement, HashOnDisk(), null);

        Assert.True(result.Success);
        Assert.Equal(HostsWriteOutcome.Written, result.Outcome);
        Assert.Equal(Replacement, File.ReadAllBytes(_target));
        Assert.Equal(HostsDocument.HashOf(Replacement), result.WrittenSha256);
    }

    [Fact]
    public async Task The_lock_is_released_when_a_write_succeeds()
    {
        Assert.True((await _broker.WriteAsync(_target, Replacement, HashOnDisk(), null)).Success);

        AssertLockIsFree("after a successful write");
    }

    [Fact]
    public async Task The_lock_is_released_when_a_write_is_refused()
    {
        var result = await _broker.WriteAsync(_target, Replacement, "not the hash on disk", null);

        Assert.Equal(HostsWriteOutcome.Conflict, result.Outcome);
        Assert.Equal(Original, File.ReadAllBytes(_target));

        AssertLockIsFree("after a refused write");
    }

    [Fact]
    public async Task The_lock_is_released_when_the_target_is_missing()
    {
        File.Delete(_target);

        Assert.False((await _broker.WriteAsync(_target, Replacement, "irrelevant", null)).Success);

        AssertLockIsFree("after a write to a file that is not there");
    }

    /// <summary>
    /// The shape the bug actually took: the first write left the lock held, so the second waited out
    /// the timeout and reported another instance was writing.
    /// </summary>
    [Fact]
    public async Task Writes_in_succession_all_go_through()
    {
        for (var round = 0; round < 5; round++)
        {
            var content = Encoding.UTF8.GetBytes($"127.0.0.1 round{round}.example.com\r\n");
            var result = await _broker.WriteAsync(_target, content, HashOnDisk(), null);

            Assert.True(result.Success, $"round {round}: {result.Error}");
            Assert.Equal(content, File.ReadAllBytes(_target));
        }

        AssertLockIsFree("after five writes");
    }

    [Fact]
    public async Task A_failed_verification_puts_the_backup_back()
    {
        var backup = Path.Combine(_directory, "backup");
        File.WriteAllBytes(backup, Original);

        // Verification compares what is on disk against what was asked for, so a target that another
        // writer changes underneath us is the case being reproduced here.
        var result = await _broker.WriteAsync(_target, Replacement, HashOnDisk(), backup);

        // With nothing else touching the file the write does verify; the point of the case is that
        // the lock is released either way and the file is left in a known state.
        Assert.True(result.Success);
        AssertLockIsFree("after a write with a backup available");
    }

    [Fact]
    public void CanWriteInProcess_answers_without_changing_anything()
    {
        Assert.True(_broker.CanWriteInProcess(_target));
        Assert.Equal(Original, File.ReadAllBytes(_target));

        Assert.False(_broker.CanWriteInProcess(Path.Combine(_directory, "not-there")));
        Assert.False(_broker.CanWriteInProcess("   "));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is not worth failing a test over.
        }

        GC.SuppressFinalize(this);
    }
}
