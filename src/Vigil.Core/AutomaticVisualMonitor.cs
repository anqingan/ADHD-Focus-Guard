namespace Vigil.Core;

public sealed class AutomaticVisualMonitor : IAsyncDisposable
{
    private readonly IFocusAiClient _ai;
    private readonly IScreenCaptureService _capture;
    private readonly IActivityContextService _activity;
    private readonly IIdleService _idle;
    private readonly IPersonalDataRepository _repository;
    private readonly Func<bool> _isManualSessionRunning;
    private readonly FocusEngineOptions _options;
    private readonly IAiBudgetTracker? _budget;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private Task? _loop;
    private Task? _request;
    private bool _pending;
    private bool _active;
    private bool _captureAllowed;
    private long _generation;
    private byte[]? _lastHash;
    private DateTimeOffset? _lastAnalysis;

    public AutomaticVisualMonitor(IFocusAiClient ai, IScreenCaptureService capture, IActivityContextService activity,
        IIdleService idle, IPersonalDataRepository repository,
        Func<bool> isManualSessionRunning, IAiBudgetTracker? budget = null, FocusEngineOptions? options = null)
    {
        _ai = ai; _capture = capture; _activity = activity; _idle = idle; _repository = repository;
        _isManualSessionRunning = isManualSessionRunning; _options = options ?? new FocusEngineOptions();
        _budget = budget;
    }

    public string StatusText { get; private set; } = "自动视觉识别待命";
    public event EventHandler<string>? StatusChanged;

    public void Start() { if (_loop is null) _loop = RunAsync(_lifetime.Token); }
    public void SetCaptureAllowed(bool allowed) { lock (_gate) _captureAllowed = allowed; if (!allowed) SetStatus("Windows 无法排除 Vigil 窗口，自动截图已禁用"); }

    public void SetActive(bool active)
    {
        lock (_gate)
        {
            if (_active == active) return; _active = active; _generation++; _pending = false;
            if (_lastHash is not null) { Array.Clear(_lastHash); _lastHash = null; }
            _lastAnalysis = null;
        }
        SetStatus(active ? "已进入自动视觉识别" : "自动视觉识别待命");
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.ObservationInterval);
        try { while (await timer.WaitForNextTickAsync(cancellationToken)) RequestObservation(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void RequestObservation(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_active || !_captureAllowed || _isManualSessionRunning()) return;
            if (_request is { IsCompleted: false }) { _pending = true; return; }
            var generation = _generation; _request = RunChainAsync(generation, cancellationToken);
        }
    }

    private async Task RunChainAsync(long generation, CancellationToken cancellationToken)
    {
        while (true)
        {
            await ObserveOnceAsync(generation, cancellationToken);
            lock (_gate)
            {
                if (_pending && _active && generation == _generation && !cancellationToken.IsCancellationRequested) { _pending = false; continue; }
                _pending = false; return;
            }
        }
    }

    private async Task ObserveOnceAsync(long generation, CancellationToken cancellationToken)
    {
        if (_idle.GetIdleDuration() >= _options.IdleThreshold) { SetStatus("用户离开，停止截图"); return; }
        if (_budget is not null && !await _budget.CanUseAutomaticAiAsync(cancellationToken)) { SetStatus("已达到每日 AI 预算，自动视觉识别暂停"); return; }
        try
        {
            using var frame = await _capture.CapturePrimaryAsync(cancellationToken); var now = DateTimeOffset.UtcNow;
            bool shouldAnalyze; lock (_gate) { shouldAnalyze = _lastHash is null || DHash.Distance(_lastHash, frame.Hash.Span) >= _options.DHashThreshold || _lastAnalysis is null || now - _lastAnalysis >= _options.MaxAiInterval; }
            if (!shouldAnalyze) return;
            var goals = await _repository.GetGoalsAsync(false, cancellationToken);
            var goalText = string.Join("\n", goals.Select(g => $"[{g.Horizon}] {g.Title}：{g.ExpectedOutcome}"));
            if (string.IsNullOrWhiteSpace(goalText)) goalText = "用户尚未填写有效目标，只需客观描述当前活动，不做强提醒。";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(_options.AiTimeout);
            var judgment = await _ai.AnalyzeFrameAsync(goalText, _activity.GetCurrent(), frame.Jpeg, timeout.Token);
            lock (_gate)
            {
                if (!_active || generation != _generation) return;
                if (_lastHash is not null) Array.Clear(_lastHash); _lastHash = frame.Hash.ToArray(); _lastAnalysis = now;
            }
            SetStatus($"视觉判断：{judgment.Level} · {judgment.Activity}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { SetStatus("视觉识别暂不可用：" + Sanitize(ex.Message)); }
    }

    private void SetStatus(string value) { StatusText = value; StatusChanged?.Invoke(this, value); }
    private static string Sanitize(string value) { var one = value.Replace('\r', ' ').Replace('\n', ' ').Trim(); return one.Length <= 160 ? one : one[..160]; }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync(); Task? loop; Task? request; lock (_gate) { _active = false; _generation++; loop = _loop; request = _request; }
        try { await Task.WhenAll(new[] { loop, request }.Where(t => t is not null).Cast<Task>()); } catch (OperationCanceledException) { }
        if (_lastHash is not null) Array.Clear(_lastHash); _lifetime.Dispose();
    }
}
