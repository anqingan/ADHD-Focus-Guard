using Vigil.Core;

namespace Vigil.Infrastructure;

public sealed class ActivityTrackingService : IAsyncDisposable
{
    private readonly IActivityWatchClient _client;
    private readonly IPersonalDataRepository _repository;
    private readonly AutomaticReminderLimiter _reminderLimiter;
    private readonly AutomaticActivitySwitchPolicy _switchPolicy;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _loop;
    private ActivitySegment? _current;
    private DateTimeOffset? _workStarted;
    private DateTimeOffset? _nonWorkStarted;
    private DateTimeOffset? _entertainmentStarted;
    private bool _entertainmentTwoMinuteSent;
    private bool _entertainmentFiveMinuteSent;
    private DateTimeOffset? _presentSince;

    public ActivityTrackingService(
        IActivityWatchClient client,
        IPersonalDataRepository repository,
        AutomaticReminderLimiter? reminderLimiter = null)
    {
        _client = client;
        _repository = repository;
        _reminderLimiter = reminderLimiter ?? new AutomaticReminderLimiter();
        _switchPolicy = new AutomaticActivitySwitchPolicy(_reminderLimiter);
    }

    public bool IsActiveWorkMode { get; private set; }
    public bool HasBeenPresentFor(TimeSpan duration) => _presentSince is not null && DateTimeOffset.Now - _presentSince >= duration;
    public string StatusText { get; private set; } = "等待 ActivityWatch…";
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? GentleReminder;
    public event EventHandler<bool>? ActiveModeChanged;

