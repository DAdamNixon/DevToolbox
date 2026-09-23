using DevToolbox.Services.Models;
using DevToolbox.Services.Services;

namespace DevToolbox.Tests;

/// <summary>
/// A1/A2's stall detection and auto-skip, exercised directly against the reporter with a fake
/// clock — <see cref="LogIngestProgressReporter.EvaluateStalls"/> is what the real 1s heartbeat
/// calls in production, but nothing here waits on a timer.
/// </summary>
public sealed class LogIngestProgressReporterStallTests
{
    [Fact]
    public void A_file_is_stalled_at_the_threshold_and_auto_skipped_only_at_its_own_timeout()
    {
        var clock = new FakeTimeProvider();
        var settings = new LogIngestSettings { StallThresholdSeconds = 10, UiAutoSkipEnabled = true, UiAutoSkipTimeoutSeconds = 60 };
        var control = new LogIngestControl(settings);
        var reporter = new LogIngestProgressReporter(sink: null, control, clock);

        reporter.FileOpening("key", "File.txt", "Local", bytesTotal: 1000);

        clock.Advance(TimeSpan.FromSeconds(9));
        reporter.EvaluateStalls();
        Assert.False(control.IsSkipped("key"));

        // Past the 10s stall threshold, but nowhere near the 60s auto-skip timeout.
        clock.Advance(TimeSpan.FromSeconds(2));
        reporter.EvaluateStalls();
        Assert.False(control.IsSkipped("key"));

        clock.Advance(TimeSpan.FromSeconds(50));
        reporter.EvaluateStalls();
        Assert.True(control.IsSkipped("key"));
    }

    [Fact]
    public void Auto_skip_never_fires_while_the_ui_toggle_is_off()
    {
        var clock = new FakeTimeProvider();
        var settings = new LogIngestSettings { StallThresholdSeconds = 1, UiAutoSkipEnabled = false, UiAutoSkipTimeoutSeconds = 1 };
        var control = new LogIngestControl(settings);
        var reporter = new LogIngestProgressReporter(sink: null, control, clock);

        reporter.FileOpening("key", "File.txt", "Local", bytesTotal: 1000);

        clock.Advance(TimeSpan.FromMinutes(10));
        reporter.EvaluateStalls();

        Assert.False(control.IsSkipped("key"));
    }

    [Fact]
    public void The_mcp_control_auto_skips_at_its_own_timeout_regardless_of_the_ui_toggle()
    {
        var clock = new FakeTimeProvider();
        var settings = new LogIngestSettings { StallThresholdSeconds = 10, UiAutoSkipEnabled = false, McpAutoSkipTimeoutSeconds = 60 };
        var control = new LogIngestControl(settings, alwaysAutoSkip: true);
        var reporter = new LogIngestProgressReporter(sink: null, control, clock);

        reporter.FileOpening("key", "File.txt", "Local", bytesTotal: 1000);

        clock.Advance(TimeSpan.FromSeconds(61));
        reporter.EvaluateStalls();

        Assert.True(control.IsSkipped("key"));
    }
}
