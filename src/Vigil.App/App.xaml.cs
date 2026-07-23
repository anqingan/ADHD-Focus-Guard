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
    private System.Drawing.Icon? _applicationIcon;

    public FocusSessionCoordinator Coordinator { get; private set; } = null!;
    public ISessionRepository Repository { get; private set; } = null!;
    public IAppSettingsStore Settings { get; private set; } = null!;
    public IFocusAiClient AiClient { get; private set; } = null!;
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

            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
            };
            _httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            AiClient = new OpenAiCompatibleClient(_httpClient, Settings);
            _trayIcon = CreateTrayIcon();
            _reminders = new WindowsReminderService(_trayIcon, () => Coordinator.MuteCurrentDistraction());
            Coordinator = new FocusSessionCoordinator(
                AiClient,
                new GdiScreenCaptureService(),
                new WindowsActivityContextService(),
                new WindowsIdleService(),
                Repository,
                _reminders);

            _mainWindow = new MainWindow(Coordinator, Repository, Settings, AiClient);
            MainWindow = _mainWindow;
            _mainWindow.Show();
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
        if (_mainWindow is null)
        {
            return;
        }
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
        else if (Coordinator.Phase == SessionPhase.Resting)
        {
            await Coordinator.StopBreakAsync();
            ShowMainWindow();
        }
    }

    public async Task ExitAsync()
    {
        if (IsExiting)
        {
            return;
        }
        IsExiting = true;
        try
        {
            if (Coordinator.Phase == SessionPhase.Running)
            {
                await Coordinator.StopAsync();
            }
            else if (Coordinator.Phase == SessionPhase.Resting)
            {
                await Coordinator.StopBreakAsync();
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
        _reminders?.Dispose();
        _trayIcon?.ContextMenuStrip?.Dispose();
        _trayIcon?.Dispose();
        _applicationIcon?.Dispose();
        _httpClient?.Dispose();
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
        menu.Items.Add("结束当前专注/休息", null, async (_, _) =>
            await Dispatcher.InvokeAsync(StopCurrentAsync).Task.Unwrap());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) =>
            await Dispatcher.InvokeAsync(ExitAsync).Task.Unwrap());
        icon.ContextMenuStrip = menu;
        return icon;
    }
}
