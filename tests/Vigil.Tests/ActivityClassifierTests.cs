using Vigil.Core;

namespace Vigil.Tests;

public sealed class ActivityClassifierTests
{
    [Fact]
    public void UserRule_WinsOverBuiltInClassification()
    {
        var activity = new ActivityWatchSnapshot(DateTimeOffset.Now, false, "chrome.exe", "Bilibili", "bilibili.com", "高等数学课程");
        var rule = new ClassificationRule { Id = Guid.NewGuid(), Scope = RuleScope.Similar, Application = "chrome.exe", Domain = "bilibili.com", TitleKeywords = "高等数学", Category = ActivityCategory.WorkAndStudy, CreatedAt = DateTimeOffset.Now };
        var result = ActivityClassifier.ApplyRules(activity, [rule]); Assert.Equal(ActivityCategory.WorkAndStudy, result.Category); Assert.Equal(1, result.Confidence);
    }

    [Fact]
    public void UnknownActivity_IsConservativelyOther()
    {
        var activity = new ActivityWatchSnapshot(DateTimeOffset.Now, false, "mystery.exe", "???", "", "???");
        Assert.Equal(ActivityCategory.Other, ActivityClassifier.ApplyRules(activity, []).Category);
    }

    [Fact]
    public void UserWideRule_WinsOverAiCacheRule()
    {
        var activity = new ActivityWatchSnapshot(DateTimeOffset.Now, false, "chrome.exe", "Video", "video.test", "Course");
        var aiCache = new ClassificationRule { Id = Guid.NewGuid(), Scope = RuleScope.Similar, Application = "chrome.exe", Domain = "video.test", TitleKeywords = "Course", Category = ActivityCategory.WorkAndStudy, CreatedAt = DateTimeOffset.MinValue };
        var userRule = new ClassificationRule { Id = Guid.NewGuid(), Scope = RuleScope.ApplicationOrDomain, Application = "chrome.exe", Domain = "video.test", Category = ActivityCategory.Entertainment, CreatedAt = DateTimeOffset.Now };
        Assert.Equal(ActivityCategory.Entertainment, ActivityClassifier.ApplyRules(activity, [aiCache, userRule]).Category);
    }
}
