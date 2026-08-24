using System.Globalization;
using System.Net;
using System.Text.Json;
using Vigil.Core;

namespace Vigil.Infrastructure;

public sealed class ActivityWatchClient : IActivityWatchClient
{
    private const long MaxResponseBytes = 1_048_576;
    private static readonly Uri BaseUri = new("http://127.0.0.1:5600/api/0/");
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private string? _windowBucket;
    private string? _afkBucket;
    private string? _webBucket;
    private DateTimeOffset _lastDiscovery;

    public ActivityWatchClient(HttpClient httpClient) => _http = httpClient;

    public async Task<ActivityWatchSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try { return await GetCurrentCoreAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
    }

    private async Task<ActivityWatchSnapshot?> GetCurrentCoreAsync(CancellationToken cancellationToken)
    {
        await EnsureBucketsAsync(cancellationToken);
        if (_windowBucket is null || _afkBucket is null) return null;

        var now = DateTimeOffset.UtcNow;
        var start = now.AddMinutes(-3);
        var windowTask = GetLatestEventAsync(_windowBucket, start, now, cancellationToken);
        var afkTask = GetLatestEventAsync(_afkBucket, start, now, cancellationToken);
        var webTask = _webBucket is null
            ? Task.FromResult<AwEvent?>(null)
            : GetLatestEventAsync(_webBucket, start, now, cancellationToken);
        await Task.WhenAll(windowTask, afkTask, webTask);

        var window = await windowTask;
        var afk = await afkTask;
        if (window is null || afk is null) return null;
        var web = await webTask;

        var app = GetString(window.Data, "app", 200);
        var windowTitle = GetString(window.Data, "title", 2_000);
        var isBrowser = IsBrowser(app);
        var browserTitle = isBrowser && web is not null ? GetString(web.Data, "title", 2_000) : "";
        var domain = isBrowser && web is not null ? SanitizeDomain(GetString(web.Data, "url", 4_096)) : "";
        var status = GetString(afk.Data, "status", 32);
        return new ActivityWatchSnapshot(now, status.Equals("afk", StringComparison.OrdinalIgnoreCase), app, windowTitle, domain, browserTitle);
    }

    private async Task EnsureBucketsAsync(CancellationToken cancellationToken)
    {
        if (_windowBucket is not null && _afkBucket is not null && DateTimeOffset.UtcNow - _lastDiscovery < TimeSpan.FromMinutes(10)) return;
        await _discoveryGate.WaitAsync(cancellationToken);
        try
        {
            if (_windowBucket is not null && _afkBucket is not null && DateTimeOffset.UtcNow - _lastDiscovery < TimeSpan.FromMinutes(10)) return;
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, "buckets/"));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK) return;
            if (response.Content.Headers.ContentLength is > MaxResponseBytes) throw new InvalidDataException("ActivityWatch bucket 响应过大。");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken); await using var limited = new LimitedReadStream(stream, MaxResponseBytes);
            using var json = await JsonDocument.ParseAsync(limited, new JsonDocumentOptions { MaxDepth = 32 }, cancellationToken);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return;

            string? window = null, afk = null, web = null;
            foreach (var bucket in json.RootElement.EnumerateObject())
            {
                var id = bucket.Name;
                var type = bucket.Value.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? "" : "";
                if (window is null && (id.StartsWith("aw-watcher-window_", StringComparison.OrdinalIgnoreCase) || type.Contains("currentwindow", StringComparison.OrdinalIgnoreCase))) window = id;
                if (afk is null && (id.StartsWith("aw-watcher-afk_", StringComparison.OrdinalIgnoreCase) || type.Contains("afkstatus", StringComparison.OrdinalIgnoreCase))) afk = id;
                if (web is null && (id.StartsWith("aw-watcher-web", StringComparison.OrdinalIgnoreCase) || type.Contains("web.tab.current", StringComparison.OrdinalIgnoreCase))) web = id;
            }
            _windowBucket = window;
            _afkBucket = afk;
            _webBucket = web;
            _lastDiscovery = DateTimeOffset.UtcNow;
        }
        catch (HttpRequestException)
        {
            _windowBucket = _afkBucket = _webBucket = null;
        }
        finally { _discoveryGate.Release(); }
    }

    private async Task<AwEvent?> GetLatestEventAsync(string bucket, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        var relative = $"buckets/{Uri.EscapeDataString(bucket)}/events?start={Uri.EscapeDataString(start.ToString("O"))}&end={Uri.EscapeDataString(end.ToString("O"))}&limit=20";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, relative));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        if (response.Content.Headers.ContentLength is > MaxResponseBytes) throw new InvalidDataException("ActivityWatch event 响应过大。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken); await using var limited = new LimitedReadStream(stream, MaxResponseBytes);
        using var json = await JsonDocument.ParseAsync(limited, new JsonDocumentOptions { MaxDepth = 32 }, cancellationToken);
        if (json.RootElement.ValueKind != JsonValueKind.Array) return null;
        AwEvent? latest = null;
        foreach (var item in json.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("timestamp", out var timestamp) || !DateTimeOffset.TryParse(timestamp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) continue;
            if (!item.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) continue;
            if (latest is null || at > latest.At) latest = new(at, data.Clone());
        }
        return latest;
    }

    private static string GetString(JsonElement data, string name, int max)
    {
        if (!data.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return "";
        var text = (value.GetString() ?? "").Trim().Replace('\0', ' ');
        return text.Length <= max ? text : text[..max];
    }

    private static string SanitizeDomain(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.HostNameType == UriHostNameType.Unknown) return "";
        return uri.IdnHost.ToLowerInvariant();
    }

    private static bool IsBrowser(string app) => app.Contains("chrome", StringComparison.OrdinalIgnoreCase)
        || app.Contains("edge", StringComparison.OrdinalIgnoreCase)
        || app.Contains("firefox", StringComparison.OrdinalIgnoreCase)
        || app.Contains("brave", StringComparison.OrdinalIgnoreCase)
        || app.Contains("opera", StringComparison.OrdinalIgnoreCase);

    private sealed record AwEvent(DateTimeOffset At, JsonElement Data);
    private sealed class LimitedReadStream(Stream inner, long maximum) : Stream
    {
        private long _read; public override bool CanRead => inner.CanRead; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => throw new NotSupportedException(); public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { var value = inner.Read(buffer, offset, count); Count(value); return value; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { var value = await inner.ReadAsync(buffer, cancellationToken); Count(value); return value; }
        private void Count(int value) { _read += value; if (_read > maximum) throw new InvalidDataException("ActivityWatch 响应超过 1 MiB 限制。"); }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(); protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); GC.SuppressFinalize(this); }
    }
}
