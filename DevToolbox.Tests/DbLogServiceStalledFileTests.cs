using System.Threading;
using DevToolbox.Services.Models;
using DevToolbox.Services.Services;

namespace DevToolbox.Tests;

/// <summary>
/// Reproduces the hang the Log Viewer showed on screen — one file whose read never returns takes
/// the whole <c>PrepareLogTableAsync</c> call down with it, because every await in the ingest path
/// sits on a real, uncancellable I/O call — and proves the fix: a stalled file can be abandoned
/// while its read is still blocked, and the search finishes without it.
/// </summary>
public sealed class DbLogServiceStalledFileTests : IDisposable
{
    private readonly TempDirectory _config = new("stall-config");
    private readonly TempDirectory _logs = new("stall-logs");
    private readonly TempDirectory _db = new("stall-db");

    private const string TemplateName = "Basic";
    private static readonly TimeSpan RaceTimeout = TimeSpan.FromSeconds(5);

    public DbLogServiceStalledFileTests()
    {
        File.WriteAllText(Path.Combine(_config.Path, "log_templates_index.yaml"), """
            templates:
              - name: "Basic"
                file: "Basic.yaml"
            """);

        File.WriteAllText(Path.Combine(_config.Path, "Basic.yaml"), """
            name: "Basic"
            extension: ".txt"
            delimiter: "|"
            columns:
              - Message
            """);
    }

    public void Dispose()
    {
        _config.Dispose();
        _logs.Dispose();
        _db.Dispose();
    }

    private DbLogService BuildService() =>
        new(new DirectoryYamlStorage(_config.Path), new SqliteLogStorageService(Path.Combine(_db.Path, $"logs.{Guid.NewGuid():N}.db")));

    private List<LogLocation> Locations() => new()
    {
        new LogLocation { Name = "Local", Path = _logs.Path }
    };

    /// <summary>Writes a matching file, today (so the date filter admits it) and returns its full path.</summary>
    private string WriteLogFile(string name, string firstLine)
    {
        var path = Path.Combine(_logs.Path, $"{name}.txt");
        File.WriteAllText(path, firstLine + "\n");
        return path;
    }

