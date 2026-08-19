using System;
using System.Collections.Generic;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Services
{
    /// <summary>
    /// Prunes a service's ping history. Pulled out of <see cref="HealthMonitoringService"/> as a
    /// pure function of its inputs — including the clock, passed in rather than read internally
    /// — so the trim boundary is testable without a live monitor loop.
    /// </summary>
    public static class HistoryTrimmer
    {
        public static void Trim(List<PingResult> history, HistoryRetention retention, DateTime utcNow, int hardCap)
        {
            var cutoff = utcNow - retention.ToTimeSpan();

            var stale = 0;
            while (stale < history.Count && history[stale].Timestamp < cutoff) stale++;
            if (stale > 0) history.RemoveRange(0, stale);

            // Independent backstop: a 24h retention at a 1-second interval would otherwise hold
            // ~86,400 entries before the time-based trim above ever caught up to it.
            if (history.Count > hardCap) history.RemoveRange(0, history.Count - hardCap);
        }
    }
}
