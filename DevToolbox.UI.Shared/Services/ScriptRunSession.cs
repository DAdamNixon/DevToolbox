using System.Collections.Concurrent;
using System.Diagnostics;
using DevToolbox.Services.Models;
using DevToolbox.Services.Services;

namespace DevToolbox.UI.Services;

/// <summary>
/// The Scripts tab's current run: its output so far, whether it is still going, and the handle
/// that stops it.
/// <para>
/// Scoped per host rather than kept in the page, for the reason <see cref="LogSearchStateService"/>
/// is: leaving the tab disposes the page, and a run that lived there carried on invisibly — an npm
/// install over nine folders, still going, with nothing to come back to. Here it survives the
/// navigation and the tab picks it up again, still streaming.
/// </para>
/// <para>
/// Lines arrive on PowerShell's pipeline thread. They are queued there and moved into
/// <see cref="Lines"/> by a pump on the caller's synchronisation context — the renderer's — ten
/// times a second, so a script writing thousands of lines a second costs ten renders, not thousands,
/// and the page never reads the list while another thread is adding to it.
/// </para>
/// </summary>
public sealed class ScriptRunSession : IDisposable
{
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(100);

    private readonly PowerShellService _powerShell;
    private readonly ConcurrentQueue<ScriptOutputLine> _incoming = new();
    private readonly List<ScriptOutputLine> _lines = new();
    private readonly Stopwatch _clock = new();
    private CancellationTokenSource? _cts;

    public ScriptRunSession(PowerShellService powerShell) => _powerShell = powerShell;

    /// <summary>Raised on the renderer's context whenever there is something new to draw.</summary>
    public event Action? OnChanged;

    /// <summary>Which script the output belongs to. The editor may have moved on to another one.</summary>
    public string ScriptName { get; private set; } = string.Empty;

    /// <summary>Everything written so far, in the order it was written.</summary>
    public IReadOnlyList<ScriptOutputLine> Lines => _lines;

    /// <summary>Lines from the error stream. Native stderr is not counted.</summary>
    public int ErrorCount { get; private set; }

    public int WarningCount { get; private set; }

    /// <summary>True from the first Run until Clear: there is a console to show.</summary>
    public bool HasRun { get; private set; }

    public bool IsRunning { get; private set; }

    /// <summary>Stop has been pressed and the pipeline has not unwound yet.</summary>
    public bool IsStopping { get; private set; }

    /// <summary>How the last run ended. Null while one is running, and before the first.</summary>
    public ScriptRunOutcome? Outcome { get; private set; }

    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>
    /// The script editor is folded to one line so the console has the column. Kept here with the
    /// run rather than in the page, the way the Log Viewer keeps its folded source card: Run folds
    /// it, and coming back to the tab mid-run must not unfold it over the output you came back for.
    /// </summary>
    public bool EditorCollapsed { get; set; }

    /// <summary>
    /// Runs <paramref name="scriptText"/>, replacing whatever the console held. Returns when the
    /// script ends; the console updates throughout.
    /// </summary>
    public async Task RunAsync(string scriptName, string scriptText, Dictionary<string, object> arguments)
    {
        if (IsRunning) return;

        _lines.Clear();
        _incoming.Clear();
        ErrorCount = 0;
        WarningCount = 0;
        ScriptName = scriptName;
        HasRun = true;
        IsRunning = true;
        IsStopping = false;
        Outcome = null;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _clock.Restart();
        OnChanged?.Invoke();

        var run = RunGuarded(scriptText, arguments, _cts.Token);

        // The pump. Awaiting on the caller's context brings every pass back to the renderer's
        // thread, which is the only place _lines is touched. It only asks for a render when there
        // is a new line or the clock has reached a new second: npm can go quiet for a minute while
        // it resolves, and redrawing two thousand unchanged lines ten times a second is waste.
        var shownSecond = -1L;
        while (!run.IsCompleted)
        {
            await Task.WhenAny(run, Task.Delay(PumpInterval));
            var second = (long)_clock.Elapsed.TotalSeconds;
            if (Drain() || second != shownSecond)
            {
                shownSecond = second;
                OnChanged?.Invoke();
            }
        }

        Outcome = await run;
        _clock.Stop();
        Drain();
        IsRunning = false;
        IsStopping = false;
        OnChanged?.Invoke();
    }

    /// <summary>
    /// The service reports what went wrong as lines and an outcome, but anything that escapes it —
    /// a runspace that will not open — would otherwise leave the console spinning forever.
    /// </summary>
    private async Task<ScriptRunOutcome> RunGuarded(string scriptText, Dictionary<string, object> arguments, CancellationToken token)
    {
        try
        {
            return await Task.Run(() => _powerShell.RunScriptAsync(scriptText, arguments, _incoming.Enqueue, token), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _incoming.Enqueue(new ScriptOutputLine(ScriptOutputKind.Error, ex.Message));
            return ScriptRunOutcome.Failed;
        }
    }

    /// <summary>Moves queued lines into <see cref="Lines"/>. True if there were any.</summary>
    private bool Drain()
    {
        var any = false;
        while (_incoming.TryDequeue(out var line))
        {
            any = true;
            _lines.Add(line);
            if (line.Kind == ScriptOutputKind.Error) ErrorCount++;
            else if (line.Kind == ScriptOutputKind.Warning) WarningCount++;
        }
        return any;
    }

    public void Stop()
    {
        if (!IsRunning || IsStopping) return;

        IsStopping = true;
        _cts?.Cancel();
        OnChanged?.Invoke();
    }

    /// <summary>Empties the console. Not while running: the pump would refill it mid-line.</summary>
    public void Clear()
    {
        if (IsRunning) return;

        _lines.Clear();
        ErrorCount = 0;
        WarningCount = 0;
        HasRun = false;
        Outcome = null;
        ScriptName = string.Empty;
        OnChanged?.Invoke();
    }

    /// <summary>The whole output as plain text, for Copy.</summary>
    public string ToPlainText() => string.Join(Environment.NewLine, _lines.Select(l => l.Text));

    /// <summary>The host is closing. A script still running is stopped rather than orphaned.</summary>
    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
