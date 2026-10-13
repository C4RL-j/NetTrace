using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using AppUsageIpc;
using Microsoft.Win32.SafeHandles;

namespace NetTrace.Services;

public sealed record AppTrackingInstallLaunchSpec(string InstallerPath, string ServiceDirectory, string ClientSid)
{
    public static AppTrackingInstallLaunchSpec Create(string applicationDirectory, string clientSid)
    {
        if (!Path.IsPathFullyQualified(applicationDirectory))
            throw new ArgumentException("The application directory must be absolute.", nameof(applicationDirectory));
        var identity = new SecurityIdentifier(clientSid);
        if (!clientSid.StartsWith("S-1-5-21-", StringComparison.Ordinal)
            || clientSid.Split('-').Length != 8 || identity.AccountDomainSid is null)
            throw new ArgumentException("The intended client must be a Windows user SID.", nameof(clientSid));
        var kit = Path.Combine(Path.GetFullPath(applicationDirectory), "AppTracking");
        return new(Path.Combine(kit, "Installer", "NetTrace.AppUsageInstaller.exe"),
            Path.Combine(kit, "Service"), clientSid);
    }

    public ProcessStartInfo CreateStartInfo()
    {
        var start = new ProcessStartInfo(InstallerPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(InstallerPath)!
        };
        start.ArgumentList.Add("install");
        start.ArgumentList.Add("--source");
        start.ArgumentList.Add(ServiceDirectory);
        start.ArgumentList.Add("--client-sid");
        start.ArgumentList.Add(ClientSid);
        return start;
    }

    public bool HasRequiredFiles(Func<string, bool> fileExists)
    {
        var installerDirectory = Path.GetDirectoryName(InstallerPath)!;
        return new[]
        {
            InstallerPath,
            Path.Combine(installerDirectory, "NetTrace.AppUsageInstaller.dll"),
            Path.Combine(installerDirectory, "NetTrace.AppUsageInstaller.deps.json"),
            Path.Combine(installerDirectory, "NetTrace.AppUsageInstaller.runtimeconfig.json"),
            Path.Combine(ServiceDirectory, "NetTrace.AppUsageService.exe"),
            Path.Combine(ServiceDirectory, "NetTrace.AppUsageService.dll"),
            Path.Combine(ServiceDirectory, "NetTrace.AppUsageService.deps.json"),
            Path.Combine(ServiceDirectory, "NetTrace.AppUsageService.runtimeconfig.json")
        }.All(fileExists);
    }
}

public sealed record AppTrackingUninstallLaunchSpec(string InstallerPath)
{
    public static AppTrackingUninstallLaunchSpec Create(string applicationDirectory)
    {
        if (!Path.IsPathFullyQualified(applicationDirectory))
            throw new ArgumentException("The application directory must be absolute.", nameof(applicationDirectory));
        return new(Path.Combine(Path.GetFullPath(applicationDirectory), "AppTracking", "Installer",
            "NetTrace.AppUsageInstaller.exe"));
    }

    public ProcessStartInfo CreateStartInfo()
    {
        var start = new ProcessStartInfo(InstallerPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(InstallerPath)!
        };
        start.ArgumentList.Add("uninstall");
        return start;
    }

    public bool HasRequiredFiles(Func<string, bool> fileExists)
    {
        var installerDirectory = Path.GetDirectoryName(InstallerPath)!;
        return new[]
        {
            InstallerPath,
            Path.Combine(installerDirectory, "NetTrace.AppUsageInstaller.dll"),
            Path.Combine(installerDirectory, "NetTrace.AppUsageInstaller.deps.json"),
            Path.Combine(installerDirectory, "NetTrace.AppUsageInstaller.runtimeconfig.json")
        }.All(fileExists);
    }
}

