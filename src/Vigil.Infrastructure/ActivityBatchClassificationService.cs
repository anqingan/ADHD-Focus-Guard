using Vigil.Core;

namespace Vigil.Infrastructure;

public sealed record ActivityBatchClassificationOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan Lookback { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan MaxPendingAge { get; init; } = TimeSpan.FromMinutes(2);
    public int MinimumBatchSize { get; init; } = 5;
    public int MaximumBatchSize { get; init; } = 50;
    public double MinimumRuleConfidence { get; init; } = .65;
}

public sealed class ActivityBatchClassificationService : IAsyncDisposable
{
    private readonly IPersonalDataRepository _repository;
    private readonly IPersonalAiService _ai;
    private readonly IAiBudgetTracker? _budget;
    private readonly ActivityBatchClassificationOptions _options;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _classificationGate = new(1, 1);
    private Task? _loop;
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;
    private int _consecutiveFailures;

    public ActivityBatchClassificationService(
        IPersonalDataRepository repository,
        IPersonalAiService ai,
        IAiBudgetTracker? budget = null,
        ActivityBatchClassificationOptions? options = null)
    {
        _repository = repository;
        _ai = ai;
        _budget = budget;
        _options = options ?? new ActivityBatchClassificationOptions();
    }

    public void Start()
    {
        if (_loop is null) _loop = RunAsync(_lifetime.Token);
    }

