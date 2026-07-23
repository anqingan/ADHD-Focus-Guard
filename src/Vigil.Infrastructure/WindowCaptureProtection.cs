using System.Runtime.InteropServices;

namespace Vigil.Infrastructure;

public static class WindowCaptureProtection
{
    private const uint WdaExcludeFromCapture = 0x00000011;

    public static bool Exclude(IntPtr windowHandle) =>
        windowHandle != IntPtr.Zero && SetWindowDisplayAffinity(windowHandle, WdaExcludeFromCapture);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
}
