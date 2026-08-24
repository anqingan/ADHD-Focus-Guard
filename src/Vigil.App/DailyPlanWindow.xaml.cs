using System.Windows;
using System.Windows.Interop;
using Vigil.Infrastructure;

namespace Vigil.App;

public partial class DailyPlanWindow : Window
{
    public DailyPlanWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowCaptureProtection.Exclude(new WindowInteropHelper(this).Handle);
    }

    public string Goal { get; private set; } = "";

    public int? SnoozeMinutes { get; private set; }

    public void SetSuggestion(string suggestion)
    {
        if (!string.IsNullOrWhiteSpace(suggestion))
        {
            AiSuggestionText.Text = "AI 建议：" + suggestion;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var goal = GoalText.Text.Trim();
        if (goal.Length == 0)
        {
            System.Windows.MessageBox.Show(
                "请填写今日目标，或者选择稍后提醒。",
                "今日目标",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        Goal = goal;
        DialogResult = true;
    }

    private void Snooze_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string value }
            && int.TryParse(value, out var minutes))
        {
            SnoozeMinutes = minutes;
        }

        DialogResult = false;
    }
}
