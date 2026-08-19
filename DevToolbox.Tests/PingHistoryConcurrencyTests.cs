using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DevToolbox.Services.Models;
using DevToolbox.Services.Services;
using Xunit;

namespace DevToolbox.Tests;

/// <summary>
/// The UI enumerates <see cref="ServiceHealth.PingHistory"/> on the Blazor render thread while a
/// monitor loop records pings on a background thread, and the UI takes no lock — so the recording
/// side must never mutate a list a reader could be walking.
/// <para>
/// This reproduces that shape directly rather than through the service, because the service's own
/// recording path needs real HTTP. A writer that mutates in place fails this within a few hundred
/// iterations with "Collection was modified; enumeration operation may not execute"; the
/// copy-on-write writer the service actually uses cannot, by construction.
/// </para>
/// </summary>
public class PingHistoryConcurrencyTests
{
    private static PingResult Ping(int i) => new()
    {
        Timestamp = DateTime.UtcNow,
        IsSuccess = i % 7 != 0,
        ResponseTimeMs = i,
    };

    [Fact]
    public async Task Reading_history_while_it_is_being_recorded_never_throws()
    {
        var health = new ServiceHealth { ServiceId = "a", ServiceName = "a" };
        for (var i = 0; i < 500; i++) health.PingHistory.Add(Ping(i));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Exception? readerFailure = null;
        var reads = 0;

        // Writer: exactly what PingAndRecordAsync does - build a replacement under the lock and
        // swap the reference, never touching the list a reader may already hold.
        var writer = Task.Run(() =>
        {
            var n = 500;
            while (!cts.IsCancellationRequested)
            {
                lock (health)
                {
                    var next = new List<PingResult>(health.PingHistory) { Ping(n++) };
                    HistoryTrimmer.Trim(next, HistoryRetention.OneHour, DateTime.UtcNow, hardCap: 600);
                    health.PingHistory = next;
                }
            }
        });

        // Reader: the UI, which holds no lock. Reads the property once (as the render does) and
        // then walks it, which is precisely where an in-place mutation would blow up.
        var reader = Task.Run(() =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var snapshot = health.PingHistory;
                    var count = 0;
                    foreach (var p in snapshot) { if (p.IsSuccess) count++; }

                    // The same full walk the strip does, over the same list reference.
                    ServiceHistoryVisualizer.BuildBuckets(snapshot, 50, TimeSpan.FromHours(1), DateTime.UtcNow);
                    Interlocked.Increment(ref reads);
                }
            }
            catch (Exception ex)
            {
                readerFailure = ex;
            }
        });

        await Task.WhenAll(writer, reader);

        Assert.Null(readerFailure);
        Assert.True(reads > 100, $"reader only completed {reads} passes - too few to be meaningful");
    }

    [Fact]
    public void A_snapshot_taken_by_a_reader_is_never_modified_afterwards()
    {
        var health = new ServiceHealth { ServiceId = "a", ServiceName = "a" };
        for (var i = 0; i < 10; i++) health.PingHistory.Add(Ping(i));

        // What the UI effectively does: grab the reference, then use it across a render.
        var snapshot = health.PingHistory;
        var countAtRead = snapshot.Count;

        // A ping lands mid-render.
        lock (health)
        {
            var next = new List<PingResult>(health.PingHistory) { Ping(99) };
            health.PingHistory = next;
        }

        // The reader's snapshot is unchanged - it is stale, which is fine for a UI, but it is
        // never inconsistent, which is what matters.
        Assert.Equal(countAtRead, snapshot.Count);
        Assert.NotSame(snapshot, health.PingHistory);
        Assert.Equal(countAtRead + 1, health.PingHistory.Count);
    }
}