public sealed record AppUsageHistoryClearLaunchSpec(string InstallerPath)
{
    public static AppUsageHistoryClearLaunchSpec Create(string applicationDirectory)
    {
        if (!Path.IsPathFullyQualified(applicationDirectory))
            throw new ArgumentException("The application directory must be absolute.", nameof(applicationDirectory));
        return new(Path.Combine(Path.GetFullPath(applicationDirectory), "AppTracking", "Installer",
            "NetTrace.AppUsageInstaller.exe"));
    }

    public ProcessStartInfo CreateStartInfo()
    {
        var start = new ProcessStartInfo(InstallerPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(InstallerPath)!
        };
        start.ArgumentList.Add("clear-history");
        return start;
    }

    public bool HasRequiredFiles(Func<string, bool> fileExists)
    {
        var installerDirectory = Path.GetDirectoryName(InstallerPath)!;
        return new[]
        {
            InstallerPath,
            Path.Combine(installerDirectory, "NetTrace.AppUsageInstaller.dll"),
            Path.Combine(installerDirectory, "NetTrace.AppUsageInstaller.deps.json"),
            Path.Combine(installerDirectory, "NetTrace.AppUsageInstaller.runtimeconfig.json")
        }.All(fileExists);
    }
}

// The launcher accepts only the fixed install invocation, never a command shell.
public interface IAppTrackingInstallerLauncher
{
    Task<int> LaunchAsync(AppTrackingInstallLaunchSpec invocation);
}

public interface IAppTrackingUninstallerLauncher
{
    Task<int> LaunchAsync(AppTrackingUninstallLaunchSpec invocation);
}

public interface IAppUsageHistoryClearLauncher
{
    Task<int> LaunchAsync(AppUsageHistoryClearLaunchSpec invocation);
}

public interface IAppUsageStorageStateReader
{
    AppUsageStorageState GetState();
}

public interface IAppTrackingServiceStateReader
{
    AppTrackingServiceState GetState();
}

public interface IAppTrackingUpdateVersionReader
{
    AppTrackingUpdateStatus GetStatus(string serviceDirectory, bool refreshBundledVersion = false);
}

public sealed class AppTrackingSetupService : IAppTrackingUpdateService
{
    private enum Operation { None, Install, Uninstall, ClearHistory, Update }

    private readonly object _operationGate = new();
    private readonly IAppTrackingInstallerLauncher _launcher;
    private readonly IAppTrackingUninstallerLauncher _uninstallerLauncher;
    private readonly IAppUsageHistoryClearLauncher _historyLauncher;
    private readonly IAppUsageStorageStateReader _storageReader;
    private readonly IAppTrackingServiceStateReader _stateReader;
    private readonly IAppTrackingUpdateVersionReader _versionReader;
    private readonly string _applicationDirectory;
    private readonly string? _clientSid;
    private readonly Func<string, bool> _fileExists;
    private Task<AppTrackingInstallOutcome>? _installation;
    private Task<AppTrackingUninstallOutcome>? _uninstallation;
    private Task<AppUsageHistoryClearOutcome>? _historyClear;
    private Task<AppTrackingUpdateOutcome>? _update;
    private Operation _operation;

    public event EventHandler? StateChanged;
    public event EventHandler? HistoryCleared;

    public AppTrackingSetupService(IAppTrackingInstallerLauncher? launcher = null,
        IAppTrackingServiceStateReader? stateReader = null, string? applicationDirectory = null,
        string? clientSid = null, Func<string, bool>? fileExists = null,
        IAppTrackingUninstallerLauncher? uninstallerLauncher = null,
        IAppUsageHistoryClearLauncher? historyLauncher = null,
        IAppUsageStorageStateReader? storageReader = null,
        IAppTrackingUpdateVersionReader? versionReader = null)
    {
        _launcher = launcher ?? new WindowsAppTrackingInstallerLauncher();
        _uninstallerLauncher = uninstallerLauncher ?? new WindowsAppTrackingUninstallerLauncher();
        _historyLauncher = historyLauncher ?? new WindowsAppUsageHistoryClearLauncher();
        _storageReader = storageReader ?? new WindowsAppUsageStorageStateReader();
        _stateReader = stateReader ?? new WindowsAppTrackingServiceStateReader();
        _versionReader = versionReader ?? new WindowsAppTrackingUpdateVersionReader();
        _applicationDirectory = applicationDirectory ?? AppContext.BaseDirectory;
        _clientSid = clientSid;
        _fileExists = fileExists ?? File.Exists;
    }

