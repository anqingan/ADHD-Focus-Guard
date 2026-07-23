using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Vigil.App;

internal sealed class GlobalHotKey : IDisposable
{
    private const int HotKeyId = 0x5647;
    private const int WmHotKey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint VkSpace = 0x20;

    private readonly IntPtr _handle;
    private readonly HwndSource _source;
    private readonly Action _action;
    private bool _registered;
    private bool _disposed;

    public GlobalHotKey(System.Windows.Window window, Action action)
    {
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle) ?? throw new InvalidOperationException("无法注册全局快捷键。");
        _action = action;
        _source.AddHook(WindowProc);
        if (!RegisterHotKey(_handle, HotKeyId, ModControl | ModAlt | ModShift, VkSpace))
        {
            _source.RemoveHook(WindowProc);
            throw new InvalidOperationException("Ctrl+Alt+Shift+Space 已被其他应用占用。");
        }
        _registered = true;
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && wParam.ToInt32() == HotKeyId)
        {
            _action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_registered)
        {
            _ = UnregisterHotKey(_handle, HotKeyId);
            _registered = false;
        }
        _source.RemoveHook(WindowProc);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
