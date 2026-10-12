using AppUsageIpc;

internal static class AggregationLifecycleTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Each service aggregation lifetime starts with a fresh instance and empty in-memory counters", Check.Sync(FreshLifetime)),
        ("Service snapshot requests preserve cumulative IPv4/IPv6, RX/TX and loopback counters", Check.Sync(CumulativeCounters)),
        ("A wall-clock adjustment cannot make monotonic service uptime negative", Check.Sync(MonotonicUptime)),
        ("No request means no pointless snapshot serialization or request-count growth", Check.Sync(RequestDriven)),
        ("Out-of-order ETW status queries preserve monotonic event and loss counters", Check.Sync(LossMonotonic)),
        ("The service snapshot preserves System and Unknown metadata buckets across serialization", BucketRoundTrip),
    ];

    private static void FreshLifetime()
    {
        var first = Fixtures.Aggregation(); first.Add(Fixtures.Raw(bytes: 100));
        var before = first.GetSnapshot(); var after = Fixtures.Aggregation().GetSnapshot();
        Check.True(before.CollectorInstanceId != after.CollectorInstanceId, "fresh instance identifies collector restart");
        Check.Equal(0L, after.EventCount, "new lifetime has no inherited event history");
        Check.Equal(0, after.Applications.Count, "no persisted applications cross restart");
        Check.Equal(0L, after.Unknown.Counters.RxBytes, "unknown counters start empty");
    }
    private static void CumulativeCounters()
    {
        var aggregation = Fixtures.Aggregation();
        aggregation.Add(Fixtures.Raw(bytes: 10)); aggregation.Add(Fixtures.Raw(701, bytes: 20));
        aggregation.Add(Fixtures.Raw(800, bytes: 30)); aggregation.Add(Fixtures.Raw(801, bytes: 40));
        var snapshot = aggregation.GetSnapshot(); var next = aggregation.GetSnapshot();
        var counters = snapshot.Applications.Single().Counters;
        Check.Equal(60L, counters.RxBytes, "independent RX"); Check.Equal(40L, counters.TxBytes, "independent TX");
        Check.Equal(20L, counters.Ipv4RxBytes, "IPv4 RX"); Check.Equal(30L, counters.Ipv6TxBytes, "IPv6 TX");
        Check.Equal(counters.RxBytes, counters.LoopbackRxBytes, "loopback classification survives service aggregation");
        Check.Equal(counters, next.Applications.Single().Counters, "reads never reset counters");
        Check.Equal(123, snapshot.Applications.Single().Processes.Single().ProcessId, "attributed PID retained");
    }
    private static void MonotonicUptime()
    {
        var clock = new ManualClock(); var aggregation = Fixtures.Aggregation(clock);
        clock.Advance(TimeSpan.FromSeconds(5)); clock.MoveWallClock(TimeSpan.FromHours(-2));
        Check.Equal(TimeSpan.FromSeconds(5), aggregation.GetSnapshot().CollectorUptime, "wall-clock rollback does not affect elapsed uptime");
    }
    private static void RequestDriven()
    {
        var aggregation = Fixtures.Aggregation();
        for (int i = 0; i < 100; i++) aggregation.Add(Fixtures.Raw(bytes: 1));
        Check.Equal(0L, aggregation.GetSnapshot(countRequest: false).Metrics.SnapshotsServed, "events do not count as IPC polling");
        aggregation.GetSnapshot();
        Check.Equal(1L, aggregation.GetSnapshot(countRequest: false).Metrics.SnapshotsServed, "only explicit request increments cadence count");
    }
    private static void LossMonotonic()
    {
        var aggregation = Fixtures.Aggregation();
        aggregation.UpdateCaptureStatus(new("owned", true, true, true, 5, 100, 3, 2));
        aggregation.UpdateCaptureStatus(new("owned", true, true, true, 2, 90, 1, 1));
        var snapshot = aggregation.GetSnapshot();
        Check.Equal(5, snapshot.LostEventCount, "out-of-order loss report cannot reduce known loss");
        Check.Equal(100L, snapshot.ProviderEventCount, "provider count remains monotonic");
        aggregation.UpdateCaptureStatus(new("owned", true, false, false, null, 100, 3, 2));
        Check.Equal(-1, aggregation.GetSnapshot().LostEventCount, "unavailable loss remains explicit");
    }
    private static async Task BucketRoundTrip()
    {
        var aggregation = new AppUsageElevatedCollector.CollectorAggregation(Fixtures.Schema,
            new Fixtures.Resolver((pid, _) => pid == 4 ? new(4, "System", null, null, "System")
                : new(pid, $"PID {pid}", null, null, "Lookup unavailable")), Fixtures.Local);
        aggregation.Add(Fixtures.Raw(pid: 4, bytes: 20)); aggregation.Add(Fixtures.Raw(701, pid: 456, bytes: 30));
        using var stream = new MemoryStream();
        await IpcProtocol.WriteResponseAsync(stream, new(Protocol.ServiceVersion, "Ok", Snapshot: aggregation.GetSnapshot()));
        stream.Position = 0; var result = (await IpcProtocol.ReadResponseAsync(stream, expectedVersion: Protocol.ServiceVersion)).Snapshot!;
        Check.Equal(20L, result.System.Counters.TxBytes, "System bytes across IPC serialization");
        Check.Equal(30L, result.Unknown.Counters.RxBytes, "Unknown bytes across IPC serialization");
        Check.Equal(456, result.Unknown.Processes.Single().ProcessId, "unknown PID remains identifiable");
    }
}
