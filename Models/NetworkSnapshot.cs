namespace NetTrace.Models;

public sealed record NetworkSnapshot(
    long BytesReceived,
    long BytesSent,
    DateTimeOffset Timestamp);
