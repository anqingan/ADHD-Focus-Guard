using Vigil.Core;
using Vigil.Infrastructure;

namespace Vigil.Tests;

public sealed class SqliteSessionRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VigilDbTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Repository_CreatesUpdatesReadsAndDeletesSummary()
    {
        Directory.CreateDirectory(_directory);
        var repository = new SqliteSessionRepository(Path.Combine(_directory, "test.db"));
        await repository.InitializeAsync();
        var summary = CreateSummary(SessionCompletionKind.Running) with { EndedAtUtc = null };

        await repository.CreateAsync(summary);
        await repository.UpdateAsync(summary with
        {
            CompletionKind = SessionCompletionKind.Manual,
            EndedAtUtc = DateTimeOffset.UtcNow,
            SummaryText = "完成"
        });

        var saved = Assert.Single(await repository.GetAllAsync());
        Assert.Equal("完成", saved.SummaryText);
        Assert.Equal(SessionCompletionKind.Manual, saved.CompletionKind);

        await repository.DeleteAsync(saved.Id);
        Assert.Empty(await repository.GetAllAsync());
    }

    [Fact]
    public async Task Startup_MarksRunningSessionsInterrupted()
    {
        Directory.CreateDirectory(_directory);
        var repository = new SqliteSessionRepository(Path.Combine(_directory, "interrupt.db"));
        await repository.InitializeAsync();
        await repository.CreateAsync(CreateSummary(SessionCompletionKind.Running) with { EndedAtUtc = null });

        await repository.MarkRunningSessionsInterruptedAsync();

        var saved = Assert.Single(await repository.GetAllAsync());
        Assert.Equal(SessionCompletionKind.Interrupted, saved.CompletionKind);
        Assert.NotNull(saved.EndedAtUtc);
    }

    private static SessionSummary CreateSummary(SessionCompletionKind kind) => new()
    {
        Id = Guid.NewGuid(),
        Goal = "测试目标",
        PlannedSeconds = 60,
        ActualSeconds = 10,
        StartedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-10),
        EndedAtUtc = DateTimeOffset.UtcNow,
        CompletionKind = kind,
        FocusedSeconds = 5,
        WanderingSeconds = 2,
        DistractedSeconds = 1,
        AwaySeconds = 1,
        UnknownSeconds = 1,
        SummaryText = ""
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
