using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Vigil.Core;

namespace Vigil.Infrastructure;

public sealed class OpenAiCompatibleClient : IFocusAiClient
{
    private const int MaxResponseBytes = 1_048_576;
    private const int MaxFrameBytes = 8 * 1_048_576;
    private readonly HttpClient _httpClient;
    private readonly IAppSettingsStore _settings;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public OpenAiCompatibleClient(HttpClient httpClient, IAppSettingsStore settings)
    {
        _httpClient = httpClient;
        _settings = settings;
    }

    public async Task<string> TestAsync(CancellationToken cancellationToken)
    {
        var jpeg = GdiScreenCaptureService.CreateSyntheticTestImage();
        try
        {
            var content = await SendVisionAsync(
                "你在做视觉能力测试。只返回一个 JSON 对象。",
                "读出图片里的英文和数字，格式：{\"text\":\"...\"}",
                jpeg,
                cancellationToken);
            var json = ExtractFirstJsonObject(content);
            using var document = JsonDocument.Parse(json);
            var text = document.RootElement.GetProperty("text").GetString() ?? "";
            if (!text.Contains("42", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("模型已响应，但没有正确识别测试图片中的“42”。");
            }
            return text;
        }
        finally
        {
            Array.Clear(jpeg);
        }
    }

    public async Task<FrameJudgment> AnalyzeFrameAsync(
        string goal,
        ActivityContext context,
        ReadOnlyMemory<byte> jpeg,
        CancellationToken cancellationToken)
    {
        const string system = """
            你是一个温和但明确的专注守护助手。根据用户目标、当前应用、窗口标题和屏幕截图，判断当前活动。
            截图是主要证据；应用名和窗口标题只作辅助。与目标合理相关的查资料、沟通、配置环境等活动应判为 focused。
            只有明确无关的娱乐、购物、社交刷屏等才判为 distracted；证据不足或关联较弱时判为 wandering。
            只返回一个 JSON 对象：
            {"level":"focused|wandering|distracted","activity":"一句中文活动摘要","reminder":"仅 distracted 时给一句具体温和的回归提示，否则为空字符串"}
            """;
        var user = $"目标：{Limit(goal, 200)}\n当前进程：{Limit(context.ProcessName, 100)}\n窗口标题：{Limit(context.WindowTitle, 500)}";
        var raw = await SendVisionAsync(system, user, jpeg, cancellationToken);
        var json = ExtractFirstJsonObject(raw);
        var wire = JsonSerializer.Deserialize<FrameWire>(json, JsonOptions)
                   ?? throw new InvalidDataException("AI 返回了空 JSON。");
        var level = wire.Level?.ToLowerInvariant() switch
        {
            "focused" => FocusLevel.Focused,
            "wandering" => FocusLevel.Wandering,
            "distracted" => FocusLevel.Distracted,
            _ => throw new InvalidDataException("AI 返回了无效的 level。")
        };
        if (wire.Activity is null || wire.Reminder is null)
        {
            throw new InvalidDataException("AI 返回缺少 activity 或 reminder 字段。");
        }
        var activity = wire.Activity.Trim();
        var reminder = wire.Reminder.Trim();
        if (activity.Length is < 1 or > 200 || reminder.Length > 300)
        {
            throw new InvalidDataException("AI 返回的 activity 或 reminder 长度无效。");
        }
        return new FrameJudgment(level, activity, reminder);
    }

    public async Task<string> SummarizeAsync(
        SessionSummary summary,
        IReadOnlyList<string> distractedActivities,
        CancellationToken cancellationToken)
    {
        const string system = """
            你是专注复盘助手。根据目标和本地统计给出 4–6 句简体中文复盘。
            先肯定真实进展，再温和指出值得改善之处，最后给出下一轮具体建议。不要虚构屏幕上没有出现的活动，不要打印原始 JSON。
            """;
        var notes = distractedActivities.Count == 0
            ? "无"
            : string.Join("；", distractedActivities.Take(5));
        var user = $"""
            目标：{summary.Goal}
            实际时长：{summary.ActualSeconds} 秒
            专注：{summary.FocusedSeconds} 秒
            走神：{summary.WanderingSeconds} 秒
            分心：{summary.DistractedSeconds} 秒
            离开：{summary.AwaySeconds} 秒
            未观察：{summary.UnknownSeconds} 秒
            分心活动摘要：{notes}
            """;
        var result = (await SendTextAsync(system, user, cancellationToken)).Trim();
        if (result.Length is < 1 or > 4_000)
        {
            throw new InvalidDataException("AI 返回的复盘长度无效。");
        }
        return result;
    }

    private async Task<string> SendVisionAsync(
        string system,
        string user,
        ReadOnlyMemory<byte> jpeg,
        CancellationToken cancellationToken)
    {
        if (jpeg.Length is < 1 or > MaxFrameBytes)
        {
            throw new ArgumentException("屏幕帧大小无效。", nameof(jpeg));
        }
        var dataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg.Span);
        try
        {
            var userContent = new object[]
            {
                new { type = "text", text = user },
                new { type = "image_url", image_url = new { url = dataUrl } }
            };
            return await SendAsync(system, userContent, cancellationToken);
        }
        finally
        {
            dataUrl = "";
        }
    }

