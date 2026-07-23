using Vigil.Core;

namespace Vigil.Tests;

public sealed class SessionAccumulatorTests
{
    [Fact]
    public void Build_AccountsForFourLevelsAndUnknown()
    {
        var accumulator = new SessionAccumulator();
        accumulator.Add(TimeSpan.FromSeconds(10), FocusLevel.Focused, ObservationAvailability.Available);
        accumulator.Add(TimeSpan.FromSeconds(5), FocusLevel.Wandering, ObservationAvailability.Available);
        accumulator.Add(TimeSpan.FromSeconds(4), FocusLevel.Distracted, ObservationAvailability.Available);
        accumulator.Add(TimeSpan.FromSeconds(3), FocusLevel.Away, ObservationAvailability.Available);
        accumulator.Add(TimeSpan.FromSeconds(8), FocusLevel.Focused, ObservationAvailability.Unavailable);

        var start = DateTimeOffset.UtcNow;
        var summary = accumulator.Build(Guid.NewGuid(), "test", 60, 30, start, start.AddSeconds(30), SessionCompletionKind.Manual);

        Assert.Equal(10, summary.FocusedSeconds);
        Assert.Equal(5, summary.WanderingSeconds);
        Assert.Equal(4, summary.DistractedSeconds);
        Assert.Equal(3, summary.AwaySeconds);
        Assert.Equal(8, summary.UnknownSeconds);
        Assert.Equal(summary.ActualSeconds, summary.ObservedSeconds + summary.UnknownSeconds);
    }
}
