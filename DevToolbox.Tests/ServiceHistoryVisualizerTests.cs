using System;
using System.Collections.Generic;
using DevToolbox.Services.Models;
using DevToolbox.Services.Services;
using Xunit;

namespace DevToolbox.Tests;

/// <summary>
/// The bucket math behind Service Pulse's "recent ping history" strip, and the gradient
/// that colors each bucket. This is the part of the feature request whose own example
/// arithmetic didn't check out ("100 bars, 24h, 30s ping -> 432 pings/bar" is actually
/// ~29/bar) — these pin down the corrected math.
/// </summary>
public class ServiceHistoryVisualizerTests
{
    private static PingResult PingAt(DateTime timestamp, bool success = true) => new() { Timestamp = timestamp, IsSuccess = success };

    [Fact]
    public void Twenty_four_hours_over_100_bars_is_roughly_29_pings_per_bar_not_432()
    {
        var now = new DateTime(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc);
        var window = TimeSpan.FromHours(24);
        const int barCount = 100;
        const int pingIntervalSeconds = 30;

        var history = new List<PingResult>();
        for (var t = TimeSpan.Zero; t < window; t += TimeSpan.FromSeconds(pingIntervalSeconds))
        {
            history.Add(PingAt(now - window + t));
        }

        var buckets = ServiceHistoryVisualizer.BuildBuckets(history, barCount, window, now);

        Assert.Equal(barCount, buckets.Count);
        Assert.All(buckets, b => Assert.InRange(b.PingCount, 27, 30)); // 2880 pings / 100 bars ~= 28.8
        Assert.DoesNotContain(buckets, b => b.PingCount > 300); // nowhere near the mistaken "432"
    }

    [Fact]
    public void Resolve_bar_count_uses_the_configured_value_when_one_is_set()
    {
        Assert.Equal(50, ServiceHistoryVisualizer.ResolveBarCount(configuredBars: 50, pingCount: 10_000, allBarsCap: 300));
    }

    [Fact]
    public void All_mode_is_one_bar_per_ping_below_the_cap()
    {
        Assert.Equal(120, ServiceHistoryVisualizer.ResolveBarCount(configuredBars: null, pingCount: 120, allBarsCap: 300));
    }

    [Fact]
    public void All_mode_degrades_to_the_cap_once_history_exceeds_it()
    {
        Assert.Equal(300, ServiceHistoryVisualizer.ResolveBarCount(configuredBars: null, pingCount: 50_000, allBarsCap: 300));
    }

    [Fact]
    public void A_bucket_with_no_pings_is_reported_as_empty()
    {
        var now = DateTime.UtcNow;
        var buckets = ServiceHistoryVisualizer.BuildBuckets(new List<PingResult>(), barCount: 10, TimeSpan.FromHours(1), now);

        Assert.Equal(10, buckets.Count);
        Assert.All(buckets, b => Assert.Equal(0, b.PingCount));
    }

    [Fact]
    public void Mixed_success_and_failure_in_one_bucket_averages_to_the_right_rate()
    {
        var now = new DateTime(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc);
        var window = TimeSpan.FromMinutes(10);
        // One bucket (barCount 1) spanning the whole window: 3 successes, 1 failure.
        var history = new List<PingResult>
        {
            PingAt(now - TimeSpan.FromMinutes(9), success: true),
            PingAt(now - TimeSpan.FromMinutes(7), success: true),
            PingAt(now - TimeSpan.FromMinutes(5), success: false),
            PingAt(now - TimeSpan.FromMinutes(3), success: true),
        };

        var buckets = ServiceHistoryVisualizer.BuildBuckets(history, barCount: 1, window, now);

        var bucket = Assert.Single(buckets);
        Assert.Equal(4, bucket.PingCount);
        Assert.Equal(0.75, bucket.SuccessRate);
    }

    [Fact]
    public void A_ping_older_than_the_window_is_ignored_rather_than_corrupting_the_oldest_bucket()
    {
        var now = new DateTime(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc);
        var window = TimeSpan.FromHours(1);
        var history = new List<PingResult>
        {
            PingAt(now - TimeSpan.FromHours(5)), // well outside the window - trim should already drop this, but the builder must not misplace it either
            PingAt(now - TimeSpan.FromMinutes(1))
        };

        var buckets = ServiceHistoryVisualizer.BuildBuckets(history, barCount: 4, window, now);

        Assert.Equal(1, buckets[0].PingCount + buckets[1].PingCount + buckets[2].PingCount + buckets[3].PingCount);
    }

    [Theory]
    [InlineData(1.0, 16, 185, 129)]  // pure success
    [InlineData(0.5, 245, 158, 11)]  // pure warning at the midpoint
    [InlineData(0.0, 239, 68, 68)]   // pure danger
    public void Gradient_hits_its_three_stops_exactly(double successRate, int r, int g, int b)
    {
        var color = ServiceHistoryVisualizer.GradientColor(successRate);

        Assert.Equal((r, g, b), color);
    }

    [Fact]
    public void Gradient_is_monotonic_between_stops()
    {
        // Green channel should rise steadily from danger (68) toward success (185) as the
        // rate climbs through the warning half - a regression here would mean the two
        // interpolation halves don't actually meet at 0.5.
        var previous = ServiceHistoryVisualizer.GradientColor(0.0).G;
        for (var rate = 0.1; rate <= 1.0; rate += 0.1)
        {
            var current = ServiceHistoryVisualizer.GradientColor(rate).G;
            Assert.True(current >= previous, $"green channel dipped at {rate}");
            previous = current;
        }
    }
}