    private Task<string> SendTextAsync(string system, string user, CancellationToken cancellationToken) =>
        SendAsync(system, user, cancellationToken, 600);

    private async Task<string> SendAsync(
        string system,
        object userContent,
        CancellationToken cancellationToken,
        int maxCompletionTokens = 200)
    {
        var provider = await _settings.LoadProviderAsync(cancellationToken);
        var apiKey = await _settings.GetApiKeyAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(provider.Model) || string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("请先保存 Base URL、Model 和 API Key。");
        }

        var baseUrl = ProviderValidation.NormalizeBaseUrl(provider.BaseUrl);
        var model = ProviderValidation.NormalizeModel(provider.Model);
        var endpoint = new Uri(baseUrl + "/chat/completions");
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = userContent }
            }
        };
        if (model is "kimi-k2.5" or "kimi-k2.6")
        {
            body["thinking"] = new { type = "disabled" };
            body["max_completion_tokens"] = maxCompletionTokens;
        }
        else
        {
            body["max_tokens"] = maxCompletionTokens;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(body, options: JsonOptions);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"云端服务返回 HTTP {(int)response.StatusCode}。");
        }
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new InvalidDataException("云端响应超过 1 MiB 限制。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var limited = new LimitedReadStream(stream, MaxResponseBytes);
        ChatResponse? wire;
        try
        {
            wire = await JsonSerializer.DeserializeAsync<ChatResponse>(limited, JsonOptions, cancellationToken);
        }
        catch (JsonException ex) when (ContainsInvalidData(ex))
        {
            throw new InvalidDataException("云端响应超过 1 MiB 限制。", ex);
        }
        var content = wire?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content) || content.Length > 100_000)
        {
            throw new InvalidDataException("云端服务返回了空 choices/content。");
        }
        return content;
    }

    public static string ExtractFirstJsonObject(string text)
    {
        var depth = 0;
        var start = -1;
        var inString = false;
        var escaped = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }
                continue;
            }
            if (ch == '"')
            {
                inString = true;
            }
            else if (ch == '{')
            {
                if (depth == 0) start = i;
                depth++;
            }
            else if (ch == '}' && depth > 0)
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    return text[start..(i + 1)];
                }
            }
        }
        throw new InvalidDataException("AI 响应中没有完整 JSON 对象。");
    }

    private sealed record FrameWire(string? Level, string? Activity, string? Reminder);
    private sealed record ChatResponse(List<Choice>? Choices);
    private sealed record Choice(Message? Message);
    private sealed record Message(string? Content);

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static bool ContainsInvalidData(Exception exception)
    {
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is InvalidDataException) return true;
        }
        return false;
    }

    private sealed class LimitedReadStream(Stream inner, long maximumBytes) : Stream
    {
        private long _read;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var allowed = AllowedCount(count);
            var read = inner.Read(buffer, offset, allowed);
            Account(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var allowed = AllowedCount(buffer.Length);
            var read = await inner.ReadAsync(buffer[..allowed], cancellationToken);
            Account(read);
            return read;
        }

        private int AllowedCount(int requested) =>
            (int)Math.Min(requested, Math.Max(1, maximumBytes - _read + 1));

        private void Account(int read)
        {
            _read += read;
            if (_read > maximumBytes)
            {
                throw new InvalidDataException("云端响应超过 1 MiB 限制。");
            }
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
