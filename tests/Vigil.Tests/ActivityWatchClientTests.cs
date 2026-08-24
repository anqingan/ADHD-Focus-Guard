using System.Net;
using System.Text;
using Vigil.Infrastructure;

namespace Vigil.Tests;

public sealed class ActivityWatchClientTests
{
    [Fact]
    public async Task BucketDiscoveryUsesCanonicalNonRedirectingPath()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var client = new ActivityWatchClient(http);

        var snapshot = await client.GetCurrentAsync();

        Assert.Null(snapshot);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://127.0.0.1:5600/api/0/buckets/", request.AbsoluteUri);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }
}
