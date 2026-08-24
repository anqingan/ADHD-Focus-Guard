using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Vigil.Core;
using Vigil.Infrastructure;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace Vigil.App;

public partial class MainWindow : Window
{
    private readonly FocusSessionCoordinator _coordinator;
    private readonly ISessionRepository _repository;
    private readonly IAppSettingsStore _settings;
    private readonly IFocusAiClient _ai;
    private readonly ObservableCollection<HistoryRow> _history = [];
    private GlobalHotKey? _hotKey;
    private bool _captureProtectionAvailable;

    public MainWindow(
        FocusSessionCoordinator coordinator,
        ISessionRepository repository,
        IAppSettingsStore settings,
        IFocusAiClient ai,
        IPersonalDataRepository personal,
        ActivityTrackingService tracker,
        IPersonalAiService personalAi,
        AutomaticVisualMonitor visualMonitor)
    {
        _coordinator = coordinator;
        _repository = repository;
        _settings = settings;
        _ai = ai;
        InitializeComponent();
        InitializeV2(personal, tracker, personalAi, visualMonitor);
        HistoryList.ItemsSource = _history;
        _coordinator.SnapshotChanged += Coordinator_SnapshotChanged;
        _coordinator.SessionCompleted += Coordinator_SessionCompleted;
        Loaded += MainWindow_Loaded;
        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;
    }

    public void FocusGoalInput()
    {
        GoalInput.Focus();
        GoalInput.SelectAll();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var provider = await _settings.LoadProviderAsync();
        BaseUrlInput.Text = provider.BaseUrl;
        TextModelInput.Text = provider.TextModel;
        ModelInput.Text = provider.Model;
        if (_captureProtectionAvailable)
        {
            SettingsStatusText.Text = provider.HasApiKey ? "已保存加密 API Key。" : "尚未保存 API Key。";
        }
        await ReloadHistoryAsync();
        await LoadV2Async();
        UpdateSnapshot(_coordinator.Snapshot);
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _captureProtectionAvailable = WindowCaptureProtection.Exclude(new WindowInteropHelper(this).Handle);
        _visualMonitor.SetCaptureAllowed(_captureProtectionAvailable);
        if (!_captureProtectionAvailable)
        {
            SettingsStatusText.Text = "Windows 无法将 Vigil 窗口排除出截图，已禁用会话启动。";
            SettingsStatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
        }
        try
        {
            _hotKey = new GlobalHotKey(this, () => ((App)Application.Current).ShowMainWindow());
        }
        catch (Exception ex)
        {
            SettingsStatusText.Text = ex.Message;
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (((App)Application.Current).IsExiting)
        {
            _hotKey?.Dispose();
            _coordinator.SnapshotChanged -= Coordinator_SnapshotChanged;
            _coordinator.SessionCompleted -= Coordinator_SessionCompleted;
            DisposeV2Events();
            return;
        }
        e.Cancel = true;
        Hide();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_captureProtectionAvailable)
            {
                throw new InvalidOperationException("当前系统无法安全排除 Vigil 窗口，不能开始屏幕观察。");
            }
            var provider = await _settings.LoadProviderAsync();
            if (!provider.HasApiKey || string.IsNullOrWhiteSpace(provider.Model))
            {
                throw new InvalidOperationException("请先在“设置”页保存云端模型和 API Key。");
            }
            var durationText = DurationInput.Text.Trim();
            if (!int.TryParse(durationText, out var minutes))
            {
                throw new ArgumentException("请输入有效的分钟数。");
            }
            await _coordinator.StartAsync(GoalInput.Text, minutes);
            Hide();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "无法开始", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _coordinator.StopAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "停止失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();

    private void Again_Click(object sender, RoutedEventArgs e)
    {
        _coordinator.ResetToIdle();
        FocusGoalInput();
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsAsync();
            SettingsStatusText.Text = "配置已保存，API Key 已用当前用户 DPAPI 加密。";
            SettingsStatusText.Foreground = System.Windows.Media.Brushes.ForestGreen;
        }
        catch (Exception ex)
        {
            SettingsStatusText.Text = ex.Message;
            SettingsStatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
        }
    }

