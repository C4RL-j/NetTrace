using System.Diagnostics;
using System.Security.Cryptography;
using AppUsageCollectorCore;
using AppUsageIpc;
using AppUsageStorage;
using Microsoft.Data.Sqlite;
using ApplicationIdentity = AppUsageStorage.ApplicationIdentity;

internal static class QueryTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Today uses the current local calendar date with exact fixture totals", Today),
        ("7D aggregates today and the previous six local dates", SevenDays),
        ("30D aggregates today and the previous twenty-nine local dates", ThirtyDays),
        ("Inclusive 7D boundaries exclude day seven and future dates", SevenDayBoundaries),
        ("Inclusive 30D boundaries exclude day thirty and future dates", ThirtyDayBoundaries),
        ("A local midnight change selects a new calendar day rather than a rolling window", LocalMidnight),
        ("Each persistent application appears once across multiple daily rows", AggregatesIdentity),
        ("Download totals are exact sums across application and daily rows", DownloadSum),
        ("Upload totals are exact sums across application and daily rows", UploadSum),
        ("Logical totals retain overlapping Brave and NetMod logical usage", LogicalSum),
        ("Tracked application count counts aggregate application identities", TrackedCount),
        ("Applications sort by total bytes descending", DescendingSort),
        ("Equal totals use deterministic application identity ordering", TieBreak),
        ("System remains a normal usage row with its stable flag", SystemRow),
        ("Unknown remains a normal unattributed usage row with its stable flag", UnknownRow),
        ("Missing history returns a valid available empty result", Empty),
        ("Zero-use rows do not contribute applications or tracking count", ZeroRows),
        ("Persisted executable metadata and historical latest names survive aggregation", Metadata),
        ("Unavailable storage returns a typed result with fixed safe diagnostics", Unavailable),
        ("Readable persisted history remains available with degraded collection state", Degraded),
        ("Readable old history reports degraded when current persistence cannot initialize", TrackingUnavailable),
        ("Unreadable history exposes no partial rows or private exception messages", FailureHasNoPartialResult),
        ("An invalid typed range is rejected before the storage reader runs", InvalidRange),
        ("Caller cancellation bounds a blocked query and leaves future queries usable", CancelledRead),
        ("An internal query deadline bounds a blocked storage reader", BoundedRead),
        ("Repeated timed-out requests retain worker admission until their reads finish", BoundedTimedOutWorkers),
        ("Saturated byte totals cannot wrap into negative usage", Overflow),
        ("Invalid stored counters cannot escape as a successful response", InvalidRows),
        ("The read-only database factory never creates missing storage", MissingDatabase),
        ("Read-only repository rejects initialization deltas and retention mutation", ReadOnlyMutationGuard),
        ("Calendar ranges safely clamp the minimum representable local date", MinimumDate),
        ("Real SQLite Today 7D 30D fixture reads do not modify database bytes", SqliteFixture),
        ("Corrupt existing database stays preserved and yields unavailable history", CorruptDatabase),
        ("Incompatible existing schema yields unavailable without changing its version", IncompatibleSchema),
        ("A writer lock produces a bounded read failure and recovers after release", LockedDatabase),
        ("Concurrent real checkpoints and queries see full batches without write loss", ConcurrentCheckpoints)
    ];

    private static async Task Today()
    {
        var result = await Fixtures.Service().QueryAsync(AppUsageRange.Today);
        Check.Equal(Fixtures.Today, result.StartLocalDate, "local start");
        Check.Equal(Fixtures.Today, result.EndLocalDate, "local end");
        Check.Equal(AppUsageRange.Today, result.Range, "requested range");
        Check.Equal(1200L, result.Applications.Single(app => app.DisplayName == "Brave").TotalBytes, "Brave today");
        Check.Equal(1000L, result.Applications.Single(app => app.DisplayName == "NetMod").TotalBytes, "NetMod today");
        Check.Equal(2200L, result.LogicalTotalBytes, "fixture today total");
    }
    private static async Task SevenDays()
    {
        var result = await Fixtures.Service().QueryAsync(AppUsageRange.SevenDays);
        Check.Equal(Fixtures.Today.AddDays(-6), result.StartLocalDate, "7D inclusive start");
        Check.Equal(Fixtures.Today, result.EndLocalDate, "7D inclusive end");
        Check.Equal(3500L, result.Applications.Single(app => app.DisplayName == "Brave").TotalBytes, "Brave 7D");
        Check.Equal(1000L, result.Applications.Single(app => app.DisplayName == "NetMod").TotalBytes, "NetMod 7D");
        Check.Equal(4500L, result.LogicalTotalBytes, "fixture 7D total");
    }
    private static async Task ThirtyDays()
    {
        var result = await Fixtures.Service().QueryAsync(AppUsageRange.ThirtyDays);
        Check.Equal(Fixtures.Today.AddDays(-29), result.StartLocalDate, "30D inclusive start");
        Check.Equal(4500L, result.LogicalTotalBytes, "fixture 30D total");
    }
    private static Task SevenDayBoundaries() => Boundaries(AppUsageRange.SevenDays, 6);
    private static Task ThirtyDayBoundaries() => Boundaries(AppUsageRange.ThirtyDays, 29);
    private static async Task Boundaries(AppUsageRange range, int days)
    {
        var rows = new[]
        {
            Fixtures.Row(Fixtures.Today, Fixtures.Brave, 1, 2),
            Fixtures.Row(Fixtures.Today.AddDays(-days), Fixtures.Brave, 3, 4),
            Fixtures.Row(Fixtures.Today.AddDays(-days - 1), Fixtures.Brave, 100, 200),
            Fixtures.Row(Fixtures.Today.AddDays(1), Fixtures.Brave, 1000, 2000)
        };
        var result = await Fixtures.Service(rows).QueryAsync(range);
        Check.Equal(10L, result.LogicalTotalBytes, "only both inclusive endpoints contribute");
    }
    private static async Task LocalMidnight()
    {
        var rows = new[] { Fixtures.Row(Fixtures.Today.AddDays(-1), Fixtures.Brave, 3, 0),
            Fixtures.Row(Fixtures.Today, Fixtures.Brave, 7, 0) };
        var before = Fixtures.Service(rows, clock: new FixedClock(new(2026, 10, 12, 15, 59, 59, TimeSpan.Zero)));
        var after = Fixtures.Service(rows, clock: new FixedClock(new(2026, 10, 12, 16, 0, 0, TimeSpan.Zero)));
        Check.Equal(3L, (await before.QueryAsync(AppUsageRange.Today)).LogicalTotalBytes, "23:59:59 local");
        Check.Equal(7L, (await after.QueryAsync(AppUsageRange.Today)).LogicalTotalBytes, "00:00:00 local");
    }
    private static async Task AggregatesIdentity()
    {
        var result = await Fixtures.Service().QueryAsync(AppUsageRange.ThirtyDays);
        Check.Equal(2, result.Applications.Count, "one row per persistent identity");
        Check.Equal(1, result.Applications.Count(app => app.ApplicationIdentity == Fixtures.Brave.Key), "Brave aggregated once");
    }
    private static async Task DownloadSum() => Check.Equal(3500L,
        (await Fixtures.Service().QueryAsync(AppUsageRange.SevenDays)).TotalDownloadBytes, "download sum");
    private static async Task UploadSum() => Check.Equal(1000L,
        (await Fixtures.Service().QueryAsync(AppUsageRange.SevenDays)).TotalUploadBytes, "upload sum");
    private static async Task LogicalSum() => Check.Equal(4500L,
        (await Fixtures.Service().QueryAsync(AppUsageRange.SevenDays)).LogicalTotalBytes, "overlapping logical apps are not reconciled");
    private static async Task TrackedCount() => Check.Equal(2,
        (await Fixtures.Service().QueryAsync(AppUsageRange.SevenDays)).TrackedApplicationCount, "tracking count");
    private static async Task DescendingSort()
    {
        var result = await Fixtures.Service().QueryAsync(AppUsageRange.Today);
        Check.True(result.Applications.Select(app => app.TotalBytes).SequenceEqual(new long[] { 1200, 1000 }), "descending byte order");
    }
    private static async Task TieBreak()
    {
        var alphaB = ApplicationIdentity.FromExecutablePath(@"C:\B\app.exe", "Alpha");
        var alphaA = ApplicationIdentity.FromExecutablePath(@"C:\A\app.exe", "Alpha");
        var zeta = ApplicationIdentity.FromExecutablePath(@"C:\Z\app.exe", "Zeta");
        var rows = new[] { Fixtures.Row(Fixtures.Today, zeta, 50, 50),
            Fixtures.Row(Fixtures.Today, alphaB, 100, 0), Fixtures.Row(Fixtures.Today, alphaA, 0, 100) };
        var forward = await Fixtures.Service(rows).QueryAsync(AppUsageRange.Today);
        var reversed = await Fixtures.Service(rows.Reverse().ToArray()).QueryAsync(AppUsageRange.Today);
        Check.True(forward.Applications.Select(app => app.ApplicationIdentity).SequenceEqual(new[] { alphaA.Key, alphaB.Key, zeta.Key }), "ordinal identity tie-break");
        Check.True(forward.Applications.SequenceEqual(reversed.Applications), "source row ordering cannot change results");
    }
    private static Task SystemRow() => SpecialRow(ApplicationIdentity.System, true, false);
    private static Task UnknownRow() => SpecialRow(ApplicationIdentity.Unknown, false, true);
    private static async Task SpecialRow(ApplicationIdentity identity, bool system, bool unknown)
    {
        var result = await Fixtures.Service([Fixtures.Row(Fixtures.Today, identity, 20, 30)]).QueryAsync(AppUsageRange.Today);
        var row = result.Applications.Single();
        Check.Equal(identity.Key, row.ApplicationIdentity, "stable special identity");
        Check.Equal(system, row.IsSystem, "System flag"); Check.Equal(unknown, row.IsUnknown, "Unknown flag");
        Check.Equal(50L, result.LogicalTotalBytes, "special row contributes logical bytes");
    }
    private static async Task Empty()
    {
        var result = await Fixtures.Service([]).QueryAsync(AppUsageRange.Today);
        Check.Equal(AppUsageAvailability.Available, result.Availability, "empty history is available");
        Check.Equal(0, result.Applications.Count, "no invented applications"); Check.Equal(0, result.TrackedApplicationCount, "empty count");
        Check.Equal(0L, result.LogicalTotalBytes, "empty bytes"); Check.True(result.Diagnostic is null, "no error for empty history");
    }
    private static async Task ZeroRows()
    {
        var rows = new[] { Fixtures.Row(Fixtures.Today, Fixtures.Brave, 0, 0), Fixtures.Row(Fixtures.Today, Fixtures.NetMod, 0, 1) };
        var result = await Fixtures.Service(rows).QueryAsync(AppUsageRange.Today);
        Check.Equal(1, result.TrackedApplicationCount, "zero app excluded");
        Check.Equal(Fixtures.NetMod.Key, result.Applications.Single().ApplicationIdentity, "nonzero app retained");
    }
    private static async Task Metadata()
    {
        var latest = Fixtures.Row(Fixtures.Today, Fixtures.Brave, 100, 10, Fixtures.Now.AddMinutes(1)) with { DisplayName = "Brave renamed" };
        var older = Fixtures.Row(Fixtures.Today.AddDays(-1), Fixtures.Brave, 200, 20, Fixtures.Now.AddMinutes(-1));
        var row = (await Fixtures.Service([latest, older]).QueryAsync(AppUsageRange.SevenDays)).Applications.Single();
        Check.Equal("Brave renamed", row.DisplayName, "latest historical metadata");
        Check.Equal(Fixtures.Brave.ExecutableName, row.ExecutableName, "stored executable name");
        Check.Equal(Fixtures.Brave.ExecutablePath, row.ExecutablePath, "stored path without current process lookup");
        Check.Equal(Fixtures.Brave.Key, row.ApplicationIdentity, "identity contains no PID");
    }
    private static async Task Unavailable()
    {
        var service = new AppUsageQueryService((_, _) => throw new IOException("private-path-secret"), timeProvider: new FixedClock(Fixtures.Now), localTimeZone: Fixtures.Zone);
        var result = await service.QueryAsync(AppUsageRange.Today);
        Check.Equal(AppUsageAvailability.Unavailable, result.Availability, "failed storage unavailable");
        Check.Equal("Usage history temporarily unavailable.", result.Diagnostic, "fixed safe diagnostic");
        Check.Equal(0, result.Applications.Count, "unavailable has no invented rows");
    }
    private static async Task Degraded()
    {
        var health = Fixtures.Healthy with { PersistenceDegraded = true, LastError = "private-storage-secret" };
        var result = await Fixtures.Service(health: health).QueryAsync(AppUsageRange.Today);
        Check.Equal(AppUsageAvailability.Degraded, result.Availability, "persisted history with tracking degradation");
        Check.Equal(2200L, result.LogicalTotalBytes, "durable totals still available");
        Check.True(result.Diagnostic is not null && !result.Diagnostic.Contains("private-storage-secret", StringComparison.Ordinal), "no privileged diagnostics leak");
    }
    private static async Task TrackingUnavailable()
    {
        var result = await Fixtures.Service(health: Fixtures.Healthy with { PersistenceAvailable = false }).QueryAsync(AppUsageRange.Today);
        Check.Equal(AppUsageAvailability.Degraded, result.Availability, "history readable while live persistence unavailable");
        Check.Equal(2200L, result.LogicalTotalBytes, "previous durable history retained");
    }
    private static async Task FailureHasNoPartialResult()
    {
        var service = new AppUsageQueryService((_, _) => throw new AppUsageStorageException(
            AppUsageStorageFailure.QueryFailed, "private-file", "private-stack-detail"), timeProvider: new FixedClock(Fixtures.Now), localTimeZone: Fixtures.Zone);
        var result = await service.QueryAsync(AppUsageRange.SevenDays);
        Check.Equal(0L, result.TotalDownloadBytes, "failed query exposes no partial sum");
        Check.Equal(0L, result.TotalUploadBytes, "failed query exposes no partial sum");
        Check.True(!result.Diagnostic!.Contains("private", StringComparison.Ordinal), "failure sanitized");
    }
    private static async Task InvalidRange()
    {
        int calls = 0; var service = new AppUsageQueryService((_, _) => { calls++; return []; });
        await Check.ThrowsAsync<ArgumentOutOfRangeException>(async () => { await service.QueryAsync((AppUsageRange)1); }, "only approved ranges");
        Check.Equal(0, calls, "no database work for invalid typed range");
    }
    private static async Task CancelledRead()
    {
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        var service = new AppUsageQueryService((_, _) => { if (Interlocked.Increment(ref reads) == 1) { entered.SetResult(); release.Wait(); } return []; });
        using var cancellation = new CancellationTokenSource();
        var blocked = service.QueryAsync(AppUsageRange.Today, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancellation.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(async () => { await blocked; }, "caller cancellation ends wait");
        }
        finally { release.Set(); }
        Check.Equal(AppUsageAvailability.Available, (await service.QueryAsync(AppUsageRange.Today)).Availability, "future query remains usable");
    }
    private static async Task BoundedRead()
    {
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new AppUsageQueryService((_, _) => { entered.SetResult(); release.Wait(); return []; });
        var watch = Stopwatch.StartNew(); var pending = service.QueryAsync(AppUsageRange.Today);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check.Equal(AppUsageAvailability.Unavailable, (await pending).Availability, "blocked storage deadline");
            Check.True(watch.Elapsed < TimeSpan.FromSeconds(3), "query wait bounded below pipe deadline");
        }
        finally { release.Set(); }
    }
    private static async Task BoundedTimedOutWorkers()
    {
        int capacity = NativePipeSecurity.MaximumClients, calls = 0, finished = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new AppUsageQueryService((_, _) =>
        {
            int ordinal = Interlocked.Increment(ref calls);
            if (ordinal <= capacity)
            {
                if (ordinal == capacity) allEntered.TrySetResult();
                try { release.Task.GetAwaiter().GetResult(); }
                finally { if (Interlocked.Increment(ref finished) == capacity) allFinished.TrySetResult(); }
            }
            return [];
        });
        try
        {
            var occupied = Enumerable.Range(0, capacity).Select(_ => service.QueryAsync(AppUsageRange.Today)).ToArray();
            await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            foreach (var result in await Task.WhenAll(occupied))
                Check.Equal(AppUsageAvailability.Unavailable, result.Availability, "blocked reads reach bounded typed deadline");
            var repeated = Enumerable.Range(0, capacity * 2).Select(_ => service.QueryAsync(AppUsageRange.SevenDays)).ToArray();
            foreach (var result in await Task.WhenAll(repeated))
                Check.Equal(AppUsageAvailability.Unavailable, result.Availability, "occupied worker admission yields bounded unavailable");
            Check.Equal(capacity, Volatile.Read(ref calls), "repeated deadlines cannot launch extra synchronous read workers");
            Check.Equal(0, Volatile.Read(ref finished), "original timed-out workers still own admission permits");
            release.TrySetResult(); await allFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check.Equal(AppUsageAvailability.Available, (await service.QueryAsync(AppUsageRange.Today)).Availability, "reads recover when actual blocked workers finish");
            Check.Equal(capacity + 1, Volatile.Read(ref calls), "healthy query enters once capacity is released");
        }
        finally { release.TrySetResult(); }
    }
    private static async Task Overflow()
    {
        var rows = new[] { Fixtures.Row(Fixtures.Today, Fixtures.Brave, long.MaxValue, long.MaxValue),
            Fixtures.Row(Fixtures.Today.AddDays(-1), Fixtures.Brave, 1, 1) };
        var result = await Fixtures.Service(rows).QueryAsync(AppUsageRange.SevenDays);
        Check.Equal(long.MaxValue, result.TotalDownloadBytes, "download saturated");
        Check.Equal(long.MaxValue, result.TotalUploadBytes, "upload saturated");
        Check.Equal(long.MaxValue, result.LogicalTotalBytes, "logical saturated");
    }
    private static async Task InvalidRows()
    {
        var result = await Fixtures.Service([Fixtures.Row(Fixtures.Today, Fixtures.Brave, -1, 1)]).QueryAsync(AppUsageRange.Today);
        Check.Equal(AppUsageAvailability.Unavailable, result.Availability, "negative storage counters rejected");
        Check.Equal(0, result.Applications.Count, "no malformed result rows");
    }
    private static async Task MissingDatabase()
    {
        using var scope = new StorageScope(); var service = Fixtures.Database(scope);
        Check.True(!Directory.Exists(scope.Root), "query construction does not initialize storage");
        Check.Equal(AppUsageAvailability.Unavailable, (await service.QueryAsync(AppUsageRange.Today)).Availability, "missing database unavailable");
        Check.True(!Directory.Exists(scope.Root), "read does not create database directories");
    }
    private static Task ReadOnlyMutationGuard()
    {
        using var scope = new StorageScope(); scope.Seed(); var reader = DailyAppUsageStore.OpenReadOnly(scope.Root);
        Check.Throws<InvalidOperationException>(reader.Initialize, "read-only repository cannot initialize schema");
        Check.Throws<InvalidOperationException>(() => reader.AddUsageDeltas([Fixtures.Delta(Fixtures.Today, Fixtures.Brave, 99, 99)]), "read-only repository cannot add bytes");
        Check.Throws<InvalidOperationException>(() => reader.RetentionCleanup(Fixtures.Today), "read-only repository cannot delete history");
        Check.Equal(3, reader.GetUsageForRange(Fixtures.Today.AddDays(-1), Fixtures.Today).Count, "all stored rows unchanged");
        return Task.CompletedTask;
    }
    private static async Task MinimumDate()
    {
        var service = new AppUsageQueryService((_, _) => [], timeProvider: new FixedClock(DateTimeOffset.MinValue), localTimeZone: TimeZoneInfo.Utc);
        var result = await service.QueryAsync(AppUsageRange.ThirtyDays);
        Check.Equal(DateOnly.MinValue, result.StartLocalDate, "start clamps safely");
        Check.Equal(DateOnly.MinValue, result.EndLocalDate, "end remains minimum local date");
        Check.Equal(AppUsageAvailability.Available, result.Availability, "empty ancient fixture still valid");
    }
    private static async Task SqliteFixture()
    {
        using var scope = new StorageScope(); scope.Seed();
        var before = SHA256.HashData(File.ReadAllBytes(scope.DatabasePath)); var service = Fixtures.Database(scope);
        foreach (var (range, expected) in new[] { (AppUsageRange.Today, 2200L), (AppUsageRange.SevenDays, 4500L), (AppUsageRange.ThirtyDays, 4500L) })
        {
            var result = await service.QueryAsync(range);
            Check.Equal(AppUsageAvailability.Available, result.Availability, "real database readable");
            Check.Equal(expected, result.LogicalTotalBytes, "real fixture total");
            Console.WriteLine($"FIXTURE {range}: apps={result.TrackedApplicationCount}; download={result.TotalDownloadBytes}; upload={result.TotalUploadBytes}; logical={result.LogicalTotalBytes}; Brave={result.Applications.Single(app => app.DisplayName == "Brave").TotalBytes}; NetMod={result.Applications.Single(app => app.DisplayName == "NetMod").TotalBytes}");
        }
        Check.True(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(scope.DatabasePath))), "read-only history does not mutate durable database");
    }
    private static async Task CorruptDatabase()
    {
        using var scope = new StorageScope(); Directory.CreateDirectory(Path.GetDirectoryName(scope.DatabasePath)!);
        byte[] contents = [1, 2, 3, 4, 5, 6]; File.WriteAllBytes(scope.DatabasePath, contents);
        Check.Equal(AppUsageAvailability.Unavailable, (await Fixtures.Database(scope).QueryAsync(AppUsageRange.Today)).Availability, "corrupt storage unavailable");
        Check.True(contents.SequenceEqual(File.ReadAllBytes(scope.DatabasePath)), "corrupt bytes preserved");
    }
    private static async Task IncompatibleSchema()
    {
        using var scope = new StorageScope(); scope.Seed();
        using var connection = new SqliteConnection($"Data Source={scope.DatabasePath};Pooling=False"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version=999"; command.ExecuteNonQuery();
        Check.Equal(AppUsageAvailability.Unavailable, (await Fixtures.Database(scope).QueryAsync(AppUsageRange.Today)).Availability, "unknown schema unavailable");
        command.CommandText = "PRAGMA user_version"; Check.Equal(999L, (long)command.ExecuteScalar()!, "query cannot migrate privileged storage");
    }
    private static async Task LockedDatabase()
    {
        using var scope = new StorageScope(); scope.Seed();
        using var connection = new SqliteConnection($"Data Source={scope.DatabasePath};Pooling=False"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "BEGIN EXCLUSIVE"; command.ExecuteNonQuery();
        var service = Fixtures.Database(scope); var watch = Stopwatch.StartNew();
        try
        {
            Check.Equal(AppUsageAvailability.Unavailable, (await service.QueryAsync(AppUsageRange.Today)).Availability, "writer lock bounded unavailable");
            Check.True(watch.Elapsed < TimeSpan.FromSeconds(3), "no long blocking behind writer");
        }
        finally { command.CommandText = "ROLLBACK"; command.ExecuteNonQuery(); }
        Check.Equal(2200L, (await service.QueryAsync(AppUsageRange.Today)).LogicalTotalBytes, "reads recover after writer releases");
    }
    private static async Task ConcurrentCheckpoints()
    {
        using var scope = new StorageScope();
        var backend = new SqliteUsageCheckpointStore(scope.Root);
        var persistence = new UsagePersistence(backend, new FixedClock(Fixtures.Now), Fixtures.Zone);
        Check.True(await persistence.CheckpointAsync(), "prepare actual checkpoint storage");
        var query = AppUsageQueryService.ForDatabase(scope.Root, () => persistence.Health, new FixedClock(Fixtures.Now), Fixtures.Zone);
        var third = ApplicationIdentity.Unknown; int written = 0, overlapping = 0, observations = 0;
        var writing = Task.Run(async () =>
        {
            for (int batch = 0; batch < 40; batch++)
            {
                persistence.Record(Fixtures.Delta(Fixtures.Today, Fixtures.Brave, 1, 1));
                persistence.Record(Fixtures.Delta(Fixtures.Today, Fixtures.NetMod, 2, 2));
                persistence.Record(Fixtures.Delta(Fixtures.Today, third, 3, 3));
                Check.True(await persistence.CheckpointAsync(), "actual full batch committed");
                Interlocked.Increment(ref written); await Task.Delay(2);
            }
        });
        while (!writing.IsCompleted || observations < 100)
        {
            var result = await query.QueryAsync(AppUsageRange.Today); observations++;
            if (!writing.IsCompleted) overlapping++;
            Check.Equal(AppUsageAvailability.Available, result.Availability, "concurrent read available");
            if (result.Applications.Count == 0) continue;
            Check.Equal(3, result.Applications.Count, "no partially committed application batch");
            long n = result.Applications.Single(app => app.ApplicationIdentity == Fixtures.Brave.Key).DownloadBytes;
            Check.Equal(n * 2, result.Applications.Single(app => app.ApplicationIdentity == Fixtures.NetMod.Key).DownloadBytes, "same committed batch for second app");
            Check.Equal(n * 3, result.Applications.Single(app => app.IsUnknown).DownloadBytes, "same committed batch for third app");
            Check.Equal(n * 12, result.LogicalTotalBytes, "one atomic full snapshot");
        }
        await writing;
        var final = await query.QueryAsync(AppUsageRange.Today);
        Check.Equal(40, written, "all checkpoints completed"); Check.Equal(480L, final.LogicalTotalBytes, "no byte loss across concurrent reads");
        Check.True(overlapping > 0, "read/write tasks genuinely overlapped");
        Check.Equal(0, persistence.Health.PendingKeys, "no retained or lost pending batch");
        Console.WriteLine($"CONCURRENCY checkpoints={written}; reads={observations}; overlapped={overlapping}; logical={final.LogicalTotalBytes}");
    }
}
