namespace NetTrace.Models;

public sealed class ConnectionObservationEventArgs(
    CurrentConnectionState state, DateTimeOffset observedAtUtc, long sequence) : EventArgs
{
    public CurrentConnectionState State { get; } = state;
    public DateTimeOffset ObservedAtUtc { get; } = observedAtUtc;
    public long Sequence { get; } = sequence;
}
