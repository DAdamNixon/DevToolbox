using DevToolbox.Services.Services;

namespace DevToolbox.Tests;

/// <summary>A clock a test can move, so the threshold logic runs with no real waiting.</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// D1: liveness is bytes advancing, never elapsed time. These run against a fake clock, so
/// "ten seconds" is an assertion, not a wait.
/// </summary>
public sealed class FileStallTrackerTests
{
    [Fact]
    public void A_file_with_no_progress_is_stalled_once_the_threshold_passes()
    {
        var clock = new FakeTimeProvider();
        var tracker = new FileStallTracker(TimeSpan.FromSeconds(10), clock);

        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.False(tracker.IsStalled);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(tracker.IsStalled);
    }

    [Fact]
    public void A_file_that_keeps_advancing_is_never_stalled_no_matter_how_long_it_runs()
    {
        var clock = new FakeTimeProvider();
        var tracker = new FileStallTracker(TimeSpan.FromSeconds(10), clock);

        for (var i = 1; i <= 20; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(8));
            tracker.RecordProgress(i * 100);
            Assert.False(tracker.IsStalled);
        }
    }

    [Fact]
    public void A_value_that_is_not_new_progress_does_not_reset_the_clock()
    {
        var clock = new FakeTimeProvider();
        var tracker = new FileStallTracker(TimeSpan.FromSeconds(10), clock);

        tracker.RecordProgress(500);
        clock.Advance(TimeSpan.FromSeconds(9));

        // A stale or repeated read (e.g. the same position reported twice) must not look like
        // fresh progress, or a genuinely stalled file would never be flagged.
        tracker.RecordProgress(500);
        tracker.RecordProgress(499);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(tracker.IsStalled);
    }
}
