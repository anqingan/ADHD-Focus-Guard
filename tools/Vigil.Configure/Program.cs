using Vigil.Infrastructure;

const string baseUrl = "https://api.moonshot.cn/v1";
const string model = "kimi-k2.6";
var apiKey = Environment.GetEnvironmentVariable("VIGIL_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("Set the temporary VIGIL_API_KEY environment variable first.");
    return 2;
}

try
{
    var settings = new JsonSettingsStore();
    await settings.SaveProviderAsync(baseUrl, model, apiKey);

    using var handler = new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10)
    };
    using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    var ai = new OpenAiCompatibleClient(http, settings);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var result = await ai.TestAsync(timeout.Token);

    Console.WriteLine($"Configured=True");
    Console.WriteLine($"BaseUrl={baseUrl}");
    Console.WriteLine($"Model={model}");
    Console.WriteLine($"SyntheticVisionTest={result}");
    return 0;
}
finally
{
    apiKey = null;
    Environment.SetEnvironmentVariable("VIGIL_API_KEY", null);
}
