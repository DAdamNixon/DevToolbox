using DevToolbox.Services.Models;
using DevToolbox.Services.Services;
using Xunit;

namespace DevToolbox.Tests;

/// <summary>
/// The two repeat modes are the one part of the alerts feature with more than one
/// reasonable reading of the request that spawned it ("every time it fails N times") —
/// these pin down the interpretation that was actually agreed: an escalating reminder at
/// N, 2N, 3N failures, not one alert per failure past N.
/// </summary>
public class AlertEvaluatorTests
{
    private static readonly AlertEvaluator.State Fresh = new(ConsecutiveFailures: 0, HasAlerted: false);

    [Fact]
    public void Success_resets_failures_and_does_not_alert_when_nothing_was_wrong()
    {
        var outcome = AlertEvaluator.Evaluate(Fresh, pingSucceeded: true, alertsEnabled: true, alertThreshold: 3, AlertRepeatMode.OnceUntilRecovery);

        Assert.Equal(0, outcome.State.ConsecutiveFailures);
        Assert.False(outcome.State.HasAlerted);
        Assert.False(outcome.RaiseDownAlert);
        Assert.False(outcome.RaiseRecoveryAlert);
    }

    [Fact]
    public void Failures_below_threshold_do_not_alert()
    {
        var state = Fresh;
        for (var i = 0; i < 2; i++)
        {
            var outcome = AlertEvaluator.Evaluate(state, pingSucceeded: false, alertsEnabled: true, alertThreshold: 3, AlertRepeatMode.OnceUntilRecovery);
            Assert.False(outcome.RaiseDownAlert);
            state = outcome.State;
        }

        Assert.Equal(2, state.ConsecutiveFailures);
    }

    [Fact]
    public void OnceUntilRecovery_fires_exactly_once_then_stays_quiet_until_recovery()
    {
        var state = Fresh;
        bool[] fired = new bool[6];

        for (var i = 0; i < 6; i++)
        {
            var outcome = AlertEvaluator.Evaluate(state, pingSucceeded: false, alertsEnabled: true, alertThreshold: 3, AlertRepeatMode.OnceUntilRecovery);
            fired[i] = outcome.RaiseDownAlert;
            state = outcome.State;
        }

        // Failures 1,2 -> quiet; failure 3 (index 2) -> fires; 4,5,6 -> stays quiet.
        Assert.Equal(new[] { false, false, true, false, false, false }, fired);
        Assert.True(state.HasAlerted);
    }

    [Fact]
    public void OnceUntilRecovery_can_alert_again_on_a_second_outage_after_recovering()
    {
        var afterFirstOutage = AlertEvaluator.Evaluate(
            new AlertEvaluator.State(2, HasAlerted: false), pingSucceeded: false, alertsEnabled: true, alertThreshold: 3, AlertRepeatMode.OnceUntilRecovery);
        Assert.True(afterFirstOutage.RaiseDownAlert); // 3rd consecutive failure fires

        var recovery = AlertEvaluator.Evaluate(afterFirstOutage.State, pingSucceeded: true, alertsEnabled: true, alertThreshold: 3, AlertRepeatMode.OnceUntilRecovery);
        Assert.True(recovery.RaiseRecoveryAlert);
        Assert.False(recovery.State.HasAlerted);

        // A brand new outage after recovery must be able to alert again.
        var state = recovery.State;
        AlertEvaluator.Outcome? third = null;
        for (var i = 0; i < 3; i++)
        {
            third = AlertEvaluator.Evaluate(state, pingSucceeded: false, alertsEnabled: true, alertThreshold: 3, AlertRepeatMode.OnceUntilRecovery);
            state = third.Value.State;
        }

        Assert.True(third!.Value.RaiseDownAlert);
    }

    [Fact]
    public void EveryNFailures_re_fires_at_each_multiple_of_the_threshold()
    {
        var state = Fresh;
        var fired = new System.Collections.Generic.List<bool>();

        for (var i = 0; i < 7; i++)
        {
            var outcome = AlertEvaluator.Evaluate(state, pingSucceeded: false, alertsEnabled: true, alertThreshold: 3, AlertRepeatMode.EveryNFailures);
            fired.Add(outcome.RaiseDownAlert);
            state = outcome.State;
        }

        // Failures 1..7: fires at 3 and 6 only.
        Assert.Equal(new[] { false, false, true, false, false, true, false }, fired);
    }

    [Fact]
    public void Disabled_alerts_never_fire_regardless_of_failure_count()
    {
        var state = Fresh;
        for (var i = 0; i < 10; i++)
        {
            var outcome = AlertEvaluator.Evaluate(state, pingSucceeded: false, alertsEnabled: false, alertThreshold: 3, AlertRepeatMode.EveryNFailures);
            Assert.False(outcome.RaiseDownAlert);
            state = outcome.State;
        }
    }

    [Fact]
    public void Disabled_alerts_do_not_recover_notify_even_if_previously_alerted()
    {
        var wasAlerted = new AlertEvaluator.State(5, HasAlerted: true);

        var outcome = AlertEvaluator.Evaluate(wasAlerted, pingSucceeded: true, alertsEnabled: false, alertThreshold: 3, AlertRepeatMode.OnceUntilRecovery);

        Assert.False(outcome.RaiseRecoveryAlert);
    }

    [Fact]
    public void A_non_positive_threshold_is_clamped_to_one_instead_of_dividing_by_zero()
    {
        var outcome = AlertEvaluator.Evaluate(Fresh, pingSucceeded: false, alertsEnabled: true, alertThreshold: 0, AlertRepeatMode.EveryNFailures);

        Assert.True(outcome.RaiseDownAlert);
    }
}
