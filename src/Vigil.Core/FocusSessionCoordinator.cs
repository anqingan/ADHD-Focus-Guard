using System.Diagnostics;

namespace Vigil.Core;

public sealed class FocusSessionCoordinator : IAsyncDisposable
{
    private readonly IFocusAiClient _ai;
    private readonly IScreenCaptureService _capture;
    private readonly IActivityContextService _activity;
    private readonly IIdleService _idle;
    private readonly ISessionRepository _repository;
    private readonly IReminderService _reminders;
    private readonly FocusEngineOptions _options;
    private readonly InterventionPolicy _policy = new();
    private readonly SemaphoreSlim _completionGate = new(1, 1);
    private readonly object _observationGate = new();
    private readonly object _stateGate = new();

    private CancellationTokenSource? _lifetimeCts;
    private CancellationTokenSource? _breakCts;
    private Task? _tickerTask;
    private Task? _observationTimerTask;
    private Task? _observationTask;
    private Task? _breakTask;
    private bool _pendingObservation;
    private long _generation;

    private Guid _sessionId;
    private string _goal = "";
    private int _plannedSeconds;
    private DateTimeOffset _startedAt;
    private DateTimeOffset _deadline;
    private DateTimeOffset _lastAccumulatedAt;
    private DateTimeOffset? _lastAiSuccessAt;
    private DateTimeOffset? _lastAiAttemptAt;
    private DateTimeOffset? _lastPersistedAt;
    private byte[]? _lastAnalyzedHash;
    private FocusLevel? _level;
    private ObservationAvailability _availability = ObservationAvailability.Unavailable;
    private string _activityText = "";
    private string _lastReminder = "";
    private string _connectionMessage = "";
    private SessionAccumulator _accumulator = new();
    private readonly List<string> _distractedActivities = [];

    public FocusSessionCoordinator(
        IFocusAiClient ai,
        IScreenCaptureService capture,
        IActivityContextService activity,
        IIdleService idle,
        ISessionRepository repository,
        IReminderService reminders,
        FocusEngineOptions? options = null)
    {
        _ai = ai;
        _capture = capture;
        _activity = activity;
        _idle = idle;
        _repository = repository;
        _reminders = reminders;
        _options = options ?? new FocusEngineOptions();
    }

    public SessionPhase Phase { get; private set; } = SessionPhase.Idle;
    public SessionSummary? LastCompleted { get; private set; }
    public SessionSnapshot Snapshot => BuildSnapshot(DateTimeOffset.UtcNow);

    public event EventHandler<SessionSnapshot>? SnapshotChanged;
    public event EventHandler<SessionSummary>? SessionCompleted;
    public event EventHandler? BreakCompleted;

