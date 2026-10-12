using System.Net;
using AppUsageElevatedCollector;
using AppUsageEtwProbe;

internal static class AggregationTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    internal static readonly ProviderSchema Schema = new(true, 3,
    [
        new(700, 1, TrafficDirection.Send, IpFamily.Ipv4, []),
        new(701, 1, TrafficDirection.Receive, IpFamily.Ipv4, []),
        new(800, 1, TrafficDirection.Send, IpFamily.Ipv6, []),
        new(801, 1, TrafficDirection.Receive, IpFamily.Ipv6, [])
    ]);
    private static readonly IReadOnlySet<IPAddress> Local = new HashSet<IPAddress>
        { IPAddress.Parse("192.0.2.10"), IPAddress.Parse("192.0.2.11") };

    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Snapshots preserve independent RX/TX, IP families, and all traffic classes", Check.Sync(DirectionalCounters)),
        ("Executable grouping preserves multiple process IDs and each process lifetime", Check.Sync(MultiplePids)),
        ("Same executable name at different paths remains separate applications", Check.Sync(DifferentPaths)),
        ("Reusing a PID with another start time retains two lifetime observations", Check.Sync(PidReuse)),
        ("System and unresolved executable identities remain visible in separate diagnostic buckets", Check.Sync(SystemAndUnknown)),
        ("Process lookup failure preserves bytes and the unresolved PID", Check.Sync(LookupFailure)),
        ("Snapshot requests preserve cumulative counters and count served snapshots separately", Check.Sync(CumulativeSnapshots)),
        ("A collector restart gets a new instance identity and fresh zero counters", Check.Sync(RestartSemantics)),
        ("Large counters saturate safely and malformed or negative activities are ignored", Check.Sync(CounterSafety)),
        ("Lost-event metadata distinguishes actual loss from unavailable loss queries", Check.Sync(LostEvents)),
        ("Out-of-order client health queries cannot decrease cumulative controller counters", Check.Sync(OutOfOrderStatus)),
        ("Concurrent event aggregation produces stable detached snapshots", ConcurrentSnapshots),
    ];

    private static void DirectionalCounters()
    {
        var collector = Create();
        foreach (var (id, bytes, kind) in new[]
        {
            (700, 10L, "external"), (701, 20L, "external"),
            (800, 30L, "external"), (801, 40L, "external"),
            (700, 50L, "loopback"), (701, 60L, "loopback"),
            (700, 70L, "local"), (701, 80L, "local"),
            (800, 90L, "unknown"), (801, 100L, "unknown")
        }) Check.True(collector.Add(Raw(id, bytes: bytes, kind: kind)), "supported activity accepted");
        var application = collector.GetSnapshot().Applications.Single();
        var counters = application.Counters;
        Check.Equal(300L, counters.RxBytes, "logical receive");
        Check.Equal(250L, counters.TxBytes, "logical send");
        Check.Equal(160L, counters.Ipv4RxBytes, "IPv4 receive");
        Check.Equal(130L, counters.Ipv4TxBytes, "IPv4 send");
        Check.Equal(140L, counters.Ipv6RxBytes, "IPv6 receive");
        Check.Equal(120L, counters.Ipv6TxBytes, "IPv6 send");
        Check.Equal(60L, counters.LoopbackRxBytes, "loopback receive");
        Check.Equal(50L, counters.LoopbackTxBytes, "loopback send");
        Check.Equal(60L, counters.NonLoopbackRxBytes, "external receive");
        Check.Equal(40L, counters.NonLoopbackTxBytes, "external send");
        Check.Equal(80L, counters.LocalToLocalRxBytes, "local proxy receive");
        Check.Equal(70L, counters.LocalToLocalTxBytes, "local proxy send");
        Check.Equal(100L, counters.UnclassifiedRxBytes, "unclassified receive");
        Check.Equal(90L, counters.UnclassifiedTxBytes, "unclassified send");
        Check.Equal(10L, application.EventCount, "each network event counted once");
        Check.Equal(counters, application.Processes.Single().Counters, "per-process detail survives reduction");
    }

    private static void MultiplePids()
    {
        var collector = Create((pid, _) => Identity(pid) with
            { ExecutablePath = pid == 101 ? @"C:\Apps\brave.exe" : @"c:\apps\BRAVE.EXE" });
        collector.Add(Raw(pid: 101, bytes: 100));
        collector.Add(Raw(701, pid: 202, bytes: 200));
        var app = collector.GetSnapshot().Applications.Single();
        Check.Equal(2, app.Processes.Count, "multiple PIDs stay inspectable");
        Check.Equal(100L, app.Counters.TxBytes, "grouped send bytes");
        Check.Equal(200L, app.Counters.RxBytes, "grouped receive bytes");
        Check.Equal(2, app.Processes.Select(process => process.LifetimeIdentity).Distinct().Count(), "process lifetimes remain distinct");
    }

    private static void DifferentPaths()
    {
        var collector = Create((pid, _) => Identity(pid) with { ExecutablePath = $@"C:\Apps\{pid}\brave.exe" });
        collector.Add(Raw(pid: 101));
        collector.Add(Raw(pid: 202));
        Check.Equal(2, collector.GetSnapshot().Applications.Count, "matching filenames do not erase executable identity");
    }

    private static void PidReuse()
    {
        var collector = Create((pid, timestamp) => Identity(pid) with
            { StartTimeUtc = timestamp == Epoch ? Epoch : Epoch.AddSeconds(1) });
        collector.Add(Raw(bytes: 100));
        collector.Add(Raw(bytes: 200) with { TimestampUtc = Epoch.AddSeconds(2) });
        var app = collector.GetSnapshot().Applications.Single();
        Check.Equal(2, app.Processes.Count, "reused PID split by lifetime");
        Check.Equal(300L, app.Counters.TxBytes, "lifetime grouping retains cumulative bytes");
        Check.Equal(2, app.Processes.Select(process => process.StartTimeUtc).Distinct().Count(), "start times cross IPC boundary");
    }

    private static void SystemAndUnknown()
    {
        var collector = Create((pid, _) => pid == 4 ? new(4, "System", null, null, "System")
            : Identity(pid) with { ExecutablePath = pid == 302 ? @"C:\Apps\client.exe" : null,
                StartTimeUtc = pid == 301 ? Epoch : null });
        collector.Add(Raw(pid: 4, bytes: 100));
        collector.Add(Raw(pid: 301, bytes: 200));
        collector.Add(Raw(pid: 302, bytes: 300));
        collector.Add(Raw(pid: 303, bytes: 400));
        var snapshot = collector.GetSnapshot();
        Check.Equal(100L, snapshot.System.Counters.TxBytes, "system traffic is retained");
        Check.Equal(900L, snapshot.Unknown.Counters.TxBytes, "unverified app identity is explicitly unknown");
        Check.Equal(3, snapshot.Unknown.Processes.Count, "unknown detail retains each PID");
        Check.Equal(0, snapshot.Applications.Count, "unresolved identity is not presented as a durable application");
    }

    private static void LookupFailure()
    {
        var collector = Create((_, _) => throw new UnauthorizedAccessException());
        collector.Add(Raw(pid: 1234, bytes: 512));
        var unknown = collector.GetSnapshot().Unknown;
        Check.Equal(512L, unknown.Counters.TxBytes, "failed lookup does not drop traffic");
        Check.Equal(1234, unknown.Processes.Single().ProcessId, "unresolved PID retained");
        Check.Equal<string?>(null, unknown.Processes.Single().ExecutablePath, "path is not fabricated");
    }

    private static void CumulativeSnapshots()
    {
        var collector = Create();
        collector.Add(Raw(bytes: 100));
        var first = collector.GetSnapshot();
        var second = collector.GetSnapshot();
        Check.Equal(first.Applications.Single().Counters, second.Applications.Single().Counters, "read does not reset counters");
        collector.Add(Raw(bytes: 50));
        var third = collector.GetSnapshot(countRequest: false);
        Check.Equal(150L, third.Applications.Single().Counters.TxBytes, "cumulative bytes advance");
        Check.Equal(100L, first.Applications.Single().Counters.TxBytes, "previous snapshot is detached");
        Check.Equal(2L, third.Metrics.SnapshotsServed, "internal snapshot does not count as an IPC request");
        Check.True(third.Capabilities.CumulativeSinceStart, "counter semantics are explicit");
    }

    private static void RestartSemantics()
    {
        var first = Create();
        first.Add(Raw());
        var second = Create();
        var before = first.GetSnapshot(); var after = second.GetSnapshot();
        Check.True(before.CollectorInstanceId != after.CollectorInstanceId, "restart has a new instance discriminator");
        Check.Equal(0L, after.EventCount, "restart does not inherit historical events");
        Check.Equal(0, after.Applications.Count, "restart begins without persisted application counters");
        Check.Equal(0L, after.Unknown.Counters.TxBytes, "restart unknown counters are fresh");
    }

    private static void CounterSafety()
    {
        var collector = Create();
        Check.True(!collector.Add(Raw(bytes: -1)), "negative bytes rejected");
        Check.True(!collector.Add(Raw(pid: -1)), "negative PID rejected");
        Check.True(!collector.Add(Raw() with { EventId = 999 }), "unsupported events rejected");
        collector.Add(Raw(bytes: long.MaxValue));
        collector.Add(Raw(bytes: 1));
        Check.Equal(long.MaxValue, collector.GetSnapshot().Applications.Single().Counters.TxBytes, "send saturates without wrapping");
        Check.Equal(2L, collector.GetSnapshot().EventCount, "invalid raw events are excluded from accepted totals");
    }

    private static void LostEvents()
    {
        var collector = Create();
        collector.UpdateCaptureStatus(new("test-session", true, true, true, 7, 100, 4, 2));
        var available = collector.GetSnapshot();
        Check.True(available.Capabilities.LostEventCountAvailable, "successful loss query is visible");
        Check.Equal(7, available.LostEventCount, "actual event loss survives snapshot");
        Check.Equal(100L, available.ProviderEventCount, "provider event counts survive snapshot");
        Check.Equal(4L, available.UnsupportedEventCount, "unsupported counts survive snapshot");
        Check.Equal(2L, available.MalformedEventCount, "malformed counts survive snapshot");
        collector.UpdateCaptureStatus(new("test-session", true, true, false, null, 100, 4, 2));
        var unavailable = collector.GetSnapshot();
        Check.True(!unavailable.Capabilities.LostEventCountAvailable, "loss query availability is explicit");
        Check.Equal(-1, unavailable.LostEventCount, "unknown loss is not fabricated as zero");
    }

    private static async Task ConcurrentSnapshots()
    {
        var collector = Create();
        var old = collector.GetSnapshot(countRequest: false);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                collector.Add(Raw(bytes: 1));
                if (i % 40 == 0) collector.GetSnapshot(countRequest: false);
            }
        })));
        var snapshot = collector.GetSnapshot();
        Check.Equal(800L, snapshot.EventCount, "concurrent events counted once");
        Check.Equal(800L, snapshot.Applications.Single().Counters.TxBytes, "concurrent byte updates not lost");
        Check.Equal(0L, old.EventCount, "a retained snapshot cannot observe later mutations");
        Check.Equal(0, old.Applications.Count, "retained collections remain detached");
    }

    private static void OutOfOrderStatus()
    {
        var collector = Create();
        collector.UpdateCaptureStatus(new("test-session", true, true, true, 7, 200, 4, 2));
        var first = collector.GetSnapshot();
        collector.UpdateCaptureStatus(new("test-session", true, true, true, 3, 100, 1, 0));
        var second = collector.GetSnapshot();
        Check.Equal(first.ProviderEventCount, second.ProviderEventCount, "a delayed client query cannot lower provider events");
        Check.Equal(first.UnsupportedEventCount, second.UnsupportedEventCount, "unsupported event counts remain cumulative");
        Check.Equal(first.MalformedEventCount, second.MalformedEventCount, "malformed event counts remain cumulative");
        Check.Equal(first.LostEventCount, second.LostEventCount, "known loss counts remain cumulative");
        collector.UpdateCaptureStatus(new("test-session", true, true, false, null, 200, 4, 2));
        Check.Equal(-1, collector.GetSnapshot().LostEventCount, "a failed query still reports unavailable loss");
        collector.UpdateCaptureStatus(new("test-session", true, true, true, 3, 200, 4, 2));
        Check.Equal(7, collector.GetSnapshot().LostEventCount, "a later available query retains the maximum known loss");
    }

    internal static CollectorAggregation Create(Func<int, DateTime, ProcessIdentity>? resolve = null) =>
        new(Schema, new Resolver(resolve ?? ((pid, _) => Identity(pid))), Local);
    private static ProcessIdentity Identity(int pid) => new(pid, "brave.exe", @"C:\Apps\brave.exe", Epoch, "Fixture resolved");
    internal static RawNetworkEvent Raw(int id = 700, int pid = 101, long bytes = 64, string kind = "external")
    {
        bool v6 = id >= 800;
        IPAddress? source = kind == "unknown" ? null : kind == "loopback" ? (v6 ? IPAddress.IPv6Loopback : IPAddress.Loopback)
            : IPAddress.Parse(v6 ? "2001:db8::10" : "192.0.2.10");
        IPAddress destination = kind == "loopback" ? (v6 ? IPAddress.IPv6Loopback : IPAddress.Parse("127.1.2.3"))
            : IPAddress.Parse(v6 ? "2001:db8::20" : kind == "local" ? "192.0.2.11" : "192.0.2.20");
        return new(id, 1, 4, Epoch, new Dictionary<string, object?>
            { ["PID"] = pid, ["size"] = bytes, ["saddr"] = source, ["daddr"] = destination });
    }
    private sealed class Resolver(Func<int, DateTime, ProcessIdentity> resolve) : IProcessResolver
    {
        public ProcessIdentity Resolve(int pid, DateTime eventTimeUtc) => resolve(pid, eventTimeUtc);
    }
}
