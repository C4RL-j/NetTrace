namespace NetTrace.Models;

// A result for the configured external probes, not proof that every internet
// service works. This measurement is independent of ICMP replies and quality.
public enum InternetReachabilityStatus
{
    Checking,
    Available,
    Limited,
    Unavailable
}

public sealed record InternetReachabilityResult(
    InternetReachabilityStatus Status,
    DateTimeOffset CheckedAtUtc,
    bool DnsSucceeded,
    bool HttpsSucceeded);
