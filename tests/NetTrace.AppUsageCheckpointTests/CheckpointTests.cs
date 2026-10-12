using AppUsageCollectorCore;
using AppUsageStorage;
using ApplicationIdentity = AppUsageStorage.ApplicationIdentity;

internal static class CheckpointTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases => new (string, Func<Task>)[]
    {
        ("Checkpoint interval is five seconds", Check.Sync(() => Check.Equal(TimeSpan.FromSeconds(5), UsagePersistence.CheckpointInterval, "preferred checkpoint interval"))),
        ("One checkpoint batches multiple applications in one storage transaction", BatchApplications),
        ("Successful checkpoint clears committed pending deltas", SuccessfulClears),
        ("Empty checkpoints initialize but perform no add transactions", EmptyNoTransactions),
        ("SQLite initialization is deferred until checkpoint", DeferredInitialization),
        ("Failed checkpoint preserves exact deltas for later retry", FailureRetains),
        ("Retry commits failed and new traffic without duplicate old bytes", FailureWithNewTraffic),
        ("ETW processing stays responsive while disk transaction is blocked", SlowStoreDoesNotBlockEvents),
        ("Concurrent checkpoint callers cannot duplicate one batch", SerializedCheckpoint),
        ("Final bounded flush persists sealed pending events", FinalFlush),
        ("Final flush drains retained batch and newer active deltas", FinalFlushDrainsBoth),
        ("Failed final flush returns false with pending intact", FailedFinalFlush),
        ("Bounded flush timeout does not duplicate an eventual commit", FlushTimeout),
        ("Sealed accumulator rejects late observations", Check.Sync(SealRejectsLate)),
        ("Retention runs once at startup and uses local ninety day cutoff", RetentionOnce),
        ("Failed startup initialization preserves pending and retries", InitializationFailure),
        ("Failed startup retention retries before writing usage", RetentionFailure),
        ("Health reports availability checkpoint time and recovery", HealthRecovery),
        ("Repeated failure diagnostics are bounded and deduplicated", BoundedDiagnostic),
        ("Diagnostic callback failure cannot discard pending usage", DiagnosticFailure),
        ("Precancelled checkpoint preserves pending without write", CancelBeforeCheckpoint),
        ("Cancellation during dispatched write waits for determinate commit", CancelDuringCheckpoint),
        ("Cancellation waiting for checkpoint lock does not dispatch duplicate", CancelWaitingCheckpoint),
        ("Negative download delta is rejected before accumulator changes", Check.Sync(() => RejectNegative(-1, 0))),
        ("Negative upload delta is rejected before accumulator changes", Check.Sync(() => RejectNegative(0, -1))),
        ("Pending signed counter overflow preserves earlier deltas", Check.Sync(Overflow)),
        ("In flight plus active signed counter overflow is rejected", InFlightOverflow),
        ("Pending unique key bound covers active and in flight batches", PendingBound),
        ("Latest observation controls metadata within pending aggregate", LatestMetadata),
        ("Manual timer checkpoints no more frequently than five seconds", TimerCadence),
        ("Manual one minute activity produces twelve checkpoint batches", TwelvePerMinute),
        ("Periodic failure waits for next timer tick before retry", PeriodicFailureCadence),
    };

    private static UsagePersistence Create(TestBackend backend, ManualClock? clock = null,
        Action<string>? diagnostic = null, int maximumPendingKeys = 10000) =>
        new(backend, clock ?? new ManualClock(), Fixtures.Zone, diagnostic, maximumPendingKeys);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task BatchApplications()
    {
        using var scope = new StorageScope();
        var backend = new TestBackend(scope.Open());
        var persistence = Create(backend);
        persistence.Record(Fixtures.Delta(11, 3));
        persistence.Record(Fixtures.Delta(22, 6, Fixtures.Editor()));
        persistence.Record(Fixtures.Delta(33, 9, ApplicationIdentity.System));
        Check.Equal(0, backend.AddCalls, "records remain memory only");
        Check.True(await persistence.CheckpointAsync(), "multi application checkpoint");
        Check.Equal(1, backend.AddCalls, "one 004.1 AddUsageDeltas transaction");
        Check.Equal(3, backend.Attempts.Single().Length, "all application keys included");
        var rows = scope.Open().GetUsageForDate(Fixtures.Today);
        Check.Equal(3, rows.Count, "all apps stored");
        Check.Equal(66L, rows.Sum(row => row.DownloadBytes), "batch exact bytes");
    }

    private static async Task SuccessfulClears()
    {
        var backend = new TestBackend();
        var persistence = Create(backend);
        persistence.Record(Fixtures.Delta(10, 2));
        await persistence.CheckpointAsync();
        Check.Equal(0, persistence.Health.PendingKeys, "committed batch removed");
        await persistence.CheckpointAsync();
        Check.Equal(1, backend.AddCalls, "no duplicate cumulative batch");
        Check.Equal(1L, persistence.Health.SuccessfulCheckpoints, "one durable commit");
    }

    private static async Task EmptyNoTransactions()
    {
        var backend = new TestBackend();
        var persistence = Create(backend);
        for (int i = 0; i < 10; i++) Check.True(await persistence.CheckpointAsync(), "empty checkpoint succeeds");
        Check.Equal(1, backend.InitializeCalls, "one initialization");
        Check.Equal(1, backend.RetentionCalls, "one startup cleanup");
        Check.Equal(0, backend.AddCalls, "no empty SQLite usage transactions");
        Check.Equal(0L, persistence.Health.CheckpointAttempts, "no write attempt for empty timer");
        Check.True(persistence.Health.LastSuccessfulCheckpoint is null, "no invented successful checkpoint timestamp");
    }

    private static async Task DeferredInitialization()
    {
        using var scope = new StorageScope();
        var adapter = new SqliteUsageCheckpointStore(scope.Root);
        var persistence = new UsagePersistence(adapter, new ManualClock(), Fixtures.Zone);
        persistence.Record(Fixtures.Delta());
        Check.True(!File.Exists(scope.DatabasePath), "event accumulation does not open SQLite");
        Check.Equal(scope.DatabasePath, adapter.DatabasePath, "adapter reuses 004.1 production-format path");
        await persistence.CheckpointAsync();
        Check.True(File.Exists(scope.DatabasePath), "checkpoint initializes reused SQLite layer");
        Check.Equal(10L, scope.Open().GetUsageForDate(Fixtures.Today).Single().DownloadBytes, "adapter forwards true delta");
    }

    private static async Task FailureRetains()
    {
        using var scope = new StorageScope();
        var backend = new TestBackend(scope.Open()) { FailuresRemaining = 1 };
        var persistence = Create(backend);
        persistence.Record(Fixtures.Delta(123, 45));
        Check.True(!await persistence.CheckpointAsync(), "injected rollback reported");
        Check.Equal(1, persistence.Health.PendingKeys, "failed batch retained");
        Check.Equal(0, scope.Open().GetUsageForDate(Fixtures.Today).Count, "failed transaction has no committed data");
        Check.True(await persistence.CheckpointAsync(), "retry succeeds");
        var row = scope.Open().GetUsageForDate(Fixtures.Today).Single();
        Check.Equal(123L, row.DownloadBytes, "retry retains exact RX bytes");
        Check.Equal(45L, row.UploadBytes, "retry retains exact TX bytes");
        Check.Equal(0, persistence.Health.PendingKeys, "retry clears retained batch");
        Check.Equal(2, backend.AddCalls, "one rollback then one commit");
    }

    private static async Task FailureWithNewTraffic()
    {
        using var scope = new StorageScope();
        var backend = new TestBackend(scope.Open()) { FailuresRemaining = 1, Entered = Signal(), Release = Signal() };
        var persistence = Create(backend);
        persistence.Record(Fixtures.Delta(100, 10));
        var failed = persistence.CheckpointAsync();
        await backend.Entered.Task;
        persistence.Record(Fixtures.Delta(200, 20));
        persistence.Record(Fixtures.Delta(300, 30, Fixtures.Editor()));
        backend.Release.TrySetResult();
        Check.True(!await failed, "first batch rolled back");
        Check.Equal(2, persistence.Health.PendingKeys, "retained and new app keys bounded together");
        await persistence.CheckpointAsync();
        await persistence.CheckpointAsync();
        await persistence.CheckpointAsync();
        var rows = scope.Open().GetUsageForDate(Fixtures.Today);
        Check.Equal(300L, rows.Single(row => row.ApplicationIdentity == Fixtures.Brave().Key).DownloadBytes, "same app old and new deltas each once");
        Check.Equal(300L, rows.Single(row => row.ApplicationIdentity == Fixtures.Editor().Key).DownloadBytes, "other app preserved during failure");
        Check.Equal(3, backend.AddCalls, "failed pinned retry and later active batch only");
        Check.Equal(2, backend.Committed.Count, "only two successful transactions");
        Check.Equal(0, persistence.Health.PendingKeys, "no pending data after both commits");
    }

    private static async Task SlowStoreDoesNotBlockEvents()
    {
        var backend = new TestBackend { Entered = Signal(), Release = Signal() };
        var persistence = Create(backend);
        var aggregation = Fixtures.Aggregation(persistence);
        aggregation.Add(Fixtures.Raw(bytes: 1));
        var checkpoint = persistence.CheckpointAsync();
        await backend.Entered.Task;
        // The gate remains closed while 1000 real parser/attribution callbacks
        // and snapshots complete; no fragile timing threshold is required.
        var events = Task.Run(() =>
        {
            for (int i = 0; i < 1000; i++) aggregation.Add(Fixtures.Raw(bytes: 2));
            return aggregation.GetSnapshot();
        });
        var snapshot = await events.WaitAsync(TimeSpan.FromSeconds(2));
        Check.True(!checkpoint.IsCompleted, "disk I/O is still blocked during event processing");
        Check.Equal(2_001L, snapshot.Applications.Single().Counters.RxBytes, "live counters update while SQLite is slow");
        backend.Release.TrySetResult();
        await checkpoint;
        await persistence.CheckpointAsync();
        Check.Equal(2_001L, backend.Committed.SelectMany(x => x).Sum(x => x.DownloadBytes), "swap preserves all concurrent bytes");
    }

    private static async Task SerializedCheckpoint()
    {
        var backend = new TestBackend { Entered = Signal(), Release = Signal() };
        var persistence = Create(backend);
        persistence.Record(Fixtures.Delta(100));
        var first = persistence.CheckpointAsync();
        await backend.Entered.Task;
        var second = persistence.CheckpointAsync();
        Check.True(!second.IsCompleted, "later checkpoint waits for pinned batch");
        Check.Equal(1, backend.AddCalls, "one in flight transaction");
        backend.Release.TrySetResult();
        Check.True(await first && await second, "serialized checkpoints succeed");
        Check.Equal(1, backend.AddCalls, "second caller cannot duplicate old batch");
    }

    private static async Task FinalFlush()
    {
        using var scope = new StorageScope();
        var persistence = new UsagePersistence(new SqliteUsageCheckpointStore(scope.Root), new ManualClock(), Fixtures.Zone);
        persistence.Record(Fixtures.Delta(88, 44));
        persistence.Seal();
        Check.True(await persistence.FlushAsync(TimeSpan.FromSeconds(2)), "normal shutdown flush succeeds");
        Check.Equal(0, persistence.Health.PendingKeys, "shutdown drains pending");
        Check.Equal(88L, scope.Open().GetUsageForDate(Fixtures.Today).Single().DownloadBytes, "pending bytes durable before next timer tick");
    }

    private static async Task FinalFlushDrainsBoth()
    {
        var backend = new TestBackend { FailuresRemaining = 1 };
        var persistence = Create(backend);
        persistence.Record(Fixtures.Delta(100));
        await persistence.CheckpointAsync();
        persistence.Record(Fixtures.Delta(200));
        persistence.Seal();
        Check.True(await persistence.FlushAsync(TimeSpan.FromSeconds(2)), "shutdown drains failed and active accumulators");
        Check.Equal(300L, backend.Committed.SelectMany(x => x).Sum(x => x.DownloadBytes), "all flush bytes exact");
        Check.Equal(0, persistence.Health.PendingKeys, "both pending references removed");
    }

    private static async Task FailedFinalFlush()
    {
        var backend = new TestBackend { FailuresRemaining = 1 };
        var persistence = Create(backend);
        persistence.Record(Fixtures.Delta());
        persistence.Seal();
        Check.True(!await persistence.FlushAsync(TimeSpan.FromSeconds(2)), "failed shutdown write reported");
        Check.Equal(1, persistence.Health.PendingKeys, "no silent loss after final failure");
        Check.Equal(1, backend.AddCalls, "final flush does not spin on error");
    }

    private static async Task FlushTimeout()
    {
        using var scope = new StorageScope();
        var backend = new TestBackend(scope.Open()) { Entered = Signal(), Release = Signal() };
        var persistence = Create(backend);
        persistence.Record(Fixtures.Delta(333));
        persistence.Seal();
        var bounded = persistence.FlushAsync(TimeSpan.FromMilliseconds(30));
        await backend.Entered.Task;
        Check.True(!await bounded, "bounded timeout returns without indefinite shutdown wait");
        Check.Equal(1, persistence.Health.PendingKeys, "ambiguous in flight batch stays owned");
        var retry = persistence.CheckpointAsync();
        backend.Release.TrySetResult();
        await retry;
        Check.Equal(1, backend.AddCalls, "timed out flush is never redispatched");
        Check.Equal(333L, scope.Open().GetUsageForDate(Fixtures.Today).Single().DownloadBytes, "late determinate commit counted once");
        Check.Equal(0, persistence.Health.PendingKeys, "late commit clears batch");
    }

    private static void SealRejectsLate()
    {
        var persistence = Create(new TestBackend());
        persistence.Record(Fixtures.Delta());
        persistence.Seal(); persistence.Seal();
        Check.Throws<InvalidOperationException>(() => persistence.Record(Fixtures.Delta()), "late event after producer shutdown");
        Check.Equal(1, persistence.Health.PendingKeys, "seal retains already accepted event");
    }

    private static async Task RetentionOnce()
    {
        using var scope = new StorageScope();
        var storage = scope.Open();
        var cutoff = DailyAppUsageStore.GetRetentionCutoff(Fixtures.Today);
        storage.AddUsageDelta(cutoff.AddDays(-1), Fixtures.Brave(), 1, 0, new(Fixtures.Epoch));
        storage.AddUsageDelta(cutoff, Fixtures.Brave(), 2, 0, new(Fixtures.Epoch));
        var backend = new TestBackend(storage);
        var clock = new ManualClock();
        var persistence = Create(backend, clock);
        await persistence.CheckpointAsync();
        for (int i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromDays(1));
            persistence.Record(Fixtures.Delta());
            await persistence.CheckpointAsync();
        }
        Check.Equal(1, backend.RetentionCalls, "startup cleanup is not five second or per checkpoint cleanup");
        Check.Equal(cutoff, backend.RetentionCutoff, "ninety local dates including today");
        Check.Equal(0, storage.GetUsageForDate(cutoff.AddDays(-1)).Count, "expired row removed");
        Check.Equal(2L, storage.GetUsageForDate(cutoff).Single().DownloadBytes, "retention boundary retained");
        Check.Equal(1L, persistence.Health.RetentionRuns, "health records successful retention once");
    }

    private static async Task InitializationFailure()
    {
        var backend = new TestBackend { InitializationFailuresRemaining = 1 };
        var persistence = Create(backend);
        persistence.Record(Fixtures.Delta(77));
        Check.True(!await persistence.CheckpointAsync(), "startup storage error reported");
        Check.True(!persistence.Health.PersistenceAvailable && persistence.Health.PersistenceDegraded, "unavailable startup storage health");
        Check.Equal(1, persistence.Health.PendingKeys, "startup failure preserves memory delta");
        Check.Equal(0, backend.AddCalls, "uninitialized backend never writes");
        Check.True(await persistence.CheckpointAsync(), "later initialization retry succeeds");
        Check.Equal(77L, backend.Committed.Single().Single().DownloadBytes, "retry has original bytes");
        Check.Equal(2, backend.InitializeCalls, "one initialization retry");
    }

    private static async Task RetentionFailure()
    {
        var backend = new TestBackend { RetentionFailuresRemaining = 1 };
        var persistence = Create(backend);
        persistence.Record(Fixtures.Delta(77));
        Check.True(!await persistence.CheckpointAsync(), "startup cleanup failure reported");
        Check.Equal(0, backend.AddCalls, "failed setup prevents write until safe retry");
        Check.True(await persistence.CheckpointAsync(), "startup cleanup retries");
        Check.Equal(2, backend.RetentionCalls, "cleanup failure retry then no repeated successful cleanup");
        Check.Equal(1, backend.InitializeCalls, "successful initialization retained");
        Check.Equal(1L, persistence.Health.RetentionRuns, "only successful cleanup counted");
    }

    private static async Task HealthRecovery()
    {
        var backend = new TestBackend { FailuresRemaining = 1 };
        var clock = new ManualClock();
        var persistence = Create(backend, clock);
        Check.True(!persistence.Health.PersistenceAvailable, "health initially uninitialized");
        persistence.Record(Fixtures.Delta(100, 20));
        await persistence.CheckpointAsync();
        var failed = persistence.Health;
        Check.True(failed.PersistenceAvailable && failed.PersistenceDegraded, "initialized but failing writes degraded");
        Check.True(failed.LastSuccessfulCheckpoint is null && failed.LastError is not null, "error does not invent checkpoint success");
        clock.Advance(TimeSpan.FromSeconds(5));
        await persistence.CheckpointAsync();
        var recovered = persistence.Health;
        Check.True(!recovered.PersistenceDegraded && recovered.LastError is null, "successful retry recovers diagnostic state");
        Check.Equal(clock.GetUtcNow(), recovered.LastSuccessfulCheckpoint, "last success checkpoint clock");
        Check.Equal(2L, recovered.CheckpointAttempts, "attempt total");
        Check.Equal(1L, recovered.SuccessfulCheckpoints, "successful total");
        Check.Equal(1L, recovered.FailedCheckpoints, "failed total");
        Check.Equal(100L, recovered.AcceptedDownloadBytes, "accepted bytes diagnostic");
        Check.Equal(100L, recovered.CommittedDownloadBytes, "committed bytes diagnostic");
        Check.Equal(20L, recovered.CommittedUploadBytes, "committed upload diagnostic");
    }

    private static async Task BoundedDiagnostic()
    {
        var messages = new List<string>();
        var backend = new TestBackend { FailuresRemaining = 2, FailureMessage = new string('x', 2000) + "\r\nsecret trail" };
        var persistence = Create(backend, diagnostic: messages.Add);
        persistence.Record(Fixtures.Delta());
        await persistence.CheckpointAsync(); await persistence.CheckpointAsync();
        Check.Equal(1, messages.Count, "unchanged errors do not flood logs");
        Check.True(persistence.Health.LastError!.Length <= 512, "stored error bounded");
        Check.True(!persistence.Health.LastError.Contains('\n'), "diagnostic cannot inject multiline logs");
        await persistence.CheckpointAsync();
        Check.Equal(1, messages.Count(message => message.Contains("persistence recovered", StringComparison.Ordinal)), "recovery emits one state transition");
        Check.Equal(1, messages.Count(message => message.Contains("checkpoint committed", StringComparison.Ordinal)), "successful checkpoint emits one bounded transaction summary");
    }

    private static async Task DiagnosticFailure()
    {
        var backend = new TestBackend { FailuresRemaining = 1 };
        var persistence = Create(backend, diagnostic: _ => throw new IOException("unavailable log sink"));
        persistence.Record(Fixtures.Delta());
        Check.True(!await persistence.CheckpointAsync(), "diagnostic failure does not replace original failed outcome");
        Check.True(await persistence.CheckpointAsync(), "diagnostic failure cannot stop retry");
        Check.Equal(10L, backend.Committed.Single().Single().DownloadBytes, "pending data unaffected by logging");
    }

    private static async Task CancelBeforeCheckpoint()
    {
        var backend = new TestBackend();
        var persistence = Create(backend);
        persistence.Record(Fixtures.Delta());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => persistence.CheckpointAsync(cancellation.Token), "cancellation before dispatch");
        Check.Equal(0, backend.InitializeCalls, "no backend operation after precancellation");
        Check.Equal(1, persistence.Health.PendingKeys, "precancel preserves delta");
        await persistence.CheckpointAsync();
        Check.Equal(1, backend.AddCalls, "later uncancelled checkpoint persists once");
    }

    private static async Task CancelDuringCheckpoint()
    {
        using var scope = new StorageScope();
        var backend = new TestBackend(scope.Open()) { Entered = Signal(), Release = Signal() };
        var persistence = Create(backend);
        persistence.Record(Fixtures.Delta(222));
        using var cancellation = new CancellationTokenSource();
        var checkpoint = persistence.CheckpointAsync(cancellation.Token);
        await backend.Entered.Task;
        cancellation.Cancel();
        Check.True(!checkpoint.IsCompleted, "in flight cancellation cannot report ambiguous commit");
        Check.True(!backend.DispatchToken.CanBeCanceled, "dispatch has determinate commit contract");
        backend.Release.TrySetResult();
        Check.True(await checkpoint, "completed transaction remains successful despite later cancellation");
        await persistence.CheckpointAsync();
        Check.Equal(1, backend.AddCalls, "no requeue of actually committed data");
        Check.Equal(222L, scope.Open().GetUsageForDate(Fixtures.Today).Single().DownloadBytes, "cancelled caller does not duplicate bytes");
    }

    private static async Task CancelWaitingCheckpoint()
    {
        var backend = new TestBackend { Entered = Signal(), Release = Signal() };
        var persistence = Create(backend);
        persistence.Record(Fixtures.Delta());
        var first = persistence.CheckpointAsync();
        await backend.Entered.Task;
        using var cancellation = new CancellationTokenSource();
        var waiting = persistence.CheckpointAsync(cancellation.Token);
        cancellation.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => waiting, "waiter cancellation");
        backend.Release.TrySetResult();
        await first;
        Check.Equal(1, backend.AddCalls, "cancelled lock waiter never dispatches duplicate");
    }

    private static void RejectNegative(long download, long upload)
    {
        var persistence = Create(new TestBackend());
        persistence.Record(Fixtures.Delta(3, 4));
        Check.Throws<ArgumentOutOfRangeException>(() => persistence.Record(Fixtures.Delta(download, upload)), "negative usage rejected");
        Check.Equal(1, persistence.Health.PendingKeys, "valid pending remains");
        Check.Equal(3L, persistence.Health.AcceptedDownloadBytes, "negative input cannot mutate accepted total");
        Check.Equal(4L, persistence.Health.AcceptedUploadBytes, "negative input cannot mutate accepted upload");
    }

    private static void Overflow()
    {
        var persistence = Create(new TestBackend());
        persistence.Record(Fixtures.Delta(long.MaxValue));
        Check.Throws<InvalidOperationException>(() => persistence.Record(Fixtures.Delta(1)), "overflow fails before wrapping negative");
        Check.Equal(long.MaxValue, persistence.Health.AcceptedDownloadBytes, "earlier maximum remains");
        Check.Equal(1L, persistence.Health.AcceptedEvents, "rejected event not counted accepted");
    }

    private static async Task InFlightOverflow()
    {
        var backend = new TestBackend { Entered = Signal(), Release = Signal() };
        var persistence = Create(backend);
        persistence.Record(Fixtures.Delta(long.MaxValue));
        var checkpoint = persistence.CheckpointAsync();
        await backend.Entered.Task;
        Check.Throws<InvalidOperationException>(() => persistence.Record(Fixtures.Delta(1)), "retained plus active cannot exceed signed range");
        backend.Release.TrySetResult();
        await checkpoint;
        Check.Equal(long.MaxValue, backend.Committed.Single().Single().DownloadBytes, "retained maximum commits unchanged");
    }

    private static async Task PendingBound()
    {
        var backend = new TestBackend { Entered = Signal(), Release = Signal() };
        var persistence = Create(backend, maximumPendingKeys: 2);
        persistence.Record(Fixtures.Delta(10));
        var checkpoint = persistence.CheckpointAsync();
        await backend.Entered.Task;
        persistence.Record(Fixtures.Delta(20)); // Same key in active and in-flight counts once.
        persistence.Record(Fixtures.Delta(30, app: Fixtures.Editor()));
        Check.Throws<InvalidOperationException>(() => persistence.Record(Fixtures.Delta(40, app: ApplicationIdentity.System)), "bounded unique app day keys");
        Check.Equal(2, persistence.Health.PendingKeys, "bound includes retained and active unique keys");
        backend.Release.TrySetResult();
        await checkpoint; await persistence.CheckpointAsync();
        Check.Equal(60L, backend.Committed.SelectMany(x => x).Sum(x => x.DownloadBytes), "bound rejection cannot replace accepted values");
    }

    private static async Task LatestMetadata()
    {
        var backend = new TestBackend();
        var persistence = Create(backend);
        persistence.Record(Fixtures.Delta(10, app: Fixtures.Brave("Current metadata"), seen: new DateTimeOffset(Fixtures.Epoch).AddSeconds(2)));
        persistence.Record(Fixtures.Delta(20, app: Fixtures.Brave("Older metadata"), seen: new DateTimeOffset(Fixtures.Epoch).AddSeconds(1)));
        await persistence.CheckpointAsync();
        var delta = backend.Committed.Single().Single();
        Check.Equal(30L, delta.DownloadBytes, "older event contributes usage");
        Check.Equal("Current metadata", delta.Application.DisplayName, "older event cannot replace newer metadata");
        Check.Equal(new DateTimeOffset(Fixtures.Epoch).AddSeconds(2), delta.LastSeen, "last seen newest");
    }

    private static async Task TimerCadence()
    {
        var backend = new TestBackend();
        var clock = new ManualClock();
        var persistence = Create(backend, clock);
        using var cancellation = new CancellationTokenSource();
        var run = persistence.RunAsync(cancellation.Token);
        await Check.Eventually(() => clock.ActiveTimers == 1, "periodic timer created");
        persistence.Record(Fixtures.Delta());
        clock.Advance(TimeSpan.FromSeconds(4));
        Check.Equal(0, backend.AddCalls, "no tick before five seconds");
        clock.Advance(TimeSpan.FromSeconds(1));
        await Check.Eventually(() => backend.Committed.Count == 1, "five second tick checkpoint");
        Check.Equal(1, backend.AddCalls, "one batch at first tick");
        cancellation.Cancel(); await run;
        Check.Equal(0, clock.ActiveTimers, "periodic timer disposed at shutdown");
    }

    private static async Task TwelvePerMinute()
    {
        var backend = new TestBackend();
        var clock = new ManualClock();
        var persistence = Create(backend, clock);
        using var cancellation = new CancellationTokenSource();
        var run = persistence.RunAsync(cancellation.Token);
        await Check.Eventually(() => clock.ActiveTimers == 1, "timer created");
        for (int i = 1; i <= 12; i++)
        {
            persistence.Record(Fixtures.Delta(1));
            clock.Advance(TimeSpan.FromSeconds(5));
            int expected = i;
            await Check.Eventually(() => backend.Committed.Count == expected, "one checkpoint for each deterministic tick");
        }
        cancellation.Cancel(); await run;
        Check.Equal(12, backend.AddCalls, "twelve usage transactions per active minute");
        Check.Equal(12L, backend.Committed.SelectMany(x => x).Sum(x => x.DownloadBytes), "twelve recorded events persist once");
        Check.Equal(1, backend.RetentionCalls, "retention is independent of checkpoint cadence");
    }

    private static async Task PeriodicFailureCadence()
    {
        var backend = new TestBackend { FailuresRemaining = 1 };
        var clock = new ManualClock();
        var persistence = Create(backend, clock);
        persistence.Record(Fixtures.Delta(99));
        using var cancellation = new CancellationTokenSource();
        var run = persistence.RunAsync(cancellation.Token);
        await Check.Eventually(() => clock.ActiveTimers == 1, "timer created");
        Check.Equal(0, backend.AddCalls, "startup only prepares storage");
        clock.Advance(TimeSpan.FromSeconds(5));
        await Check.Eventually(() => persistence.Health.FailedCheckpoints == 1, "first periodic write fails");
        Check.Equal(1, backend.AddCalls, "one first tick attempt");
        clock.Advance(TimeSpan.FromSeconds(4));
        Check.Equal(1, backend.AddCalls, "failure does not spin before next timer tick");
        clock.Advance(TimeSpan.FromSeconds(1));
        await Check.Eventually(() => backend.Committed.Count == 1, "next tick retries retained failure");
        cancellation.Cancel(); await run;
        Check.Equal(2, backend.AddCalls, "single later retry");
        Check.Equal(99L, backend.Committed.Single().Single().DownloadBytes, "retry exact once");
    }
}
