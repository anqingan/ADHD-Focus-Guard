namespace Vigil.Core;

public interface IPersonalDataRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GoalRecord>> GetGoalsAsync(bool includeInactive = true, CancellationToken cancellationToken = default);
    Task SaveGoalAsync(GoalRecord goal, string changeKind, CancellationToken cancellationToken = default);
    Task DeleteGoalAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GoalHistoryRecord>> GetGoalHistoryAsync(Guid goalId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActionItemRecord>> GetActionItemsAsync(bool includeInactive = true, CancellationToken cancellationToken = default);
    Task SaveActionItemAsync(ActionItemRecord item, CancellationToken cancellationToken = default);
    Task DeleteActionItemAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoryRecord>> GetMemoriesAsync(bool includeArchived = false, CancellationToken cancellationToken = default);
    Task SaveMemoryAsync(MemoryRecord memory, CancellationToken cancellationToken = default);
    Task DeleteMemoryAsync(Guid id, CancellationToken cancellationToken = default);

    Task SaveActivitySegmentAsync(ActivitySegment segment, CancellationToken cancellationToken = default);
    Task DeleteActivitySegmentAsync(Guid id, CancellationToken cancellationToken = default);
    Task DeleteActivityRangeAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ActivitySegment>> GetActivitySegmentsAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken = default);
    Task<ActivityTotals> GetActivityTotalsAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ClassificationRule>> GetClassificationRulesAsync(CancellationToken cancellationToken = default);
    Task SaveClassificationRuleAsync(ClassificationRule rule, CancellationToken cancellationToken = default);
    Task DeleteClassificationRuleAsync(Guid id, CancellationToken cancellationToken = default);

    Task SaveReportAsync(ReportRecord report, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReportRecord>> GetReportsAsync(CancellationToken cancellationToken = default);

    Task<DailyPlanState?> GetDailyPlanStateAsync(DateOnly activityDate, CancellationToken cancellationToken = default);
    Task SaveDailyPlanStateAsync(DailyPlanState state, CancellationToken cancellationToken = default);
    Task DeleteAllPersonalDataAsync(CancellationToken cancellationToken = default);
}

public interface IActivityWatchClient
{
    Task<ActivityWatchSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default);
}

public sealed record ActivityWatchSnapshot(
    DateTimeOffset ObservedAt,
    bool IsAfk,
    string Application,
    string WindowTitle,
    string Domain,
    string BrowserTitle);

public interface IPersonalAiService
{
    Task<IReadOnlyList<ActionDraft>> OrganizeActionsAsync(
        string sourceText,
        IReadOnlyList<GoalRecord> activeGoals,
        CancellationToken cancellationToken = default);

    Task<(ActivityCategory Category, string DisplayName, double Confidence)> ClassifyActivityAsync(
        ActivityWatchSnapshot activity,
        IReadOnlyList<GoalRecord> activeGoals,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActivityClassification>> ClassifyActivitiesAsync(
        IReadOnlyList<ActivitySegment> activities,
        IReadOnlyList<GoalRecord> activeGoals,
        CancellationToken cancellationToken = default);

    Task<(string Inference, string Suggestions)> GenerateReportNarrativeAsync(
        ReportPeriod period,
        string facts,
        IReadOnlyList<GoalRecord> activeGoals,
        CancellationToken cancellationToken = default);

    Task<string> SuggestDailyPlanAsync(
        IReadOnlyList<GoalRecord> activeGoals,
        IReadOnlyList<ActionItemRecord> pendingActions,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DailyGoalDraft>> AnalyzeDailyGoalsAsync(
        string sourceText,
        IReadOnlyList<GoalRecord> activeGoals,
        CancellationToken cancellationToken = default);
}

public interface IAiBudgetTracker
{
    event EventHandler<AiBudgetSnapshot>? BudgetReached;
    Task<AiBudgetSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task<bool> CanUseAutomaticAiAsync(CancellationToken cancellationToken = default);
    Task RecordUsageAsync(string model, int promptTokens, int cachedPromptTokens, int completionTokens, CancellationToken cancellationToken = default);
    Task ContinueTodayAsync(CancellationToken cancellationToken = default);
    Task PauseTodayAsync(CancellationToken cancellationToken = default);
}
