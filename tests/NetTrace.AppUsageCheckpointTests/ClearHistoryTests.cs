using AppUsageCollectorCore;
using AppUsageIpc;
using AppUsageStorage;

internal static class ClearHistoryTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases => new (string, Func<Task>)[]
    {
        ("Clear History empties all IPC query ranges", AllRangesEmpty),
        ("Clear History discards active preclear deltas", ActiveDeltasDiscarded),
        ("Clear History discards failed retained checkpoint batches", RetainedDeltasDiscarded),
        ("Clear History waits for in-flight checkpoint commit", InFlightCheckpoint),
        ("Clear History drops observations accepted during deletion", EventsDuringClear),
        ("Clear History ignores late buffered ETW observations", BufferedObservations),
        ("Same collector continues recording only new usage after clear", ContinuedCollector),
        ("Failed Clear History preserves rows and pending checkpoint retry", FailedClear),
        ("Clear History cancellation before admission preserves pending usage", Precancelled),
        ("Cancelled Clear History lock waiter never dispatches deletion", CancelledWaiter),
        ("Clear History cancellation after dispatch cannot report ambiguous commit", CancelDuringClear),
        ("Clear History cannot modify a sealed accumulator", SealedAccumulator),
        ("Checkpoint-only backend rejects unsupported history clear", UnsupportedStore),
        ("Production SQLite adapter preserves schema and accepts new usage", SqliteAdapter),
        ("Repeated Clear History advances the old-observation boundary", RepeatedClear),
        ("Storage idle waits for SQL remaining after bounded flush timeout", StorageIdleAfterFlushTimeout),
        ("Cancelled storage idle acknowledgement does not release in-flight SQL", CancelledStorageIdle),
    };

    private static UsagePersistence Create(HistoryBackend backend, ManualClock? clock = null) =>
        new(backend, clock ?? new ManualClock(), Fixtures.Zone);

    private static async Task AllRangesEmpty()
    {
        using var scope = new StorageScope();
        var store = scope.Open();
        store.AddUsageDeltas(Enumerable.Range(0, 40).Select(day =>
            Fixtures.Delta(100, 20, date: Fixtures.Today.AddDays(-day),
                seen: new DateTimeOffset(Fixtures.Epoch).AddDays(-day))).ToArray());
        var backend = new HistoryBackend(store);
        var clock = new ManualClock();
        var persistence = Create(backend, clock);
        await persistence.CheckpointAsync();
        Check.Equal(40, await persistence.ClearHistoryAsync(), "every historical day cleared");
        var queries = AppUsageQueryService.ForDatabase(scope.Root, () => persistence.Health, clock, Fixtures.Zone);
        foreach (var range in Enum.GetValues<AppUsageRange>())
        {
            var result = await queries.QueryAsync(range);
            Check.Equal(AppUsageAvailability.Available, result.Availability, "empty history remains available");
            Check.Equal(0, result.Applications.Count, $"{range} no applications");
            Check.Equal(0L, result.LogicalTotalBytes, $"{range} zero usage");
        }
    }

    private static async Task ActiveDeltasDiscarded()
    {
        using var scope = new StorageScope();
        var backend = new HistoryBackend(scope.Open());
        var persistence = Create(backend);
        await persistence.CheckpointAsync();
        persistence.Record(Fixtures.Delta(900));
        Check.Equal(0, await persistence.ClearHistoryAsync(), "pending-only clear has no stored rows");
        Check.Equal(0, persistence.Health.PendingKeys, "pending keys discarded");
        await persistence.CheckpointAsync();
        Check.Equal(0, backend.Checkpoints.AddCalls, "old pending batch never written later");
        Check.Equal(900L, persistence.Health.AcceptedDownloadBytes, "lifetime diagnostics unchanged");
        Check.Equal(0, scope.Open().GetUsageForDate(Fixtures.Today).Count, "history remains empty");
    }

    private static async Task RetainedDeltasDiscarded()
    {
        using var scope = new StorageScope();
        var store = scope.Open();
        store.AddUsageDelta(Fixtures.Today, Fixtures.Brave(), 5, 2, new(Fixtures.Epoch));
        var backend = new HistoryBackend(store);
        backend.Checkpoints.FailuresRemaining = 1;
        var persistence = Create(backend);
        persistence.Record(Fixtures.Delta(120));
        Check.True(!await persistence.CheckpointAsync(), "old batch retained after failure");
        persistence.Record(Fixtures.Delta(30, 10));
        Check.Equal(1, await persistence.ClearHistoryAsync(), "existing stored row cleared");
        Check.Equal(0, persistence.Health.PendingKeys, "active and retained references removed");
        Check.True(!persistence.Health.PersistenceDegraded, "successful clear repairs failed old batch state");
        await persistence.CheckpointAsync();
        Check.Equal(1, backend.Checkpoints.AddCalls, "failed old batch is not retried");
        persistence.Record(Fixtures.Delta(20, 3, seen: new DateTimeOffset(Fixtures.Epoch).AddSeconds(1)));
        await persistence.CheckpointAsync();
        var row = store.GetUsageForDate(Fixtures.Today).Single();
        Check.Equal(20L, row.DownloadBytes, "only new download stored");
        Check.Equal(3L, row.UploadBytes, "only new upload stored");
    }

    private static async Task InFlightCheckpoint()
    {
        using var scope = new StorageScope();
        var backend = new HistoryBackend(scope.Open());
        backend.Checkpoints.Entered = Signal();
        backend.Checkpoints.Release = Signal();
        var persistence = Create(backend);
        persistence.Record(Fixtures.Delta(210));
        var checkpoint = persistence.CheckpointAsync();
        await backend.Checkpoints.Entered.Task;
        persistence.Record(Fixtures.Delta(99));
        var clear = persistence.ClearHistoryAsync();
        Check.True(!clear.IsCompleted, "clear waits for existing checkpoint");
        Check.Equal(0, backend.ClearCalls, "delete cannot race an in-flight transaction");
        backend.Checkpoints.Release.TrySetResult();
        Check.True(await checkpoint, "in-flight checkpoint commits determinately");
        Check.Equal(1, await clear, "committed row deleted before reporting success");
        await persistence.CheckpointAsync();
        Check.Equal(1, backend.Checkpoints.AddCalls, "active old observations discarded too");
        Check.Equal(0, scope.Open().GetUsageForDate(Fixtures.Today).Count, "no in-flight bytes resurrected");
    }

    private static async Task EventsDuringClear()
    {
        using var scope = new StorageScope();
        var backend = new HistoryBackend(scope.Open()) { ClearEntered = Signal(), ClearRelease = Signal() };
        var clock = new ManualClock();
        var persistence = Create(backend, clock);
        await persistence.CheckpointAsync();
        var clear = persistence.ClearHistoryAsync();
        await backend.ClearEntered.Task;
        clock.Advance(TimeSpan.FromSeconds(1));
        persistence.Record(Fixtures.Delta(40, seen: clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromSeconds(1));
        backend.ClearRelease.TrySetResult();
        await clear;
        Check.Equal(0, persistence.Health.PendingKeys, "events before completed clear boundary discarded");
        clock.Advance(TimeSpan.FromSeconds(1));
        persistence.Record(Fixtures.Delta(11, seen: clock.GetUtcNow()));
        await persistence.CheckpointAsync();
        Check.Equal(11L, scope.Open().GetUsageForDate(Fixtures.Today).Single().DownloadBytes, "new postclear event recorded");
    }

    private static async Task BufferedObservations()
    {
        using var scope = new StorageScope();
        var backend = new HistoryBackend(scope.Open());
        var clock = new ManualClock();
        var persistence = Create(backend, clock);
        await persistence.CheckpointAsync();
        await persistence.ClearHistoryAsync();
        persistence.Record(Fixtures.Delta(1_000, seen: clock.GetUtcNow().AddSeconds(-1)));
        persistence.Record(Fixtures.Delta(2_000, seen: clock.GetUtcNow()));
        Check.Equal(0, persistence.Health.PendingKeys, "old and exact-boundary buffered events ignored");
        Check.Equal(0L, persistence.Health.AcceptedEvents, "discarded event does not inflate accepted persistence diagnostics");
        persistence.Record(Fixtures.Delta(17, seen: clock.GetUtcNow().AddTicks(1)));
        await persistence.CheckpointAsync();
        Check.Equal(17L, scope.Open().GetUsageForDate(Fixtures.Today).Single().DownloadBytes, "first new observation accepted");
    }

    private static async Task ContinuedCollector()
    {
        using var scope = new StorageScope();
        var backend = new HistoryBackend(scope.Open());
        var clock = new ManualClock();
        var persistence = Create(backend, clock);
        var aggregation = Fixtures.Aggregation(persistence);
        var instance = aggregation.CollectorInstanceId;
        aggregation.Add(Fixtures.Raw(bytes: 700));
        await persistence.CheckpointAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        await persistence.ClearHistoryAsync();
        aggregation.Add(Fixtures.Raw(bytes: 900, timestamp: Fixtures.Epoch));
        aggregation.Add(Fixtures.Raw(bytes: 43, timestamp: Fixtures.Epoch.AddSeconds(2)));
        await persistence.CheckpointAsync();
        Check.Equal(instance, aggregation.CollectorInstanceId, "same collector lifetime continues");
        Check.Equal(43L, scope.Open().GetUsageForDate(Fixtures.Today).Single().DownloadBytes, "new transfer persists after clear");
        Check.Equal(1_643L, aggregation.GetSnapshot().Applications.Single().Counters.RxBytes, "live collector diagnostics not reset by persisted history maintenance");
    }

    private static async Task FailedClear()
    {
        using var scope = new StorageScope();
        var store = scope.Open();
        store.AddUsageDelta(Fixtures.Today, Fixtures.Brave(), 5, 0, new(Fixtures.Epoch));
        var backend = new HistoryBackend(store) { ClearFailuresRemaining = 1 };
        backend.Checkpoints.FailuresRemaining = 1;
        var persistence = Create(backend);
        persistence.Record(Fixtures.Delta(100));
        Check.True(!await persistence.CheckpointAsync(), "checkpoint failure pins retained batch");
        persistence.Record(Fixtures.Delta(25));
        await Check.ThrowsAsync<IOException>(() => persistence.ClearHistoryAsync(), "clear failure is reported");
        Check.Equal(5L, store.GetUsageForDate(Fixtures.Today).Single().DownloadBytes, "stored rows preserved on failure");
        Check.Equal(1, persistence.Health.PendingKeys, "both batches remain retryable");
        await persistence.CheckpointAsync();
        await persistence.CheckpointAsync();
        Check.Equal(130L, store.GetUsageForDate(Fixtures.Today).Single().DownloadBytes, "all pending bytes recovered exactly once");
        Check.Equal(1, await persistence.ClearHistoryAsync(), "user can retry failed clear");
        Check.Equal(0, store.GetUsageForDate(Fixtures.Today).Count, "retry succeeds");
    }

    private static async Task Precancelled()
    {
        using var scope = new StorageScope();
        var backend = new HistoryBackend(scope.Open());
        var persistence = Create(backend);
        persistence.Record(Fixtures.Delta(20));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => persistence.ClearHistoryAsync(cancellation.Token), "precancelled clear");
        Check.Equal(0, backend.ClearCalls, "no deletion dispatched");
        Check.Equal(1, persistence.Health.PendingKeys, "pending observation preserved");
        await persistence.CheckpointAsync();
        Check.Equal(20L, scope.Open().GetUsageForDate(Fixtures.Today).Single().DownloadBytes, "pending checkpoint still writes");
    }

    private static async Task CancelledWaiter()
    {
        using var scope = new StorageScope();
        var backend = new HistoryBackend(scope.Open());
        backend.Checkpoints.Entered = Signal();
        backend.Checkpoints.Release = Signal();
        var persistence = Create(backend);
        persistence.Record(Fixtures.Delta(55));
        var checkpoint = persistence.CheckpointAsync();
        await backend.Checkpoints.Entered.Task;
        using var cancellation = new CancellationTokenSource();
        var clear = persistence.ClearHistoryAsync(cancellation.Token);
        cancellation.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => clear, "waiting clear can cancel safely");
        Check.Equal(0, backend.ClearCalls, "cancelled waiter never clears later");
        backend.Checkpoints.Release.TrySetResult();
        await checkpoint;
        Check.Equal(55L, scope.Open().GetUsageForDate(Fixtures.Today).Single().DownloadBytes, "in-flight checkpoint unaffected");
    }

    private static async Task CancelDuringClear()
    {
        using var scope = new StorageScope();
        var backend = new HistoryBackend(scope.Open()) { ClearEntered = Signal(), ClearRelease = Signal() };
        var persistence = Create(backend);
        await persistence.CheckpointAsync();
        persistence.Record(Fixtures.Delta(80));
        using var cancellation = new CancellationTokenSource();
        var clear = persistence.ClearHistoryAsync(cancellation.Token);
        await backend.ClearEntered.Task;
        cancellation.Cancel();
        Check.True(!clear.IsCompleted, "caller must wait for determinate transaction result");
        Check.True(!backend.ClearToken.CanBeCanceled, "dispatched deletion cannot become ambiguous");
        backend.ClearRelease.TrySetResult();
        Check.Equal(0, await clear, "committed clear remains successful despite cancelled caller");
        Check.Equal(0, persistence.Health.PendingKeys, "pending old data discarded after known success");
    }

    private static async Task SealedAccumulator()
    {
        using var scope = new StorageScope();
        var backend = new HistoryBackend(scope.Open());
        var persistence = Create(backend);
        persistence.Record(Fixtures.Delta(30));
        persistence.Seal();
        await Check.ThrowsAsync<InvalidOperationException>(() => persistence.ClearHistoryAsync(), "shutdown accumulator cannot clear");
        Check.Equal(0, backend.ClearCalls, "sealed guard precedes deletion");
        Check.True(await persistence.FlushAsync(TimeSpan.FromSeconds(2)), "normal final checkpoint still works");
        Check.Equal(30L, scope.Open().GetUsageForDate(Fixtures.Today).Single().DownloadBytes, "shutdown usage retained");
    }

    private static async Task UnsupportedStore()
    {
        var backend = new TestBackend();
        var persistence = new UsagePersistence(backend, new ManualClock(), Fixtures.Zone);
        persistence.Record(Fixtures.Delta(25));
        await Check.ThrowsAsync<NotSupportedException>(() => persistence.ClearHistoryAsync(), "optional clear capability required");
        Check.Equal(1, persistence.Health.PendingKeys, "unsupported backend retains pending data");
        await persistence.CheckpointAsync();
        Check.Equal(25L, backend.Committed.Single().Single().DownloadBytes, "ordinary checkpoint backend remains compatible");
    }

    private static async Task SqliteAdapter()
    {
        using var scope = new StorageScope();
        var clock = new ManualClock();
        var store = new SqliteUsageCheckpointStore(scope.Root);
        var persistence = new UsagePersistence(store, clock, Fixtures.Zone);
        persistence.Record(Fixtures.Delta(60));
        await persistence.CheckpointAsync();
        Check.Equal(1, await persistence.ClearHistoryAsync(), "real SQLite clear adapter deletes one row");
        Check.True(File.Exists(scope.DatabasePath), "real adapter retains database file");
        clock.Advance(TimeSpan.FromSeconds(1));
        persistence.Record(Fixtures.Delta(7, 3, seen: clock.GetUtcNow()));
        await persistence.CheckpointAsync();
        var row = DailyAppUsageStore.OpenReadOnly(scope.Root).GetUsageForDate(Fixtures.Today).Single();
        Check.Equal(7L, row.DownloadBytes, "new real-adapter download");
        Check.Equal(3L, row.UploadBytes, "new real-adapter upload");
    }

    private static async Task RepeatedClear()
    {
        using var scope = new StorageScope();
        var backend = new HistoryBackend(scope.Open());
        var clock = new ManualClock();
        var persistence = Create(backend, clock);
        await persistence.CheckpointAsync();
        await persistence.ClearHistoryAsync();
        clock.Advance(TimeSpan.FromSeconds(2));
        persistence.Record(Fixtures.Delta(70, seen: clock.GetUtcNow()));
        await persistence.CheckpointAsync();
        Check.Equal(1, await persistence.ClearHistoryAsync(), "later clear deletes new accumulated row");
        persistence.Record(Fixtures.Delta(70, seen: clock.GetUtcNow()));
        Check.Equal(0, persistence.Health.PendingKeys, "latest clear boundary filters exact timestamp again");
        clock.Advance(TimeSpan.FromSeconds(1));
        persistence.Record(Fixtures.Delta(4, seen: clock.GetUtcNow()));
        await persistence.CheckpointAsync();
        Check.Equal(4L, scope.Open().GetUsageForDate(Fixtures.Today).Single().DownloadBytes, "latest boundary admits newer usage");
    }

    private static async Task StorageIdleAfterFlushTimeout()
    {
        using var scope = new StorageScope();
        var backend = new HistoryBackend(scope.Open());
        backend.Checkpoints.Entered = Signal();
        backend.Checkpoints.Release = Signal();
        var persistence = Create(backend);
        persistence.Record(Fixtures.Delta(77));
        persistence.Seal();
        var flush = persistence.FlushAsync(TimeSpan.FromMilliseconds(30));
        await backend.Checkpoints.Entered.Task;
        Check.True(!await flush, "bounded flush returns while determinate SQL remains blocked");
        var idle = persistence.WaitForStorageIdleAsync();
        Check.True(!idle.IsCompleted, "history lease must stay held while timed-out flush owns a batch");
        backend.Checkpoints.Release.TrySetResult();
        await idle;
        Check.Equal(0, persistence.Health.PendingKeys, "all committed references released before idle acknowledgement");
        Check.Equal(77L, scope.Open().GetUsageForDate(Fixtures.Today).Single().DownloadBytes, "actual transaction committed before idle acknowledgement");
        await persistence.WaitForStorageIdleAsync();
        Check.Equal(1, backend.Checkpoints.AddCalls, "idle acknowledgement introduces no replay or new writes");
    }

    private static async Task CancelledStorageIdle()
    {
        using var scope = new StorageScope();
        var backend = new HistoryBackend(scope.Open());
        backend.Checkpoints.Entered = Signal();
        backend.Checkpoints.Release = Signal();
        var persistence = Create(backend);
        persistence.Record(Fixtures.Delta(33));
        persistence.Seal();
        var flush = persistence.FlushAsync(TimeSpan.FromMilliseconds(30));
        await backend.Checkpoints.Entered.Task;
        await flush;
        using var cancellation = new CancellationTokenSource();
        var idle = persistence.WaitForStorageIdleAsync(cancellation.Token);
        cancellation.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => idle, "cancelled idle caller cannot claim quiescence");
        var secondIdle = persistence.WaitForStorageIdleAsync();
        Check.True(!secondIdle.IsCompleted, "actual SQL still remains owned after cancelling first waiter");
        backend.Checkpoints.Release.TrySetResult();
        await secondIdle;
        Check.Equal(33L, scope.Open().GetUsageForDate(Fixtures.Today).Single().DownloadBytes, "later acknowledgement awaits actual commit");
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class HistoryBackend(DailyAppUsageStore store) : IUsageCheckpointStore, IUsageHistoryStore
    {
        public TestBackend Checkpoints { get; } = new(store);
        public int ClearCalls { get; private set; }
        public int ClearFailuresRemaining { get; set; }
        public TaskCompletionSource? ClearEntered { get; init; }
        public TaskCompletionSource? ClearRelease { get; init; }
        public CancellationToken ClearToken { get; private set; }
        public Task InitializeAsync(CancellationToken token) => Checkpoints.InitializeAsync(token);
        public Task AddUsageDeltasAsync(IReadOnlyCollection<UsageDelta> values, CancellationToken token) =>
            Checkpoints.AddUsageDeltasAsync(values, token);
        public Task<int> RetentionCleanupAsync(DateOnly date, CancellationToken token) =>
            Checkpoints.RetentionCleanupAsync(date, token);
        public async Task<int> ClearHistoryAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ClearCalls++;
            ClearToken = token;
            ClearEntered?.TrySetResult();
            if (ClearRelease is not null) await ClearRelease.Task;
            if (ClearFailuresRemaining-- > 0) throw new IOException("Injected determinate clear rollback");
            return store.ClearHistory();
        }
    }
}
