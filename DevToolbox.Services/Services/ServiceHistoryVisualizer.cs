using System;
using System.Collections.Generic;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Services
{
    /// <summary>
    /// The pure math behind Service Pulse's "recent ping history" strip: how many pings land
    /// in each bar, and what color that bar's success rate should render as. Pulled out of
    /// <c>ServicePulse.razor.cs</c> — same reason as <see cref="AlertEvaluator"/> and
    /// <see cref="HistoryTrimmer"/> — so the bucket-boundary and gradient math are testable
    /// without a Blazor component. The actual Tailwind classes / inline styles stay in the
    /// UI project; this only ever hands back numbers.
    /// </summary>
    public static class ServiceHistoryVisualizer
    {
        /// <summary>One bar's worth of data. <see cref="SuccessRate"/> is meaningless when
        /// <see cref="PingCount"/> is 0 — callers must check that first, same as
        /// <c>HealthMonitoringService.CalculateMetrics</c> already does before averaging.</summary>
        public readonly record struct HistoryBucket(int PingCount, double SuccessRate);

        /// <summary>
        /// "All" (<paramref name="configuredBars"/> null) means one bar per retained ping, but
        /// that has to stop somewhere or a long retention window at a fast ping interval renders
        /// thousands of one-pixel slivers — beyond <paramref name="allBarsCap"/> it degrades to
        /// the same bucketed rendering every other bar count uses.
        /// </summary>
        public static int ResolveBarCount(int? configuredBars, int pingCount, int allBarsCap) =>
            configuredBars ?? Math.Min(pingCount, allBarsCap);

        /// <summary>
        /// Slices <paramref name="window"/> into <paramref name="barCount"/> equal, fixed time
        /// buckets — oldest first, right up to <paramref name="utcNow"/> — and sums each ping
        /// into whichever bucket its timestamp falls in. One forward pass over the history
        /// rather than one pass per bucket, so a large history and a large bar count don't
        /// multiply against each other on every render.
        /// </summary>
        public static List<HistoryBucket> BuildBuckets(IReadOnlyList<PingResult> history, int barCount, TimeSpan window, DateTime utcNow)
        {
            var buckets = new List<HistoryBucket>(Math.Max(0, barCount));
            if (barCount <= 0) return buckets;

            var bucketSpan = window / barCount;
            var windowStart = utcNow - window;

            var counts = new int[barCount];
            var successes = new int[barCount];

            foreach (var ping in history)
            {
                var offset = ping.Timestamp - windowStart;
                if (offset < TimeSpan.Zero) continue; // older than the window - the trim should already have dropped this

                var index = (int)(offset / bucketSpan);
                if (index >= barCount) index = barCount - 1; // still "in progress" in the newest bucket
                if (index < 0) continue;

                counts[index]++;
                if (ping.IsSuccess) successes[index]++;
            }

            for (var i = 0; i < barCount; i++)
            {
                buckets.Add(counts[i] == 0 ? new HistoryBucket(0, 0) : new HistoryBucket(counts[i], successes[i] / (double)counts[i]));
            }

            return buckets;
        }

        /// <summary>
        /// Green → amber → red as success rate falls from 100% to 0%, continuous rather than
        /// the app's usual discrete status colors. The three stops intentionally match this
        /// app's success/warning/danger theme tokens (<c>theme.css</c>'s dark-theme values — a
        /// light-theme session will show slightly more saturated colors here until this reads
        /// the active theme instead of a fixed triple).
        /// </summary>
        public static (int R, int G, int B) GradientColor(double successRate)
        {
            var danger = (r: 239, g: 68, b: 68);
            var warning = (r: 245, g: 158, b: 11);
            var success = (r: 16, g: 185, b: 129);

            var (from, to, t) = successRate >= 0.5
                ? (warning, success, (successRate - 0.5) * 2)
                : (danger, warning, successRate * 2);

            return (Lerp(from.r, to.r, t), Lerp(from.g, to.g, t), Lerp(from.b, to.b, t));
        }

        private static int Lerp(int from, int to, double t) => from + (int)Math.Round((to - from) * t);
    }
}