    public Task<AppTrackingUpdateStatus> GetUpdateStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return _versionReader.GetStatus(Path.Combine(Path.GetFullPath(_applicationDirectory),
                    "AppTracking", "Service"));
            }
            catch { return new AppTrackingUpdateStatus(AppTrackingUpdateAvailability.Unavailable); }
        }, cancellationToken);
    }

    public Task<AppUsageStorageState> GetStorageStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return _storageReader.GetState(); }
            catch { return AppUsageStorageState.Unavailable; }
        }, cancellationToken);
    }

    public Task<AppTrackingServiceState> GetStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_operationGate)
        {
            if (_operation == Operation.Install)
                return Task.FromResult(AppTrackingServiceState.Installing);
            if (_operation == Operation.Uninstall)
                return Task.FromResult(AppTrackingServiceState.Uninstalling);
            if (_operation == Operation.Update)
                return Task.FromResult(AppTrackingServiceState.Updating);
        }
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return _stateReader.GetState(); }
            catch { return AppTrackingServiceState.Unavailable; }
        }, cancellationToken);
    }

    public Task<AppTrackingInstallOutcome> InstallAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<AppTrackingInstallOutcome> installation;
        lock (_operationGate)
        {
            if (_operation != Operation.None && _operation != Operation.Install)
                return Task.FromResult(AppTrackingInstallOutcome.Failed);
            if (_operation != Operation.Install)
            {
                _operation = Operation.Install;
                _installation = Task.Run(async () =>
                {
                    try { return await InstallCoreAsync().ConfigureAwait(false); }
                    finally { CompleteOperation(); }
                });
            }
            installation = _installation!;
        }
        // A window can stop waiting; the elevated installer must finish safely.
        // Its lifetime is not tied to a page, window or the caller's token.
        return installation.WaitAsync(cancellationToken);
    }

    public Task<AppTrackingUninstallOutcome> UninstallAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<AppTrackingUninstallOutcome> uninstallation;
        lock (_operationGate)
        {
            if (_operation != Operation.None && _operation != Operation.Uninstall)
                return Task.FromResult(AppTrackingUninstallOutcome.Failed);
            if (_operation != Operation.Uninstall)
            {
                _operation = Operation.Uninstall;
                _uninstallation = Task.Run(async () =>
                {
                    try { return await UninstallCoreAsync().ConfigureAwait(false); }
                    finally { CompleteOperation(); }
                });
            }
            uninstallation = _uninstallation!;
        }
        // As with installation, a cancelled view cannot interrupt an elevated child.
        return uninstallation.WaitAsync(cancellationToken);
    }

    public Task<AppUsageHistoryClearOutcome> ClearHistoryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<AppUsageHistoryClearOutcome> clear;
        lock (_operationGate)
        {
            if (_operation != Operation.None && _operation != Operation.ClearHistory)
                return Task.FromResult(AppUsageHistoryClearOutcome.Failed);
            if (_operation != Operation.ClearHistory)
            {
                _operation = Operation.ClearHistory;
                _historyClear = Task.Run(async () =>
                {
                    var outcome = AppUsageHistoryClearOutcome.Failed;
                    try
                    {
                        outcome = await ClearHistoryCoreAsync().ConfigureAwait(false);
                        return outcome;
                    }
                    finally
                    {
                        CompleteOperation(notifyStateChanged: false);
                        if (outcome == AppUsageHistoryClearOutcome.Succeeded) Notify(HistoryCleared);
                    }
                });
            }
            clear = _historyClear!;
        }
        // Closing a view only cancels its wait. The elevated transaction must finish.
        return clear.WaitAsync(cancellationToken);
    }

    public Task<AppTrackingUpdateOutcome> UpdateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<AppTrackingUpdateOutcome> update;
        lock (_operationGate)
        {
            if (_operation != Operation.None && _operation != Operation.Update)
                return Task.FromResult(AppTrackingUpdateOutcome.Failed);
            if (_operation != Operation.Update)
            {
                _operation = Operation.Update;
                _update = Task.Run(async () =>
                {
                    try { return await UpdateCoreAsync().ConfigureAwait(false); }
                    finally { CompleteOperation(); }
                });
            }
            update = _update!;
        }
        // Recreated windows share the operation; cancelling their wait cannot
        // interrupt the elevated installer's existing stop/stage/restart path.
        return update.WaitAsync(cancellationToken);
    }

    private void CompleteOperation(bool notifyStateChanged = true)
    {
        lock (_operationGate) _operation = Operation.None;
        // Notify after clearing the marker so observers can immediately inspect SCM.
        // A failing observer must not turn a completed installer operation into failure.
        if (notifyStateChanged) Notify(StateChanged);
    }

    private void Notify(EventHandler? notification)
    {
        if (notification is null) return;
        foreach (EventHandler handler in notification.GetInvocationList())
        {
            try { handler(this, EventArgs.Empty); }
            catch { }
        }
    }

    private async Task<AppTrackingInstallOutcome> InstallCoreAsync()
    {
        try
        {
            string sid;
            if (_clientSid is not null) sid = _clientSid;
            else
            {
                using var identity = WindowsIdentity.GetCurrent();
                sid = identity.User?.Value ?? throw new InvalidOperationException();
            }
            var invocation = AppTrackingInstallLaunchSpec.Create(_applicationDirectory, sid);
            if (!invocation.HasRequiredFiles(_fileExists)) return AppTrackingInstallOutcome.FilesMissing;
            return await _launcher.LaunchAsync(invocation).ConfigureAwait(false) == 0
                ? AppTrackingInstallOutcome.Succeeded : AppTrackingInstallOutcome.Failed;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        { return AppTrackingInstallOutcome.Cancelled; }
        catch { return AppTrackingInstallOutcome.Failed; }
    }

    private async Task<AppTrackingUpdateOutcome> UpdateCoreAsync()
    {
        try
        {
            string sid;
            if (_clientSid is not null) sid = _clientSid;
            else
            {
                using var identity = WindowsIdentity.GetCurrent();
                sid = identity.User?.Value ?? throw new InvalidOperationException();
            }
            var invocation = AppTrackingInstallLaunchSpec.Create(_applicationDirectory, sid);
            if (!invocation.HasRequiredFiles(_fileExists)) return AppTrackingUpdateOutcome.FilesMissing;
            // Recheck SCM and the source kit at the explicit click. No elevation
            // occurs for a current, absent, or unverifiable service registration.
            var status = _versionReader.GetStatus(invocation.ServiceDirectory, refreshBundledVersion: true);
            if (status.Availability == AppTrackingUpdateAvailability.Current)
                return AppTrackingUpdateOutcome.Succeeded;
            if (status.Availability == AppTrackingUpdateAvailability.NotInstalled)
                return AppTrackingUpdateOutcome.NotInstalled;
            if (status.Availability != AppTrackingUpdateAvailability.UpdateAvailable)
                return AppTrackingUpdateOutcome.Failed;
            // The existing installer owns configuration, database preservation,
            // rollback, delayed start, LocalSystem, recovery and IPC verification.
            return await _launcher.LaunchAsync(invocation).ConfigureAwait(false) == 0
                ? AppTrackingUpdateOutcome.Succeeded : AppTrackingUpdateOutcome.Failed;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        { return AppTrackingUpdateOutcome.Cancelled; }
        catch { return AppTrackingUpdateOutcome.Failed; }
    }

    private async Task<AppTrackingUninstallOutcome> UninstallCoreAsync()
    {
        try
        {
            var invocation = AppTrackingUninstallLaunchSpec.Create(_applicationDirectory);
            if (!invocation.HasRequiredFiles(_fileExists)) return AppTrackingUninstallOutcome.FilesMissing;
            return await _uninstallerLauncher.LaunchAsync(invocation).ConfigureAwait(false) == 0
                ? AppTrackingUninstallOutcome.Succeeded : AppTrackingUninstallOutcome.Failed;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        { return AppTrackingUninstallOutcome.Cancelled; }
        catch { return AppTrackingUninstallOutcome.Failed; }
    }

    private async Task<AppUsageHistoryClearOutcome> ClearHistoryCoreAsync()
    {
        try
        {
            var invocation = AppUsageHistoryClearLaunchSpec.Create(_applicationDirectory);
            if (!invocation.HasRequiredFiles(_fileExists)) return AppUsageHistoryClearOutcome.FilesMissing;
            return (await _historyLauncher.LaunchAsync(invocation).ConfigureAwait(false)) switch
            {
                0 => AppUsageHistoryClearOutcome.Succeeded,
                3 => AppUsageHistoryClearOutcome.StorageMissing,
                4 => AppUsageHistoryClearOutcome.CollectorUpdateRequired,
                _ => AppUsageHistoryClearOutcome.Failed
            };
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        { return AppUsageHistoryClearOutcome.Cancelled; }
        catch { return AppUsageHistoryClearOutcome.Failed; }
    }
}

public sealed class WindowsAppTrackingInstallerLauncher : IAppTrackingInstallerLauncher
{
    public async Task<int> LaunchAsync(AppTrackingInstallLaunchSpec invocation)
    {
        // Process.Start may wait for the UAC prompt, so never run it on the UI thread.
        using var process = await Task.Run(() => Process.Start(invocation.CreateStartInfo())
            ?? throw new InvalidOperationException("The installer did not start.")).ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }
}

public sealed class WindowsAppTrackingUninstallerLauncher : IAppTrackingUninstallerLauncher
{
    public async Task<int> LaunchAsync(AppTrackingUninstallLaunchSpec invocation)
    {
        using var process = await Task.Run(() => Process.Start(invocation.CreateStartInfo())
            ?? throw new InvalidOperationException("The uninstaller did not start.")).ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }
}

public sealed class WindowsAppUsageHistoryClearLauncher : IAppUsageHistoryClearLauncher
{
    public async Task<int> LaunchAsync(AppUsageHistoryClearLaunchSpec invocation)
    {
        using var process = await Task.Run(() => Process.Start(invocation.CreateStartInfo())
            ?? throw new InvalidOperationException("The history tool did not start.")).ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }
}

public sealed class WindowsAppUsageStorageStateReader : IAppUsageStorageStateReader
{
    public AppUsageStorageState GetState()
    {
        // File.Exists hides access-denied failures. A normal desktop token cannot
        // inspect the protected directory, but may explicitly ask the elevated tool.
        // This presence check neither opens SQLite nor creates files or directories.
        var database = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NetTrace", "app-usage.db");
        var attributes = GetFileAttributes(database);
        if (attributes != uint.MaxValue)
            return (attributes & ((uint)FileAttributes.Directory | (uint)FileAttributes.ReparsePoint)) == 0
                ? AppUsageStorageState.Exists : AppUsageStorageState.Unavailable;
        return Marshal.GetLastWin32Error() switch
        {
            2 or 3 => AppUsageStorageState.Missing,
            5 => AppUsageStorageState.Protected,
            _ => AppUsageStorageState.Unavailable
        };
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileAttributesW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributes(string filename);
}

public sealed class WindowsAppTrackingServiceStateReader : IAppTrackingServiceStateReader
{
    private readonly string _serviceName;

    public WindowsAppTrackingServiceStateReader(string serviceName = ServicePipeSecurity.ProductionServiceName)
    {
        ServicePipeSecurity.ValidateServiceName(serviceName);
        _serviceName = serviceName;
    }

    public AppTrackingServiceState GetState()
    {
        using var manager = OpenSCManager(null, null, 1); // SC_MANAGER_CONNECT
        if (manager.IsInvalid) return AppTrackingServiceState.Unavailable;
        using var service = OpenService(manager, _serviceName, 4); // QUERY_STATUS
        if (service.IsInvalid)
            return Marshal.GetLastWin32Error() == 1060
                ? AppTrackingServiceState.Missing : AppTrackingServiceState.Unavailable;
        if (!QueryServiceStatus(service, out var status)) return AppTrackingServiceState.Unavailable;
        return status.CurrentState switch
        {
            1 => AppTrackingServiceState.Stopped,
            4 => AppTrackingServiceState.Running,
            _ => AppTrackingServiceState.Unavailable
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode,
            ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenService(ServiceHandle manager, string service, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(ServiceHandle service, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr service);
}

public sealed class WindowsAppTrackingUpdateVersionReader : IAppTrackingUpdateVersionReader
{
    private const string OwnershipDescription = "NetTrace App Usage production collector; installation format 005.1.";
    private readonly object _versionGate = new();
    private string? _bundledDirectory, _bundledVersion;

    public AppTrackingUpdateStatus GetStatus(string serviceDirectory, bool refreshBundledVersion = false)
    {
        string? installed = null, bundled = null;
        try
        {
            using var manager = OpenSCManager(null, null, 1); // SC_MANAGER_CONNECT
            if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            using var service = OpenService(manager, ServicePipeSecurity.ProductionServiceName, 1); // QUERY_CONFIG
            if (service.IsInvalid)
            {
                if (Marshal.GetLastWin32Error() == 1060)
                    return new(AppTrackingUpdateAvailability.NotInstalled);
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            var config = ReadConfiguration(service);
            if (config.ServiceType != 0x10 || !TryReadInstalledVersion(config.BinaryCommandLine,
                    config.Identity, ReadDescription(service),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), out installed))
                return new(AppTrackingUpdateAvailability.Unavailable);
            string directory = Path.GetFullPath(serviceDirectory);
            lock (_versionGate)
            {
                // Hash the complete bundle once per application session. An
                // explicit update always revalidates it before requesting UAC.
                if (refreshBundledVersion || _bundledDirectory != directory || _bundledVersion is null)
                {
                    _bundledVersion = ReadBundledVersion(directory);
                    _bundledDirectory = directory;
                }
                bundled = _bundledVersion;
            }
            return new(string.Equals(installed, bundled, StringComparison.OrdinalIgnoreCase)
                ? AppTrackingUpdateAvailability.Current : AppTrackingUpdateAvailability.UpdateAvailable,
                installed, bundled);
        }
        catch { return new(AppTrackingUpdateAvailability.Unavailable, installed, bundled); }
    }

    // Status reads only SCM metadata. Program Files versions are protected from
    // a normal desktop token; the existing elevated installer verifies their
    // manifest and binaries again before making any modification.
    public static bool TryReadInstalledVersion(string binaryCommandLine, string identity, string description,
        string programFilesRoot, string commonDataRoot, out string? version)
    {
        version = null;
        try
        {
            if (identity != "LocalSystem" || description != OwnershipDescription
                || !Path.IsPathFullyQualified(programFilesRoot) || !Path.IsPathFullyQualified(commonDataRoot)
                || !binaryCommandLine.StartsWith('"')) return false;
            int closing = binaryCommandLine.IndexOf('"', 1);
            if (closing <= 1) return false;
            string image = binaryCommandLine[1..closing];
            if (!Path.IsPathFullyQualified(image)) return false;
            string directory = Path.GetDirectoryName(image)!;
            string installedVersion = Path.GetFileName(directory);
            if (!CollectorPackageVersion.IsVersion(installedVersion)) return false;
            string expectedImage = Path.Combine(Path.GetFullPath(programFilesRoot), "NetTrace",
                "AppUsageCollector", "Versions", installedVersion, "NetTrace.AppUsageService.exe");
            if (!image.Equals(expectedImage, StringComparison.OrdinalIgnoreCase)) return false;
            const string clientPrefix = " --client-sid ";
            string suffix = binaryCommandLine[(closing + 1)..];
            if (!suffix.StartsWith(clientPrefix, StringComparison.Ordinal)) return false;
            string clientSid = suffix[clientPrefix.Length..].Split(' ', 2)[0];
            var client = new SecurityIdentifier(clientSid);
            if (!clientSid.StartsWith("S-1-5-21-", StringComparison.Ordinal)
                || clientSid.Split('-').Length != 8 || client.AccountDomainSid is null) return false;
            string log = Path.Combine(Path.GetFullPath(commonDataRoot), "NetTrace", "AppUsageService", "service.log");
            string expectedSuffix = $" --client-sid {clientSid} --service-name {ServicePipeSecurity.ProductionServiceName} --log \"{log}\"";
            if (suffix != expectedSuffix) return false;
            version = installedVersion.ToLowerInvariant();
            return true;
        }
        catch { return false; }
    }

    private static string ReadBundledVersion(string source)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException();
        VerifyNoReparse(source);
        var inventory = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories))
        {
            VerifyNoReparse(path);
            if (Directory.Exists(path)) continue;
            string relative = Path.GetRelativePath(source, path);
            if (relative.StartsWith("installation-0051", StringComparison.Ordinal) || inventory.Count >= 10000)
                throw new ArgumentException("The bundled collector is not a valid service kit.");
            using var stream = File.OpenRead(path);
            inventory.Add(relative, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
        }
        string[] required = ["NetTrace.AppUsageService.exe", "NetTrace.AppUsageService.dll",
            "NetTrace.AppUsageService.deps.json", "NetTrace.AppUsageService.runtimeconfig.json", "AppUsageCollectorCore.dll",
            "AppUsageIpc.dll", "AppUsageStorage.dll", "Microsoft.Data.Sqlite.dll", "Microsoft.Diagnostics.Tracing.TraceEvent.dll"];
        if (required.Any(name => !inventory.ContainsKey(name))
            || !inventory.Keys.Any(name => name.EndsWith("e_sqlite3.dll", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The bundled collector kit is incomplete.");
        return CollectorPackageVersion.Compute(inventory);
    }

    private static void VerifyNoReparse(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("The bundled collector contains a reparse point.");
            current = Path.GetDirectoryName(current);
        }
    }

    private sealed record Configuration(uint ServiceType, string BinaryCommandLine, string Identity);

    private static Configuration ReadConfiguration(ServiceHandle service)
    {
        QueryServiceConfig(service, IntPtr.Zero, 0, out uint required);
        if (required == 0 || required > 65536) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            if (!QueryServiceConfig(service, buffer, required, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var config = Marshal.PtrToStructure<QueryConfiguration>(buffer);
            return new(config.ServiceType, Marshal.PtrToStringUni(config.BinaryPathName) ?? "",
                Marshal.PtrToStringUni(config.ServiceStartName) ?? "");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string ReadDescription(ServiceHandle service)
    {
        QueryServiceConfig2(service, 1, IntPtr.Zero, 0, out uint required);
        if (required == 0 || required > 65536) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            if (!QueryServiceConfig2(service, 1, buffer, required, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer)) ?? "";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryConfiguration
    {
        public uint ServiceType, StartType, ErrorControl;
        public IntPtr BinaryPathName, LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies, ServiceStartName, DisplayName;
    }

    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenService(ServiceHandle manager, string service, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryServiceConfig(ServiceHandle service, IntPtr buffer, uint size, out uint required);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryServiceConfig2(ServiceHandle service, uint level, IntPtr buffer, uint size, out uint required);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr service);
}
