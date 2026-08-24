namespace Vigil.Core;

/// <summary>
/// Coordinates every reminder emitted while the app inferred work automatically.
/// A continuous automatic-work episode may show at most two lightweight rounds.
/// Full-screen intervention belongs exclusively to an explicit focus session.
/// </summary>
public sealed class AutomaticReminderLimiter
{
    private readonly object _gate = new();
    private int _issuedCount;

    public int IssuedCount
    {
        get { lock (_gate) return _issuedCount; }
    }

    public bool TryAcquire()
    {
        lock (_gate)
        {
            if (_issuedCount >= 2) return false;
            _issuedCount++;
            return true;
        }
    }

    public void Reset()
    {
        lock (_gate) _issuedCount = 0;
    }
}

public sealed class AutomaticInterventionPolicy
{
    private readonly InterventionPolicy _inner = new();
    private readonly AutomaticReminderLimiter _limiter;

    public AutomaticInterventionPolicy(AutomaticReminderLimiter limiter) => _limiter = limiter;

    public void Reset() => _inner.Reset();
    public void MuteCurrentDistraction() => _inner.MuteCurrentDistraction();

    public IReadOnlyList<ReminderRequest> Evaluate(
        FocusLevel level,
        DateTimeOffset now,
        string goal,
        string reminder,
        bool freshAiJudgment,
        TimeSpan idleDuration)
    {
        var proposed = _inner.Evaluate(level, now, goal, reminder, freshAiJudgment, idleDuration);
        if (level == FocusLevel.Focused)
        {
            _limiter.Reset();
            return proposed;
        }
        var hide = proposed.FirstOrDefault(item => item.Kind == ReminderKind.HideSoftReminder);
        if (hide is not null) return [hide];
        if (proposed.Count == 0 || !_limiter.TryAcquire()) return [];

        var source = proposed.First();
        var message = level == FocusLevel.Distracted
            ? "检测到工作内容发生明显切换，确认这是当前安排吗？"
            : "当前内容和刚才的工作方向有些偏离，确认一下是否继续。";
        var light = new ReminderRequest(ReminderKind.AutomaticTray, source.Level, message, "");
        return proposed.Any(item => item.Kind == ReminderKind.Sound)
            ? [light, new ReminderRequest(ReminderKind.Sound, source.Level, "", source.Goal)]
            : [light];
    }
}