    public async Task StartAsync(string goal, int durationMinutes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goal);
        var trimmedGoal = goal.Trim();
        if (trimmedGoal.Length is < 1 or > 200)
        {
            throw new ArgumentException("目标长度必须为 1–200 个字符。", nameof(goal));
        }
        if (durationMinutes is < 1 or > 300)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMinutes), "时长必须为 1–300 分钟。");
        }
        if (Phase is SessionPhase.Running or SessionPhase.Resting or SessionPhase.Summarizing)
        {
            throw new InvalidOperationException("已有专注会话正在进行。");
        }

        _generation++;
        _sessionId = Guid.NewGuid();
        _goal = trimmedGoal;
        _plannedSeconds = durationMinutes * 60;
        _startedAt = DateTimeOffset.UtcNow;
        _deadline = _startedAt.AddSeconds(_plannedSeconds);
        _lastAccumulatedAt = _startedAt;
        _lastAiSuccessAt = null;
        _lastAiAttemptAt = null;
        _lastPersistedAt = _startedAt;
        _lastAnalyzedHash = null;
        _level = null;
        _availability = ObservationAvailability.Unavailable;
        _activityText = "";
        _lastReminder = "";
        _connectionMessage = "正在等待第一次屏幕判断…";
        _accumulator = new SessionAccumulator();
        _distractedActivities.Clear();
        _policy.Reset();
        LastCompleted = null;
        Phase = SessionPhase.Running;

        var running = BuildSummary(SessionCompletionKind.Running, _startedAt, null, "");
        try
        {
            await _repository.CreateAsync(running, cancellationToken);
        }
        catch
        {
            Phase = SessionPhase.FailedToStart;
            RaiseSnapshot();
            throw;
        }

        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _lifetimeCts.Token;
        var generation = _generation;
        _tickerTask = RunTickerAsync(generation, token);
        _observationTimerTask = RunObservationTimerAsync(generation, token);
        RequestObservation(generation, token);
        RaiseSnapshot();
    }

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        CompleteAsync(SessionCompletionKind.Manual, cancellationToken);

    public Task StartBreakAsync(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "休息时长必须大于 0 且不超过 60 分钟。");
        }
        if (Phase != SessionPhase.Completed)
        {
            throw new InvalidOperationException("只能在完成一轮专注后开始休息。");
        }

        _generation++;
        _breakCts?.Dispose();
        _breakCts = new CancellationTokenSource();
        _goal = "休息中";
        _plannedSeconds = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds));
        _deadline = DateTimeOffset.UtcNow.Add(duration);
        _level = null;
        _availability = ObservationAvailability.Unavailable;
        _activityText = "休息期间不会观察屏幕，也不会调用 AI。";
        _connectionMessage = "";
        Phase = SessionPhase.Resting;
        var generation = _generation;
        _breakTask = RunBreakTimerAsync(generation, _breakCts.Token);
        RaiseSnapshot();
        return Task.CompletedTask;
    }

    public async Task StopBreakAsync()
    {
        if (Phase != SessionPhase.Resting)
        {
            return;
        }

        _generation++;
        Phase = SessionPhase.Idle;
        var cancellation = _breakCts;
        var task = _breakTask;
        _breakCts = null;
        _breakTask = null;
        if (cancellation is not null)
        {
            await cancellation.CancelAsync();
        }
        if (task is not null)
        {
            try { await task; } catch (OperationCanceledException) { }
        }
        cancellation?.Dispose();
        ResetBreakSnapshot();
        RaiseSnapshot();
    }

    public void MuteCurrentDistraction()
    {
        _policy.MuteCurrentDistraction();
        _reminders.CloseAll();
    }

    public void ResetToIdle()
    {
        if (Phase is SessionPhase.Running or SessionPhase.Resting or SessionPhase.Summarizing)
        {
            throw new InvalidOperationException("进行中的会话不能重置。");
        }
        Phase = SessionPhase.Idle;
        RaiseSnapshot();
    }

    private async Task RunBreakTimerAsync(long generation, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (generation != _generation || Phase != SessionPhase.Resting)
                {
                    return;
                }
                if (DateTimeOffset.UtcNow < _deadline)
                {
                    RaiseSnapshot();
                    continue;
                }

                _breakCts?.Dispose();
                _breakCts = null;
                _breakTask = null;
                Phase = SessionPhase.Idle;
                ResetBreakSnapshot();
                RaiseSnapshot();
                _reminders.Handle(new ReminderRequest(
                    ReminderKind.BreakCompleted,
                    FocusLevel.Focused,
                    "休息结束了，准备开始下一轮。",
                    ""));
                BreakCompleted?.Invoke(this, EventArgs.Empty);
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (generation == _generation)
            {
                _breakCts?.Dispose();
                _breakCts = null;
                _breakTask = null;
            }
        }
    }

    private void ResetBreakSnapshot()
    {
        _goal = "";
        _plannedSeconds = 0;
        _level = null;
        _availability = ObservationAvailability.Unavailable;
        _activityText = "";
        _connectionMessage = "";
    }

    private async Task RunTickerAsync(long generation, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (generation != _generation || Phase != SessionPhase.Running)
                {
                    return;
                }

                var now = DateTimeOffset.UtcNow;
                AccumulateTo(now);

                lock (_stateGate)
                {
                    if (_lastAiSuccessAt is not null
                        && now - _lastAiSuccessAt > _options.MaxAiInterval
                        && _level != FocusLevel.Away
                        && _availability != ObservationAvailability.Unavailable)
                    {
                        AccumulateToCore(now);
                        _availability = ObservationAvailability.Unavailable;
                    }
                }

                if (_lastPersistedAt is null || now - _lastPersistedAt >= _options.PersistenceInterval)
                {
                    _lastPersistedAt = now;
                    try
                    {
                        await _repository.UpdateAsync(BuildSummary(SessionCompletionKind.Running, now, null, ""), cancellationToken);
                    }
                    catch when (!cancellationToken.IsCancellationRequested)
                    {
                        // A transient database lock or disk error must not stop the
                        // timer. Completion performs another best-effort upsert.
                    }
                }

                RaiseSnapshot();
                if (now >= _deadline)
                {
                    _ = Task.Run(() => CompleteAsync(SessionCompletionKind.Automatic, CancellationToken.None));
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunObservationTimerAsync(long generation, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.ObservationInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                RequestObservation(generation, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RequestObservation(long generation, CancellationToken cancellationToken)
    {
        lock (_observationGate)
        {
            if (Phase != SessionPhase.Running || generation != _generation)
            {
                return;
            }
            if (_observationTask is { IsCompleted: false })
            {
                _pendingObservation = true;
                return;
            }
            _observationTask = RunObservationChainAsync(generation, cancellationToken);
        }
    }

    private async Task RunObservationChainAsync(long generation, CancellationToken cancellationToken)
    {
        while (true)
        {
            await ObserveOnceAsync(generation, cancellationToken);

            lock (_observationGate)
            {
                if (_pendingObservation
                    && generation == _generation
                    && Phase == SessionPhase.Running
                    && !cancellationToken.IsCancellationRequested)
                {
                    _pendingObservation = false;
                    continue;
                }
                _pendingObservation = false;
                _observationTask = null;
                return;
            }
        }
    }

    private async Task ObserveOnceAsync(long generation, CancellationToken cancellationToken)
    {
        if (generation != _generation || Phase != SessionPhase.Running)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var idleDuration = _idle.GetIdleDuration();
        if (idleDuration >= _options.IdleThreshold)
        {
            ApplyLevel(FocusLevel.Away, now, idleDuration, "用户暂时离开电脑", "", false);
            return;
        }

        try
        {
            using var frame = await _capture.CapturePrimaryAsync(cancellationToken);
            var retryDue = _lastAiAttemptAt is null || now - _lastAiAttemptAt >= _options.RetryInterval;
            var distance = _lastAnalyzedHash is null
                ? int.MaxValue
                : DHash.Distance(_lastAnalyzedHash, frame.Hash.Span);
            var forceAfterAway = _level == FocusLevel.Away;
            var maxAiInterval = _level == FocusLevel.Distracted
                ? _options.DistractedMaxAiInterval
                : _options.MaxAiInterval;
            var shouldAnalyze = _lastAnalyzedHash is null
                                || forceAfterAway
                                || distance >= _options.DHashThreshold
                                || (_lastAiSuccessAt is null || now - _lastAiSuccessAt >= maxAiInterval);

            if (!shouldAnalyze || (!string.IsNullOrEmpty(_connectionMessage) && !retryDue))
            {
                return;
            }

            _lastAiAttemptAt = now;
            var context = _activity.GetCurrent();
            FrameJudgment judgment;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(_options.AiTimeout);
                judgment = await _ai.AnalyzeFrameAsync(_goal, context, frame.Jpeg, timeout.Token);
            }

            if (generation != _generation || Phase != SessionPhase.Running)
            {
                return;
            }

            lock (_stateGate)
            {
                if (_lastAnalyzedHash is not null)
                {
                    Array.Clear(_lastAnalyzedHash);
                }
                _lastAnalyzedHash = frame.Hash.ToArray();
                _lastAiSuccessAt = DateTimeOffset.UtcNow;
                _connectionMessage = "";
                _lastReminder = judgment.Reminder;
                if (judgment.Level == FocusLevel.Distracted
                    && !string.IsNullOrWhiteSpace(judgment.Activity)
                    && _distractedActivities.Count < 5
                    && !_distractedActivities.Contains(judgment.Activity, StringComparer.OrdinalIgnoreCase))
                {
                    _distractedActivities.Add(judgment.Activity);
                }
            }
            ApplyLevel(judgment.Level, DateTimeOffset.UtcNow, idleDuration, judgment.Activity, judgment.Reminder, true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            MarkAiFailure("AI 请求超时，正在后台重试。", now);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            MarkAiFailure($"AI 暂时不可用：{SanitizeError(ex.Message)}", now);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested || generation != _generation)
        {
            // A provider may surface its own exception while the session is being
            // canceled. It belongs to the old generation and must be discarded.
        }
    }

    private void MarkAiFailure(string message, DateTimeOffset now)
    {
        _connectionMessage = message;
        _lastAiAttemptAt = now;
        lock (_stateGate)
        {
            if (_lastAiSuccessAt is null || now - _lastAiSuccessAt > _options.MaxAiInterval)
            {
                AccumulateToCore(now);
                _availability = ObservationAvailability.Unavailable;
            }
        }
        RaiseSnapshot();
    }

    private void ApplyLevel(
        FocusLevel level,
        DateTimeOffset now,
        TimeSpan idleDuration,
        string activity,
        string reminder,
        bool freshAiJudgment)
    {
        if (Phase != SessionPhase.Running)
        {
            return;
        }
        lock (_stateGate)
        {
            AccumulateToCore(now);
            _level = level;
            _availability = ObservationAvailability.Available;
            _activityText = activity;
        }
        foreach (var action in _policy.Evaluate(level, now, _goal, reminder, freshAiJudgment, idleDuration))
        {
            _reminders.Handle(action);
        }
        RaiseSnapshot();
    }

    private async Task CompleteAsync(SessionCompletionKind kind, CancellationToken cancellationToken)
    {
        await _completionGate.WaitAsync(cancellationToken);
        try
        {
            if (Phase != SessionPhase.Running)
            {
                return;
            }

            Phase = SessionPhase.Summarizing;
            _generation++;
            var now = DateTimeOffset.UtcNow;
            AccumulateTo(now);
            if (_lifetimeCts is not null)
            {
                await _lifetimeCts.CancelAsync();
            }
            _reminders.CloseAll();
            RaiseSnapshot();

            var actual = Math.Max(0, (int)Math.Round((now - _startedAt).TotalSeconds));
            SessionSummary draft;
            string[] distractedActivities;
            lock (_stateGate)
            {
                draft = _accumulator.Build(
                    _sessionId, _goal, _plannedSeconds, actual, _startedAt, now, kind);
                distractedActivities = _distractedActivities.ToArray();
            }

            string summaryText;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                summaryText = await _ai.SummarizeAsync(draft, distractedActivities, timeout.Token);
            }
            catch
            {
                summaryText = BuildLocalSummary(draft);
            }

            var completed = draft with { SummaryText = summaryText };
            try
            {
                // Once a stop has been accepted, finalization must not be left in
                // Summarizing merely because the caller cancels its wait.
                await _repository.UpdateAsync(completed, CancellationToken.None);
            }
            catch
            {
                completed = completed with
                {
                    SummaryText = completed.SummaryText + "\n\n注意：本次摘要未能写入本地历史记录。"
                };
            }
            LastCompleted = completed;
            Phase = SessionPhase.Completed;
            ClearTransientObservationData();
            _level = null;
            _availability = ObservationAvailability.Unavailable;
            _connectionMessage = "";
            RaiseSnapshot();
            SessionCompleted?.Invoke(this, completed);
        }
        finally
        {
            _lifetimeCts?.Dispose();
            _lifetimeCts = null;
            _completionGate.Release();
        }
    }

    private void AccumulateTo(DateTimeOffset now)
    {
        lock (_stateGate)
        {
            AccumulateToCore(now);
        }
    }

    private void AccumulateToCore(DateTimeOffset now)
    {
        var capped = now > _deadline ? _deadline : now;
        if (capped <= _lastAccumulatedAt)
        {
            return;
        }
        _accumulator.Add(capped - _lastAccumulatedAt, _level, _availability);
        _lastAccumulatedAt = capped;
    }

    private SessionSummary BuildSummary(
        SessionCompletionKind kind,
        DateTimeOffset now,
        DateTimeOffset? endedAt,
        string summaryText)
    {
        var actual = Math.Max(0, (int)Math.Round((now - _startedAt).TotalSeconds));
        lock (_stateGate)
        {
            return _accumulator.Build(_sessionId, _goal, _plannedSeconds, actual, _startedAt, now, kind, summaryText)
                with { EndedAtUtc = endedAt };
        }
    }

    private SessionSnapshot BuildSnapshot(DateTimeOffset now)
    {
        var remaining = Phase is SessionPhase.Running or SessionPhase.Resting
            ? Math.Max(0, (int)Math.Ceiling((_deadline - now).TotalSeconds))
            : 0;
        return new(
            _sessionId,
            Phase,
            _goal,
            _plannedSeconds,
            remaining,
            _level,
            _availability,
            _activityText,
            _connectionMessage);
    }

    private void RaiseSnapshot() => SnapshotChanged?.Invoke(this, BuildSnapshot(DateTimeOffset.UtcNow));

    private static string BuildLocalSummary(SessionSummary summary)
    {
        var focusedMinutes = summary.FocusedSeconds / 60.0;
        var coverage = summary.ObservationCoverage * 100;
        return $"本轮围绕“{summary.Goal}”进行了 {summary.ActualSeconds / 60.0:0.#} 分钟。" +
               $"其中确认专注约 {focusedMinutes:0.#} 分钟，监督覆盖率为 {coverage:0}%。" +
               "云端复盘暂时不可用，但计时和本地统计已经完整保存。下一轮可以继续使用同一目标，观察专注比例是否提升。";
    }

    private static string SanitizeError(string message)
    {
        var oneLine = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= 160 ? oneLine : oneLine[..160];
    }

    private void ClearTransientObservationData()
    {
        _distractedActivities.Clear();
        _activityText = "";
        _lastReminder = "";
        if (_lastAnalyzedHash is not null)
        {
            Array.Clear(_lastAnalyzedHash);
            _lastAnalyzedHash = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_lifetimeCts is not null)
        {
            await _lifetimeCts.CancelAsync();
        }
        if (_breakCts is not null)
        {
            await _breakCts.CancelAsync();
        }
        var tasks = new[] { _tickerTask, _observationTimerTask, _observationTask, _breakTask }
            .Where(task => task is not null)
            .Cast<Task>();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // All background tasks are observed here. Their failures have already
            // been converted into session availability where applicable.
        }
        _lifetimeCts?.Dispose();
        _breakCts?.Dispose();
        ClearTransientObservationData();
        _completionGate.Dispose();
    }
}
