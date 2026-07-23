using System.Windows;
using System.Windows.Interop;
using Vigil.Infrastructure;

namespace Vigil.App;

public partial class OverlayWindow : Window
{
    private readonly Action _mute;

    public OverlayWindow(Action mute)
    {
        _mute = mute;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            if (!WindowCaptureProtection.Exclude(new WindowInteropHelper(this).Handle))
            {
                throw new InvalidOperationException("无法将遮罩窗口排除出截图。");
            }
        };
    }

    public void SetContent(string goal, string reminder)
    {
        GoalText.Text = "当前承诺：" + goal;
        ReminderText.Text = reminder;
        // WPF coordinates are device-independent units. The primary display always
        // starts at (0, 0), so these values remain correct at non-100% DPI.
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
    }

    private void Return_Click(object sender, RoutedEventArgs e) => Close();

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        _mute();
        Close();
    }
}
