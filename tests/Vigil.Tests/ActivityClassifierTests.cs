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

    [Theory]
    [InlineData("WeChat.exe")]
    [InlineData("Weixin.exe")]
    [InlineData("QQ.exe")]
    [InlineData("QQNT.exe")]
    [InlineData("TIM.exe")]
    [InlineData("WXWork.exe")]
    public void CommunicationApps_AreNeutralByDefault(string application)
    {
        var activity = new ActivityWatchSnapshot(DateTimeOffset.Now, false, application, "聊天", "", "聊天");
        var entertainmentRule = new ClassificationRule { Id = Guid.NewGuid(), Scope = RuleScope.ApplicationOrDomain, Application = application, Category = ActivityCategory.Entertainment, CreatedAt = DateTimeOffset.Now };
        Assert.Equal(ActivityCategory.Other, ActivityClassifier.ApplyRules(activity, []).Category);
        Assert.Equal(ActivityCategory.Other, ActivityClassifier.ApplyRules(activity, [entertainmentRule]).Category);
    }

    [Fact]
    public void CommunicationApp_CanStillBeMarkedAsWorkAndStudy()
    {
        var activity = new ActivityWatchSnapshot(DateTimeOffset.Now, false, "QQ.exe", "课程群", "", "课程群");
        var workRule = new ClassificationRule { Id = Guid.NewGuid(), Scope = RuleScope.Similar, Application = "QQ.exe", TitleKeywords = "课程群", Category = ActivityCategory.WorkAndStudy, CreatedAt = DateTimeOffset.Now };
        Assert.Equal(ActivityCategory.WorkAndStudy, ActivityClassifier.ApplyRules(activity, [workRule]).Category);
        Assert.False(ActivityClassifier.IsNeutralCommunicationActivity("QQBrowser.exe"));
    }
}
