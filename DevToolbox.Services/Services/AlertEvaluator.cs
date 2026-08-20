using System;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Services
{
    /// <summary>
    /// The alert threshold/repeat state machine, as a pure function of the ping that just
    /// happened and the state before it. Pulled out of <see cref="HealthMonitoringService"/> —
    /// same reason as <see cref="HistoryTrimmer"/> — so the two repeat modes can be tested
    /// without a live monitor loop or the health lock.
    /// </summary>
    public static class AlertEvaluator
    {
        public readonly record struct State(int ConsecutiveFailures, bool HasAlerted);

        public readonly record struct Outcome(State State, bool RaiseDownAlert, bool RaiseRecoveryAlert);

        public static Outcome Evaluate(State before, bool pingSucceeded, bool alertsEnabled, int alertThreshold, AlertRepeatMode alertRepeat)
        {
            if (pingSucceeded)
            {
                // Recovery only ever fires for an outage this evaluator itself alerted on —
                // never for a service that was simply never down.
                var recovered = alertsEnabled && before.HasAlerted;
                return new Outcome(new State(0, false), RaiseDownAlert: false, RaiseRecoveryAlert: recovered);
            }

            var failures = before.ConsecutiveFailures + 1;
            if (!alertsEnabled) return new Outcome(new State(failures, before.HasAlerted), false, false);

            // Clamped the same way PingIntervalSeconds/TimeoutSeconds are elsewhere in this
            // project — a stray 0 in config becomes "every failure" instead of a division by zero.
            var threshold = Math.Max(1, alertThreshold);
            if (failures < threshold) return new Outcome(new State(failures, before.HasAlerted), false, false);

            var shouldAlert = alertRepeat == AlertRepeatMode.EveryNFailures
                ? failures % threshold == 0   // fires again at N, 2N, 3N... while still down
                : !before.HasAlerted;         // fires exactly once per outage

            return shouldAlert
                ? new Outcome(new State(failures, true), RaiseDownAlert: true, RaiseRecoveryAlert: false)
                : new Outcome(new State(failures, before.HasAlerted), false, false);
        }
    }
}
