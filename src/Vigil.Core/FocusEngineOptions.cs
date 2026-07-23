namespace Vigil.Core;

public sealed record FocusEngineOptions
{
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ObservationInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxAiInterval { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan DistractedMaxAiInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan IdleThreshold { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan AiTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan PersistenceInterval { get; init; } = TimeSpan.FromSeconds(30);
    public int DHashThreshold { get; init; } = 40;
}
