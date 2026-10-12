using AppUsageStorage;

namespace AppUsageCollectorCore;

// A completed write must mean a determinate commit or rollback. Implementations
// must not return cancellation while a transaction could still commit later.
public interface IUsageCheckpointStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task AddUsageDeltasAsync(IReadOnlyCollection<UsageDelta> deltas, CancellationToken cancellationToken);
    Task<int> RetentionCleanupAsync(DateOnly cutoffDate, CancellationToken cancellationToken);
}

public sealed record UsagePersistenceHealth(
    bool PersistenceAvailable,
    DateTimeOffset? LastSuccessfulCheckpoint,
    bool PersistenceDegraded,
    long CheckpointAttempts,
    long SuccessfulCheckpoints,
    long FailedCheckpoints,
    int PendingKeys,
    string? LastError,
    long RetentionRuns)
{
    public long AcceptedEvents { get; init; }
    public long AcceptedDownloadBytes { get; init; }
    public long AcceptedUploadBytes { get; init; }
    public long CommittedDownloadBytes { get; init; }
    public long CommittedUploadBytes { get; init; }
}

// Pending values are event deltas, never differences between cumulative IPC
// snapshots. Only short accumulator operations share the producer lock.
public sealed class UsagePersistence
{
    public static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(5);
    private const int MaximumErrorLength = 512;
    private readonly object gate = new();
    private readonly SemaphoreSlim checkpointGate = new(1, 1);
    private readonly IUsageCheckpointStore store;
    private readonly TimeProvider timeProvider;
    private readonly TimeZoneInfo localTimeZone;
    private readonly Action<string>? diagnostic;
    private readonly int maximumPendingKeys;
    private Dictionary<UsageKey, UsageDelta> active = new();
    private Dictionary<UsageKey, UsageDelta>? retained;
    // Reference counts include active and retained/in-flight batches, so the
    // bound cannot be evaded while SQLite is slow or temporarily unavailable.
    private readonly Dictionary<UsageKey, int> pendingKeys = new();
    private bool sealedAccumulator;
    private bool initialized;
    private bool retentionCompleted;
    private bool degraded;
    private DateTimeOffset? lastSuccessfulCheckpoint;
    private string? lastError;
    private long checkpointAttempts;
    private long successfulCheckpoints;
    private long failedCheckpoints;
    private long retentionRuns;
    private long acceptedEvents;
    private long acceptedDownloadBytes;
    private long acceptedUploadBytes;
    private long committedDownloadBytes;
    private long committedUploadBytes;

