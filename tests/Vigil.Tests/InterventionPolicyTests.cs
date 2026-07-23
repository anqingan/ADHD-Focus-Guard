using Vigil.Core;

namespace Vigil.Tests;

public sealed class InterventionPolicyTests
{
    [Fact]
    public void Distracted_EscalatesAfterThirtySecondsAndTwoJudgments()
    {
        var policy = new InterventionPolicy();
        var now = DateTimeOffset.UtcNow;

        var first = policy.Evaluate(FocusLevel.Distracted, now, "写报告", "回到报告", true, TimeSpan.Zero);
        var second = policy.Evaluate(FocusLevel.Distracted, now.AddSeconds(30), "写报告", "回到报告", true, TimeSpan.Zero);

        Assert.Contains(first, item => item.Kind == ReminderKind.Capsule);
        Assert.Contains(first, item => item.Kind == ReminderKind.Tray);
        Assert.Contains(first, item => item.Kind == ReminderKind.Sound);
        Assert.Contains(second, item => item.Kind == ReminderKind.FullScreenOverlay);
    }

    [Fact]
    public void Mute_LastsUntilLeavingDistractedState()
    {
        var policy = new InterventionPolicy();
        var now = DateTimeOffset.UtcNow;
        _ = policy.Evaluate(FocusLevel.Distracted, now, "goal", "", true, TimeSpan.Zero);
        policy.MuteCurrentDistraction();

        Assert.Empty(policy.Evaluate(FocusLevel.Distracted, now.AddMinutes(2), "goal", "", true, TimeSpan.Zero));
        _ = policy.Evaluate(FocusLevel.Focused, now.AddMinutes(3), "goal", "", true, TimeSpan.Zero);
        Assert.NotEmpty(policy.Evaluate(FocusLevel.Distracted, now.AddMinutes(4), "goal", "", true, TimeSpan.Zero));
    }

    [Fact]
    public void WanderingAndAway_UseDelayedSoftReminders()
    {
        var policy = new InterventionPolicy();
        var now = DateTimeOffset.UtcNow;
        Assert.Empty(policy.Evaluate(FocusLevel.Wandering, now, "goal", "", true, TimeSpan.Zero));
        Assert.Contains(
            policy.Evaluate(FocusLevel.Wandering, now.AddSeconds(120), "goal", "", false, TimeSpan.Zero),
            action => action.Kind == ReminderKind.Capsule);

        policy.Reset();
        Assert.Contains(
            policy.Evaluate(FocusLevel.Away, now, "goal", "", false, TimeSpan.FromSeconds(180)),
            action => action.Kind == ReminderKind.Sound);
    }
}
