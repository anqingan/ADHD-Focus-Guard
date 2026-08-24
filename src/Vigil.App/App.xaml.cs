using System.Net.Http;
using System.Windows;
using Vigil.Core;
using Vigil.Infrastructure;
using MessageBox = System.Windows.MessageBox;

namespace Vigil.App;

public partial class App : System.Windows.Application
{
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private HttpClient? _httpClient;
    private WindowsReminderService? _reminders;
    private ActivityTrackingService? _tracker;
    private AutomaticVisualMonitor? _visualMonitor;
    private ActivityBatchClassificationService? _batchClassifier;
    private ReportGenerationService? _reportGenerator;
    private AiBudgetTracker? _budget;
    private LocalWebServer? _webServer;
    private System.Drawing.Icon? _applicationIcon;

    public FocusSessionCoordinator Coordinator { get; private set; } = null!;
    public ISessionRepository Repository { get; private set; } = null!;
    public IAppSettingsStore Settings { get; private set; } = null!;
    public IFocusAiClient AiClient { get; private set; } = null!;
    public IPersonalDataRepository PersonalRepository { get; private set; } = null!;
    public bool IsExiting { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            AppPaths.EnsureCreated();
            Settings = new JsonSettingsStore();
            Repository = new SqliteSessionRepository();
            await Repository.InitializeAsync();
            await Repository.MarkRunningSessionsInterruptedAsync();
            PersonalRepository = new SqlitePersonalDataRepository();
            await PersonalRepository.InitializeAsync();
            await DailyGoalRollover.ExpireAsync(PersonalRepository, DateTimeOffset.Now);

            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
            };
            _httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            _budget = new AiBudgetTracker();
            _budget.BudgetReached += Budget_BudgetReached;
            AiClient = new OpenAiCompatibleClient(_httpClient, Settings, _budget);
            var personalAi = new DeepSeekPersonalAiService(_httpClient, Settings, _budget);
            _batchClassifier = new ActivityBatchClassificationService(PersonalRepository, personalAi, _budget);
            _reportGenerator = new ReportGenerationService(PersonalRepository, personalAi, _budget);
            var automaticReminderLimiter = new AutomaticReminderLimiter();
            _tracker = new ActivityTrackingService(new ActivityWatchClient(_httpClient), PersonalRepository, automaticReminderLimiter);
            _trayIcon = CreateTrayIcon();
            _reminders = new WindowsReminderService(_trayIcon, () =>
            {
                Coordinator.MuteCurrentDistraction();
            });
            Coordinator = new FocusSessionCoordinator(
                AiClient,
                new GdiScreenCaptureService(),
                new WindowsActivityContextService(),
                new WindowsIdleService(),
                Repository,
                _reminders);
            _visualMonitor = new AutomaticVisualMonitor(
                AiClient,
                new GdiScreenCaptureService(),
                new WindowsActivityContextService(),
                new WindowsIdleService(),
                PersonalRepository,
                () => Coordinator.Phase == SessionPhase.Running,
                _budget);

