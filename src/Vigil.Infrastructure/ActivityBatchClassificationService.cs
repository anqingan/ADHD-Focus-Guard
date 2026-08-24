using Vigil.Core;

namespace Vigil.Infrastructure;

public sealed class ActivityBatchClassificationService : IAsyncDisposable
{
    private readonly IPersonalDataRepository _repository;
    private readonly IPersonalAiService _ai;
    private readonly IAiBudgetTracker? _budget;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _loop;

    public ActivityBatchClassificationService(IPersonalDataRepository repository, IPersonalAiService ai, IAiBudgetTracker? budget = null) { _repository = repository; _ai = ai; _budget = budget; }
    public void Start() { if (_loop is null) _loop = RunAsync(_lifetime.Token); }

    public async Task ClassifyPendingAsync(CancellationToken cancellationToken = default)
    {
        if (_budget is not null && !await _budget.CanUseAutomaticAiAsync(cancellationToken)) return;
        var end = DateTimeOffset.Now; var start = end.AddHours(-24); var segments = await _repository.GetActivitySegmentsAsync(start, end, cancellationToken);
        var candidates = segments.Where(s => s.Source == ActivitySource.ActivityWatch && s.ClassificationSource == ClassificationSource.BuiltInRule && s.Confidence < .75)
            .GroupBy(s => (s.Application, s.Domain, s.DisplayName)).Select(g => g.OrderByDescending(s => s.DurationSeconds).First()).Take(50).ToList();
        if (candidates.Count == 0) return; var goals = await _repository.GetGoalsAsync(false, cancellationToken); var results = await _ai.ClassifyActivitiesAsync(candidates, goals, cancellationToken); var map = results.ToDictionary(r => r.Id);
        foreach (var candidate in candidates)
        {
            if (!map.TryGetValue(candidate.Id, out var result)) continue;
            var matching = segments.Where(s => s.Application == candidate.Application && s.Domain == candidate.Domain && s.DisplayName == candidate.DisplayName && s.ClassificationSource == ClassificationSource.BuiltInRule);
            foreach (var segment in matching) await _repository.SaveActivitySegmentAsync(segment with { Category = result.Category, DisplayName = string.IsNullOrWhiteSpace(result.DisplayName) ? segment.DisplayName : result.DisplayName, Confidence = result.Confidence, ClassificationSource = ClassificationSource.Ai }, cancellationToken);
            await _repository.SaveClassificationRuleAsync(new ClassificationRule { Id = Guid.NewGuid(), Scope = RuleScope.Similar, Application = candidate.Application, Domain = candidate.Domain, TitleKeywords = candidate.DisplayName, Category = result.Category, CreatedAt = DateTimeOffset.MinValue, LastMatchedAt = DateTimeOffset.Now }, cancellationToken);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        try { while (await timer.WaitForNextTickAsync(cancellationToken)) { try { await ClassifyPendingAsync(cancellationToken); } catch (Exception ex) when (ex is not OperationCanceledException) { await SimpleLog.WriteAsync("activity-classification", ex.GetType().Name + ": " + ex.Message); } } }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync() { await _lifetime.CancelAsync(); if (_loop is not null) { try { await _loop; } catch (OperationCanceledException) { } } _lifetime.Dispose(); }
}
