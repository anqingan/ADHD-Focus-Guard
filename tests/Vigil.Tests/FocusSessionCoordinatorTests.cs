using System.Collections.Concurrent;
using Vigil.Core;

namespace Vigil.Tests;

public sealed class FocusSessionCoordinatorTests
{
    [Fact]
    public async Task ObservationScheduler_AllowsOnlyOneAiCallAndCollapsesPendingTicks()
    {
        var ai = new SlowAi(TimeSpan.FromMilliseconds(35), honorCancellation: true);
        var repository = new MemoryRepository();
        var coordinator = CreateCoordinator(ai, repository, new MemoryReminder(), new FocusEngineOptions
        {
            TickInterval = TimeSpan.FromMilliseconds(10),
            ObservationInterval = TimeSpan.FromMilliseconds(5),
            MaxAiInterval = TimeSpan.Zero,
            RetryInterval = TimeSpan.Zero,
            AiTimeout = TimeSpan.FromSeconds(1),
            DHashThreshold = 0
        });

        await coordinator.StartAsync("测试 latest only", 1);
        await Task.Delay(150);
        await coordinator.StopAsync();

        Assert.Equal(1, ai.MaxConcurrent);
        Assert.InRange(ai.AnalyzeCalls, 2, 6);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task LateDistractedResult_AfterStopDoesNotTriggerReminder()
    {
        var ai = new SlowAi(TimeSpan.FromMilliseconds(90), honorCancellation: false, FocusLevel.Distracted);
        var reminders = new MemoryReminder();
        var coordinator = CreateCoordinator(ai, new MemoryRepository(), reminders, new FocusEngineOptions
        {
            TickInterval = TimeSpan.FromMilliseconds(10),
            ObservationInterval = TimeSpan.FromMilliseconds(10),
            MaxAiInterval = TimeSpan.Zero,
            RetryInterval = TimeSpan.Zero,
            AiTimeout = TimeSpan.FromSeconds(1),
            DHashThreshold = 0
        });

        await coordinator.StartAsync("迟到结果", 1);
        await ai.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await coordinator.StopAsync();
        await Task.Delay(120);

        Assert.Empty(reminders.Items);
        Assert.Equal(SessionPhase.Completed, coordinator.Phase);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task AiFailure_DoesNotStopTimerAndUsesLocalSummary()
    {
        var coordinator = CreateCoordinator(
            new FailingAi(),
            new MemoryRepository(),
            new MemoryReminder(),
            new FocusEngineOptions
            {
                TickInterval = TimeSpan.FromMilliseconds(10),
                ObservationInterval = TimeSpan.FromMilliseconds(10),
                MaxAiInterval = TimeSpan.FromMilliseconds(20),
                RetryInterval = TimeSpan.FromMilliseconds(20),
                AiTimeout = TimeSpan.FromMilliseconds(20)
            });

        await coordinator.StartAsync("断网继续", 1);
        await Task.Delay(50);
        Assert.Equal(SessionPhase.Running, coordinator.Phase);
        await coordinator.StopAsync();

        Assert.Equal(SessionPhase.Completed, coordinator.Phase);
        Assert.Contains("云端复盘暂时不可用", coordinator.LastCompleted!.SummaryText);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task CallerCancellation_DuringFinalizationDoesNotLeaveSummarizingState()
    {
        var coordinator = CreateCoordinator(
            new DelayedSummaryAi(), new MemoryRepository(), new MemoryReminder(),
            new FocusEngineOptions { TickInterval = TimeSpan.FromMilliseconds(5) });
        await coordinator.StartAsync("可靠停止", 1);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(5));

        await coordinator.StopAsync(cancellation.Token);

        Assert.Equal(SessionPhase.Completed, coordinator.Phase);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task StaleAiResult_BecomesUnavailableWhileNextRequestIsPending()
    {
        var ai = new FirstThenBlockingAi();
        var coordinator = CreateCoordinator(ai, new MemoryRepository(), new MemoryReminder(), new FocusEngineOptions
        {
            TickInterval = TimeSpan.FromMilliseconds(5),
            ObservationInterval = TimeSpan.FromMilliseconds(5),
            MaxAiInterval = TimeSpan.FromMilliseconds(20),
            RetryInterval = TimeSpan.Zero,
            AiTimeout = TimeSpan.FromSeconds(1),
            DHashThreshold = 0
        });
        await coordinator.StartAsync("陈旧结果", 1);
        await ai.FirstCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await ai.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await WaitUntilAsync(
            () => coordinator.Snapshot.Availability == ObservationAvailability.Unavailable,
            TimeSpan.FromSeconds(1));

        await coordinator.StopAsync();
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task TransientPersistenceFailure_DoesNotStopSessionTicker()
    {
        var repository = new FailFirstUpdateRepository();
        var coordinator = CreateCoordinator(new SlowAi(TimeSpan.FromMilliseconds(2), true), repository,
            new MemoryReminder(), new FocusEngineOptions
            {
                TickInterval = TimeSpan.FromMilliseconds(5),
                ObservationInterval = TimeSpan.FromMilliseconds(10),
                PersistenceInterval = TimeSpan.FromMilliseconds(10),
                MaxAiInterval = TimeSpan.FromMilliseconds(20),
                RetryInterval = TimeSpan.Zero,
                AiTimeout = TimeSpan.FromSeconds(1),
                DHashThreshold = 0
            });
        await coordinator.StartAsync("持久化重试", 1);
        await WaitUntilAsync(() => repository.UpdateCalls >= 2, TimeSpan.FromSeconds(1));

        Assert.Equal(SessionPhase.Running, coordinator.Phase);
        await coordinator.StopAsync();
        Assert.Equal(SessionPhase.Completed, coordinator.Phase);
        await coordinator.DisposeAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException();
            await Task.Delay(5);
        }
    }

    private static FocusSessionCoordinator CreateCoordinator(
        IFocusAiClient ai,
        ISessionRepository repository,
        IReminderService reminders,
        FocusEngineOptions options) => new(
            ai,
            new IncrementingCapture(),
            new FixedActivity(),
            new FixedIdle(),
            repository,
            reminders,
            options);

    private sealed class IncrementingCapture : IScreenCaptureService
    {
        private int _value;
        public Task<CapturedFrame> CapturePrimaryAsync(CancellationToken cancellationToken)
        {
            var value = (byte)Interlocked.Increment(ref _value);
            return Task.FromResult(new CapturedFrame([1, 2, 3], Enumerable.Repeat(value, 32).ToArray()));
        }
    }

    private sealed class FixedActivity : IActivityContextService
    {
        public ActivityContext GetCurrent() => new("test", "window");
    }

    private sealed class FixedIdle : IIdleService
    {
        public TimeSpan GetIdleDuration() => TimeSpan.Zero;
    }

    private sealed class MemoryReminder : IReminderService
    {
        public ConcurrentQueue<ReminderRequest> Items { get; } = new();
        public void Handle(ReminderRequest request) => Items.Enqueue(request);
        public void CloseAll() { }
    }

    private class MemoryRepository : ISessionRepository
    {
        private readonly ConcurrentDictionary<Guid, SessionSummary> _items = new();
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MarkRunningSessionsInterruptedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CreateAsync(SessionSummary session, CancellationToken cancellationToken = default)
        {
            _items[session.Id] = session;
            return Task.CompletedTask;
        }
        public virtual Task UpdateAsync(SessionSummary session, CancellationToken cancellationToken = default)
        {
            _items[session.Id] = session;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<SessionSummary>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionSummary>>(_items.Values.ToList());
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _items.TryRemove(id, out _);
            return Task.CompletedTask;
        }
        public Task DeleteAllAsync(CancellationToken cancellationToken = default)
        {
            _items.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed class SlowAi(TimeSpan delay, bool honorCancellation, FocusLevel level = FocusLevel.Focused) : IFocusAiClient
    {
        private int _concurrent;
        public int AnalyzeCalls;
        public int MaxConcurrent;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> TestAsync(CancellationToken cancellationToken) => Task.FromResult("ok");

        public async Task<FrameJudgment> AnalyzeFrameAsync(
            string goal, ActivityContext context, ReadOnlyMemory<byte> jpeg, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            Interlocked.Increment(ref AnalyzeCalls);
            var concurrent = Interlocked.Increment(ref _concurrent);
            MaxConcurrent = Math.Max(MaxConcurrent, concurrent);
            try
            {
                await Task.Delay(delay, honorCancellation ? cancellationToken : CancellationToken.None);
                return new FrameJudgment(level, "activity", level == FocusLevel.Distracted ? "return" : "");
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }

        public Task<string> SummarizeAsync(SessionSummary summary, IReadOnlyList<string> distractedActivities, CancellationToken cancellationToken) =>
            Task.FromResult("summary");
    }

    private sealed class FailingAi : IFocusAiClient
    {
        public Task<string> TestAsync(CancellationToken cancellationToken) => throw new HttpRequestException();
        public Task<FrameJudgment> AnalyzeFrameAsync(string goal, ActivityContext context, ReadOnlyMemory<byte> jpeg, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
        public Task<string> SummarizeAsync(SessionSummary summary, IReadOnlyList<string> distractedActivities, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }

    private sealed class DelayedSummaryAi : IFocusAiClient
    {
        public Task<string> TestAsync(CancellationToken cancellationToken) => Task.FromResult("ok");
        public Task<FrameJudgment> AnalyzeFrameAsync(string goal, ActivityContext context,
            ReadOnlyMemory<byte> jpeg, CancellationToken cancellationToken) =>
            Task.FromResult(new FrameJudgment(FocusLevel.Focused, "工作", ""));
        public async Task<string> SummarizeAsync(SessionSummary summary,
            IReadOnlyList<string> distractedActivities, CancellationToken cancellationToken)
        {
            await Task.Delay(30, cancellationToken);
            return "summary";
        }
    }

    private sealed class FirstThenBlockingAi : IFocusAiClient
    {
        private int _calls;
        public TaskCompletionSource FirstCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> TestAsync(CancellationToken cancellationToken) => Task.FromResult("ok");
        public async Task<FrameJudgment> AnalyzeFrameAsync(string goal, ActivityContext context,
            ReadOnlyMemory<byte> jpeg, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstCompleted.TrySetResult();
                return new FrameJudgment(FocusLevel.Focused, "工作", "");
            }
            SecondStarted.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return new FrameJudgment(FocusLevel.Focused, "工作", "");
        }
        public Task<string> SummarizeAsync(SessionSummary summary,
            IReadOnlyList<string> distractedActivities, CancellationToken cancellationToken) => Task.FromResult("summary");
    }

    private sealed class FailFirstUpdateRepository : MemoryRepository
    {
        public int UpdateCalls;
        public override Task UpdateAsync(SessionSummary session, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref UpdateCalls) == 1)
            {
                throw new IOException("transient");
            }
            return base.UpdateAsync(session, cancellationToken);
        }
    }
}