            _mainWindow = new MainWindow(Coordinator, Repository, Settings, AiClient, PersonalRepository, _tracker, personalAi, _visualMonitor);
            MainWindow = _mainWindow;
            _mainWindow.Show();
            _mainWindow.Hide();
            _tracker.GentleReminder += Tracker_GentleReminder;
            _tracker.ActiveModeChanged += Tracker_ActiveModeChanged;
            _tracker.Start();
            _visualMonitor.Start();
            _batchClassifier.Start();
            _reportGenerator.Start();
            _webServer = new LocalWebServer(
                PersonalRepository,
                Repository,
                Settings,
                AiClient,
                personalAi,
                _budget,
                _tracker,
                _visualMonitor,
                Coordinator);
            await _webServer.StartAsync();
            _webServer.OpenInBrowser();
        }
        catch (Exception ex)
        {
            await SimpleLog.WriteAsync("startup", ex.GetType().Name + ": " + ex.Message);
            MessageBox.Show($"ADHD Focus Guard 启动失败：{ex.Message}", "ADHD Focus Guard", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    public void ShowMainWindow()
    {
        if (_webServer is not null)
        {
            _webServer.OpenInBrowser();
            return;
        }
        if (_mainWindow is null) return;
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        _mainWindow.FocusGoalInput();
    }

    public async Task StopCurrentAsync()
    {
        if (Coordinator.Phase == SessionPhase.Running)
        {
            await Coordinator.StopAsync();
        }
    }

    public async Task ExitAsync()
    {
        if (IsExiting)
        {
            return;
        }
        IsExiting = true;
        if (_webServer is not null)
        {
            try { await _webServer.DisposeAsync(); } catch { }
        }
        try
        {
            if (Coordinator.Phase == SessionPhase.Running)
            {
                await Coordinator.StopAsync();
            }
        }
        catch
        {
        }
        try
        {
            await Coordinator.DisposeAsync();
        }
        catch
        {
        }
        if (_tracker is not null)
        {
            _tracker.GentleReminder -= Tracker_GentleReminder;
            _tracker.ActiveModeChanged -= Tracker_ActiveModeChanged;
            try { await _tracker.DisposeAsync(); } catch { }
        }
        if (_visualMonitor is not null)
        {
            try { await _visualMonitor.DisposeAsync(); } catch { }
        }
        if (_batchClassifier is not null)
        {
            try { await _batchClassifier.DisposeAsync(); } catch { }
        }
        if (_reportGenerator is not null)
        {
            try { await _reportGenerator.DisposeAsync(); } catch { }
        }
        _reminders?.Dispose();
        _trayIcon?.ContextMenuStrip?.Dispose();
        _trayIcon?.Dispose();
        _applicationIcon?.Dispose();
        _httpClient?.Dispose();
        if (_budget is not null) _budget.BudgetReached -= Budget_BudgetReached;
        Shutdown();
    }

    private System.Windows.Forms.NotifyIcon CreateTrayIcon()
    {
        _applicationIcon = System.Drawing.Icon.ExtractAssociatedIcon(
            Environment.ProcessPath ?? throw new InvalidOperationException("找不到应用程序路径。"));
        var icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _applicationIcon ?? System.Drawing.SystemIcons.Shield,
            Text = "ADHD 专注守护",
            Visible = true
        };
        icon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindow);

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("显示 ADHD 专注守护", null, (_, _) => Dispatcher.Invoke(ShowMainWindow));
        menu.Items.Add("结束当前专注", null, async (_, _) =>
            await Dispatcher.InvokeAsync(StopCurrentAsync).Task.Unwrap());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) =>
            await Dispatcher.InvokeAsync(ExitAsync).Task.Unwrap());
        icon.ContextMenuStrip = menu;
        return icon;
    }

    private void Tracker_GentleReminder(object? sender, string message)
    {
        if (_reminders is null) return;
        Dispatcher.Invoke(() =>
        {
            _reminders.Handle(new ReminderRequest(ReminderKind.Capsule, FocusLevel.Wandering, message, ""));
        });
    }

    private void Tracker_ActiveModeChanged(object? sender, bool active) => _visualMonitor?.SetActive(active);

    private void Budget_BudgetReached(object? sender, AiBudgetSnapshot snapshot)
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            var result = MessageBox.Show(
                $"今天的 AI 估算费用已达到 {snapshot.EstimatedCny:0.00} 元。\n\n选择“是”表示今天继续自动视觉识别和分类；选择“否”表示暂停自动 AI，本地记录不会停止。",
                "Vigil · 每日 AI 预算",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (_budget is null) return;
            if (result == MessageBoxResult.Yes) await _budget.ContinueTodayAsync();
            else await _budget.PauseTodayAsync();
        });
    }
}
