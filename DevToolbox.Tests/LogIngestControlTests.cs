using DevToolbox.Services.Models;

namespace DevToolbox.Tests;

public sealed class LogIngestControlTests
{
    [Fact]
    public void A_file_that_was_never_named_is_not_skipped()
    {
        var control = new LogIngestControl();
        Assert.False(control.IsSkipped("some/file.txt"));
    }

    [Fact]
    public async Task Skip_resolves_the_waiting_task_with_the_manual_reason()
    {
        var control = new LogIngestControl();
        var waiting = control.WaitForAbandonAsync("a");

        Assert.False(waiting.IsCompleted);
        control.Skip("a");

        var reason = await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(SkipReason.Manual, reason);
        Assert.True(control.IsSkipped("a"));
    }

    [Fact]
    public void SkipAll_marks_every_file_skipped_including_ones_named_afterwards()
    {
        var control = new LogIngestControl();

        // Named before Cancel — the common case, a file already in flight.
        _ = control.WaitForAbandonAsync("already-open");
        control.SkipAll();

        Assert.True(control.IsSkipped("already-open"));

        // Named after Cancel — a file whose parse task had not even reached the check yet.
        Assert.True(control.IsSkipped("not-yet-opened"));
    }

    [Fact]
    public async Task SkipAll_resolves_an_in_flight_wait_with_the_cancelled_reason()
    {
        var control = new LogIngestControl();
        var waiting = control.WaitForAbandonAsync("a");

        control.SkipAll();

        var reason = await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(SkipReason.Cancelled, reason);
    }
}
