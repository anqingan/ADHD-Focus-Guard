namespace Vigil.Core;

public static class DailyGoalRollover
{
    public static async Task<int> ExpireAsync(
        IPersonalDataRepository repository,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var boundary = CurrentActivityDayStart(now);
        var goals = await repository.GetGoalsAsync(false, cancellationToken);
        var expired = goals
            .Where(goal => goal.Horizon == GoalHorizon.Today
                && goal.Status is GoalStatus.NotStarted or GoalStatus.InProgress
                && goal.UpdatedAt < boundary)
            .ToArray();

        foreach (var goal in expired)
        {
            await repository.SaveGoalAsync(
                goal with { Status = GoalStatus.Incomplete, UpdatedAt = now },
                "daily-incomplete",
                cancellationToken);
        }
        return expired.Length;
    }

    public static DateTimeOffset CurrentActivityDayStart(DateTimeOffset now)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).DateTime;
        var activityDate = DateOnly.FromDateTime(localNow.Hour < 8 ? localNow.AddDays(-1) : localNow);
        var localBoundary = activityDate.ToDateTime(new TimeOnly(8, 0), DateTimeKind.Unspecified);
        return new DateTimeOffset(localBoundary, TimeZoneInfo.Local.GetUtcOffset(localBoundary));
    }
}
