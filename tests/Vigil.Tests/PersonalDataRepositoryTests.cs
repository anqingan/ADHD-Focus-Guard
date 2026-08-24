using System.Text;
using Vigil.Core;
using Vigil.Infrastructure;

namespace Vigil.Tests;

public sealed class PersonalDataRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Vigil-PersonalTests-" + Guid.NewGuid().ToString("N"));
    public PersonalDataRepositoryTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task RoundTrip_EncryptsSensitiveTextAndPreservesTotals()
    {
        var file = Path.Combine(_directory, "personal.db"); var repository = new SqlitePersonalDataRepository(file); await repository.InitializeAsync(); var now = DateTimeOffset.Now;
        var goal = new GoalRecord { Id = Guid.NewGuid(), Horizon = GoalHorizon.Today, Title = "超级秘密目标", ExpectedOutcome = "完成秘密成果", Status = GoalStatus.InProgress, CreatedAt = now, UpdatedAt = now };
        await repository.SaveGoalAsync(goal, "created");
        await repository.SaveActivitySegmentAsync(new ActivitySegment { Id = Guid.NewGuid(), StartedAt = now.AddMinutes(-20), EndedAt = now.AddMinutes(-10), Application = "secret-app", DisplayName = "秘密窗口", Category = ActivityCategory.WorkAndStudy, Source = ActivitySource.User, ClassificationSource = ClassificationSource.User, Confidence = 1 });

        var restored = Assert.Single(await repository.GetGoalsAsync()); Assert.Equal(goal.Title, restored.Title); Assert.Single(await repository.GetGoalHistoryAsync(goal.Id));
        var totals = await repository.GetActivityTotalsAsync(now.AddHours(-1), now); Assert.Equal(600, totals.WorkAndStudySeconds);
        var raw = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)); Assert.DoesNotContain("超级秘密目标", raw); Assert.DoesNotContain("秘密窗口", raw);
    }

    [Fact]
    public async Task MemoryAndActions_KeepInactiveHistory()
    {
        var repository = new SqlitePersonalDataRepository(Path.Combine(_directory, "history.db")); await repository.InitializeAsync(); var now = DateTimeOffset.Now;
        await repository.SaveActionItemAsync(new ActionItemRecord { Id = Guid.NewGuid(), Title = "已完成事务", Status = ActionItemStatus.Completed, CreatedAt = now, UpdatedAt = now });
        await repository.SaveMemoryAsync(new MemoryRecord { Id = Guid.NewGuid(), Text = "AI 候选", Author = MemoryAuthor.Ai, Status = MemoryStatus.PendingReview, CreatedAt = now, UpdatedAt = now });
        Assert.Single(await repository.GetActionItemsAsync(true)); Assert.Empty(await repository.GetActionItemsAsync(false)); Assert.Equal(MemoryStatus.PendingReview, Assert.Single(await repository.GetMemoriesAsync()).Status);
    }

    [Fact]
    public async Task DeleteGoal_RemovesGoalAndItsHistory()
    {
        var repository = new SqlitePersonalDataRepository(Path.Combine(_directory, "delete-goal.db"));
        await repository.InitializeAsync();
        var now = DateTimeOffset.Now;
        var goal = new GoalRecord { Id = Guid.NewGuid(), Horizon = GoalHorizon.Today, Title = "允许永久删除", Status = GoalStatus.InProgress, CreatedAt = now, UpdatedAt = now };
        await repository.SaveGoalAsync(goal, "created");
        Assert.Single(await repository.GetGoalHistoryAsync(goal.Id));

        await repository.DeleteGoalAsync(goal.Id);

        Assert.Empty(await repository.GetGoalsAsync(true));
        Assert.Empty(await repository.GetGoalHistoryAsync(goal.Id));
    }

    public void Dispose() { try { Directory.Delete(_directory, true); } catch (IOException) { } }
}
