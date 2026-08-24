namespace Vigil.Core;

public enum FocusLevel
{
    Focused,
    Wandering,
    Distracted,
    Away
}

public enum ObservationAvailability
{
    Available,
    Unavailable
}

public enum SessionPhase
{
    Idle,
    Running,
    Summarizing,
    Completed,
    FailedToStart,
    Interrupted
}

public enum SessionCompletionKind
{
    Running,
    Automatic,
    Manual,
    Interrupted
}

public enum ReminderKind
{
    Capsule,
    Tray,
    AutomaticTray,
    Sound,
    FullScreenOverlay,
    HideSoftReminder
}

public sealed record ProviderSettings(string BaseUrl, string Model, bool HasApiKey)
{
    public string TextModel { get; init; } = "deepseek-v4-flash";
    public static ProviderSettings Default { get; } = new(
        "https://api.deepseek.com",
        "deepseek-v4-flash-vision-exp",
        false)
    { TextModel = "deepseek-v4-flash" };
}

public sealed record ActivityContext(string ProcessName, string WindowTitle);

public sealed record FrameJudgment(FocusLevel Level, string Activity, string Reminder);

public sealed record ReminderRequest(
    ReminderKind Kind,
    FocusLevel Level,
    string Message,
    string Goal,
    bool CanMuteCurrentStreak = false);

public sealed record SessionSnapshot(
    Guid Id,
    SessionPhase Phase,
    string Goal,
    int PlannedSeconds,
    int RemainingSeconds,
    FocusLevel? Level,
    ObservationAvailability Availability,
    string Activity,
    string ConnectionMessage);

public sealed record SessionSummary
{
    public required Guid Id { get; init; }
    public required string Goal { get; init; }
    public required int PlannedSeconds { get; init; }
    public required int ActualSeconds { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? EndedAtUtc { get; init; }
    public required SessionCompletionKind CompletionKind { get; init; }
    public required int FocusedSeconds { get; init; }
    public required int WanderingSeconds { get; init; }
    public required int DistractedSeconds { get; init; }
    public required int AwaySeconds { get; init; }
    public required int UnknownSeconds { get; init; }
    public string SummaryText { get; init; } = "";

    public int ObservedSeconds => FocusedSeconds + WanderingSeconds + DistractedSeconds + AwaySeconds;
    public double ObservationCoverage => ActualSeconds <= 0 ? 0 : Math.Clamp((double)ObservedSeconds / ActualSeconds, 0, 1);
}

public sealed class CapturedFrame : IDisposable
{
    private byte[]? _jpeg;
    private byte[]? _hash;

    public CapturedFrame(byte[] jpeg, byte[] hash)
    {
        _jpeg = jpeg;
        _hash = hash;
    }

    public ReadOnlyMemory<byte> Jpeg => _jpeg ?? throw new ObjectDisposedException(nameof(CapturedFrame));
    public ReadOnlyMemory<byte> Hash => _hash ?? throw new ObjectDisposedException(nameof(CapturedFrame));

    public void Dispose()
    {
        if (_jpeg is not null)
        {
            Array.Clear(_jpeg);
            _jpeg = null;
        }
        if (_hash is not null)
        {
            Array.Clear(_hash);
            _hash = null;
        }
    }
}
