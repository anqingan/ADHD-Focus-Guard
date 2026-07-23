using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Vigil.Core;
using Vigil.Infrastructure;
using Brushes = System.Windows.Media.Brushes;

namespace Vigil.App;

public partial class CapsuleWindow : Window
{
    public CapsuleWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            if (!WindowCaptureProtection.Exclude(new WindowInteropHelper(this).Handle))
            {
                throw new InvalidOperationException("无法将提醒窗口排除出截图。");
            }
        };
    }

    public void SetContent(FocusLevel level, string message)
    {
        TitleText.Text = level switch
        {
            FocusLevel.Wandering => "轻微走神",
            FocusLevel.Distracted => "回来一下",
            FocusLevel.Away => "Vigil 还在等你",
            _ => "Vigil"
        };
        MessageText.Text = message;
        StateDot.Fill = level switch
        {
            FocusLevel.Wandering => Brushes.Goldenrod,
            FocusLevel.Distracted => Brushes.OrangeRed,
            FocusLevel.Away => Brushes.SlateGray,
            _ => Brushes.ForestGreen
        };
        PositionTopCenter();
    }

    public void SetBreakContent(string message)
    {
        TitleText.Text = "休息结束";
        MessageText.Text = message;
        StateDot.Fill = Brushes.ForestGreen;
        PositionTopCenter();
    }

    private void PositionTopCenter()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + 14;
    }
}
