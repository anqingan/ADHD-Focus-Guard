using Vigil.Infrastructure;

namespace Vigil.Tests;

public sealed class AiBudgetTrackerTests
{
    [Fact]
    public async Task OneYuanLimit_PausesUntilUserContinues()
    {
        var file = Path.Combine(Path.GetTempPath(), "vigil-budget-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var budget = new AiBudgetTracker(file); var reached = 0; budget.BudgetReached += (_, _) => reached++;
            await budget.RecordUsageAsync("deepseek-v4-flash", 1_000_000, 0, 0);
            Assert.Equal(1, reached); Assert.False(await budget.CanUseAutomaticAiAsync());
            await budget.ContinueTodayAsync(); Assert.True(await budget.CanUseAutomaticAiAsync());
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }
}
