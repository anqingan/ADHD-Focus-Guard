using Vigil.Core;

namespace Vigil.Tests;

public sealed class AutomaticInterventionPolicyTests
{
    [Fact]
    public void Distracted_UsesOnlyTwoLightweightReminderRounds()
    {
        var limiter = new AutomaticReminderLimiter();
        var policy = new AutomaticInterventionPolicy(limiter);
        var now = DateTimeOffset.UtcNow;

        var first = policy.Evaluate(FocusLevel.Distracted, now, "写报告", "刚才在写报告", true, TimeSpan.Zero);
        var second = policy.Evaluate(FocusLevel.Distracted, now.AddSeconds(30), "写报告", "刚才在写报告", true, TimeSpan.Zero);
        var third = policy.Evaluate(FocusLevel.Distracted, now.AddSeconds(90), "写报告", "刚才在写报告", true, TimeSpan.Zero);

        Assert.Single(first, item => item.Kind == ReminderKind.AutomaticTray);
        Assert.Single(second, item => item.Kind == ReminderKind.AutomaticTray);
        Assert.DoesNotContain(first.Concat(second), item => item.Kind is ReminderKind.Capsule or ReminderKind.FullScreenOverlay);
        Assert.Empty(third);
        Assert.Equal(2, limiter.IssuedCount);
    }

    [Fact]
    public void SharedLimiter_CapsVisualAndActivityWatchRemindersTogether()
    {
        var limiter = new AutomaticReminderLimiter();
        var policy = new AutomaticInterventionPolicy(limiter);
        var now = DateTimeOffset.UtcNow;

        _ = policy.Evaluate(FocusLevel.Distracted, now, "项目", "检查任务切换", true, TimeSpan.Zero);

        Assert.True(limiter.TryAcquire());
        Assert.False(limiter.TryAcquire());

        limiter.Reset();
        Assert.True(limiter.TryAcquire());
    }
}
