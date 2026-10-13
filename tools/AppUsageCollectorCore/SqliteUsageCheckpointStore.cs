using AppUsageStorage;

namespace AppUsageCollectorCore;

// This adapter only schedules the existing 004.1 synchronous operations away
// from ETW callbacks. DailyAppUsageStore owns all SQL and nonpooled connections.
public sealed class SqliteUsageCheckpointStore : IUsageCheckpointStore, IUsageHistoryStore
{
    private readonly object initializationGate = new();
    private readonly string commonDataRoot;
    private DailyAppUsageStore? store;

    public SqliteUsageCheckpointStore(string? commonDataRoot = null)
    {
        this.commonDataRoot = commonDataRoot
            ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        DatabasePath = DailyAppUsageStore.GetDatabasePath(this.commonDataRoot);
    }

    public string DatabasePath { get; }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (initializationGate) store ??= new DailyAppUsageStore(commonDataRoot);
        }, CancellationToken.None);
    }

    public Task AddUsageDeltasAsync(IReadOnlyCollection<UsageDelta> deltas, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deltas);
        cancellationToken.ThrowIfCancellationRequested();
        var batch = deltas.ToArray();
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetInitializedStore().AddUsageDeltas(batch);
        }, CancellationToken.None);
    }

    public Task<int> RetentionCleanupAsync(DateOnly cutoffDate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return GetInitializedStore().RetentionCleanup(cutoffDate);
        }, CancellationToken.None);
    }

    public Task<int> ClearHistoryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return GetInitializedStore().ClearHistory();
        }, CancellationToken.None);
    }

    private DailyAppUsageStore GetInitializedStore()
    {
        lock (initializationGate)
            return store ?? throw new InvalidOperationException("Initialize application usage storage before writing.");
    }
}
