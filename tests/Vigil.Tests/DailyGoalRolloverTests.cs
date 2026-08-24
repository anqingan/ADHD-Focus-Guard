using Vigil.Core;
using Vigil.Infrastructure;

namespace Vigil.Tests;

public sealed class DailyGoalRolloverTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Vigil-RolloverTests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PreviousActivityDaysOpenTodayGoals_BecomeIncomplete()
    {
        Directory.CreateDirectory(_directory);
        var repository = new SqlitePersonalDataRepository(Path.Combine(_directory, "personal.db"));
        await repository.InitializeAsync();
        var now = Local(2026, 8, 25, 9);
        var old = Goal("昨日进行中", GoalHorizon.Today, GoalStatus.InProgress, Local(2026, 8, 24, 10));
        var notStarted = Goal("昨日未开始", GoalHorizon.Today, GoalStatus.NotStarted, Local(2026, 8, 24, 11));
        await repository.SaveGoalAsync(old, "created");
        await repository.SaveGoalAsync(notStarted, "created");

        var count = await DailyGoalRollover.ExpireAsync(repository, now);
        var goals = await repository.GetGoalsAsync(true);

        Assert.Equal(2, count);
        Assert.All(goals, goal => Assert.Equal(GoalStatus.Incomplete, goal.Status));
        Assert.Contains(await repository.GetGoalHistoryAsync(old.Id), item => item.ChangeKind == "daily-incomplete");
    }

    [Fact]
    public async Task CurrentDayAndNonDailyGoals_AreNotChanged()
    {
        Directory.CreateDirectory(_directory);
        var repository = new SqlitePersonalDataRepository(Path.Combine(_directory, "personal.db"));
        await repository.InitializeAsync();
        var now = Local(2026, 8, 25, 9);
        var current = Goal("今天进行中", GoalHorizon.Today, GoalStatus.InProgress, Local(2026, 8, 25, 8, 30));
        var weekly = Goal("本周目标", GoalHorizon.Week, GoalStatus.InProgress, Local(2026, 8, 24, 10));
        var paused = Goal("暂停的今日目标", GoalHorizon.Today, GoalStatus.Paused, Local(2026, 8, 24, 10));
        await repository.SaveGoalAsync(current, "created");
        await repository.SaveGoalAsync(weekly, "created");
        await repository.SaveGoalAsync(paused, "created");

        Assert.Equal(0, await DailyGoalRollover.ExpireAsync(repository, now));
        var goals = await repository.GetGoalsAsync(true);
        Assert.Equal(GoalStatus.InProgress, goals.Single(goal => goal.Id == current.Id).Status);
        Assert.Equal(GoalStatus.InProgress, goals.Single(goal => goal.Id == weekly.Id).Status);
        Assert.Equal(GoalStatus.Paused, goals.Single(goal => goal.Id == paused.Id).Status);
    }

    private static GoalRecord Goal(string title, GoalHorizon horizon, GoalStatus status, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(),
        Horizon = horizon,
        Title = title,
        Status = status,
        CreatedAt = at,
        UpdatedAt = at
    };

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute = 0)
    {
        var value = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(value, TimeZoneInfo.Local.GetUtcOffset(value));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
