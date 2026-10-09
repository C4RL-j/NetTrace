namespace NetTrace.Models;

// EventType is an extensible persisted value; adding an event kind needs no schema change.
public sealed record ConnectionEvent(
    long Id,
    DateTimeOffset OccurredAtUtc,
    string EventType,
    long? SessionId,
    long? ProfileId,
    string? NetworkType,
    string? NetworkIdentifier,
    string Message);
