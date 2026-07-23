namespace Vigil.Core;

public sealed class InterventionPolicy
{
    private FocusLevel? _lastLevel;
    private DateTimeOffset? _wanderingSince;
    private DateTimeOffset? _nextWanderingReminder;
    private DateTimeOffset? _distractedSince;
    private DateTimeOffset? _nextDistractedReminder;
    private int _distractedJudgments;
    private bool _overlayShown;
    private bool _distractedMuted;
    private DateTimeOffset? _nextAwaySound;

    public void Reset()
    {
        _lastLevel = null;
        _wanderingSince = null;
        _nextWanderingReminder = null;
        ResetDistraction();
        _nextAwaySound = null;
    }

    public void MuteCurrentDistraction() => _distractedMuted = true;

    public IReadOnlyList<ReminderRequest> Evaluate(
        FocusLevel level,
        DateTimeOffset now,
        string goal,
        string reminder,
        bool freshAiJudgment,
        TimeSpan idleDuration)
    {
        var actions = new List<ReminderRequest>();

        if (level != FocusLevel.Distracted)
        {
            ResetDistraction();
        }
        if (level != FocusLevel.Wandering)
        {
            _wanderingSince = null;
            _nextWanderingReminder = null;
        }
        if (level != FocusLevel.Away)
        {
            if (_lastLevel == FocusLevel.Away)
            {
                actions.Add(new(ReminderKind.HideSoftReminder, level, "", goal));
            }
            _nextAwaySound = null;
        }

        switch (level)
        {
            case FocusLevel.Focused:
                break;

            case FocusLevel.Wandering:
                _wanderingSince ??= now;
                if (now - _wanderingSince >= TimeSpan.FromSeconds(120)
                    && (_nextWanderingReminder is null || now >= _nextWanderingReminder))
                {
                    actions.Add(new(ReminderKind.Capsule, level, "走神有一会儿了，回到当前目标？", goal));
                    _nextWanderingReminder = now.AddSeconds(60);
                }
                break;

            case FocusLevel.Distracted:
                _distractedSince ??= now;
                if (freshAiJudgment)
                {
                    _distractedJudgments++;
                }
                if (_distractedMuted)
                {
                    break;
                }
                if (_lastLevel != FocusLevel.Distracted)
                {
                    var message = string.IsNullOrWhiteSpace(reminder) ? "似乎偏离目标了，回来一下。" : reminder;
                    actions.Add(new(ReminderKind.Capsule, level, message, goal));
                    actions.Add(new(ReminderKind.Tray, level, message, goal));
                    actions.Add(new(ReminderKind.Sound, level, message, goal));
                    _nextDistractedReminder = now.AddSeconds(60);
                }
                else if (!_overlayShown
                         && now - _distractedSince >= TimeSpan.FromSeconds(30)
                         && _distractedJudgments >= 2)
                {
                    actions.Add(new(ReminderKind.FullScreenOverlay, level,
                        string.IsNullOrWhiteSpace(reminder) ? "回到当前承诺。" : reminder,
                        goal,
                        true));
                    _overlayShown = true;
                    _nextDistractedReminder = now.AddSeconds(60);
                }
                else if (_overlayShown && _nextDistractedReminder is not null && now >= _nextDistractedReminder)
                {
                    actions.Add(new(ReminderKind.Capsule, level,
                        string.IsNullOrWhiteSpace(reminder) ? "仍然偏离目标，回来一下。" : reminder,
                        goal));
                    _nextDistractedReminder = now.AddSeconds(60);
                }
                break;

            case FocusLevel.Away:
                if (idleDuration >= TimeSpan.FromSeconds(180)
                    && (_nextAwaySound is null || now >= _nextAwaySound))
                {
                    actions.Add(new(ReminderKind.Capsule, level, "似乎离开电脑很久了，回来继续？", goal));
                    actions.Add(new(ReminderKind.Sound, level, "", goal));
                    _nextAwaySound = now.AddSeconds(30);
                }
                break;
        }

        _lastLevel = level;
        return actions;
    }

    private void ResetDistraction()
    {
        _distractedSince = null;
        _nextDistractedReminder = null;
        _distractedJudgments = 0;
        _overlayShown = false;
        _distractedMuted = false;
    }
}
