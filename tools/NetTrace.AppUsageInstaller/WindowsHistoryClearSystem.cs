using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using AppUsageCollectorCore;
using AppUsageEtwProbe;
using AppUsageIpc;
using AppUsageStorage;

namespace NetTrace.AppUsageInstaller;

public sealed partial class WindowsInstallationSystem : IAppUsageHistoryClearSystem
{
    public void ValidateClearEnvironment()
    {
        // Reuse the protected roots and ownership validation without preparing
        // files, changing ACLs, registering anything or cleaning ETW sessions.
        ValidateEnvironment(null, null);
        string database = DailyAppUsageStore.GetDatabasePath(commonData);
        VerifyExistingProtectedTree(Path.GetDirectoryName(database)!);
        foreach (string file in new[] { database, database + "-wal", database + "-shm", AppUsageHistoryLease.GetPath(commonData) })
            if (File.Exists(file)) VerifyNoUntrustedWriters(file);
    }

    public HistoryServiceSnapshot ReadClearService()
    {
        var registration = ReadService();
        if (registration is null) return new(HistoryServiceState.Missing);
        uint state = ScmNative.ReadCurrentState(InstallationConfiguration.ProductionServiceName);
        return new(state switch
        {
            1 when registration.ProcessId == 0 => HistoryServiceState.Stopped,
            4 when registration.Running && registration.ProcessId != 0 => HistoryServiceState.Running,
            _ => HistoryServiceState.Pending
        }, registration);
    }

    public void ValidateClearRegistration(ServiceRegistration registration) => ValidateOwnedRegistration(registration);
    public bool SupportsOfflineCoordination(ServiceRegistration registration)
    {
        // Inspect only verified protected bytes. Never load or execute installed
        // code in the helper. Pre-005.4 hosts do not retain the history lease and
        // could otherwise start while an offline transaction is in progress.
        var configuration = ValidateOwnedRegistration(registration);
        using var file = File.OpenRead(Path.Combine(configuration.BinaryDirectory, "NetTrace.AppUsageService.dll"));
        using var image = new PEReader(file);
        if (!image.HasMetadata) return false;
        var metadata = image.GetMetadataReader();
        foreach (var handle in metadata.TypeReferences)
        {
            var type = metadata.GetTypeReference(handle);
            if (metadata.GetString(type.Namespace) == "AppUsageCollectorCore"
                && metadata.GetString(type.Name) == nameof(AppUsageHistoryLease)) return true;
        }
        return false;
    }
    public bool HistoryStorageExists() => File.Exists(DailyAppUsageStore.GetDatabasePath(commonData));

    public Task<int> ClearRunningAsync(CancellationToken cancellationToken)
        => new AppUsageHistoryManagementClient().ClearHistoryAsync(cancellationToken);

    public async Task<IDisposable> AcquireOfflineHistoryLeaseAsync(CancellationToken cancellationToken)
    {
        // Do not create storage merely because a stale settings view launched
        // the helper; both the database and its protected directory must exist.
        if (!HistoryStorageExists()) throw new FileNotFoundException("Application usage storage no longer exists.");
        ValidateClearEnvironment();
        var lease = await AppUsageHistoryLease.AcquireAsync(commonData, TimeSpan.FromSeconds(15), cancellationToken,
            requireProtectedDirectory: true).ConfigureAwait(false);
        try { ValidateClearEnvironment(); return lease; }
        catch { lease.Dispose(); throw; }
    }

    public void VerifyNoOfflineCollector()
    {
        if (ScmNative.Read(ServicePipeSecurity.DefaultServiceName) is not null)
            throw new InvalidOperationException("The temporary development collector is installed; offline history clear is unsafe.");
        foreach (var process in Process.GetProcessesByName("NetTrace.AppUsageService"))
            using (process) throw new InvalidOperationException("A collector process is using application usage storage.");
        if (NativeEtwController.Query(SessionOwnership.SessionName) is not null
            || ScmNative.PipeExists(ServicePipeSecurity.DefaultPipeName)
            || ScmNative.PipeExists(AppUsageHistoryManagement.PipeName))
            throw new InvalidOperationException("An owned collector session or pipe is still present; history was not cleared.");
    }

    public int ClearExistingHistory()
    {
        ValidateClearEnvironment();
        return DailyAppUsageStore.OpenExistingWritable(commonData).ClearHistory();
    }
}
