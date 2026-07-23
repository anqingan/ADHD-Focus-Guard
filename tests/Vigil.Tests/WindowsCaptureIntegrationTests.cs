using Vigil.Infrastructure;
using System.Runtime.InteropServices;

namespace Vigil.Tests;

public sealed class WindowsCaptureIntegrationTests
{
    [Fact]
    public async Task CapturePrimary_ReturnsJpegAnd256BitHashWithoutWritingImageFile()
    {
        var before = Directory.Exists(AppPaths.Root)
            ? Directory.GetFiles(AppPaths.Root, "*.*", SearchOption.AllDirectories)
                .Where(IsImage).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        using var frame = await new GdiScreenCaptureService().CapturePrimaryAsync(CancellationToken.None);

        Assert.True(frame.Jpeg.Length > 4);
        Assert.Equal(0xFF, frame.Jpeg.Span[0]);
        Assert.Equal(0xD8, frame.Jpeg.Span[1]);
        Assert.Equal(32, frame.Hash.Length);
        var after = Directory.Exists(AppPaths.Root)
            ? Directory.GetFiles(AppPaths.Root, "*.*", SearchOption.AllDirectories).Where(IsImage)
            : [];
        Assert.Empty(after.Except(before, StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsImage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp";

    [Fact]
    public void ActivityContext_RepeatedReadsDoNotLeakProcessHandles()
    {
        var service = new WindowsActivityContextService();
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        for (var i = 0; i < 100; i++) _ = service.GetCurrent();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        current.Refresh();
        var before = current.HandleCount;

        for (var i = 0; i < 1_000; i++) _ = service.GetCurrent();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        current.Refresh();

        Assert.InRange(current.HandleCount - before, -5, 10);
    }

    [Fact]
    public async Task CapturePrimary_RepeatedCapturesDoNotLeakGdiHandles()
    {
        var service = new GdiScreenCaptureService();
        using (await service.CapturePrimaryAsync(CancellationToken.None)) { }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        var before = GetGuiResources(current.Handle, 0);

        for (var i = 0; i < 20; i++)
        {
            using var frame = await service.CapturePrimaryAsync(CancellationToken.None);
            Assert.Equal(32, frame.Hash.Length);
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var after = GetGuiResources(current.Handle, 0);

        Assert.InRange(after - before, 0, 3);
    }

    [DllImport("user32.dll")]
    private static extern int GetGuiResources(IntPtr process, int flags);
}
