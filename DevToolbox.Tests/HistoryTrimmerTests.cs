using System;
using System.Collections.Generic;
using DevToolbox.Services.Models;
using DevToolbox.Services.Services;
using Xunit;

namespace DevToolbox.Tests;

/// <summary>
/// The trim used to be a flat 100-entry cap regardless of ping interval, which is the bug
/// that started this feature — a 30s interval only covered 50 minutes of a session that
/// can run for hours. These pin down the replacement: a time-based window, with the entry
/// count cap only as a backstop underneath it.
/// </summary>
public class HistoryTrimmerTests
{
    private static PingResult PingAt(DateTime timestamp) => new() { Timestamp = timestamp, IsSuccess = true };

    [Fact]
    public void Entries_older_than_the_retention_window_are_removed()
    {
        var now = new DateTime(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);
        var history = new List<PingResult>
        {
            PingAt(now - TimeSpan.FromHours(2)),   // older than 1h retention
            PingAt(now - TimeSpan.FromMinutes(30)) // within 1h retention
        };

        HistoryTrimmer.Trim(history, HistoryRetention.OneHour, now, hardCap: 1000);

        var kept = Assert.Single(history);
        Assert.Equal(now - TimeSpan.FromMinutes(30), kept.Timestamp);
    }

    [Fact]
    public void An_entry_exactly_at_the_cutoff_is_kept()
    {
        var now = new DateTime(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);
        var cutoff = now - HistoryRetention.OneHour.ToTimeSpan();
        var history = new List<PingResult> { PingAt(cutoff) };

        HistoryTrimmer.Trim(history, HistoryRetention.OneHour, now, hardCap: 1000);

        Assert.Single(history);
    }

    [Fact]
    public void Everything_within_the_window_survives_a_generous_hard_cap()
    {
        var now = DateTime.UtcNow;
        var history = new List<PingResult>();
        for (var i = 49; i >= 0; i--) history.Add(PingAt(now - TimeSpan.FromMinutes(i))); // oldest first

        HistoryTrimmer.Trim(history, HistoryRetention.TwentyFourHours, now, hardCap: 1000);

        Assert.Equal(50, history.Count);
    }

    [Fact]
    public void The_hard_cap_trims_even_recent_entries_once_it_is_exceeded()
    {
        var now = DateTime.UtcNow;

        // Chronological, oldest first - the order PingHistory is actually built in (each ping
        // is Add()-ed as it happens), so index 0 is the oldest entry, not the newest.
        var history = new List<PingResult>();
        for (var i = 9; i >= 0; i--) history.Add(PingAt(now - TimeSpan.FromSeconds(i)));

        HistoryTrimmer.Trim(history, HistoryRetention.OneHour, now, hardCap: 4);

        Assert.Equal(4, history.Count);
        // The newest 4 survive - the cap trims from the front, same as the time-based trim.
        Assert.Equal(now - TimeSpan.FromSeconds(3), history[0].Timestamp);
    }

    [Fact]
    public void An_empty_history_is_a_no_op()
    {
        var history = new List<PingResult>();

        HistoryTrimmer.Trim(history, HistoryRetention.OneHour, DateTime.UtcNow, hardCap: 100);

        Assert.Empty(history);
    }
}
