using System.Net;
using System.Text;
using Vigil.Core;
using Vigil.Infrastructure;

namespace Vigil.Tests;

public sealed class DailyGoalAnalysisTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Vigil-DailyGoalTests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AnalyzeDailyGoals_KeepsOnlyKnownParentGoals()
    {
        Directory.CreateDirectory(_directory);
        var parentId = Guid.NewGuid();
        var unknownId = Guid.NewGuid();
        var settings = new JsonSettingsStore(
            Path.Combine(_directory, "settings.json"),
            Path.Combine(_directory, "secret.bin"));
        await settings.SaveProviderModelsAsync("https://example.com/v1", "text-model", "vision-model", "test-secret");
        var content = $$"""
            [{"title":"完成报告图表","expectedOutcome":"导出三张最终图","classification":"推进目标","priority":1,"estimatedMinutes":90,"relatedGoalIds":["{{parentId}}","{{unknownId}}"],"reasoning":"直接推进本周报告"}]
            """;
        var envelope = "{\"choices\":[{\"message\":{\"content\":" + System.Text.Json.JsonSerializer.Serialize(content) + "}}]}";
        using var http = new HttpClient(new StubHandler(envelope));
        var service = new DeepSeekPersonalAiService(http, settings);
        var goals = new[]
        {
            new GoalRecord
            {
                Id = parentId,
                Horizon = GoalHorizon.Week,
                Title = "完成报告",
                Status = GoalStatus.InProgress,
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now
            }
        };

        var result = Assert.Single(await service.AnalyzeDailyGoalsAsync("今天做报告图表", goals));

        Assert.Equal("推进目标", result.Classification);
        Assert.Equal(90, result.EstimatedMinutes);
        Assert.Equal(new[] { parentId }, result.RelatedGoalIds);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
