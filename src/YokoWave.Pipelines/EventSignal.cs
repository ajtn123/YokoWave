namespace YokoWave.Pipelines;

/// <summary>One capture's immutable event data. Producers own I/O; graph processors only inspect these snapshots.</summary>
public sealed record EventSource(string ClientId, Guid Registration);
public sealed record EventValue(string ClientId, string EventId, Guid Registration, bool IsTrigger, double Value,
    long Sequence, DateTimeOffset CapturedAt, DateTimeOffset ValidUntil, bool Simulated);
public sealed record EventSignal(EventSource[] Sources, EventValue[] Messages)
{
    public static EventSignal Empty { get; } = new([], []);
}