    [Fact]
    public async Task A_file_whose_read_never_returns_hangs_the_whole_prepare_without_a_skip()
    {
        var filePath = WriteLogFile("Stuck", "first line");
        var stream = new GatedStream(preamble: "first line\n"u8.ToArray());

        var service = BuildService();
        service.FileOpener = path => path == filePath ? stream : File.OpenRead(path);

        try
        {
            // No control at all — the ingest's default. Confirms the seam alone changes nothing;
            // the fix is the abandonment path exercised by the tests below, not the opener
            // indirection.
            var prepareTask = service.PrepareLogTableAsync(
                "Stuck", Locations(), DateTime.Today, DateTime.Today, TemplateName);

            var entered = await Task.WhenAny(stream.EnteredBlockingRead.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(stream.EnteredBlockingRead.Task, entered);

            var completed = await Task.WhenAny(prepareTask, Task.Delay(RaceTimeout));
            Assert.NotSame(prepareTask, completed);

            // Let it finish before the next test runs, rather than merely releasing the gate and
            // hoping — a background completion racing the next test's semaphore wait is exactly
            // the kind of flake this project's standard rules out.
            stream.Release();
            var eventually = await prepareTask.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(eventually.IsComplete);
        }
        finally
        {
            stream.Release();
        }
    }

    [Fact]
    public async Task Skipping_a_file_while_its_read_is_blocked_lets_the_search_finish_with_zero_of_its_rows()
    {
        var filePath = WriteLogFile("Stuck", "first line");
        var stream = new GatedStream(preamble: "first line\n"u8.ToArray());

        var service = BuildService();
        service.FileOpener = path => path == filePath ? stream : File.OpenRead(path);

        try
        {
            var control = new LogIngestControl();
            var prepareTask = service.PrepareLogTableAsync(
                "Stuck", Locations(), DateTime.Today, DateTime.Today, TemplateName, control: control);

            await stream.EnteredBlockingRead.Task.WaitAsync(TimeSpan.FromSeconds(2));

            // The dev clicking Skip on the one file the screenshot named stuck.
            control.Skip(filePath);

            var completed = await Task.WhenAny(prepareTask, Task.Delay(RaceTimeout));
            Assert.Same(prepareTask, completed);

            var result = await prepareTask;
            Assert.False(result.IsComplete);
            var skipped = Assert.Single(result.NotIngested);
            Assert.Equal(FileIngestState.Skipped, skipped.State);
            Assert.Equal("stalled — skipped by you", skipped.Reason);
            Assert.Equal("Stuck.txt", skipped.FileName);
        }
        finally
        {
            stream.Release();
        }
    }

    [Fact]
    public async Task Cancel_returns_promptly_even_though_the_read_it_abandoned_never_does()
    {
        var filePath = WriteLogFile("Stuck", "first line");
        var stream = new GatedStream(preamble: "first line\n"u8.ToArray());

        var service = BuildService();
        service.FileOpener = path => path == filePath ? stream : File.OpenRead(path);

        try
        {
            var control = new LogIngestControl();
            var prepareTask = service.PrepareLogTableAsync(
                "Stuck", Locations(), DateTime.Today, DateTime.Today, TemplateName, control: control);

            await stream.EnteredBlockingRead.Task.WaitAsync(TimeSpan.FromSeconds(2));

            control.SkipAll(); // what the Cancel button calls

            var completed = await Task.WhenAny(prepareTask, Task.Delay(RaceTimeout));
            Assert.Same(prepareTask, completed);

            var result = await prepareTask;
            Assert.Equal("not read — search cancelled", Assert.Single(result.NotIngested).Reason);
        }
        finally
        {
            stream.Release();
        }
    }

    [Fact]
    public async Task A_second_prepare_runs_while_the_first_ones_abandoned_read_is_still_blocked()
    {
        var stuckPath = WriteLogFile("StuckOnly", "first line");
        WriteLogFile("NormalOnly", "row one");
        var stream = new GatedStream(preamble: "first line\n"u8.ToArray());

        var service = BuildService();
        service.FileOpener = path => path == stuckPath ? stream : File.OpenRead(path);

        try
        {
            var control = new LogIngestControl();
            var firstPrepare = service.PrepareLogTableAsync(
                "StuckOnly", Locations(), DateTime.Today, DateTime.Today, TemplateName, control: control);

            await stream.EnteredBlockingRead.Task.WaitAsync(TimeSpan.FromSeconds(2));
            control.Skip(stuckPath);

            var first = await firstPrepare.WaitAsync(RaceTimeout);
            Assert.False(first.IsComplete);

            // The orphan from the first prepare is still blocked on a thread pool thread right
            // now. If the static, process-wide _loadSemaphore were not released on abandonment,
            // this would queue behind it and the WaitAsync below would time out.
            var second = await service
                .PrepareLogTableAsync("NormalOnly", Locations(), DateTime.Today, DateTime.Today, TemplateName)
                .WaitAsync(RaceTimeout);

            Assert.True(second.IsComplete);
        }
        finally
        {
            stream.Release();
        }
    }

    [Fact]
    public async Task A_file_that_throws_on_open_is_reported_failed_and_other_files_still_ingest()
    {
        WriteLogFile("Broken", "irrelevant");
        WriteLogFile("Fine", "row one");

        var service = BuildService();
        service.FileOpener = path => path.Contains("Broken")
            ? throw new IOException("simulated share failure")
            : File.OpenRead(path);

        // "Broken*" and "Fine*" both match a blank prefix; EnumerateMatchingFiles filters by name
        // prefix, not extension, so an empty logFile with today's date range picks up both.
        var result = await service.PrepareLogTableAsync(
            "", Locations(), DateTime.Today, DateTime.Today, TemplateName)
            .WaitAsync(RaceTimeout);

        Assert.False(result.IsComplete);
        var failed = Assert.Single(result.NotIngested);
        Assert.Equal(FileIngestState.Failed, failed.State);
        Assert.Equal("Broken.txt", failed.FileName);
        Assert.StartsWith("failed:", failed.Reason);
    }

    [Fact]
    public async Task A_slow_but_healthy_file_is_never_flagged_stalled_and_all_its_rows_arrive()
    {
        var filePath = WriteLogFile("Trickling", "placeholder");
        // Three lines, well inside the 1s stall threshold used below, with real (short) delays —
        // this is the one behaviour that has to be proven against real time: the heartbeat that
        // re-evaluates staleness runs on a real timer in production.
        var stream = new TrickleStream(new[] { "one\n", "two\n", "three\n" }, TimeSpan.FromMilliseconds(150));

        var service = BuildService();
        service.FileOpener = path => path == filePath ? stream : File.OpenRead(path);

        // Auto-skip deliberately ON with a short fuse: if trickling bytes were ever misread as a
        // stall, this is what would catch it, rather than the assertion below passing by accident.
        var control = new LogIngestControl(new LogIngestSettings
        {
            StallThresholdSeconds = 1,
            UiAutoSkipEnabled = true,
            UiAutoSkipTimeoutSeconds = 1
        });
        var result = await service.PrepareLogTableAsync(
            "Trickling", Locations(), DateTime.Today, DateTime.Today, TemplateName, control: control)
            .WaitAsync(RaceTimeout);

        Assert.True(result.IsComplete);
        Assert.False(control.IsSkipped(filePath));
    }
}

/// <summary>
/// A stream that serves <paramref name="preamble"/> once, then blocks forever in both <see cref="Read"/>
/// and <see cref="ReadAsync(byte[], int, int, CancellationToken)"/> and ignores the token — the shape of
/// an SMB read against a server that stopped answering. <see cref="EnteredBlockingRead"/> resolves the
/// instant the block begins, so a test can wait for "genuinely stuck" before acting instead of guessing
/// with a sleep.
/// </summary>
internal sealed class GatedStream : Stream
{
    private readonly byte[] _preamble;
    private int _position;
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal GatedStream(byte[] preamble) => _preamble = preamble;

