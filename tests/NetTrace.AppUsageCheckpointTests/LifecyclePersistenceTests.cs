using AppUsageCollectorCore;
using AppUsageElevatedCollector;
using AppUsageStorage;
using Microsoft.Data.Sqlite;

// These tests use real SQLite and the production parser-to-persistence path.
// A new aggregation/persistence pair represents a restarted collector process;
// they do not pretend to send Windows power broadcasts or exercise SCM recovery.
internal static class LifecyclePersistenceTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases => new (string, Func<Task>)[]
    {
        ("Lifecycle restart flushes pending events and startup cannot replay history", GracefulRestart),
        ("Lifecycle repeated restarts add only small new observations to large historical totals", RepeatedRestarts),
        ("Lifecycle crash preserves committed history without replaying lost pending events", AbruptRestart),
        ("Lifecycle sleep across local midnight preserves old day and adds only post-resume events", SleepAcrossMidnight),
        ("Lifecycle reused PID and fresh process lifetime preserve one executable history row", ReusedPid),
        ("Lifecycle wall-clock rollback cannot produce negative or giant persisted deltas", ClockRollback),
    };

    private static (UsagePersistence Persistence, CollectorAggregation Aggregation) Lifetime(
        StorageScope scope, ManualClock? clock = null, CountingResolver? resolver = null)
    {
        var persistence = new UsagePersistence(new SqliteUsageCheckpointStore(scope.Root),
            clock ?? new ManualClock(), Fixtures.Zone);
        return (persistence, Fixtures.Aggregation(persistence, resolver));
    }

    private static async Task Finish(UsagePersistence persistence)
    {
        // Production seals only after the ETW producer has drained. These tests
        // deliver all observations before applying the same final flush boundary.
        persistence.Seal();
        Check.True(await persistence.FlushAsync(TimeSpan.FromSeconds(2)), "bounded final checkpoint succeeds");
        await persistence.WaitForStorageIdleAsync();
        Check.Equal(0, persistence.Health.PendingKeys, "final flush leaves no pending observations");
    }

    private static void Integrity(StorageScope scope)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = scope.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        Check.Equal("ok", Convert.ToString(command.ExecuteScalar()), "complete SQLite integrity check");
        command.CommandText = "PRAGMA user_version;";
        Check.Equal((long)DailyAppUsageStore.CurrentSchemaVersion, Convert.ToInt64(command.ExecuteScalar()), "schema survives lifecycle");
        command.CommandText = "PRAGMA application_id;";
        Check.Equal(0x4E544155L, Convert.ToInt64(command.ExecuteScalar()), "App Usage database identity survives lifecycle");
    }

    private static async Task GracefulRestart()
    {
        using var scope = new StorageScope();
        var (first, before) = Lifetime(scope);
        before.Add(Fixtures.Raw(bytes: 512));
        before.Add(Fixtures.Raw(700, bytes: 128));
        Check.True(await first.CheckpointAsync(), "initial periodic checkpoint");
        before.Add(Fixtures.Raw(801, bytes: 37));
        before.Add(Fixtures.Raw(800, bytes: 19));
        await Finish(first);
        var original = scope.Open().GetUsageForDate(Fixtures.Today).Single();
        var creation = File.GetCreationTimeUtc(scope.DatabasePath);
        var (second, after) = Lifetime(scope);
        Check.True(await second.CheckpointAsync(), "new process initializes existing database");
        Check.Equal(original, scope.Open().GetUsageForDate(Fixtures.Today).Single(), "empty startup cannot rewrite earlier history");
        Check.True(before.CollectorInstanceId != after.CollectorInstanceId, "restart owns a fresh counter lifetime");
        Check.Equal(0L, after.GetSnapshot().EventCount, "fresh lifetime does not seed events from historical rows");
        Check.Equal(0, after.GetSnapshot().Applications.Count, "fresh lifetime does not seed byte counters from historical rows");
        after.Add(Fixtures.Raw(bytes: 13));
        after.Add(Fixtures.Raw(700, bytes: 7));
        await Finish(second);
        var final = scope.Open().GetUsageForDate(Fixtures.Today).Single();
        Check.Equal(562L, final.DownloadBytes, "periodic, final flush and restart RX each added once");
        Check.Equal(154L, final.UploadBytes, "periodic, final flush and restart TX each added once");
        Check.Equal(creation, File.GetCreationTimeUtc(scope.DatabasePath), "restart preserves database file");
        Integrity(scope);
    }

    private static async Task RepeatedRestarts()
    {
        using var scope = new StorageScope();
        scope.Open().AddUsageDeltas([
            Fixtures.Delta(1_000_000_000, 700_000_000),
            Fixtures.Delta(71, 29, Fixtures.Editor(), Fixtures.Today.AddDays(-7))
        ]);
        var earlier = scope.Open().GetUsageForDate(Fixtures.Today.AddDays(-7)).Single();
        var instances = new HashSet<Guid>();
        for (int index = 1; index <= 5; index++)
        {
            var (persistence, aggregation) = Lifetime(scope);
            Check.True(instances.Add(aggregation.CollectorInstanceId), "each restart has distinct collector identity");
            Check.True(await persistence.CheckpointAsync(), "empty startup checkpoint");
            aggregation.Add(Fixtures.Raw(bytes: index * 11));
            aggregation.Add(Fixtures.Raw(700, bytes: index * 3));
            for (int query = 0; query < 25; query++) aggregation.GetSnapshot();
            Check.True(await persistence.CheckpointAsync(), "new observation checkpoint");
            for (int query = 0; query < 25; query++) aggregation.GetSnapshot();
            await Finish(persistence);
            var row = scope.Open().GetUsageForDate(Fixtures.Today).Single();
            long factor = index * (index + 1) / 2;
            Check.Equal(1_000_000_000L + factor * 11, row.DownloadBytes, "history never becomes a new lifetime delta");
            Check.Equal(700_000_000L + factor * 3, row.UploadBytes, "TX history never becomes a new lifetime delta");
            Check.Equal(earlier, scope.Open().GetUsageForDate(Fixtures.Today.AddDays(-7)).Single(), "older date remains byte-for-byte equivalent");
        }
        Integrity(scope);
    }

    private static async Task AbruptRestart()
    {
        using var scope = new StorageScope();
        var (oldPersistence, oldAggregation) = Lifetime(scope);
        oldAggregation.Add(Fixtures.Raw(bytes: 2_048));
        oldAggregation.Add(Fixtures.Raw(700, bytes: 256));
        Check.True(await oldPersistence.CheckpointAsync(), "traffic before the forced termination window is durable");
        var committed = scope.Open().GetUsageForDate(Fixtures.Today).Single();
        oldAggregation.Add(Fixtures.Raw(bytes: 17));
        oldAggregation.Add(Fixtures.Raw(700, bytes: 5));
        Check.Equal(1, oldPersistence.Health.PendingKeys, "a process death can lose its uncheckpointed memory window");

        // Deliberately do not flush this abandoned lifetime. An actual killed
        // process cannot promise to recover these last accepted observations.
        var (recovered, aggregation) = Lifetime(scope);
        Check.True(await recovered.CheckpointAsync(), "recovery opens existing committed rows");
        Check.Equal(committed, scope.Open().GetUsageForDate(Fixtures.Today).Single(), "forced termination preserves every committed byte");
        Check.True(oldAggregation.CollectorInstanceId != aggregation.CollectorInstanceId, "crash recovery resets only live instance identity");
        aggregation.Add(Fixtures.Raw(bytes: 23));
        aggregation.Add(Fixtures.Raw(700, bytes: 7));
        await Finish(recovered);
        var row = scope.Open().GetUsageForDate(Fixtures.Today).Single();
        Check.Equal(2_071L, row.DownloadBytes, "recovered process adds exactly its new RX observations");
        Check.Equal(263L, row.UploadBytes, "recovered process adds exactly its new TX observations");
        Check.Equal(23L, recovered.Health.CommittedDownloadBytes, "new checkpoint health excludes old cumulative counters");
        Check.Equal(7L, recovered.Health.CommittedUploadBytes, "new TX checkpoint health excludes old cumulative counters");
        Integrity(scope);
    }

    private static async Task SleepAcrossMidnight()
    {
        using var scope = new StorageScope();
        var clock = new ManualClock();
        clock.Advance(TimeSpan.FromHours(7) + TimeSpan.FromMinutes(59));
        var beforeSleepAt = clock.GetUtcNow();
        var (beforeSleep, oldAggregation) = Lifetime(scope, clock);
        oldAggregation.Add(Fixtures.Raw(bytes: 333, timestamp: beforeSleepAt.UtcDateTime));
        oldAggregation.Add(Fixtures.Raw(700, bytes: 111, timestamp: beforeSleepAt.UtcDateTime));
        await Finish(beforeSleep);
        var priorDay = scope.Open().GetUsageForDate(Fixtures.Today).Single();

        // No observation during the sleep gap. Advancing time itself must not
        // synthesize bytes, regardless of checkpoint timer or midnight crossing.
        clock.Advance(TimeSpan.FromHours(6));
        var (afterSleep, aggregation) = Lifetime(scope, clock);
        Check.True(await afterSleep.CheckpointAsync(), "resume storage initialization");
        Check.Equal(priorDay, scope.Open().GetUsageForDate(Fixtures.Today).Single(), "resume retains pre-sleep day unchanged");
        Check.Equal(0, scope.Open().GetUsageForDate(Fixtures.Today.AddDays(1)).Count, "sleep time alone adds no usage");
        var afterSleepAt = clock.GetUtcNow();
        aggregation.Add(Fixtures.Raw(801, bytes: 41, timestamp: afterSleepAt.UtcDateTime));
        aggregation.Add(Fixtures.Raw(800, bytes: 13, timestamp: afterSleepAt.UtcDateTime));
        await Finish(afterSleep);
        var resumedDay = scope.Open().GetUsageForDate(Fixtures.Today.AddDays(1)).Single();
        Check.Equal(41L, resumedDay.DownloadBytes, "resume RX belongs to event's new local day");
        Check.Equal(13L, resumedDay.UploadBytes, "resume TX belongs to event's new local day");
        Check.Equal(afterSleepAt, resumedDay.LastSeen, "last seen reflects actual post-resume event");
        Check.Equal(priorDay, scope.Open().GetUsageForDate(Fixtures.Today).Single(), "post-resume checkpoint does not rewrite earlier day");
        Check.True(oldAggregation.CollectorInstanceId != aggregation.CollectorInstanceId, "resume creates fresh live counters");
        Integrity(scope);
    }

    private static async Task ReusedPid()
    {
        using var scope = new StorageScope();
        var beforeAt = Fixtures.Epoch;
        var afterAt = beforeAt.AddMinutes(2);
        var (first, oldAggregation) = Lifetime(scope, resolver: new CountingResolver((pid, _) =>
            Fixtures.Identity(pid, start: beforeAt.AddMinutes(-1))));
        oldAggregation.Add(Fixtures.Raw(pid: 4321, bytes: 99, timestamp: beforeAt));
        var oldProcess = oldAggregation.GetSnapshot().Applications.Single().Processes.Single();
        await Finish(first);
        var (second, newAggregation) = Lifetime(scope, resolver: new CountingResolver((pid, _) =>
            Fixtures.Identity(pid, start: afterAt.AddMinutes(-1))));
        newAggregation.Add(Fixtures.Raw(pid: 4321, bytes: 7, timestamp: afterAt));
        var newProcess = newAggregation.GetSnapshot().Applications.Single().Processes.Single();
        Check.True(oldProcess.LifetimeIdentity != newProcess.LifetimeIdentity, "reused PID has a distinct verified process lifetime");
        await Finish(second);
        var row = scope.Open().GetUsageForDate(Fixtures.Today).Single();
        Check.Equal(Fixtures.Brave().Key, row.ApplicationIdentity, "durable history follows executable rather than PID");
        Check.Equal(106L, row.DownloadBytes, "reused PID adds only accepted bytes once");
        Check.Equal(new DateTimeOffset(afterAt), row.LastSeen, "latest resumed process observation updates metadata timestamp");
        Integrity(scope);
    }

    private static async Task ClockRollback()
    {
        using var scope = new StorageScope();
        var (first, aggregation) = Lifetime(scope);
        aggregation.Add(Fixtures.Raw(bytes: 900, timestamp: Fixtures.Epoch));
        aggregation.Add(Fixtures.Raw(700, bytes: 90, timestamp: Fixtures.Epoch));
        await Finish(first);
        var earlier = scope.Open().GetUsageForDate(Fixtures.Today).Single();
        var rolledBackEvent = Fixtures.Epoch.AddHours(-2);
        var (second, recovered) = Lifetime(scope);
        recovered.Add(Fixtures.Raw(bytes: 3, timestamp: rolledBackEvent));
        recovered.Add(Fixtures.Raw(700, bytes: 1, timestamp: rolledBackEvent));
        Check.True(!recovered.Add(Fixtures.Raw(bytes: -1, timestamp: rolledBackEvent)), "negative payload rejected after recovery");
        await Finish(second);
        var row = scope.Open().GetUsageForDate(Fixtures.Today).Single();
        Check.Equal(903L, row.DownloadBytes, "wall-clock rollback adds only real RX event size");
        Check.Equal(91L, row.UploadBytes, "wall-clock rollback adds only real TX event size");
        Check.Equal(earlier.LastSeen, row.LastSeen, "older timestamp cannot replace durable last seen");
        Integrity(scope);
    }
}
