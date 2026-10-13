namespace NetTrace.Services;

public enum AppTrackingServiceState { Missing, Running, Stopped, Unavailable, Installing, Uninstalling, Updating }
public enum AppTrackingInstallOutcome { Succeeded, Cancelled, Failed, FilesMissing }
public enum AppTrackingUninstallOutcome { Succeeded, Cancelled, Failed, FilesMissing }
public enum AppUsageStorageState { Exists, Missing, Protected, Unavailable }
public enum AppUsageHistoryClearOutcome { Succeeded, Cancelled, Failed, FilesMissing, StorageMissing, CollectorUpdateRequired }
public enum AppTrackingUpdateAvailability { Current, UpdateAvailable, Unavailable, NotInstalled }
public enum AppTrackingUpdateOutcome { Succeeded, Cancelled, Failed, FilesMissing, NotInstalled }

public sealed record AppTrackingUpdateStatus(AppTrackingUpdateAvailability Availability,
    string? InstalledVersion = null, string? BundledVersion = null);

public interface IAppTrackingSetupService
{
    Task<AppTrackingServiceState> GetStateAsync(CancellationToken cancellationToken);
    Task<AppTrackingInstallOutcome> InstallAsync(CancellationToken cancellationToken);
}

public interface IAppTrackingManagementService : IAppTrackingSetupService
{
    event EventHandler? StateChanged;
    Task<AppTrackingUninstallOutcome> UninstallAsync(CancellationToken cancellationToken);
}

public interface IAppUsageHistoryManagementService : IAppTrackingManagementService
{
    event EventHandler? HistoryCleared;
    Task<AppUsageStorageState> GetStorageStateAsync(CancellationToken cancellationToken);
    Task<AppUsageHistoryClearOutcome> ClearHistoryAsync(CancellationToken cancellationToken);
}

public interface IAppTrackingUpdateService : IAppUsageHistoryManagementService
{
    Task<AppTrackingUpdateStatus> GetUpdateStatusAsync(CancellationToken cancellationToken);
    Task<AppTrackingUpdateOutcome> UpdateAsync(CancellationToken cancellationToken);
}
