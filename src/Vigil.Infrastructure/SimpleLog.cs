namespace Vigil.Infrastructure;

public static class SimpleLog
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task WriteAsync(string category, string message)
    {
        AppPaths.EnsureCreated();
        var sanitized = message.Replace('\r', ' ').Replace('\n', ' ');
        if (sanitized.Length > 500)
        {
            sanitized = sanitized[..500];
        }

        await Gate.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(
                AppPaths.LogFile,
                $"{DateTimeOffset.UtcNow:O}\t{category}\t{sanitized}{Environment.NewLine}");
            TrimIfNeeded();
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void TrimIfNeeded()
    {
        var file = new FileInfo(AppPaths.LogFile);
        if (!file.Exists || file.Length < 1_000_000)
        {
            return;
        }
        var lines = File.ReadLines(AppPaths.LogFile).TakeLast(2_000).ToArray();
        File.WriteAllLines(AppPaths.LogFile, lines);
    }
}
