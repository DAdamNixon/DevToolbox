using System.Collections.Concurrent;
using System.Diagnostics;
using DevToolbox.Services.Models;
using DevToolbox.Services.Services;
using Xunit;

namespace DevToolbox.Tests;

/// <summary>
/// The Scripts tab's output, delivered while the script runs rather than after it.
/// <para>
/// npm-install over nine folders sat on "Running..." with an empty pane for minutes and then printed
/// everything at once, which is indistinguishable from a hang until the moment it is not. Everything
/// here is about a line reaching the screen when it is written, from whichever stream wrote it, and
/// saying which stream that was.
/// </para>
/// </summary>
public sealed class ScriptStreamingTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>A file the script waits for, so a test can hold it mid-run without sleeping.</summary>
    private readonly string _release = Path.Combine(Path.GetTempPath(), $"devtoolbox-release-{Guid.NewGuid():N}");

    public void Dispose() => File.Delete(_release);

    private string WaitForRelease => $"while (-not (Test-Path '{_release}')) {{ Start-Sleep -Milliseconds 50 }}";

    private sealed class Recorder
    {
        public ConcurrentQueue<ScriptOutputLine> Lines { get; } = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _waiting = new();

        public void Add(ScriptOutputLine line)
        {
            Lines.Enqueue(line);
            if (_waiting.TryGetValue(line.Text, out var seen)) seen.TrySetResult();
        }

        /// <summary>Completes when a line with exactly this text arrives, even if it already has.</summary>
        public Task Seen(string text)
        {
            var seen = _waiting.GetOrAdd(text, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            if (Lines.Any(l => l.Text == text)) seen.TrySetResult();
            return seen.Task;
        }
    }

    private static Task<ScriptRunOutcome> Run(string script, Recorder recorder, CancellationToken token = default) =>
        new PowerShellService().RunScriptAsync(script, null, recorder.Add, token);

    [Fact]
    public async Task Write_Host_arrives_while_the_script_is_still_running()
    {
        var recorder = new Recorder();
        var run = Run($"Write-Host 'first'; {WaitForRelease}; Write-Host 'second'", recorder);

        await recorder.Seen("first").WaitAsync(Patience);
        Assert.False(run.IsCompleted);
        Assert.DoesNotContain(recorder.Lines, l => l.Text == "second");

        await File.WriteAllTextAsync(_release, "");
        Assert.Equal(ScriptRunOutcome.Completed, await run.WaitAsync(Patience));
        Assert.Contains(recorder.Lines, l => l.Text == "second");
    }

    [Fact]
    public async Task Pipeline_output_arrives_while_the_script_is_still_running()
    {
        // The harder of the two: InvokeAsync's return value only exists at the end, so this is the
        // one that proves output goes through a collection that raises an event per object.
        var recorder = new Recorder();
        var run = Run($"Write-Output 'first'; {WaitForRelease}", recorder);

        await recorder.Seen("first").WaitAsync(Patience);
        Assert.False(run.IsCompleted);
        Assert.Equal(ScriptOutputKind.Output, recorder.Lines.Single(l => l.Text == "first").Kind);

        await File.WriteAllTextAsync(_release, "");
        await run.WaitAsync(Patience);
    }

    [Fact]
    public async Task A_native_program_is_heard_while_it_is_still_running()
    {
        // What npm install is: a program writing to stdout while the script waits on it. ping
        // writes a line a second for a minute, so the first one arriving early is the whole test.
        using var cts = new CancellationTokenSource();
        var recorder = new Recorder();
        var run = Run("ping -n 60 127.0.0.1", recorder, cts.Token);

        await WaitFor(() => recorder.Lines.Any(l => l.Kind == ScriptOutputKind.Output && l.Text.Contains("127.0.0.1")));
        Assert.False(run.IsCompleted);

        cts.Cancel();
        await run.WaitAsync(Patience);
    }

    [Fact]
    public async Task Lines_from_different_streams_keep_the_order_they_were_written_in()
    {
        // Two separate boxes lost this entirely: "Installing in X" sat in one and X's error in the
        // other, with nothing to say which went with which.
        var recorder = new Recorder();
        await Run("Write-Host 'a'; Write-Error 'b'; Write-Output 'c'; Write-Warning 'd'; Write-Host 'e'", recorder).WaitAsync(Patience);

        var lines = recorder.Lines.ToList();
        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, lines.Select(l => l.Kind == ScriptOutputKind.Error ? "b" : l.Text));
        Assert.Equal(
            new[] { ScriptOutputKind.Host, ScriptOutputKind.Error, ScriptOutputKind.Output, ScriptOutputKind.Warning, ScriptOutputKind.Host },
            lines.Select(l => l.Kind));
    }

    [Fact]
    public async Task A_native_programs_stderr_is_not_reported_as_an_error()
    {
        // npm writes its warnings and progress to stderr. Shown as errors, a successful npm install
        // looked like a failed one in every folder.
        var recorder = new Recorder();
        var outcome = await Run("cmd.exe /c \"echo from-stderr 1>&2\"; Write-Error 'a real one'", recorder).WaitAsync(Patience);

        Assert.Equal(ScriptRunOutcome.Completed, outcome);
        Assert.Contains(recorder.Lines, l => l.Kind == ScriptOutputKind.NativeStderr && l.Text.Contains("from-stderr"));
        Assert.Single(recorder.Lines, l => l.Kind == ScriptOutputKind.Error);
    }

    [Fact]
    public async Task Write_Host_keeps_the_colour_the_script_asked_for()
    {
        var recorder = new Recorder();
        await Run("Write-Host 'heading' -ForegroundColor Cyan; Write-Host 'plain'", recorder).WaitAsync(Patience);

        Assert.Equal(ConsoleColor.Cyan, recorder.Lines.Single(l => l.Text == "heading").Color);
        Assert.Null(recorder.Lines.Single(l => l.Text == "plain").Color);
    }

    [Fact]
    public async Task NoNewline_joins_the_next_Write_Host_onto_the_same_line()
    {
        var recorder = new Recorder();
        await Run("Write-Host 'Status: ' -NoNewline; Write-Host 'OK' -ForegroundColor Green; Write-Host 'trailing' -NoNewline", recorder).WaitAsync(Patience);

        var texts = recorder.Lines.Select(l => l.Text).ToList();
        Assert.Contains("Status: OK", texts);
        Assert.Contains("trailing", texts);
    }

    [Fact]
    public async Task An_embedded_newline_is_the_blank_line_the_script_asked_for()
    {
        // Every bundled script spaces its sections with "`n...".
        var recorder = new Recorder();
        await Run("Write-Host \"`nFound 9 directories\"", recorder).WaitAsync(Patience);

        Assert.Equal(new[] { "", "Found 9 directories" }, recorder.Lines.Select(l => l.Text));
    }

    [Fact]
    public async Task Stopping_ends_the_run_and_says_so()
    {
        using var cts = new CancellationTokenSource();
        var recorder = new Recorder();
        var run = Run("Write-Host 'started'; while ($true) { Start-Sleep -Milliseconds 50 }", recorder, cts.Token);

        await recorder.Seen("started").WaitAsync(Patience);
        cts.Cancel();

        Assert.Equal(ScriptRunOutcome.Stopped, await run.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Stopping_takes_a_native_program_down_with_it()
    {
        // Otherwise Stop on npm-install would stop watching an npm install that carries on regardless.
        using var cts = new CancellationTokenSource();
        var recorder = new Recorder();
        var run = Run("ping -n 120 127.0.0.1", recorder, cts.Token);

        await WaitFor(() => recorder.Lines.Any(l => l.Text.Contains("127.0.0.1")));
        var before = ChildPings();
        Assert.NotEmpty(before);

        cts.Cancel();
        Assert.Equal(ScriptRunOutcome.Stopped, await run.WaitAsync(TimeSpan.FromSeconds(10)));

        await Task.Delay(500);
        Assert.DoesNotContain(before, IsRunning);
    }

    [Fact]
    public async Task A_script_that_writes_errors_but_finishes_is_completed_not_failed()
    {
        var recorder = new Recorder();
        Assert.Equal(ScriptRunOutcome.Completed, await Run("Write-Error 'one'; Write-Host 'went on'", recorder).WaitAsync(Patience));
    }

    [Fact]
    public async Task A_terminating_error_fails_the_run_and_is_reported_as_a_line()
    {
        var recorder = new Recorder();
        Assert.Equal(ScriptRunOutcome.Failed, await Run("Write-Host 'before'; throw 'stopped here'", recorder).WaitAsync(Patience));
        Assert.Contains(recorder.Lines, l => l.Kind == ScriptOutputKind.Error && l.Text.Contains("stopped here"));
    }

    [Fact]
    public async Task A_syntax_error_fails_without_running_anything()
    {
        var recorder = new Recorder();
        Assert.Equal(ScriptRunOutcome.Failed, await Run("Write-Host 'never'; if ( {", recorder).WaitAsync(Patience));
        Assert.DoesNotContain(recorder.Lines, l => l.Text == "never");
        Assert.All(recorder.Lines, l => Assert.Equal(ScriptOutputKind.Error, l.Kind));
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the expected output never arrived");
            await Task.Delay(25);
        }
    }

    /// <summary>
    /// ping.exe processes this test process started. PowerShell starts a native program itself,
    /// in-process, so the test host is the parent.
    /// </summary>
    private static List<int> ChildPings() =>
        ParentsOf("PING.EXE").Where(p => p.Parent == Environment.ProcessId).Select(p => p.Id).ToList();

    private static IEnumerable<(int Id, int Parent)> ParentsOf(string image)
    {
        using var search = new System.Management.ManagementObjectSearcher(
            $"SELECT ProcessId, ParentProcessId FROM Win32_Process WHERE Name = '{image}'");
        foreach (var process in search.Get())
        {
            yield return (Convert.ToInt32(process["ProcessId"]), Convert.ToInt32(process["ParentProcessId"]));
        }
    }

    private static bool IsRunning(int id)
    {
        try { return !Process.GetProcessById(id).HasExited; }
        catch (ArgumentException) { return false; }
    }
}
