using Vigil.Core;

namespace Vigil.Tests;

public sealed class AutomaticActivitySwitchPolicyTests
{
    [Fact]
    public void ShortWorkThenSwitch_DoesNotRemind()
    {
        var policy = new AutomaticActivitySwitchPolicy(new AutomaticReminderLimiter());
        var now = DateTimeOffset.UtcNow;

        Assert.Null(policy.Observe("code.exe", "", "编写报告", ActivityCategory.WorkAndStudy, now));
        Assert.Null(policy.Observe("wechat.exe", "", "项目群", ActivityCategory.Other, now.AddMinutes(5)));
    }

    [Fact]
    public void LongWorkThenDifferentActivity_ShowsOneSwitchReminder()
    {
        var policy = new AutomaticActivitySwitchPolicy(new AutomaticReminderLimiter());
        var now = DateTimeOffset.UtcNow;

        _ = policy.Observe("code.exe", "", "编写报告", ActivityCategory.WorkAndStudy, now);
        _ = policy.Observe("code.exe", "", "编写报告", ActivityCategory.WorkAndStudy, now.AddMinutes(15));
        var reminder = policy.Observe("wechat.exe", "", "回复项目群", ActivityCategory.Other, now.AddMinutes(16));

        Assert.Contains("编写报告", reminder);
        Assert.Contains("回复项目群", reminder);
    }

    [Fact]
    public void LongTaskArming_ResetsTheTwoReminderAllowance()
    {
        var limiter = new AutomaticReminderLimiter();
        Assert.True(limiter.TryAcquire());
        Assert.True(limiter.TryAcquire());
        var policy = new AutomaticActivitySwitchPolicy(limiter);
        var now = DateTimeOffset.UtcNow;

        _ = policy.Observe("code.exe", "", "任务 A", ActivityCategory.WorkAndStudy, now);
        _ = policy.Observe("code.exe", "", "任务 A", ActivityCategory.WorkAndStudy, now.AddMinutes(15));

        Assert.NotNull(policy.Observe("chrome.exe", "github.com", "任务 B", ActivityCategory.WorkAndStudy, now.AddMinutes(16)));
        Assert.Equal(1, limiter.IssuedCount);
    }
}