    public UsagePersistence(IUsageCheckpointStore store, TimeProvider? timeProvider = null,
        TimeZoneInfo? localTimeZone = null, Action<string>? diagnostic = null, int maximumPendingKeys = 10000)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (maximumPendingKeys <= 0) throw new ArgumentOutOfRangeException(nameof(maximumPendingKeys));
        this.store = store;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.localTimeZone = localTimeZone ?? TimeZoneInfo.Local;
        this.diagnostic = diagnostic;
        this.maximumPendingKeys = maximumPendingKeys;
    }

    public UsagePersistenceHealth Health
    {
        get
        {
            lock (gate)
                return new(initialized, lastSuccessfulCheckpoint, degraded, checkpointAttempts,
                    successfulCheckpoints, failedCheckpoints, pendingKeys.Count, lastError, retentionRuns)
                {
                    AcceptedEvents = acceptedEvents,
                    AcceptedDownloadBytes = acceptedDownloadBytes,
                    AcceptedUploadBytes = acceptedUploadBytes,
                    CommittedDownloadBytes = committedDownloadBytes,
                    CommittedUploadBytes = committedUploadBytes
                };
        }
    }

    public void Record(UsageDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        ArgumentNullException.ThrowIfNull(delta.Application);
        if (delta.DownloadBytes < 0) throw new ArgumentOutOfRangeException(nameof(delta), "Download bytes cannot be negative.");
        if (delta.UploadBytes < 0) throw new ArgumentOutOfRangeException(nameof(delta), "Upload bytes cannot be negative.");
        var key = new UsageKey(delta.LocalDate, delta.Application.Key);
        lock (gate)
        {
            if (sealedAccumulator) throw new InvalidOperationException("The persistence accumulator has been sealed.");
            if (delta.DownloadBytes == 0 && delta.UploadBytes == 0) return;
            var hasActive = active.TryGetValue(key, out var existing);
            UsageDelta accumulated;
            try
            {
                accumulated = delta;
                if (hasActive)
                {
                    var current = existing!;
                    var latest = delta.LastSeen >= current.LastSeen ? delta : current;
                    accumulated = latest with
                    {
                        DownloadBytes = checked(current.DownloadBytes + delta.DownloadBytes),
                        UploadBytes = checked(current.UploadBytes + delta.UploadBytes)
                    };
                }
                if (retained is not null && retained.TryGetValue(key, out var waiting))
                {
                    _ = checked(accumulated.DownloadBytes + waiting.DownloadBytes);
                    _ = checked(accumulated.UploadBytes + waiting.UploadBytes);
                }
            }
            catch (OverflowException exception)
            {
                throw new InvalidOperationException("Pending usage exceeds the supported signed byte range; safe collection cannot continue.", exception);
            }
            if (!hasActive)
            {
                if (!pendingKeys.TryGetValue(key, out var references))
                {
                    if (pendingKeys.Count >= maximumPendingKeys)
                        throw new InvalidOperationException("The pending application/day limit was reached; safe collection cannot continue.");
                    references = 0;
                }
                pendingKeys[key] = references + 1;
            }
            active[key] = accumulated;
            acceptedEvents = SaturatingAdd(acceptedEvents, 1);
            acceptedDownloadBytes = SaturatingAdd(acceptedDownloadBytes, delta.DownloadBytes);
            acceptedUploadBytes = SaturatingAdd(acceptedUploadBytes, delta.UploadBytes);
        }
    }

    // The service calls this only after the ETW producer has finished. Sealing
    // separately from flushing prevents a final checkpoint racing late events.
    public void Seal()
    {
        lock (gate) sealedAccumulator = true;
    }

    public async Task<bool> CheckpointAsync(CancellationToken cancellationToken = default)
    {
        await checkpointGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await PrepareStorageAsync(cancellationToken).ConfigureAwait(false)) return false;
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<UsageKey, UsageDelta>? batch;
            lock (gate)
            {
                batch = retained;
                if (batch is null && active.Count > 0)
                {
                    batch = active;
                    active = new();
                    retained = batch;
                }
                if (batch is null) return true;
                checkpointAttempts = SaturatingAdd(checkpointAttempts, 1);
            }

            try
            {
                // A token cancelled during synchronous SQLite I/O must never
                // cause the caller to requeue a batch that actually committed.
                await store.AddUsageDeltasAsync(batch.Values, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                lock (gate) failedCheckpoints = SaturatingAdd(failedCheckpoints, 1);
                SetFailure(exception);
                // The exact batch remains pinned. Later observations can use
                // active independently; retries commit this batch only once.
                return false;
            }

            bool recovered;
            long batchDownloadBytes = 0;
            long batchUploadBytes = 0;
            DateTimeOffset committedAt;
            lock (gate)
            {
                foreach (var (key, delta) in batch)
                {
                    var references = pendingKeys[key] - 1;
                    if (references == 0) pendingKeys.Remove(key);
                    else pendingKeys[key] = references;
                    committedDownloadBytes = SaturatingAdd(committedDownloadBytes, delta.DownloadBytes);
                    committedUploadBytes = SaturatingAdd(committedUploadBytes, delta.UploadBytes);
                    batchDownloadBytes = SaturatingAdd(batchDownloadBytes, delta.DownloadBytes);
                    batchUploadBytes = SaturatingAdd(batchUploadBytes, delta.UploadBytes);
                }
                retained = null;
                successfulCheckpoints = SaturatingAdd(successfulCheckpoints, 1);
                committedAt = timeProvider.GetUtcNow();
                lastSuccessfulCheckpoint = committedAt;
                recovered = degraded;
                degraded = false;
                lastError = null;
            }
            if (recovered) Report("Application usage persistence recovered.");
            Report($"Application usage checkpoint committed at {committedAt:O}: rows={batch.Count}; downloadBytes={batchDownloadBytes}; uploadBytes={batchUploadBytes}.");
            return true;
        }
        finally { checkpointGate.Release(); }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Initialize and retain at startup, but allow the first data batch
            // to collect for the full interval before writing it.
            await checkpointGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await PrepareStorageAsync(cancellationToken).ConfigureAwait(false); }
            finally { checkpointGate.Release(); }
            using var timer = new PeriodicTimer(CheckpointInterval, timeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await CheckpointAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    // Timeout bounds the service's wait, not an already-started transaction.
    // That task retains ownership of its batch until its commit is determinate.
    public async Task<bool> FlushAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        try
        {
            return await FlushCoreAsync(timeoutCancellation.Token)
                .WaitAsync(timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            Report("Application usage final flush reached its bounded timeout.");
            return false;
        }
    }

    private async Task<bool> FlushCoreAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (!await CheckpointAsync(cancellationToken).ConfigureAwait(false)) return false;
            lock (gate)
                if (pendingKeys.Count == 0) return true;
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private async Task<bool> PrepareStorageAsync(CancellationToken cancellationToken)
    {
        try
        {
            bool needsInitialization;
            bool needsRetention;
            lock (gate)
            {
                needsInitialization = !initialized;
                needsRetention = !retentionCompleted;
            }
            if (needsInitialization)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                lock (gate) initialized = true;
            }
            if (needsRetention)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), localTimeZone).DateTime);
                await store.RetentionCleanupAsync(DailyAppUsageStore.GetRetentionCutoff(today),
                    CancellationToken.None).ConfigureAwait(false);
                lock (gate)
                {
                    retentionCompleted = true;
                    retentionRuns = SaturatingAdd(retentionRuns, 1);
                    degraded = false;
                    lastError = null;
                }
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            SetFailure(exception);
            return false;
        }
    }

    private void SetFailure(Exception exception)
    {
        var message = $"{exception.GetType().Name}: {exception.Message}".Replace('\r', ' ').Replace('\n', ' ');
        if (message.Length > MaximumErrorLength) message = message[..MaximumErrorLength];
        bool changed;
        lock (gate)
        {
            changed = !degraded || !StringComparer.Ordinal.Equals(lastError, message);
            degraded = true;
            lastError = message;
        }
        if (changed) Report($"Application usage persistence degraded: {message}");
    }

    private void Report(string message)
    {
        try { diagnostic?.Invoke(message); }
        catch { /* A diagnostic sink must not discard usage or stop retries. */ }
    }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private readonly record struct UsageKey(DateOnly LocalDate, string ApplicationIdentity);
}