    public async Task<int> ClassifyPendingAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!await _classificationGate.WaitAsync(0, cancellationToken)) return 0;
        try
        {
            var now = DateTimeOffset.Now;
            var segments = await _repository.GetActivitySegmentsAsync(now - _options.Lookback, now, cancellationToken);
            var updated = await NormalizeCommunicationActivitiesAsync(segments, cancellationToken);
            if (_budget is not null && !await _budget.CanUseAutomaticAiAsync(cancellationToken)) return updated;

            var groups = BuildCandidateGroups(segments, _options.MaximumBatchSize);
            if (groups.Count == 0) return updated;
            if (!force && groups.Count < _options.MinimumBatchSize && groups.All(group => now - group.FirstSeen < _options.MaxPendingAge)) return updated;

            var representatives = groups.Select(group => group.Representative).ToArray();
            var goals = await _repository.GetGoalsAsync(false, cancellationToken);
            var results = await _ai.ClassifyActivitiesAsync(representatives, goals, cancellationToken);
            var validIds = representatives.Select(candidate => candidate.Id).ToHashSet();
            var resultMap = results
                .Where(result => validIds.Contains(result.Id))
                .GroupBy(result => result.Id)
                .ToDictionary(group => group.Key, group => group.First());
            if (resultMap.Count != representatives.Length)
                throw new InvalidDataException($"AI 批量分类结果不完整：请求 {representatives.Length} 项，收到 {resultMap.Count} 项。");

            foreach (var group in groups)
            {
                if (!resultMap.TryGetValue(group.Representative.Id, out var result)) continue;
                var category = result.Category == ActivityCategory.Entertainment
                    && ActivityClassifier.IsNeutralCommunicationActivity(group.Representative.Application, group.Representative.Domain)
                        ? ActivityCategory.Other
                        : result.Category;
                var displayName = string.IsNullOrWhiteSpace(result.DisplayName)
                    ? group.Representative.DisplayName
                    : result.DisplayName;
                foreach (var segment in group.Segments)
                {
                    await _repository.SaveActivitySegmentAsync(segment with
                    {
                        Category = category,
                        DisplayName = displayName,
                        Confidence = result.Confidence,
                        ClassificationSource = ClassificationSource.Ai
                    }, cancellationToken);
                    updated++;
                }

                if (category == ActivityCategory.Other || result.Confidence < _options.MinimumRuleConfidence) continue;
                await _repository.SaveClassificationRuleAsync(new ClassificationRule
                {
                    Id = Guid.NewGuid(),
                    Scope = RuleScope.Similar,
                    Application = group.Representative.Application,
                    Domain = group.Representative.Domain,
                    TitleKeywords = group.Representative.DisplayName,
                    Category = category,
                    CreatedAt = DateTimeOffset.MinValue,
                    LastMatchedAt = now
                }, cancellationToken);
            }

            _consecutiveFailures = 0;
            _retryAfter = DateTimeOffset.MinValue;
            return updated;
        }
        finally
        {
            _classificationGate.Release();
        }
    }

    private async Task<int> NormalizeCommunicationActivitiesAsync(
        IReadOnlyList<ActivitySegment> segments,
        CancellationToken cancellationToken)
    {
        var updated = 0;
        foreach (var segment in segments.Where(segment =>
                     segment.Category == ActivityCategory.Entertainment
                     && ActivityClassifier.IsNeutralCommunicationActivity(segment.Application, segment.Domain)))
        {
            await _repository.SaveActivitySegmentAsync(segment with
            {
                Category = ActivityCategory.Other,
                ClassificationSource = ClassificationSource.BuiltInRule,
                Confidence = .92
            }, cancellationToken);
            updated++;
        }

        var rules = await _repository.GetClassificationRulesAsync(cancellationToken);
        foreach (var rule in rules.Where(rule =>
                     rule.Category == ActivityCategory.Entertainment
                     && ActivityClassifier.IsNeutralCommunicationActivity(rule.Application, rule.Domain)))
            await _repository.DeleteClassificationRuleAsync(rule.Id, cancellationToken);
        return updated;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_options.InitialDelay, cancellationToken);
            using var timer = new PeriodicTimer(_options.PollInterval);
            do
            {
                if (DateTimeOffset.Now >= _retryAfter)
                {
                    try
                    {
                        await ClassifyPendingAsync(cancellationToken: cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _consecutiveFailures++;
                        var retryMinutes = _consecutiveFailures switch
                        {
                            1 => 2,
                            2 => 5,
                            3 => 15,
                            _ => 30
                        };
                        _retryAfter = DateTimeOffset.Now.AddMinutes(retryMinutes);
                        await SimpleLog.WriteAsync("activity-classification", ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static IReadOnlyList<CandidateGroup> BuildCandidateGroups(IReadOnlyList<ActivitySegment> segments, int maximumBatchSize)
    {
        return segments
            .Where(segment => segment.Source == ActivitySource.ActivityWatch
                && segment.Category == ActivityCategory.Other
                && segment.ClassificationSource == ClassificationSource.BuiltInRule
                && segment.Confidence < .75
                && !segment.DisplayName.Equals("短暂切换", StringComparison.Ordinal))
            .GroupBy(segment => new ActivityKey(
                segment.Application.Trim().ToLowerInvariant(),
                segment.Domain.Trim().ToLowerInvariant(),
                segment.DisplayName.Trim().ToLowerInvariant()))
            .Select(group =>
            {
                var items = group.OrderBy(segment => segment.StartedAt).ToArray();
                var firstSeen = items.Min(segment => segment.StartedAt);
                var totalSeconds = items.Sum(segment => segment.DurationSeconds);
                var representative = items.OrderByDescending(segment => segment.DurationSeconds).First() with
                {
                    StartedAt = firstSeen,
                    EndedAt = firstSeen.AddSeconds(totalSeconds)
                };
                return new CandidateGroup(
                    representative,
                    items,
                    firstSeen,
                    totalSeconds);
            })
            .OrderByDescending(group => group.TotalSeconds)
            .ThenBy(group => group.FirstSeen)
            .Take(Math.Clamp(maximumBatchSize, 1, 50))
            .ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        if (_loop is not null)
        {
            try { await _loop; }
            catch (OperationCanceledException) { }
        }
        await _classificationGate.WaitAsync();
        _classificationGate.Release();
        _classificationGate.Dispose();
        _lifetime.Dispose();
    }

    private sealed record ActivityKey(string Application, string Domain, string DisplayName);
    private sealed record CandidateGroup(
        ActivitySegment Representative,
        IReadOnlyList<ActivitySegment> Segments,
        DateTimeOffset FirstSeen,
        int TotalSeconds);
}
