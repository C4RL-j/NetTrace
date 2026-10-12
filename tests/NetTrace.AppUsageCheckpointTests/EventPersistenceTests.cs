using AppUsageCollectorCore;
using AppUsageEtwProbe;
using AppUsageStorage;
using ApplicationIdentity = AppUsageStorage.ApplicationIdentity;

internal static class EventPersistenceTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases => new (string, Func<Task>)[]
    {
        ("RX ETW event persists download without upload", () => Direction(701, 111, 0)),
        ("TX ETW event persists upload without download", () => Direction(700, 0, 111)),
        ("IPv4 nonloopback event persists", () => Direction(701, 111, 0)),
        ("IPv6 nonloopback RX event persists", () => Direction(801, 111, 0)),
        ("IPv6 nonloopback TX event persists", () => Direction(800, 0, 111)),
        ("IPv4 loopback stays diagnostic and persists zero", () => Excluded(701, TrafficClass.Loopback)),
        ("IPv6 loopback stays diagnostic and persists zero", () => Excluded(801, TrafficClass.Loopback)),
        ("Local to local traffic is excluded from default persistence", () => Excluded(701, TrafficClass.LocalToLocal)),
        ("Unclassifiable traffic is excluded from default persistence", () => Excluded(701, TrafficClass.Unknown)),
        ("Multiple RX and TX events accumulate as true deltas", Accumulate),
        ("Multiple PIDs and path case variants aggregate one app day", SameExecutable),
        ("System PID uses stable System fallback", System),
        ("PID zero uses stable Unknown fallback", () => Unknown(new(0, "Unknown / Unattributed", null, null, "zero PID"))),
        ("Unavailable executable uses stable Unknown fallback", () => Unknown(new(123, "PID 123", null, Fixtures.Epoch.AddHours(-1), "path unavailable"))),
        ("Unverified lifetime uses stable Unknown fallback", () => Unknown(new(123, "Brave", @"C:\Apps\Brave\brave.exe", null, "lifetime unavailable"))),
        ("Executable metadata and event last seen reach SQLite", Metadata),
        ("Event date uses explicit local timezone instead of checkpoint clock", EventLocalDate),
        ("Midnight checkpoint preserves 1000 previous day and 2000 next day", Midnight),
        ("Repeated read only snapshots cannot duplicate usage", SnapshotsReadOnly),
        ("Accepted event resolves process identity exactly once", ResolveOnce),
        ("Rejected negative ETW event leaves live and pending counters unchanged", NegativeEtw),
        ("Direct mapper rejects negative event bytes", Check.Sync(NegativeMapper)),
        ("Zero byte event causes no durable transaction", ZeroEvent),
        ("New collector lifetime continues existing persisted row", Restart),
    };

    private static async Task Direction(int id, long download, long upload)
    {
        using var scope = new StorageScope();
        var persistence = new UsagePersistence(new SqliteUsageCheckpointStore(scope.Root), new ManualClock(), Fixtures.Zone);
        var aggregation = Fixtures.Aggregation(persistence);
        Check.True(aggregation.Add(Fixtures.Raw(id, bytes: 111)), "raw network event accepted");
        Check.True(await persistence.CheckpointAsync(), "checkpoint succeeds");
        var row = scope.Open().GetUsageForDate(Fixtures.Today).Single();
        Check.Equal(download, row.DownloadBytes, "RX to download");
        Check.Equal(upload, row.UploadBytes, "TX to upload");
    }

    private static async Task Excluded(int id, TrafficClass traffic)
    {
        using var scope = new StorageScope();
        var backend = new TestBackend(scope.Open());
        var persistence = new UsagePersistence(backend, new ManualClock(), Fixtures.Zone);
        var aggregation = Fixtures.Aggregation(persistence);
        Check.True(aggregation.Add(Fixtures.Raw(id, bytes: 111, traffic: traffic)), "excluded locality remains valid diagnostic event");
        var snapshot = aggregation.GetSnapshot();
        Check.Equal(111L, snapshot.Applications.Single().Counters.RxBytes, "live diagnostic counter remains");
        await persistence.CheckpointAsync();
        Check.Equal(0, backend.AddCalls, "excluded traffic causes no persistence batch");
        Check.Equal(0, scope.Open().GetUsageForDate(Fixtures.Today).Count, "zero persisted app rows");
    }

    private static async Task Accumulate()
    {
        using var scope = new StorageScope();
        var backend = new TestBackend(scope.Open());
        var persistence = new UsagePersistence(backend, new ManualClock(), Fixtures.Zone);
        var aggregation = Fixtures.Aggregation(persistence);
        for (int i = 0; i < 100; i++)
        {
            aggregation.Add(Fixtures.Raw(701, bytes: 11));
            aggregation.Add(Fixtures.Raw(700, bytes: 3));
        }
        Check.Equal(0, backend.AddCalls, "200 events perform no disk writes");
        Check.Equal(1, persistence.Health.PendingKeys, "same app coalesced");
        await persistence.CheckpointAsync();
        Check.Equal(1, backend.AddCalls, "single batched write");
        var row = scope.Open().GetUsageForDate(Fixtures.Today).Single();
        Check.Equal(1_100L, row.DownloadBytes, "event-derived exact download");
        Check.Equal(300L, row.UploadBytes, "event-derived exact upload");
    }

    private static async Task SameExecutable()
    {
        using var scope = new StorageScope();
        var persistence = new UsagePersistence(new SqliteUsageCheckpointStore(scope.Root), new ManualClock(), Fixtures.Zone);
        var resolver = new CountingResolver((pid, _) => Fixtures.Identity(pid,
            pid == 2 ? "c:/APPS/BRAVE/brave.exe" : @"C:\Apps\Brave\brave.exe"));
        var aggregation = Fixtures.Aggregation(persistence, resolver);
        aggregation.Add(Fixtures.Raw(pid: 1, bytes: 10));
        aggregation.Add(Fixtures.Raw(pid: 2, bytes: 20));
        aggregation.Add(Fixtures.Raw(pid: 3, bytes: 30));
        await persistence.CheckpointAsync();
        var row = scope.Open().GetUsageForDate(Fixtures.Today).Single();
        Check.Equal(Fixtures.Brave().Key, row.ApplicationIdentity, "normalized executable identity");
        Check.Equal(60L, row.DownloadBytes, "three PIDs contribute once each");
        Check.True(!row.ApplicationIdentity.Contains("pid", StringComparison.OrdinalIgnoreCase), "no PID identity persisted");
    }

    private static async Task System()
    {
        using var scope = new StorageScope();
        var persistence = new UsagePersistence(new SqliteUsageCheckpointStore(scope.Root), new ManualClock(), Fixtures.Zone);
        var aggregation = Fixtures.Aggregation(persistence, new CountingResolver((pid, _) => new(pid, "System", null, null, "System PID")));
        aggregation.Add(Fixtures.Raw(pid: 4, bytes: 99));
        await persistence.CheckpointAsync();
        var row = scope.Open().GetUsageForDate(Fixtures.Today).Single();
        Check.Equal(ApplicationIdentity.System.Key, row.ApplicationIdentity, "stable System identity");
        Check.Equal(99L, row.DownloadBytes, "System bytes retained");
    }

    private static async Task Unknown(ProcessIdentity identity)
    {
        using var scope = new StorageScope();
        var persistence = new UsagePersistence(new SqliteUsageCheckpointStore(scope.Root), new ManualClock(), Fixtures.Zone);
        var aggregation = Fixtures.Aggregation(persistence, new CountingResolver((_, _) => identity));
        aggregation.Add(Fixtures.Raw(pid: identity.Pid, bytes: 99));
        await persistence.CheckpointAsync();
        var row = scope.Open().GetUsageForDate(Fixtures.Today).Single();
        Check.Equal(ApplicationIdentity.Unknown.Key, row.ApplicationIdentity, "stable Unknown identity");
        Check.Equal(99L, row.DownloadBytes, "Unknown bytes retained");
        Check.True(row.ExecutablePath is null, "fallback does not invent executable metadata");
    }

    private static async Task Metadata()
    {
        using var scope = new StorageScope();
        var persistence = new UsagePersistence(new SqliteUsageCheckpointStore(scope.Root), new ManualClock(), Fixtures.Zone);
        Fixtures.Aggregation(persistence).Add(Fixtures.Raw(timestamp: Fixtures.Epoch.AddSeconds(7)));
        await persistence.CheckpointAsync();
        var row = scope.Open().GetUsageForDate(Fixtures.Today).Single();
        Check.Equal("Brave", row.DisplayName, "cached process name metadata");
        Check.Equal("brave.exe", row.ExecutableName, "executable filename metadata");
        Check.Equal(@"C:\Apps\Brave\brave.exe", row.ExecutablePath, "normalized executable path metadata");
        Check.Equal(new DateTimeOffset(Fixtures.Epoch.AddSeconds(7)), row.LastSeen, "ETW event last seen rather than checkpoint time");
    }

    private static async Task EventLocalDate()
    {
        using var scope = new StorageScope();
        var persistence = new UsagePersistence(new SqliteUsageCheckpointStore(scope.Root), new ManualClock(), Fixtures.Zone);
        var eventUtc = new DateTime(2026, 10, 12, 16, 1, 0, DateTimeKind.Utc);
        Fixtures.Aggregation(persistence, new CountingResolver((pid, _) => Fixtures.Identity(pid, start: eventUtc.AddHours(-1))))
            .Add(Fixtures.Raw(timestamp: eventUtc));
        await persistence.CheckpointAsync();
        Check.Equal(64L, scope.Open().GetUsageForDate(Fixtures.Today).Single().DownloadBytes, "UTC previous day maps to local next day");
        Check.Equal(0, scope.Open().GetUsageForDate(Fixtures.Today.AddDays(-1)).Count, "UTC date not used");
    }

    private static async Task Midnight()
    {
        using var scope = new StorageScope();
        var clock = new ManualClock();
        var persistence = new UsagePersistence(new SqliteUsageCheckpointStore(scope.Root), clock, Fixtures.Zone);
        var first = new DateTime(2026, 10, 12, 15, 59, 59, DateTimeKind.Utc);
        var second = first.AddSeconds(2);
        var aggregation = Fixtures.Aggregation(persistence,
            new CountingResolver((pid, _) => Fixtures.Identity(pid, start: first.AddHours(-1))));
        aggregation.Add(Fixtures.Raw(bytes: 1_000, timestamp: first));
        aggregation.Add(Fixtures.Raw(bytes: 2_000, timestamp: second));
        Check.Equal(2, persistence.Health.PendingKeys, "date is part of pending key");
        clock.Advance(TimeSpan.FromDays(1));
        await persistence.CheckpointAsync();
        var storage = scope.Open();
        Check.Equal(1_000L, storage.GetUsageForDate(Fixtures.Today.AddDays(-1)).Single().DownloadBytes, "23:59:59 previous local day");
        Check.Equal(2_000L, storage.GetUsageForDate(Fixtures.Today).Single().DownloadBytes, "00:00:01 next local day");
    }

    private static async Task SnapshotsReadOnly()
    {
        using var scope = new StorageScope();
        var backend = new TestBackend(scope.Open());
        var persistence = new UsagePersistence(backend, new ManualClock(), Fixtures.Zone);
        var aggregation = Fixtures.Aggregation(persistence);
        aggregation.Add(Fixtures.Raw(bytes: 123));
        for (int i = 0; i < 50; i++) aggregation.GetSnapshot();
        await persistence.CheckpointAsync();
        for (int i = 0; i < 50; i++) aggregation.GetSnapshot();
        await persistence.CheckpointAsync();
        Check.Equal(1, backend.AddCalls, "snapshot reads never persist cumulative totals");
        Check.Equal(123L, scope.Open().GetUsageForDate(Fixtures.Today).Single().DownloadBytes, "original event counted once");
    }

    private static async Task ResolveOnce()
    {
        var backend = new TestBackend();
        var persistence = new UsagePersistence(backend, new ManualClock(), Fixtures.Zone);
        var resolver = new CountingResolver((pid, _) => Fixtures.Identity(pid));
        var aggregation = Fixtures.Aggregation(persistence, resolver);
        aggregation.Add(Fixtures.Raw());
        Check.Equal(1, resolver.Calls, "live and persistence reuse a single resolution");
        await persistence.CheckpointAsync();
        Check.Equal(1, backend.Committed.Single().Length, "same attribution reaches checkpoint");
    }

    private static async Task NegativeEtw()
    {
        var backend = new TestBackend();
        var persistence = new UsagePersistence(backend, new ManualClock(), Fixtures.Zone);
        var aggregation = Fixtures.Aggregation(persistence);
        Check.True(!aggregation.Add(Fixtures.Raw(bytes: -1)), "negative payload is rejected by proven parser");
        await persistence.CheckpointAsync();
        Check.Equal(0L, aggregation.GetSnapshot().EventCount, "live counters unchanged");
        Check.Equal(0, backend.AddCalls, "negative bytes never reach storage");
    }

    private static void NegativeMapper()
    {
        var persistence = new UsagePersistence(new TestBackend(), new ManualClock(), Fixtures.Zone);
        var mapper = new UsageEventPersistence(persistence, Fixtures.Zone);
        Check.Throws<ArgumentOutOfRangeException>(() => mapper.RecordActivity(new(123, Fixtures.Epoch, -1,
            TrafficDirection.Receive, IpFamily.Ipv4, TrafficClass.NonLoopback, null, null, null), Fixtures.Identity(123)), "negative mapper input");
        Check.Equal(0, persistence.Health.PendingKeys, "negative mapper input cannot create pending data");
    }

    private static async Task ZeroEvent()
    {
        var backend = new TestBackend();
        var persistence = new UsagePersistence(backend, new ManualClock(), Fixtures.Zone);
        Fixtures.Aggregation(persistence).Add(Fixtures.Raw(bytes: 0));
        await persistence.CheckpointAsync();
        Check.Equal(0, backend.AddCalls, "zero event has no write");
    }

    private static async Task Restart()
    {
        using var scope = new StorageScope();
        var first = new UsagePersistence(new SqliteUsageCheckpointStore(scope.Root), new ManualClock(), Fixtures.Zone);
        var firstAggregation = Fixtures.Aggregation(first);
        firstAggregation.Add(Fixtures.Raw(bytes: 300));
        firstAggregation.Add(Fixtures.Raw(700, bytes: 20));
        first.Seal();
        Check.True(await first.FlushAsync(TimeSpan.FromSeconds(2)), "first service lifetime final flush");
        var second = new UsagePersistence(new SqliteUsageCheckpointStore(scope.Root), new ManualClock(), Fixtures.Zone);
        var secondAggregation = Fixtures.Aggregation(second);
        secondAggregation.Add(Fixtures.Raw(bytes: 700));
        secondAggregation.Add(Fixtures.Raw(700, bytes: 40));
        Check.Equal(700L, secondAggregation.GetSnapshot().Applications.Single().Counters.RxBytes, "new collector starts independent live counters");
        second.Seal();
        Check.True(await second.FlushAsync(TimeSpan.FromSeconds(2)), "second service lifetime final flush");
        var row = scope.Open().GetUsageForDate(Fixtures.Today).Single();
        Check.Equal(1_000L, row.DownloadBytes, "existing daily row survives collector restart");
        Check.Equal(60L, row.UploadBytes, "restart adds only new TX events");
    }
}