    internal TaskCompletionSource EnteredBlockingRead { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Lets the block end, as EOF. Every test using this stream calls it before returning — an
    /// orphan left blocked forever would starve <c>DbLogService</c>'s static, process-wide
    /// semaphore for every test that runs after it, since nothing would ever release it. This is
    /// a test-hygiene requirement, not something production code has any way to do — the whole
    /// point of D3 is that the real orphan is never released and the code does not wait for it.
    /// </summary>
    internal void Release() => _released.TrySetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _preamble.Length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position < _preamble.Length)
            return CopyPreamble(buffer, offset, count);

        EnteredBlockingRead.TrySetResult();
        _released.Task.Wait();
        return 0;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_position < _preamble.Length)
            return CopyPreamble(buffer, offset, count);

        EnteredBlockingRead.TrySetResult();
        // Awaits the release gate, not the token: the real SMB read this stands in for cannot be
        // cancelled either, which is the entire premise this plan is built on (finding #4).
        await _released.Task;
        return 0;
    }

    private int CopyPreamble(byte[] buffer, int offset, int count)
    {
        var n = Math.Min(count, _preamble.Length - _position);
        Array.Copy(_preamble, _position, buffer, offset, n);
        _position += n;
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A stream that yields one line at a time with a small real delay — a slow but healthy share.</summary>
internal sealed class TrickleStream : Stream
{
    private readonly byte[] _bytes;
    private readonly TimeSpan _perLine;
    private int _position;
    private int _delivered;

    internal TrickleStream(IEnumerable<string> lines, TimeSpan perLine)
    {
        _bytes = System.Text.Encoding.UTF8.GetBytes(string.Concat(lines));
        _perLine = perLine;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _bytes.Length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_position >= _bytes.Length) return 0;
        if (_delivered++ > 0) await Task.Delay(_perLine, cancellationToken);

        // One byte per call keeps every read well under the stall threshold without needing to
        // know the caller's buffer size.
        buffer[offset] = _bytes[_position++];
        return 1;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
