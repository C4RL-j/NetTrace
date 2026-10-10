namespace NetTrace.Models;

// Raw ICMP observations do not establish whether the internet is online.
public sealed record IcmpProbeResult(
    DateTimeOffset TimestampUtc,
    string Target,
    int SuccessfulReplies,
    int FailedReplies,
    double? AverageLatencyMs,
    double PacketLossPercent,
    double? JitterMs = null);
