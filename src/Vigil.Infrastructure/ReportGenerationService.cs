using System.Text.Json;
using Vigil.Core;

namespace Vigil.Infrastructure;

public sealed class ReportGenerationService : IAsyncDisposable
{
    private readonly IPersonalDataRepository _repository;
    private readonly IPersonalAiService _ai;
    private readonly IAiBudgetTracker? _budget;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _loop;

    public ReportGenerationService(IPersonalDataRepository repository, IPersonalAiService ai, IAiBudgetTracker? budget = null) { _repository = repository; _ai = ai; _budget = budget; }
    public void Start() { if (_loop is null) _loop = RunAsync(_lifetime.Token); }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await TryGenerateAsync(cancellationToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
            while (await timer.WaitForNextTickAsync(cancellationToken)) await TryGenerateAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { await SimpleLog.WriteAsync("reports", ex.GetType().Name + ": " + ex.Message); }
    }
    private async Task TryGenerateAsync(CancellationToken cancellationToken) { try { await GenerateMissingAsync(cancellationToken); } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; } catch (Exception ex) { await SimpleLog.WriteAsync("reports", ex.GetType().Name + ": " + ex.Message); } }

    public async Task GenerateMissingAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.Now; var todayStart = ActivityDayStart(now); var periods = new List<(ReportPeriod Period, DateTimeOffset Start, DateTimeOffset End)> { (ReportPeriod.Daily, todayStart.AddDays(-1), todayStart) };
        var daysFromMonday = ((int)todayStart.DayOfWeek + 6) % 7; var thisWeek = todayStart.AddDays(-daysFromMonday); periods.Add((ReportPeriod.Weekly, thisWeek.AddDays(-7), thisWeek));
        var thisMonth = AtLocal(new DateOnly(todayStart.Year, todayStart.Month, 1)); var previousMonth = thisMonth.AddMonths(-1); periods.Add((ReportPeriod.Monthly, previousMonth, thisMonth));
        var existing = await _repository.GetReportsAsync(cancellationToken);
        foreach (var period in periods)
        {
            if (existing.Any(r => r.Period == period.Period && r.PeriodStart == period.Start)) continue;
            await GenerateAsync(period.Period, period.Start, period.End, cancellationToken);
        }
    }

    private async Task GenerateAsync(ReportPeriod period, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        var totals = await _repository.GetActivityTotalsAsync(start, end, cancellationToken); var goals = await _repository.GetGoalsAsync(false, cancellationToken);
        var facts = $"统计范围：{start.LocalDateTime:yyyy-MM-dd HH:mm} 至 {end.LocalDateTime:yyyy-MM-dd HH:mm}\n学习与工作：{Format(totals.WorkAndStudySeconds)}\n娱乐：{Format(totals.EntertainmentSeconds)}\n其它：{Format(totals.OtherSeconds)}\nActivityWatch 与补录活动合计：{Format(totals.ObservedSeconds)}";
        string inference = "AI 分析暂不可用。", suggestions = "请依据确定性统计检查时间分配。";
        try { if (_budget is null || await _budget.CanUseAutomaticAiAsync(cancellationToken)) (inference, suggestions) = await _ai.GenerateReportNarrativeAsync(period, facts, goals, cancellationToken); } catch (Exception ex) { await SimpleLog.WriteAsync("report-ai", ex.GetType().Name + ": " + ex.Message); }
        var report = new ReportRecord { Id = Guid.NewGuid(), Period = period, PeriodStart = start, PeriodEnd = end, Version = 1, CreatedAt = DateTimeOffset.Now, FactsText = facts, InferenceText = inference, SuggestionsText = suggestions, GoalSnapshotJson = JsonSerializer.Serialize(goals), Coverage = totals.ObservedSeconds > 0 ? 1 : 0 };
        await _repository.SaveReportAsync(report, cancellationToken);
        if (period is ReportPeriod.Weekly or ReportPeriod.Monthly && !string.IsNullOrWhiteSpace(inference) && !inference.StartsWith("AI 分析暂不可用", StringComparison.Ordinal))
        {
            var now = DateTimeOffset.Now; await _repository.SaveMemoryAsync(new MemoryRecord { Id = Guid.NewGuid(), Text = inference, Author = MemoryAuthor.Ai, Status = MemoryStatus.PendingReview, CreatedAt = now, UpdatedAt = now, Tags = "长期规律", SourceReference = $"report:{report.Id}" }, cancellationToken);
        }
    }

    private static DateTimeOffset ActivityDayStart(DateTimeOffset now) { var date = DateOnly.FromDateTime(now.LocalDateTime.Hour < 8 ? now.LocalDateTime.AddDays(-1) : now.LocalDateTime); return AtLocal(date); }
    private static DateTimeOffset AtLocal(DateOnly date) { var local = date.ToDateTime(new TimeOnly(8, 0), DateTimeKind.Unspecified); return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)); }
    private static string Format(int seconds) => $"{seconds / 60.0:0.#} 分钟";
    public async ValueTask DisposeAsync() { await _lifetime.CancelAsync(); if (_loop is not null) { try { await _loop; } catch (OperationCanceledException) { } } _lifetime.Dispose(); }
}
