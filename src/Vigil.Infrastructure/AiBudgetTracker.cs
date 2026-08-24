using System.Text.Json;
using Vigil.Core;

namespace Vigil.Infrastructure;

public sealed class AiBudgetTracker : IAiBudgetTracker
{
    private const double DailyLimitCny = 1.0;
    private const double InputCnyPerMillion = 1.0;
    private const double CacheHitCnyPerMillion = .02;
    private const double OutputCnyPerMillion = 2.0;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _file;
    private BudgetDocument? _cached;
    public AiBudgetTracker(string? file = null) => _file = file ?? Path.Combine(AppPaths.Root, "ai-usage.json");
    public event EventHandler<AiBudgetSnapshot>? BudgetReached;

    public async Task<AiBudgetSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken); try { return ToSnapshot(await LoadCurrentAsync(cancellationToken)); } finally { _gate.Release(); }
    }
    public async Task<bool> CanUseAutomaticAiAsync(CancellationToken cancellationToken = default) { var state = await GetSnapshotAsync(cancellationToken); return !state.IsPaused; }

    public async Task RecordUsageAsync(string model, int promptTokens, int cachedPromptTokens, int completionTokens, CancellationToken cancellationToken = default)
    {
        AiBudgetSnapshot? reached = null; await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadCurrentAsync(cancellationToken); var wasBelow = document.EstimatedCny < DailyLimitCny;
            var cached = Math.Clamp(cachedPromptTokens, 0, Math.Max(0, promptTokens)); var miss = Math.Max(0, promptTokens - cached);
            var cost = miss / 1_000_000d * InputCnyPerMillion + cached / 1_000_000d * CacheHitCnyPerMillion + Math.Max(0, completionTokens) / 1_000_000d * OutputCnyPerMillion;
            document = document with { EstimatedCny = document.EstimatedCny + cost, PromptTokens = document.PromptTokens + Math.Max(0, promptTokens), CompletionTokens = document.CompletionTokens + Math.Max(0, completionTokens) }; _cached = document; await SaveAsync(document, cancellationToken);
            if (wasBelow && document.EstimatedCny >= DailyLimitCny && !document.ContinueAfterLimit) reached = ToSnapshot(document);
        }
        finally { _gate.Release(); }
        if (reached is not null) BudgetReached?.Invoke(this, reached);
    }

    public Task ContinueTodayAsync(CancellationToken cancellationToken = default) => SetChoiceAsync(true, cancellationToken);
    public Task PauseTodayAsync(CancellationToken cancellationToken = default) => SetChoiceAsync(false, cancellationToken);
    private async Task SetChoiceAsync(bool continueAfter, CancellationToken cancellationToken) { await _gate.WaitAsync(cancellationToken); try { var d = await LoadCurrentAsync(cancellationToken); d = d with { ContinueAfterLimit = continueAfter, ChoiceMade = true }; _cached = d; await SaveAsync(d, cancellationToken); } finally { _gate.Release(); } }

    private async Task<BudgetDocument> LoadCurrentAsync(CancellationToken cancellationToken)
    {
        var date = ActivityDate(); if (_cached?.ActivityDate == date) return _cached;
        if (File.Exists(_file)) try { await using var stream = File.OpenRead(_file); var saved = await JsonSerializer.DeserializeAsync<BudgetDocument>(stream, cancellationToken: cancellationToken); if (saved?.ActivityDate == date) return _cached = saved; } catch (Exception ex) when (ex is IOException or JsonException) { await SimpleLog.WriteAsync("budget", ex.GetType().Name); }
        return _cached = new(date, 0, 0, 0, false, false);
    }
    private async Task SaveAsync(BudgetDocument document, CancellationToken cancellationToken) { Directory.CreateDirectory(Path.GetDirectoryName(_file) ?? AppPaths.Root); var bytes = JsonSerializer.SerializeToUtf8Bytes(document); var temp = _file + ".tmp-" + Guid.NewGuid().ToString("N"); try { await File.WriteAllBytesAsync(temp, bytes, cancellationToken); File.Move(temp, _file, true); } finally { Array.Clear(bytes); if (File.Exists(temp)) File.Delete(temp); } }
    private static AiBudgetSnapshot ToSnapshot(BudgetDocument d) => new(d.ActivityDate, d.EstimatedCny, DailyLimitCny, d.ContinueAfterLimit, d.EstimatedCny >= DailyLimitCny && !d.ContinueAfterLimit);
    private static DateOnly ActivityDate() { var now = DateTime.Now; return DateOnly.FromDateTime(now.Hour < 8 ? now.AddDays(-1) : now); }
    private sealed record BudgetDocument(DateOnly ActivityDate, double EstimatedCny, long PromptTokens, long CompletionTokens, bool ContinueAfterLimit, bool ChoiceMade);
}
