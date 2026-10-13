using AppUsageIpc;

namespace NetTrace.AppUsageInstaller;

public enum HistoryServiceState { Missing, Stopped, Running, Pending }
public sealed record HistoryServiceSnapshot(HistoryServiceState State, ServiceRegistration? Registration = null);
public sealed record HistoryClearResult(bool StorageExists, int ClearedRows, bool ServiceRunning,
    string Operation = "clear-history");

// No service control or configuration mutation is available through this seam.
public interface IAppUsageHistoryClearSystem
{
    void RequireElevation();
    void ValidateClearEnvironment();
    HistoryServiceSnapshot ReadClearService();
    void ValidateClearRegistration(ServiceRegistration registration);
    bool SupportsOfflineCoordination(ServiceRegistration registration);
    bool HistoryStorageExists();
    Task<int> ClearRunningAsync(CancellationToken cancellationToken);
    Task<IDisposable> AcquireOfflineHistoryLeaseAsync(CancellationToken cancellationToken);
    void VerifyNoOfflineCollector();
    int ClearExistingHistory();
}

public sealed class HistoryClearEngine(IAppUsageHistoryClearSystem system)
{
    public async Task<HistoryClearResult> ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        system.RequireElevation();
        system.ValidateClearEnvironment();
        var before = ReadAndValidate();
        if (!system.HistoryStorageExists()) return new(false, 0, before.State == HistoryServiceState.Running);
        if (before.State == HistoryServiceState.Running)
        {
            // A service without management support fails safely. Never fall
            // back to an offline write while the live collector could replay.
            int cleared = await system.ClearRunningAsync(cancellationToken).ConfigureAwait(false);
            return new(true, cleared, true);
        }
        if (before.State is not (HistoryServiceState.Missing or HistoryServiceState.Stopped))
            throw new InvalidOperationException("Wait for app tracking to finish starting or stopping before clearing history.");
        RequireOfflineCoordination(before);
        using var lease = await system.AcquireOfflineHistoryLeaseAsync(cancellationToken).ConfigureAwait(false);
        var after = ReadAndValidate();
        if (after.State is not (HistoryServiceState.Missing or HistoryServiceState.Stopped)
            || after.Registration?.ProcessId is > 0)
            throw new InvalidOperationException("App tracking started while preparing the clear; retry after it is running.");
        RequireOfflineCoordination(after);
        system.VerifyNoOfflineCollector();
        // Lease blocks new service SQLite initialization until this exact
        // existing database transaction has committed. Schema stays intact.
        if (!system.HistoryStorageExists()) return new(false, 0, false);
        cancellationToken.ThrowIfCancellationRequested();
        int rows = system.ClearExistingHistory();
        return new(true, rows, false);
    }

    private HistoryServiceSnapshot ReadAndValidate()
    {
        var service = system.ReadClearService();
        if (service.Registration is not null) system.ValidateClearRegistration(service.Registration);
        return service;
    }

    private void RequireOfflineCoordination(HistoryServiceSnapshot service)
    {
        if (service.State == HistoryServiceState.Stopped && service.Registration is not null
            && !system.SupportsOfflineCoordination(service.Registration))
            throw new AppUsageHistoryManagementUnavailableException("Update App Tracking before clearing history; this installed collector does not coordinate offline storage maintenance.");
    }
}
