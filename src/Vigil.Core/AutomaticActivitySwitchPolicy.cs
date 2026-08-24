namespace Vigil.Core;

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

public sealed class AutomaticActivitySwitchPolicy
{
    public static readonly TimeSpan LongTaskThreshold = TimeSpan.FromMinutes(15);

    private readonly AutomaticReminderLimiter _limiter;
    private string _activityKey = "";
    private string _displayName = "";
    private ActivityCategory _category;
    private DateTimeOffset _activityStarted;
    private bool _longTaskArmed;

    public AutomaticActivitySwitchPolicy(AutomaticReminderLimiter limiter) => _limiter = limiter;

    public string? Observe(
        string application,
        string domain,
        string displayName,
        ActivityCategory category,
        DateTimeOffset now)
    {
        var key = BuildKey(application, domain, displayName, category);
        if (_activityKey.Length == 0)
        {
            Begin(key, displayName, category, now);
            return null;
        }

        if (string.Equals(_activityKey, key, StringComparison.OrdinalIgnoreCase))
        {
            ArmLongTaskIfNeeded(now);
            return null;
        }

        ArmLongTaskIfNeeded(now);
        var previousName = _displayName;
        var previousMinutes = Math.Max(1, (int)Math.Round((now - _activityStarted).TotalMinutes));
        var shouldRemind = _longTaskArmed && _category == ActivityCategory.WorkAndStudy;
        Begin(key, displayName, category, now);

        if (!shouldRemind || !_limiter.TryAcquire()) return null;
        return $"你已经在“{previousName}”投入约 {previousMinutes} 分钟，现在切换到了“{displayName}”。确认这是有意安排吗？";
    }

    public void Reset()
    {
        _activityKey = "";
        _displayName = "";
        _activityStarted = default;
        _longTaskArmed = false;
    }

    private void ArmLongTaskIfNeeded(DateTimeOffset now)
    {
        if (_longTaskArmed || _category != ActivityCategory.WorkAndStudy
            || now - _activityStarted < LongTaskThreshold) return;
        _longTaskArmed = true;
        _limiter.Reset();
    }

    private void Begin(string key, string displayName, ActivityCategory category, DateTimeOffset now)
    {
        _activityKey = key;
        _displayName = displayName;
        _category = category;
        _activityStarted = now;
        _longTaskArmed = false;
    }

    private static string BuildKey(string application, string domain, string displayName, ActivityCategory category) =>
        $"{application.Trim()}\n{domain.Trim()}\n{displayName.Trim()}\n{category}";
}
