namespace Vigil.Core;

public interface IFocusAiClient
{
    Task<string> TestAsync(CancellationToken cancellationToken);
    Task<FrameJudgment> AnalyzeFrameAsync(
        string goal,
        ActivityContext context,
        ReadOnlyMemory<byte> jpeg,
        CancellationToken cancellationToken);
    Task<string> SummarizeAsync(
        SessionSummary summary,
        IReadOnlyList<string> distractedActivities,
        CancellationToken cancellationToken);
}

public interface IScreenCaptureService
{
    Task<CapturedFrame> CapturePrimaryAsync(CancellationToken cancellationToken);
}

public interface IActivityContextService
{
    ActivityContext GetCurrent();
}

public interface IIdleService
{
    TimeSpan GetIdleDuration();
}

public interface ISessionRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task MarkRunningSessionsInterruptedAsync(CancellationToken cancellationToken = default);
    Task CreateAsync(SessionSummary session, CancellationToken cancellationToken = default);
    Task UpdateAsync(SessionSummary session, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionSummary>> GetAllAsync(CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task DeleteAllAsync(CancellationToken cancellationToken = default);
}

public interface IReminderService
{
    void Handle(ReminderRequest request);
    void CloseAll();
}

public interface IAppSettingsStore
{
    Task<ProviderSettings> LoadProviderAsync(CancellationToken cancellationToken = default);
    Task SaveProviderAsync(string baseUrl, string model, string apiKey, CancellationToken cancellationToken = default);
    Task<string?> GetApiKeyAsync(CancellationToken cancellationToken = default);
}