    public void Start()
    {
        if (_loop is not null) return;
        _loop = RunAsync(_lifetime.Token);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            await PollSafelyAsync(cancellationToken);
            while (await timer.WaitForNextTickAsync(cancellationToken)) await PollSafelyAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            StatusText = "ActivityWatch 记录暂停：" + ex.Message;
            StatusChanged?.Invoke(this, StatusText);
            await SimpleLog.WriteAsync("activity-watch", ex.GetType().Name + ": " + ex.Message);
        }
        finally { await FlushCurrentAsync(CancellationToken.None); }
    }

    private async Task PollSafelyAsync(CancellationToken cancellationToken)
    {
        try { await PollAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { StatusText = "ActivityWatch 本轮读取失败，5 秒后重试。"; StatusChanged?.Invoke(this, StatusText); await SimpleLog.WriteAsync("activity-watch", ex.GetType().Name + ": " + ex.Message); }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        ActivityWatchSnapshot? snapshot;
        try { snapshot = await _client.GetCurrentAsync(cancellationToken); }
        catch (HttpRequestException) { snapshot = null; }
        if (snapshot is null)
        {
            StatusText = "无法连接 ActivityWatch；本地目标和计时仍可使用。";
            StatusChanged?.Invoke(this, StatusText);
            return;
        }
        if (snapshot.IsAfk)
        {
            await FlushCurrentAsync(cancellationToken);
            SetActive(false);
            _workStarted = _nonWorkStarted = _entertainmentStarted = null;
            _switchPolicy.Reset();
            _presentSince = null;
            StatusText = "已离开电脑，暂停活动计时和截图。";
            StatusChanged?.Invoke(this, StatusText);
            return;
        }

        var rules = await _repository.GetClassificationRulesAsync(cancellationToken);
        _presentSince ??= snapshot.ObservedAt;
        var (category, confidence) = ActivityClassifier.ApplyRules(snapshot, rules);
        if (category == ActivityCategory.Other)
        {
            var goals = await _repository.GetGoalsAsync(false, cancellationToken);
            var activityText = (snapshot.BrowserTitle + " " + snapshot.WindowTitle).ToLowerInvariant();
            if (goals.Any(g => GoalTitleMatches(g.Title, activityText))) { category = ActivityCategory.WorkAndStudy; confidence = .76; }
        }
        var display = ActivityClassifier.BuildDisplayName(snapshot);
        var now = snapshot.ObservedAt;
        var switchReminder = _switchPolicy.Observe(snapshot.Application, snapshot.Domain, display, category, now);
        if (switchReminder is not null) GentleReminder?.Invoke(this, switchReminder);
        await AppendAsync(snapshot, display, category, confidence, now, cancellationToken);
        UpdateMode(category, now);
        StatusText = $"{CategoryName(category)} · {display}";
        StatusChanged?.Invoke(this, StatusText);
    }

    private async Task AppendAsync(ActivityWatchSnapshot snapshot, string display, ActivityCategory category, double confidence, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var same = _current is not null
            && _current.Application.Equals(snapshot.Application, StringComparison.OrdinalIgnoreCase)
            && _current.Domain.Equals(snapshot.Domain, StringComparison.OrdinalIgnoreCase)
            && _current.DisplayName.Equals(display, StringComparison.Ordinal)
            && _current.Category == category;
        if (same)
        {
            _current = _current! with { EndedAt = now };
            if (_current.DurationSeconds >= 10) await _repository.SaveActivitySegmentAsync(_current, cancellationToken);
            return;
        }

        await FlushCurrentAsync(cancellationToken);
        _current = new ActivitySegment
        {
            Id = Guid.NewGuid(),
            StartedAt = now,
            EndedAt = now.AddSeconds(1),
            Application = snapshot.Application,
            Domain = snapshot.Domain,
            DisplayName = display,
            Category = category,
            Source = ActivitySource.ActivityWatch,
            ClassificationSource = confidence >= .99 ? ClassificationSource.UserRule : ClassificationSource.BuiltInRule,
            Confidence = confidence
        };
    }

    private async Task FlushCurrentAsync(CancellationToken cancellationToken)
    {
        var current = _current;
        _current = null;
        if (current is null) return;
        if (current.DurationSeconds < 10) current = current with { Category = ActivityCategory.Other, DisplayName = "短暂切换" };
        await _repository.SaveActivitySegmentAsync(current, cancellationToken);
    }

    private void UpdateMode(ActivityCategory category, DateTimeOffset now)
    {
        if (category == ActivityCategory.WorkAndStudy)
        {
            _workStarted ??= now;
            _nonWorkStarted = _entertainmentStarted = null;
            _entertainmentTwoMinuteSent = _entertainmentFiveMinuteSent = false;
            if (!IsActiveWorkMode && now - _workStarted >= TimeSpan.FromSeconds(30)) SetActive(true);
            return;
        }

        _workStarted = null;
        _nonWorkStarted ??= now;
        if (category == ActivityCategory.Entertainment && IsActiveWorkMode)
        {
            _entertainmentStarted ??= now;
            var duration = now - _entertainmentStarted;
            if (!_entertainmentTwoMinuteSent && duration >= TimeSpan.FromMinutes(2))
            {
                _entertainmentTwoMinuteSent = true;
                SetActive(false);
                if (_reminderLimiter.TryAcquire())
                    GentleReminder?.Invoke(this, "已经连续娱乐 2 分钟，确认一下是否要回到刚才的工作。");
            }
        }
        else if (category != ActivityCategory.Entertainment) _entertainmentStarted = null;

        if (_entertainmentStarted is not null && !_entertainmentFiveMinuteSent && now - _entertainmentStarted >= TimeSpan.FromMinutes(5))
        {
            _entertainmentFiveMinuteSent = true;
            if (_reminderLimiter.TryAcquire())
                GentleReminder?.Invoke(this, "本次娱乐已持续 5 分钟，之后只记录时间，不再重复提醒。");
        }
        if (IsActiveWorkMode && now - _nonWorkStarted >= TimeSpan.FromMinutes(2)) SetActive(false);
    }

    private void SetActive(bool active)
    {
        if (IsActiveWorkMode == active) return;
        IsActiveWorkMode = active;
        if (active) _reminderLimiter.Reset();
        ActiveModeChanged?.Invoke(this, active);
    }

    private static string CategoryName(ActivityCategory category) => category switch
    {
        ActivityCategory.WorkAndStudy => "学习与工作",
        ActivityCategory.Entertainment => "娱乐",
        _ => "其它"
    };

    private static bool GoalTitleMatches(string goal, string activityText)
    {
        var normalized = goal.Trim().ToLowerInvariant(); if (normalized.Length >= 4 && activityText.Contains(normalized, StringComparison.Ordinal)) return true;
        var tokens = normalized.Split([' ', '，', ',', '、', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Any(token => token.Length >= 3 && activityText.Contains(token, StringComparison.Ordinal));
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { }
        }
        _lifetime.Dispose();
    }
}