    private async void TestSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsAsync();
            SettingsStatusText.Text = "正在测试合成图片识别…";
            SettingsStatusText.Foreground = System.Windows.Media.Brushes.DarkGoldenrod;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await _ai.TestAsync(timeout.Token);
            SettingsStatusText.Text = "视觉测试通过：" + result;
            SettingsStatusText.Foreground = System.Windows.Media.Brushes.ForestGreen;
        }
        catch (Exception ex)
        {
            SettingsStatusText.Text = "测试失败：" + ex.Message;
            SettingsStatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
        }
    }

    private Task SaveSettingsAsync() => _settings.SaveProviderModelsAsync(
        BaseUrlInput.Text,
        TextModelInput.Text,
        ModelInput.Text,
        ApiKeyInput.Password);

    private void Coordinator_SnapshotChanged(object? sender, SessionSnapshot snapshot)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }
        _ = Dispatcher.InvokeAsync(() => UpdateSnapshot(snapshot));
    }

    private async void Coordinator_SessionCompleted(object? sender, SessionSummary summary)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }
        try
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
                await ReloadHistoryAsync();
            }).Task.Unwrap();
        }
        catch (Exception ex) when (!Dispatcher.HasShutdownStarted)
        {
            MessageBox.Show(ex.Message, "读取历史失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdateSnapshot(SessionSnapshot snapshot)
    {
        StartCard.Visibility = snapshot.Phase is SessionPhase.Idle or SessionPhase.FailedToStart
            ? Visibility.Visible
            : Visibility.Collapsed;
        RunningCard.Visibility = snapshot.Phase is SessionPhase.Running or SessionPhase.Summarizing
            ? Visibility.Visible
            : Visibility.Collapsed;
        CompletedCard.Visibility = snapshot.Phase == SessionPhase.Completed
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (snapshot.Phase is SessionPhase.Running or SessionPhase.Summarizing)
        {
            RunningGoalText.Text = snapshot.Goal;
            RemainingText.Text = FormatDuration(snapshot.RemainingSeconds);
            CurrentStateText.Text = snapshot.Phase switch
            {
                SessionPhase.Summarizing => "正在生成复盘…",
                _ => LevelName(snapshot.Level, snapshot.Availability)
            };
            ObservationPanel.Visibility = Visibility.Visible;
            StopButton.Content = "结束本轮";
            ActivityText.Text = string.IsNullOrWhiteSpace(snapshot.Activity) ? "等待下一次有效判断…" : snapshot.Activity;
            ConnectionText.Text = snapshot.ConnectionMessage;
        }

        if (snapshot.Phase == SessionPhase.Completed && _coordinator.LastCompleted is { } completed)
        {
            RatioText.Text = BuildRatioText(completed);
            SummaryText.Text = completed.SummaryText;
        }
    }

    private async Task ReloadHistoryAsync()
    {
        var sessions = await _repository.GetAllAsync();
        _history.Clear();
        foreach (var session in sessions)
        {
            _history.Add(new HistoryRow(session));
        }
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryRow row)
        {
            DeleteSelectedButton.Visibility = Visibility.Collapsed;
            return;
        }
        HistoryGoalText.Text = row.Summary.Goal;
        HistoryMetaText.Text = row.Display + Environment.NewLine + BuildRatioText(row.Summary);
        HistorySummaryText.Text = row.Summary.SummaryText;
        DeleteSelectedButton.Visibility = Visibility.Visible;
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryRow row)
        {
            return;
        }
        await _repository.DeleteAsync(row.Summary.Id);
        await ReloadHistoryAsync();
        HistoryGoalText.Text = "选择一条历史记录";
        HistoryMetaText.Text = "";
        HistorySummaryText.Text = "";
        DeleteSelectedButton.Visibility = Visibility.Collapsed;
    }

    private async void DeleteAll_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show("确定清空全部会话摘要？此操作无法撤销。", "清空历史",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }
        await _repository.DeleteAllAsync();
        await ReloadHistoryAsync();
    }

    private static string LevelName(FocusLevel? level, ObservationAvailability availability)
    {
        if (availability == ObservationAvailability.Unavailable && level != FocusLevel.Away)
        {
            return "暂时无法观察";
        }
        return level switch
        {
            FocusLevel.Focused => "专注中",
            FocusLevel.Wandering => "轻微走神",
            FocusLevel.Distracted => "明确分心",
            FocusLevel.Away => "已离开电脑",
            _ => "等待判断"
        };
    }

    private static string BuildRatioText(SessionSummary session)
    {
        var observed = Math.Max(session.ObservedSeconds, 1);
        return $"专注 {session.FocusedSeconds * 100 / observed}% · " +
               $"走神 {session.WanderingSeconds * 100 / observed}% · " +
               $"分心 {session.DistractedSeconds * 100 / observed}% · " +
               $"离开 {session.AwaySeconds * 100 / observed}% · " +
               $"观察覆盖 {session.ObservationCoverage * 100:0}%";
    }

    private static string FormatDuration(int seconds) => $"{seconds / 60:00}:{seconds % 60:00}";

    private sealed class HistoryRow
    {
        public HistoryRow(SessionSummary summary)
        {
            Summary = summary;
            Display = $"{summary.StartedAtUtc.ToLocalTime():MM-dd HH:mm}  {summary.Goal}\n" +
                      $"{summary.ActualSeconds / 60.0:0.#} 分钟 · {summary.CompletionKind}";
        }

        public SessionSummary Summary { get; }
        public string Display { get; }
        public override string ToString() => Display;
    }
}
