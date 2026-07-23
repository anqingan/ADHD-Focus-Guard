using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Vigil.Core;

namespace Vigil.Infrastructure;

public sealed class WindowsActivityContextService : IActivityContextService
{
    public Vigil.Core.ActivityContext GetCurrent()
    {
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero)
        {
            return new("Unknown", "");
        }

        var titleLength = GetWindowTextLength(handle);
        var title = new StringBuilder(Math.Max(titleLength + 1, 1));
        _ = GetWindowText(handle, title, title.Capacity);

        _ = GetWindowThreadProcessId(handle, out var processId);
        string processName;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
        }
        catch
        {
            processName = "Unknown";
        }
        return new(processName, Limit(title.ToString(), 500));
    }

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
