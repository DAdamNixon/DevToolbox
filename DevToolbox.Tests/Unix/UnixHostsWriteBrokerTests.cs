using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using DevToolbox.Services.Models.Hosts;
using DevToolbox.Services.Services.Hosts;

namespace DevToolbox.Tests.Hosts;

/// <summary>
/// The Linux broker, against a temporary file — never the real hosts file, and never through pkexec.
/// <para>
/// The elevated step is a shell script, so it is run here the way pkexec would run it, with sh,
/// against files this test owns. That proves every refusal and the swap itself; the only part left
/// untested is pkexec asking for the password.
/// </para>
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class UnixHostsWriteBrokerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DevToolbox.Tests", Guid.NewGuid().ToString("n"));
    private readonly UnixHostsWriteBroker _broker = new();
    private readonly string _target;

    public UnixHostsWriteBrokerTests()
    {
        Directory.CreateDirectory(_directory);
        _target = Path.Combine(_directory, "hosts");
        File.WriteAllBytes(_target, Original);
    }

    private static byte[] Original => Encoding.UTF8.GetBytes("127.0.0.1 a.example.com\n");

    private static byte[] Replacement => Encoding.UTF8.GetBytes("127.0.0.1 b.example.com\n");

    private static string Hash(byte[] bytes) => HostsDocument.HashOf(bytes);

    [UnixFact]
    public async Task A_write_replaces_the_file_and_reports_the_hash_it_wrote()
    {
        var result = await _broker.WriteAsync(_target, Replacement, Hash(Original), restoreFromPath: null);

        Assert.Equal(HostsWriteOutcome.Written, result.Outcome);
        Assert.Equal(Replacement, File.ReadAllBytes(_target));
        Assert.Equal(Hash(Replacement), result.WrittenSha256);
    }

    [UnixFact]
    public async Task A_file_changed_since_it_was_read_is_left_alone()
    {
        var result = await _broker.WriteAsync(_target, Replacement, Hash(Encoding.UTF8.GetBytes("something else")), restoreFromPath: null);

        Assert.Equal(HostsWriteOutcome.Conflict, result.Outcome);
        Assert.Equal(Original, File.ReadAllBytes(_target));
    }

    [UnixFact]
    public async Task Writes_in_succession_all_go_through()
    {
        // The lock is a file held open for the write; one left held would time the next one out.
        var current = Original;
        for (var i = 0; i < 5; i++)
        {
            var next = Encoding.UTF8.GetBytes($"127.0.0.1 host{i}.example.com\n");
            var result = await _broker.WriteAsync(_target, next, Hash(current), restoreFromPath: null);
            Assert.True(result.Success, result.Error);
            current = next;
        }
    }

    [UnixFact]
    public void CanWriteInProcess_is_true_for_a_file_we_own_and_false_for_the_real_one()
    {
        Assert.True(_broker.CanWriteInProcess(_target));
        Assert.Equal(Original, File.ReadAllBytes(_target));

        // /etc/hosts is root's. If this ever runs as root the answer is legitimately true.
        if (Environment.UserName != "root") Assert.False(_broker.CanWriteInProcess("/etc/hosts"));
    }

    // ------------------------------------------------------------ the elevated step, without pkexec

    private int RunElevatedScript(string payload, string target, string wantPayload, string wantOriginal)
    {
        var startInfo = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardError = true };
        foreach (var argument in new[] { "-c", UnixHostsWriteBroker.ElevatedScript, "devtoolbox-hosts", payload, target, wantPayload, wantOriginal })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
        return process.ExitCode;
    }

    private string Staged(byte[] content)
    {
        var path = Path.Combine(_directory, "payload.hosts");
        File.WriteAllBytes(path, content);
        return path;
    }

    [UnixFact]
    public void The_elevated_step_swaps_the_file_in_and_keeps_its_permissions()
    {
        File.SetUnixFileMode(_target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var exit = RunElevatedScript(Staged(Replacement), _target, Hash(Replacement), Hash(Original));

        Assert.Equal(HostsWriterExitCodes.Success, exit);
        Assert.Equal(Replacement, File.ReadAllBytes(_target));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                     File.GetUnixFileMode(_target));
        Assert.False(File.Exists(_target + ".dtb.tmp"));
    }

    [UnixFact]
    public void The_elevated_step_refuses_a_hosts_file_that_changed_while_the_prompt_was_open()
    {
        var exit = RunElevatedScript(Staged(Replacement), _target, Hash(Replacement), Hash(Encoding.UTF8.GetBytes("stale")));

        Assert.Equal(HostsWriterExitCodes.TargetChanged, exit);
        Assert.Equal(Original, File.ReadAllBytes(_target));
    }

    [UnixFact]
    public void The_elevated_step_refuses_a_payload_that_is_not_the_one_requested()
    {
        var exit = RunElevatedScript(Staged(Replacement), _target, Hash(Encoding.UTF8.GetBytes("what was meant")), Hash(Original));

        Assert.Equal(HostsWriterExitCodes.PayloadMismatch, exit);
        Assert.Equal(Original, File.ReadAllBytes(_target));
    }

    [UnixFact]
    public void The_elevated_step_never_creates_a_hosts_file_that_is_not_there()
    {
        var missing = Path.Combine(_directory, "no-such-hosts");

        var exit = RunElevatedScript(Staged(Replacement), missing, Hash(Replacement), Hash(Original));

        Assert.Equal(HostsWriterExitCodes.MalformedRequest, exit);
        Assert.False(File.Exists(missing));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
