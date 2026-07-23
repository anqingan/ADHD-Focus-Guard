using System.Net;
using System.Text;
using Vigil.Core;
using Vigil.Infrastructure;

namespace Vigil.Tests;

public sealed class OpenAiCompatibleClientTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VigilTests", Guid.NewGuid().ToString("N"));
    private readonly List<HttpClient> _httpClients = [];

    [Fact]
    public void ExtractFirstJsonObject_ToleratesCodeFenceAndBracesInsideString()
    {
        var raw = "prefix ```json\n{\"level\":\"focused\",\"activity\":\"编辑 {文档}\",\"reminder\":\"\"}\n``` suffix";
        var json = OpenAiCompatibleClient.ExtractFirstJsonObject(raw);
        Assert.Contains("编辑 {文档}", json);
    }

    [Fact]
    public async Task AnalyzeFrame_SendsImageAndParsesJudgment()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            "{\"choices\":[{\"message\":{\"content\":\"```json\\n{\\\"level\\\":\\\"distracted\\\",\\\"activity\\\":\\\"浏览娱乐内容\\\",\\\"reminder\\\":\\\"回到报告\\\"}\\n```\"}}]}");
        var client = await CreateClientAsync(handler);

        var result = await client.AnalyzeFrameAsync(
            "写报告",
            new ActivityContext("browser", "video"),
            new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 },
            CancellationToken.None);

        Assert.Equal(FocusLevel.Distracted, result.Level);
        Assert.Contains("image_url", handler.RequestBody);
        Assert.DoesNotContain("test-secret", handler.RequestBody);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AnalyzeFrame_ThrowsForHttpErrors(HttpStatusCode status)
    {
        var client = await CreateClientAsync(new StubHandler(status, "{}"));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.AnalyzeFrameAsync(
            "goal", new ActivityContext("app", "title"), new byte[] { 1, 2, 3 }, CancellationToken.None));
        Assert.Contains(((int)status).ToString(), exception.Message);
    }

    [Fact]
    public async Task SettingsStore_EncryptsApiKeyAtRest()
    {
        var settings = CreateSettings();
        await settings.SaveProviderAsync("https://example.com/v1", "vision", "test-secret");

        var secretBytes = await File.ReadAllBytesAsync(Path.Combine(_directory, "secret.bin"));
        Assert.DoesNotContain("test-secret", Encoding.UTF8.GetString(secretBytes));
        Assert.Equal("test-secret", await settings.GetApiKeyAsync());
    }

    [Theory]
    [InlineData("http://example.com/v1")]
    [InlineData("https://user:password@example.com/v1")]
    [InlineData("https://example.com/v1?redirect=evil")]
    [InlineData("https://example.com/v1#fragment")]
    public async Task SettingsStore_RejectsUnsafeBaseUrls(string baseUrl)
    {
        var settings = CreateSettings();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            settings.SaveProviderAsync(baseUrl, "vision", "test-secret"));
    }

    [Fact]
    public async Task SettingsStore_DoesNotReleaseSecretAfterEndpointTampering()
    {
        var settings = CreateSettings();
        await settings.SaveProviderAsync("https://example.com/v1", "vision", "test-secret");
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "settings.json"),
            "{\"BaseUrl\":\"https://evil.example/v1\",\"Model\":\"vision\"}");

        var provider = await settings.LoadProviderAsync();
        Assert.False(provider.HasApiKey);
        Assert.Null(await settings.GetApiKeyAsync());
    }

    [Fact]
    public async Task SettingsStore_RequiresNewKeyWhenEndpointChanges()
    {
        var settings = CreateSettings();
        await settings.SaveProviderAsync("https://example.com/v1", "vision", "test-secret");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            settings.SaveProviderAsync("https://other.example/v1", "vision", ""));
    }

    [Fact]
    public async Task AnalyzeFrame_RejectsOversizedResponse()
    {
        var huge = "{\"choices\":[],\"padding\":\"" + new string('x', 1_048_577) + "\"}";
        var client = await CreateClientAsync(new StubHandler(HttpStatusCode.OK, huge, suppressContentLength: true));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.AnalyzeFrameAsync(
            "goal", new ActivityContext("app", "title"), new byte[] { 1, 2, 3 }, CancellationToken.None));
    }

    [Fact]
    public async Task AnalyzeFrame_RejectsMissingRequiredFields()
    {
        var body = "{\"choices\":[{\"message\":{\"content\":\"{\\\"level\\\":\\\"focused\\\"}\"}}]}";
        var client = await CreateClientAsync(new StubHandler(HttpStatusCode.OK, body));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.AnalyzeFrameAsync(
            "goal", new ActivityContext("app", "title"), new byte[] { 1, 2, 3 }, CancellationToken.None));
    }

    private async Task<OpenAiCompatibleClient> CreateClientAsync(HttpMessageHandler handler)
    {
        var settings = CreateSettings();
        await settings.SaveProviderAsync("https://example.com/v1", "vision", "test-secret");
        var httpClient = new HttpClient(handler);
        _httpClients.Add(httpClient);
        return new OpenAiCompatibleClient(httpClient, settings);
    }

    private JsonSettingsStore CreateSettings() => new(
        Path.Combine(_directory, "settings.json"),
        Path.Combine(_directory, "secret.bin"));

    public void Dispose()
    {
        foreach (var client in _httpClients) client.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class StubHandler(
        HttpStatusCode status,
        string responseBody,
        bool suppressContentLength = false) : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
            if (suppressContentLength)
            {
                response.Content.Headers.ContentLength = null;
            }
            return response;
        }
    }
}
