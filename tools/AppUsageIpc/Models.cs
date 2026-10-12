namespace AppUsageIpc;

// This assembly has no ETW dependency. Endpoints, ports and raw events never cross the pipe.
public static class Protocol
{
    public const int Version = 1;
    public const int MaximumRequestBytes = 4096;
    public const int MaximumResponseBytes = 4 * 1024 * 1024;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
}

public sealed record PipeRequest(int ProtocolVersion, string Operation);
public sealed record PipeResponse(int ProtocolVersion, string Status,
    CollectorCapabilities? Capabilities = null, CollectorSnapshot? Snapshot = null, string? Error = null);

public sealed record CollectorCapabilities(bool CollectorElevated, bool Ipv4, bool Ipv6,
    bool LoopbackClassification, bool LostEventCountAvailable, bool CumulativeSinceStart,
    bool ShutdownForTestEnabled, string Provider, string CounterSemantics);

public sealed record TrafficCounters(long RxBytes, long TxBytes, long Ipv4RxBytes, long Ipv4TxBytes,
    long Ipv6RxBytes, long Ipv6TxBytes, long LoopbackRxBytes, long LoopbackTxBytes,
    long NonLoopbackRxBytes, long NonLoopbackTxBytes, long LocalToLocalRxBytes, long LocalToLocalTxBytes,
    long UnclassifiedRxBytes, long UnclassifiedTxBytes);

public sealed record ProcessSummary(int ProcessId, string ProcessName, string? ExecutablePath,
    DateTime? StartTimeUtc, string LifetimeIdentity, string AttributionStatus,
    long EventCount, TrafficCounters Counters);

public sealed record ApplicationSummary(string Identity, string ProcessName, string? ExecutablePath,
    long EventCount, TrafficCounters Counters, IReadOnlyList<ProcessSummary> Processes);

public sealed record CollectorMetrics(double CpuPercentOfOneCore, long WorkingSetBytes, long SnapshotsServed);

public sealed record CollectorSnapshot(Guid CollectorInstanceId, DateTime CapturedAtUtc,
    TimeSpan CollectorUptime, long EventCount, int LostEventCount, long ProviderEventCount,
    long UnsupportedEventCount, long MalformedEventCount, IReadOnlyList<ApplicationSummary> Applications,
    ApplicationSummary System, ApplicationSummary Unknown, CollectorCapabilities Capabilities,
    CollectorMetrics Metrics);
