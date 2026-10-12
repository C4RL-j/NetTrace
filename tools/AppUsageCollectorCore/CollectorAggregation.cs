using System.Diagnostics;
using System.Net;
using AppUsageEtwProbe;
using AppUsageIpc;

namespace AppUsageElevatedCollector;

// One lock covers event acceptance and complete snapshot copying. No mutable
// parser/aggregator state or endpoint metadata crosses the IPC boundary.
public sealed class CollectorAggregation
{
    private readonly object gate = new();
    private readonly ProviderSchema schema;
    private readonly IReadOnlyDictionary<(int, int), NetworkEventSchema> descriptors;
    private readonly IReadOnlySet<IPAddress> localAddresses;
    private readonly ActivityAggregator aggregation;
    private readonly TimeProvider timeProvider;
    private readonly long startedTimestamp;
    private readonly TimeSpan cpuAtStart;
    private readonly bool shutdownForTestEnabled;
    private readonly bool collectorElevated;
    private EtwCaptureLiveStatus captureStatus = new(null, false, false, false, null, 0, 0, 0);
    private long snapshotsServed;
    private long rejectedEvents;
    private int maximumKnownLostEvents = -1;
    private CollectorSnapshot? cachedSnapshot;
    private long cachedSnapshotTimestamp;
    private readonly TimeSpan snapshotCacheInterval;

    public CollectorAggregation(ProviderSchema schema, IProcessResolver resolver,
        IReadOnlySet<IPAddress> localAddresses, Guid? instanceId = null, TimeProvider? timeProvider = null,
        bool shutdownForTestEnabled = false, bool collectorElevated = true,
        TimeSpan? snapshotCacheInterval = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(localAddresses);
        this.schema = schema;
        descriptors = schema.Events.ToDictionary(value => (value.EventId, value.Version));
        this.localAddresses = localAddresses.Select(ActivityParser.Normalize).ToHashSet();
        aggregation = new(resolver);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.shutdownForTestEnabled = shutdownForTestEnabled;
        this.collectorElevated = collectorElevated;
        CollectorInstanceId = instanceId ?? Guid.NewGuid();
        StartedAtUtc = this.timeProvider.GetUtcNow().UtcDateTime;
        this.snapshotCacheInterval = snapshotCacheInterval ?? TimeSpan.Zero;
        if (this.snapshotCacheInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(snapshotCacheInterval));
        startedTimestamp = this.timeProvider.GetTimestamp();
        using var process = Process.GetCurrentProcess();
        cpuAtStart = process.TotalProcessorTime;
    }

    public Guid CollectorInstanceId { get; }
    public DateTime StartedAtUtc { get; }
    public DateTime? LastEventAtUtc { get; private set; }

    public bool Add(RawNetworkEvent raw)
    {
        lock (gate)
        {
            if (!descriptors.TryGetValue((raw.EventId, raw.Version), out var descriptor)
                || !ActivityParser.TryParse(raw, descriptor, localAddresses, out var activity))
            {
                rejectedEvents = ByteTotals.SaturatingAdd(rejectedEvents, 1);
                return false;
            }
            aggregation.Add(activity!);
            if (LastEventAtUtc is null || raw.TimestampUtc > LastEventAtUtc) LastEventAtUtc = raw.TimestampUtc;
            return true;
        }
    }

    public void UpdateCaptureStatus(EtwCaptureLiveStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (gate)
        {
            // Independent pipe clients can finish queries out of order. Keep the
            // cumulative controller counters monotonic while retaining explicit
            // unavailability when a loss query fails.
            bool lossAvailable = status.LostEventCountAvailable && status.EventsLost is >= 0;
            if (lossAvailable) maximumKnownLostEvents = Math.Max(maximumKnownLostEvents, status.EventsLost!.Value);
            captureStatus = status with
            {
                ProviderEventCount = Math.Max(captureStatus.ProviderEventCount, status.ProviderEventCount),
                UnsupportedEventCount = Math.Max(captureStatus.UnsupportedEventCount, status.UnsupportedEventCount),
                MalformedEventCount = Math.Max(captureStatus.MalformedEventCount, status.MalformedEventCount),
                EventsLost = lossAvailable ? maximumKnownLostEvents : null
            };
        }
    }

    public CollectorCapabilities GetCapabilities()
    {
        lock (gate) return Capabilities();
    }

