using System.Media;
using System.Windows;
using Vigil.Core;
using Application = System.Windows.Application;

namespace Vigil.App;

public sealed class WindowsReminderService : IReminderService, IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _tray;
    private readonly Action _mute;
    private CapsuleWindow? _capsule;
    private OverlayWindow? _overlay;
    private CancellationTokenSource? _capsuleTimer;
    private CancellationTokenSource? _overlayTimer;
    private bool _disposed;

    public WindowsReminderService(System.Windows.Forms.NotifyIcon tray, Action mute)
    {
        _tray = tray;
        _mute = mute;
    }

    public void Handle(ReminderRequest request)
    {
        if (_disposed)
        {
            return;
        }
        var dispatcher = Application.Current.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => Handle(request));
            return;
        }

        switch (request.Kind)
        {
            case ReminderKind.Capsule:
                ShowCapsule(request);
                break;
            case ReminderKind.Tray:
                _tray.BalloonTipTitle = "Vigil · 回到目标";
                _tray.BalloonTipText = request.Message;
                _tray.ShowBalloonTip(8_000);
                break;
            case ReminderKind.AutomaticTray:
                _tray.BalloonTipTitle = "Vigil · 活动切换提醒";
                _tray.BalloonTipText = request.Message;
                _tray.ShowBalloonTip(8_000);
                break;
            case ReminderKind.Sound:
                SystemSounds.Exclamation.Play();
                break;
            case ReminderKind.FullScreenOverlay:
                ShowOverlay(request);
                break;
            case ReminderKind.HideSoftReminder:
                CloseCapsule();
                break;
        }
    }

    public void CloseAll()
    {
        var dispatcher = Application.Current.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.Invoke(CloseAll);
            return;
        }
        CloseCapsule();
        CloseOverlay();
    }

    private void ShowCapsule(ReminderRequest request)
    {
        CloseCapsule();
        var window = new CapsuleWindow();
        _capsule = window;
        window.Closed += (_, _) => ReleaseCapsule(window);
        window.SetContent(request.Level, request.Message);
        window.Show();
        _capsuleTimer = new CancellationTokenSource();
        _ = CloseLaterAsync(window, TimeSpan.FromSeconds(8), _capsuleTimer.Token);
    }

    private void ShowOverlay(ReminderRequest request)
    {
        CloseOverlay();
        var window = new OverlayWindow(_mute);
        _overlay = window;
        window.Closed += (_, _) => ReleaseOverlay(window);
        window.SetContent(request.Goal, request.Message);
        window.Show();
        window.Activate();
        _overlayTimer = new CancellationTokenSource();
        _ = CloseLaterAsync(window, TimeSpan.FromSeconds(15), _overlayTimer.Token);
    }

    private async Task CloseLaterAsync(Window window, TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            await window.Dispatcher.InvokeAsync(() =>
            {
                if (window.IsVisible)
                {
                    window.Close();
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CloseCapsule()
    {
        var window = _capsule;
        _capsule = null;
        _capsuleTimer?.Cancel();
        _capsuleTimer?.Dispose();
        _capsuleTimer = null;
        window?.Close();
    }

    private void ReleaseCapsule(CapsuleWindow window)
    {
        if (!ReferenceEquals(_capsule, window))
        {
            return;
        }
        _capsule = null;
        _capsuleTimer?.Cancel();
        _capsuleTimer?.Dispose();
        _capsuleTimer = null;
    }

    private void CloseOverlay()
    {
        var window = _overlay;
        _overlay = null;
        _overlayTimer?.Cancel();
        _overlayTimer?.Dispose();
        _overlayTimer = null;
        window?.Close();
    }

    private void ReleaseOverlay(OverlayWindow window)
    {
        if (!ReferenceEquals(_overlay, window))
        {
            return;
        }
        _overlay = null;
        _overlayTimer?.Cancel();
        _overlayTimer?.Dispose();
        _overlayTimer = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        CloseAll();
        _disposed = true;
    }
}
