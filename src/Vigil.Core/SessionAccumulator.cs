namespace Vigil.Core;

public sealed class SessionAccumulator
{
    private double _focused;
    private double _wandering;
    private double _distracted;
    private double _away;
    private double _unknown;

    public void Add(TimeSpan elapsed, FocusLevel? level, ObservationAvailability availability)
    {
        var seconds = Math.Max(0, elapsed.TotalSeconds);
        if (seconds <= 0)
        {
            return;
        }

        if (availability == ObservationAvailability.Unavailable && level != FocusLevel.Away)
        {
            _unknown += seconds;
            return;
        }

        switch (level)
        {
            case FocusLevel.Focused: _focused += seconds; break;
            case FocusLevel.Wandering: _wandering += seconds; break;
            case FocusLevel.Distracted: _distracted += seconds; break;
            case FocusLevel.Away: _away += seconds; break;
            default: _unknown += seconds; break;
        }
    }

    public SessionSummary Build(
        Guid id,
        string goal,
        int plannedSeconds,
        int actualSeconds,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        SessionCompletionKind completionKind,
        string summaryText = "")
    {
        var focused = (int)Math.Round(_focused);
        var wandering = (int)Math.Round(_wandering);
        var distracted = (int)Math.Round(_distracted);
        var away = (int)Math.Round(_away);
        var knownTotal = focused + wandering + distracted + away;
        var unknown = Math.Max(0, actualSeconds - knownTotal);

        return new SessionSummary
        {
            Id = id,
            Goal = goal,
            PlannedSeconds = plannedSeconds,
            ActualSeconds = actualSeconds,
            StartedAtUtc = startedAt,
            EndedAtUtc = endedAt,
            CompletionKind = completionKind,
            FocusedSeconds = focused,
            WanderingSeconds = wandering,
            DistractedSeconds = distracted,
            AwaySeconds = away,
            UnknownSeconds = unknown,
            SummaryText = summaryText
        };
    }
}