    public CollectorSnapshot GetSnapshot(bool countRequest = true)
    {
        lock (gate)
        {
            if (countRequest) snapshotsServed = ByteTotals.SaturatingAdd(snapshotsServed, 1);
            if (countRequest && cachedSnapshot is not null
                && timeProvider.GetElapsedTime(cachedSnapshotTimestamp) < snapshotCacheInterval)
                return cachedSnapshot with { Metrics = cachedSnapshot.Metrics with { SnapshotsServed = snapshotsServed } };
            var processes = aggregation.Processes;
            var systemProcesses = processes.Where(process => process.Identity.Pid == 4).ToArray();
            var unknownProcesses = processes.Where(process => process.Identity.Pid != 4 && IsUnknown(process)).ToArray();
            var knownProcesses = processes.Where(process => process.Identity.Pid != 4 && !IsUnknown(process)).ToArray();
            var applications = knownProcesses.GroupBy(process => process.Identity.ApplicationKey, StringComparer.Ordinal)
                .Select(group => Application(group.Key, group.First().Identity.Name,
                    group.First().Identity.ExecutablePath, group.ToArray()))
                .OrderByDescending(application => ByteTotals.SaturatingAdd(application.Counters.RxBytes,
                    application.Counters.TxBytes)).ToArray();
            var uptime = timeProvider.GetElapsedTime(startedTimestamp);
            if (uptime < TimeSpan.Zero) uptime = TimeSpan.Zero;
            using var processMetrics = Process.GetCurrentProcess();
            var cpu = Math.Max(0, (processMetrics.TotalProcessorTime - cpuAtStart).TotalSeconds)
                / Math.Max(uptime.TotalSeconds, 0.001) * 100;
            bool lossAvailable = captureStatus.LostEventCountAvailable && captureStatus.EventsLost is >= 0;
            var snapshot = new CollectorSnapshot(CollectorInstanceId, timeProvider.GetUtcNow().UtcDateTime, uptime,
                aggregation.EventCount, lossAvailable ? captureStatus.EventsLost!.Value : -1,
                captureStatus.ProviderEventCount, captureStatus.UnsupportedEventCount,
                ByteTotals.SaturatingAdd(captureStatus.MalformedEventCount, rejectedEvents),
                Array.AsReadOnly(applications), Application("system", "System", null, systemProcesses),
                Application("unknown", "Unknown / Unattributed", null, unknownProcesses), Capabilities(),
                new(cpu, processMetrics.WorkingSet64, snapshotsServed));
            cachedSnapshot = snapshot;
            cachedSnapshotTimestamp = timeProvider.GetTimestamp();
            return snapshot;
        }
    }

    private CollectorCapabilities Capabilities() => new(collectorElevated, schema.SupportsIpv4,
        schema.SupportsIpv6, schema.Events.Any(),
        captureStatus.LostEventCountAvailable && captureStatus.EventsLost is >= 0,
        true, shutdownForTestEnabled, ProviderSchema.ProviderName,
        "Cumulative logical network event bytes since this collector instance started. Local app and VPN transport layers remain separate from physical adapter usage; no deduplication is performed.");

    private static bool IsUnknown(ProcessObservation process) => process.Identity.Pid == 0
        || process.Identity.ExecutablePath is null || process.Identity.StartTimeUtc is null;

    private static ApplicationSummary Application(string key, string name, string? path,
        IReadOnlyList<ProcessObservation> processes)
    {
        long eventCount = 0;
        foreach (var process in processes) eventCount = ByteTotals.SaturatingAdd(eventCount, process.EventCount);
        return new(key, name, path, eventCount, Counters(processes), Array.AsReadOnly(processes.Select(process =>
            new ProcessSummary(process.Identity.Pid, process.Identity.Name, process.Identity.ExecutablePath,
                process.Identity.StartTimeUtc, process.Identity.LifetimeKey, process.Identity.AttributionStatus,
                process.EventCount, Counters([process]))).ToArray()));
    }

    private static TrafficCounters Counters(IReadOnlyList<ProcessObservation> processes)
    {
        var values = new long[14];
        void Add(int index, long value) => values[index] = ByteTotals.SaturatingAdd(values[index], value);
        foreach (var process in processes)
        {
            Add(0, process.Bytes.Receive); Add(1, process.Bytes.Send);
            foreach (var (bucket, bytes) in process.Breakdown)
            {
                int family = bucket.StartsWith("Ipv4/", StringComparison.Ordinal) ? 2 : 4;
                Add(family, bytes.Receive); Add(family + 1, bytes.Send);
                int locality = bucket.EndsWith("/Loopback", StringComparison.Ordinal) ? 6
                    : bucket.EndsWith("/NonLoopback", StringComparison.Ordinal) ? 8
                    : bucket.EndsWith("/LocalToLocal", StringComparison.Ordinal) ? 10 : 12;
                Add(locality, bytes.Receive); Add(locality + 1, bytes.Send);
            }
        }
        return new(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7],
            values[8], values[9], values[10], values[11], values[12], values[13]);
    }
}
