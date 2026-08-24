namespace Vigil.Core;

public enum ActivityCategory
{
    WorkAndStudy,
    Entertainment,
    Other
}

public enum GoalHorizon
{
    Direction,
    Stage,
    Week,
    Today
}

public enum GoalStatus
{
    NotStarted,
    InProgress,
    Completed,
    Paused,
    Abandoned,
    Archived
}

public enum ActionItemStatus
{
    Pending,
    InProgress,
    Completed,
    Paused,
    Abandoned
}

public enum MemoryAuthor
{
    User,
    Ai
}

public enum MemoryStatus
{
    PendingReview,
    Confirmed,
    Archived
}

public enum ReportPeriod
{
    Daily,
    Weekly,
    Monthly
}

public enum ActivitySource
{
    ActivityWatch,
    User
}

public enum ClassificationSource
{
    UserRule,
    BuiltInRule,
    Ai,
    User
}

public enum RuleScope
{
    Exact,
    Similar,
    ApplicationOrDomain
}

public sealed record GoalRecord
{
    public required Guid Id { get; init; }
    public required GoalHorizon Horizon { get; init; }
    public required string Title { get; init; }
    public string ExpectedOutcome { get; init; } = "";
    public GoalStatus Status { get; init; } = GoalStatus.NotStarted;
    public int Priority { get; init; } = 2;
    public int? EstimatedMinutes { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public string CompletionEvidence { get; init; } = "";
    public IReadOnlyList<Guid> RelatedGoalIds { get; init; } = [];
}

public sealed record GoalHistoryRecord(
    Guid Id,
    Guid GoalId,
    DateTimeOffset ChangedAt,
    string ChangeKind,
    string SnapshotJson);

public sealed record ActionItemRecord
{
    public required Guid Id { get; init; }
    public required string Title { get; init; }
    public string ExpectedOutcome { get; init; } = "";
    public ActionItemStatus Status { get; init; } = ActionItemStatus.Pending;
    public int Priority { get; init; } = 2;
    public int? EstimatedMinutes { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public string SourceText { get; init; } = "";
    public IReadOnlyList<Guid> RelatedGoalIds { get; init; } = [];
}

public sealed record MemoryRecord
{
    public required Guid Id { get; init; }
    public required string Text { get; init; }
    public required MemoryAuthor Author { get; init; }
    public required MemoryStatus Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string Tags { get; init; } = "";
    public string SourceReference { get; init; } = "";
    public Guid? RelatedGoalId { get; init; }
    public bool IsPinned { get; init; }
}

public sealed record ActivitySegment
{
    public required Guid Id { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset EndedAt { get; init; }
    public required string Application { get; init; }
    public string Domain { get; init; } = "";
    public required string DisplayName { get; init; }
    public required ActivityCategory Category { get; init; }
    public required ActivitySource Source { get; init; }
    public required ClassificationSource ClassificationSource { get; init; }
    public double Confidence { get; init; }
    public Guid? RelatedGoalId { get; init; }
    public int DurationSeconds => Math.Max(0, (int)(EndedAt - StartedAt).TotalSeconds);
}

public sealed record ClassificationRule
{
    public required Guid Id { get; init; }
    public required RuleScope Scope { get; init; }
    public string Application { get; init; } = "";
    public string Domain { get; init; } = "";
    public string TitleKeywords { get; init; } = "";
    public required ActivityCategory Category { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastMatchedAt { get; init; }
    public bool IsEnabled { get; init; } = true;
}

public sealed record ReportRecord
{
    public required Guid Id { get; init; }
    public required ReportPeriod Period { get; init; }
    public required DateTimeOffset PeriodStart { get; init; }
    public required DateTimeOffset PeriodEnd { get; init; }
    public required int Version { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string FactsText { get; init; }
    public string InferenceText { get; init; } = "";
    public string SuggestionsText { get; init; } = "";
    public string GoalSnapshotJson { get; init; } = "[]";
    public double Coverage { get; init; }
}

public sealed record ActivityTotals(
    int WorkAndStudySeconds,
    int EntertainmentSeconds,
    int OtherSeconds,
    int ObservedSeconds);

public sealed record DailyPlanState(
    DateOnly ActivityDate,
    bool HasBeenPrompted,
    DateTimeOffset? SnoozedUntil,
    DateTimeOffset? CompletedAt);

public sealed record ActionDraft(
    string Title,
    string ExpectedOutcome,
    DateTimeOffset? DueAt,
    int Priority,
    int? EstimatedMinutes,
    IReadOnlyList<Guid> RelatedGoalIds);

public sealed record DailyGoalDraft(
    string Title,
    string ExpectedOutcome,
    string Classification,
    int Priority,
    int? EstimatedMinutes,
    IReadOnlyList<Guid> RelatedGoalIds,
    string Reasoning);

public sealed record ActivityClassification(Guid Id, ActivityCategory Category, string DisplayName, double Confidence);

public sealed record AiBudgetSnapshot(DateOnly ActivityDate, double EstimatedCny, double LimitCny, bool ContinueAfterLimit, bool IsPaused);
