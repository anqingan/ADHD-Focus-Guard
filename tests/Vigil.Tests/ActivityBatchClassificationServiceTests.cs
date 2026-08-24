using Vigil.Core;
using Vigil.Infrastructure;

namespace Vigil.Tests;

public sealed class ActivityBatchClassificationServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Vigil-BatchTests-" + Guid.NewGuid().ToString("N"));

    public ActivityBatchClassificationServiceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ForceBatch_ReclassifiesEveryMatchingSegmentAndCreatesReusableRule()
    {
        var repository = await CreateRepositoryAsync("reclassify.db");
        var now = DateTimeOffset.Now;
        await repository.SaveActivitySegmentAsync(Segment(now.AddMinutes(-20), now.AddMinutes(-15), "课程视频 - Chrome"));
        await repository.SaveActivitySegmentAsync(Segment(now.AddMinutes(-10), now.AddMinutes(-7), "课程视频 - Chrome"));
        var ai = new BatchAi((activities) => activities.Select(activity =>
            new ActivityClassification(activity.Id, ActivityCategory.WorkAndStudy, "观看课程视频", .91)).ToArray());
        await using var service = new ActivityBatchClassificationService(repository, ai);

        var updated = await service.ClassifyPendingAsync(force: true);

        Assert.Equal(2, updated);
        var saved = await repository.GetActivitySegmentsAsync(now.AddDays(-1), now.AddMinutes(1));
        Assert.All(saved, segment =>
        {
            Assert.Equal(ActivityCategory.WorkAndStudy, segment.Category);
            Assert.Equal(ClassificationSource.Ai, segment.ClassificationSource);
            Assert.Equal("观看课程视频", segment.DisplayName);
        });
        var rule = Assert.Single(await repository.GetClassificationRulesAsync());
        Assert.Equal(ActivityCategory.WorkAndStudy, rule.Category);
        Assert.Equal(DateTimeOffset.MinValue, rule.CreatedAt);
        Assert.Equal(480, Assert.Single(ai.Batches).Single().DurationSeconds);
    }

    [Fact]
    public async Task AiOther_IsSavedWithoutCreatingPermanentRule()
    {
        var repository = await CreateRepositoryAsync("other.db");
        var now = DateTimeOffset.Now;
        await repository.SaveActivitySegmentAsync(Segment(now.AddMinutes(-4), now.AddMinutes(-2), "未知系统窗口"));
        var ai = new BatchAi((activities) => activities.Select(activity =>
            new ActivityClassification(activity.Id, ActivityCategory.Other, "系统窗口", .43)).ToArray());
        await using var service = new ActivityBatchClassificationService(repository, ai);

        Assert.Equal(1, await service.ClassifyPendingAsync(force: true));

        var saved = Assert.Single(await repository.GetActivitySegmentsAsync(now.AddDays(-1), now));
        Assert.Equal(ActivityCategory.Other, saved.Category);
        Assert.Equal(ClassificationSource.Ai, saved.ClassificationSource);
        Assert.Empty(await repository.GetClassificationRulesAsync());
    }

    [Fact]
    public async Task ScheduledBatch_WaitsForFiveDistinctFreshTitles()
    {
        var repository = await CreateRepositoryAsync("threshold.db");
        var now = DateTimeOffset.Now;
        for (var index = 0; index < 4; index++)
            await repository.SaveActivitySegmentAsync(Segment(now.AddSeconds(-20), now.AddSeconds(-10), $"新标题 {index}"));
        var ai = new BatchAi((activities) => activities.Select(activity =>
            new ActivityClassification(activity.Id, ActivityCategory.WorkAndStudy, activity.DisplayName, .8)).ToArray());
        var options = new ActivityBatchClassificationOptions { MaxPendingAge = TimeSpan.FromHours(1) };
        await using var service = new ActivityBatchClassificationService(repository, ai, options: options);

        Assert.Equal(0, await service.ClassifyPendingAsync());
        Assert.Empty(ai.Batches);

        await repository.SaveActivitySegmentAsync(Segment(now.AddSeconds(-20), now.AddSeconds(-10), "新标题 4"));
        Assert.Equal(5, await service.ClassifyPendingAsync());
        Assert.Equal(5, Assert.Single(ai.Batches).Count);
    }

    [Fact]
    public async Task IncompleteAiBatch_DoesNotPartiallyOverwriteActivities()
    {
        var repository = await CreateRepositoryAsync("incomplete.db");
        var now = DateTimeOffset.Now;
        await repository.SaveActivitySegmentAsync(Segment(now.AddMinutes(-4), now.AddMinutes(-3), "标题一"));
        await repository.SaveActivitySegmentAsync(Segment(now.AddMinutes(-3), now.AddMinutes(-2), "标题二"));
        var ai = new BatchAi((activities) =>
        [
            new ActivityClassification(activities[0].Id, ActivityCategory.Entertainment, "娱乐内容", .9)
        ]);
        await using var service = new ActivityBatchClassificationService(repository, ai);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ClassifyPendingAsync(force: true));

        var saved = await repository.GetActivitySegmentsAsync(now.AddDays(-1), now);
        Assert.All(saved, segment =>
        {
            Assert.Equal(ActivityCategory.Other, segment.Category);
            Assert.Equal(ClassificationSource.BuiltInRule, segment.ClassificationSource);
        });
    }

    [Fact]
    public async Task CommunicationEntertainment_IsLocallyCorrectedWithoutCallingAi()
    {
        var repository = await CreateRepositoryAsync("communication.db");
        var now = DateTimeOffset.Now;
        var segment = Segment(now.AddMinutes(-10), now.AddMinutes(-5), "微信聊天") with
        {
            Application = "WeChat.exe",
            Category = ActivityCategory.Entertainment,
            ClassificationSource = ClassificationSource.Ai,
            Confidence = .9
        };
        await repository.SaveActivitySegmentAsync(segment);
        await repository.SaveClassificationRuleAsync(new ClassificationRule
        {
            Id = Guid.NewGuid(),
            Scope = RuleScope.ApplicationOrDomain,
            Application = "WeChat.exe",
            Category = ActivityCategory.Entertainment,
            CreatedAt = DateTimeOffset.MinValue
        });
        var ai = new BatchAi(_ => throw new InvalidOperationException("不应调用 AI"));
        await using var service = new ActivityBatchClassificationService(repository, ai);

        Assert.Equal(1, await service.ClassifyPendingAsync(force: true));

        var saved = Assert.Single(await repository.GetActivitySegmentsAsync(now.AddDays(-1), now));
        Assert.Equal(ActivityCategory.Other, saved.Category);
        Assert.Equal(.92, saved.Confidence);
        Assert.Empty(await repository.GetClassificationRulesAsync());
        Assert.Empty(ai.Batches);
    }

    private async Task<SqlitePersonalDataRepository> CreateRepositoryAsync(string name)
    {
        var repository = new SqlitePersonalDataRepository(Path.Combine(_directory, name));
        await repository.InitializeAsync();
        return repository;
    }

    private static ActivitySegment Segment(DateTimeOffset start, DateTimeOffset end, string title) => new()
    {
        Id = Guid.NewGuid(),
        StartedAt = start,
        EndedAt = end,
        Application = "chrome.exe",
        Domain = "example.test",
        DisplayName = title,
        Category = ActivityCategory.Other,
        Source = ActivitySource.ActivityWatch,
        ClassificationSource = ClassificationSource.BuiltInRule,
        Confidence = .35
    };

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); }
        catch (IOException) { }
    }

    private sealed class BatchAi(Func<IReadOnlyList<ActivitySegment>, IReadOnlyList<ActivityClassification>> classify) : IPersonalAiService
    {
        public List<IReadOnlyList<ActivitySegment>> Batches { get; } = [];

        public Task<IReadOnlyList<ActivityClassification>> ClassifyActivitiesAsync(IReadOnlyList<ActivitySegment> activities, IReadOnlyList<GoalRecord> activeGoals, CancellationToken cancellationToken = default)
        {
            Batches.Add(activities);
            return Task.FromResult(classify(activities));
        }

        public Task<IReadOnlyList<ActionDraft>> OrganizeActionsAsync(string sourceText, IReadOnlyList<GoalRecord> activeGoals, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(ActivityCategory Category, string DisplayName, double Confidence)> ClassifyActivityAsync(ActivityWatchSnapshot activity, IReadOnlyList<GoalRecord> activeGoals, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(string Inference, string Suggestions)> GenerateReportNarrativeAsync(ReportPeriod period, string facts, IReadOnlyList<GoalRecord> activeGoals, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> SuggestDailyPlanAsync(IReadOnlyList<GoalRecord> activeGoals, IReadOnlyList<ActionItemRecord> pendingActions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DailyGoalDraft>> AnalyzeDailyGoalsAsync(string sourceText, IReadOnlyList<GoalRecord> activeGoals, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
