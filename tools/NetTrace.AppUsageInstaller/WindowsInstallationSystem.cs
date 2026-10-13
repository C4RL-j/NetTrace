using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using AppUsageCollectorCore;
using AppUsageIpc;

namespace NetTrace.AppUsageInstaller;

public sealed partial class WindowsInstallationSystem : IServiceInstallationSystem
{
    private static readonly SecurityIdentifier SystemSid = new("S-1-5-18");
    private static readonly SecurityIdentifier AdministratorsSid = new("S-1-5-32-544");
    private static readonly HashSet<string> TrustedWriters = new(StringComparer.Ordinal)
    { "S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464" };
    private readonly string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private readonly string commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private uint stoppedProcessId;

    private sealed record InstalledManifest(int Format, InstallationConfiguration Configuration,
        IReadOnlyDictionary<string, string> Files);

    public void RequireElevation()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The installer requires Windows.");
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Run this install/uninstall command from an elevated Administrator terminal. NetTrace itself stays unelevated.");
    }

    public void ValidateEnvironment(string? sourceDirectory, string? clientSid)
    {
        if (ScmNative.Read(ServicePipeSecurity.DefaultServiceName) is not null)
            throw new InvalidOperationException("Remove the temporary development collector service before using the production installer; both collectors share the owned ETW/IPC identity.");
        var service = ReadService();
        uint registeredPid = service?.ProcessId ?? 0;
        foreach (var process in Process.GetProcessesByName("NetTrace.AppUsageService"))
        {
            using (process)
                if ((uint)process.Id != registeredPid)
                    throw new InvalidOperationException("Another collector process is running. Stop its owning service before installation or uninstall.");
        }
        var sample = InstallationConfiguration.Create(programFiles, commonData, clientSid ?? CurrentSid(), new string('0', 64));
        VerifyNoReparse(programFiles); VerifyNoReparse(commonData);
        VerifyNoUntrustedWriters(programFiles);
        VerifyExistingProtectedTree(sample.InstallationRoot);
        VerifyExistingProtectedTree(sample.DataDirectory);
        if (clientSid is not null && clientSid != CurrentSid())
            throw new ArgumentException("The intended client SID must match the account running this elevated installer. Elevate the same account that runs NetTrace.");
        if (sourceDirectory is not null)
        {
            if (!Path.IsPathFullyQualified(sourceDirectory)) throw new ArgumentException("The service source directory must be absolute.");
            VerifyNoReparse(sourceDirectory);
            if (Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar)
                    .Equals(sample.InstallationRoot, StringComparison.OrdinalIgnoreCase)
                || IsWithin(sourceDirectory, sample.InstallationRoot))
                throw new ArgumentException("Use a separate complete build/publish directory as the installation source.");
            _ = ReadKit(sourceDirectory);
        }
    }

    public ServiceRegistration? ReadService() => ScmNative.Read(InstallationConfiguration.ProductionServiceName);

    public InstallationConfiguration ValidateOwnedRegistration(ServiceRegistration registration)
        => ValidateOwnedRegistration(registration, requireDescription: true);

    private InstallationConfiguration ValidateOwnedRegistration(ServiceRegistration registration, bool requireDescription)
    {
        if (registration.ServiceName != InstallationConfiguration.ProductionServiceName
            || registration.Identity != "LocalSystem"
            || requireDescription && registration.Description != InstallationConfiguration.OwnershipDescription)
            throw new InvalidOperationException("The production service registration does not belong to this installer; refusing to modify it.");
        string command = registration.BinaryCommandLine;
        if (!command.StartsWith('"')) throw new InvalidOperationException("The owned service image must be quoted.");
        int closing = command.IndexOf('"', 1);
        if (closing <= 1) throw new InvalidOperationException("Invalid owned service image.");
        string executable = Path.GetFullPath(command[1..closing]);
        string directory = Path.GetDirectoryName(executable)!;
        string root = Path.Combine(programFiles, "NetTrace", "AppUsageCollector");
        string version = Path.GetFileName(directory);
        if (!IsWithin(directory, Path.Combine(root, "Versions")) || executable != Path.Combine(directory, "NetTrace.AppUsageService.exe")
            || version.Length != 64 || version.Any(value => !Uri.IsHexDigit(value)))
            throw new InvalidOperationException("The production service image is outside the protected installation layout.");
        VerifyExistingProtectedTree(directory);
        string clientPrefix = " --client-sid ";
        string afterExecutable = command[(closing + 1)..];
        if (!afterExecutable.StartsWith(clientPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid owned service client identity arguments.");
        string clientSid = afterExecutable[clientPrefix.Length..].Split(' ', 2)[0];
        InstallationConfiguration.ValidateClientSid(clientSid);
        string manifestPath = Path.Combine(directory, ClientManifestName(clientSid));
        VerifyNoUntrustedWriters(manifestPath);
        var manifest = JsonSerializer.Deserialize<InstalledManifest>(File.ReadAllText(manifestPath))
            ?? throw new InvalidOperationException("The protected installation manifest is missing or invalid.");
        var expected = InstallationConfiguration.Create(programFiles, commonData, manifest.Configuration.ClientSid, version);
        if (manifest.Format != 1 || manifest.Configuration != expected || command != expected.BinaryCommandLine)
            throw new InvalidOperationException("The service command does not exactly match its protected ownership manifest.");
        VerifyManifest(directory, manifest.Files);
        return expected;
    }

    public InstallationConfiguration PrepareFiles(string sourceDirectory, string clientSid)
    {
        var files = ReadKit(sourceDirectory);
        string version = KitVersion(files);
        var configuration = InstallationConfiguration.Create(programFiles, commonData, clientSid, version);
        ProtectDirectory(Path.Combine(programFiles, "NetTrace"));
        ProtectDirectory(configuration.InstallationRoot);
        ProtectDirectory(Path.Combine(configuration.InstallationRoot, "Versions"));
        ProtectDirectory(configuration.BinaryDirectory);
        foreach (var entry in files)
        {
            string target = Path.Combine(configuration.BinaryDirectory, entry.Key);
            ProtectDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                VerifyNoUntrustedWriters(target);
                if (FileHash(target) != entry.Value)
                    throw new InvalidOperationException("An existing protected binary version differs from its content hash.");
            }
            else
            {
                using (var input = File.OpenRead(Path.Combine(sourceDirectory, entry.Key)))
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);
                ProtectFile(target);
                if (FileHash(target) != entry.Value)
                    throw new InvalidOperationException("The service source changed during staging; the service was not registered.");
            }
        }
        VerifyManifest(configuration.BinaryDirectory, files);
        string manifest = Path.Combine(configuration.BinaryDirectory, ClientManifestName(clientSid));
        if (File.Exists(manifest)) VerifyNoUntrustedWriters(manifest);
        // The version may be reused for a different intended client on a later
        // install. A running service is stopped before its configuration changes.
        WriteProtectedJson(manifest, new InstalledManifest(1, configuration, files));
        ProtectDirectory(configuration.DataDirectory);
        ProtectDirectory(Path.GetDirectoryName(configuration.LogPath)!);
        foreach (string database in new[] { configuration.DatabasePath, configuration.DatabasePath + "-wal", configuration.DatabasePath + "-shm" })
            if (File.Exists(database)) { VerifyNoUntrustedWriters(database); ProtectFile(database); }
        if (File.Exists(configuration.LogPath)) { VerifyNoUntrustedWriters(configuration.LogPath); ProtectFile(configuration.LogPath); }
        return configuration;
    }

    public void Configure(InstallationConfiguration configuration, bool create)
    {
        bool created = false;
        try
        {
            if (create)
            {
                RunSc("create", configuration.ServiceName, "binPath=", configuration.BinaryCommandLine,
                    "type=", "own", "start=", "disabled", "obj=", "LocalSystem", "DisplayName=", "NetTrace App Usage Collector");
                created = true;
            }
            else RunSc("config", configuration.ServiceName, "binPath=", configuration.BinaryCommandLine,
                "type=", "own", "start=", "disabled", "obj=", "LocalSystem", "DisplayName=", "NetTrace App Usage Collector");
            RunSc("description", configuration.ServiceName, InstallationConfiguration.OwnershipDescription);
            RunSc("sidtype", configuration.ServiceName, "unrestricted");
            RunSc("failure", configuration.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000/none/0");
            RunSc("failureflag", configuration.ServiceName, "1");
            RunSc("config", configuration.ServiceName, "start=", "delayed-auto");
            ScmNative.VerifyConfiguration(configuration);
        }
        catch (Exception original) when (created)
        {
            // Metadata failure can happen immediately after CreateService, before
            // the description marker exists. Only this just-created, still-stopped
            // registration may use the exact image/manifest ownership proof.
            try
            {
                var partial = ReadService();
                if (partial is not null)
                {
                    if (partial.Running || partial.ProcessId != 0 || partial.BinaryCommandLine != configuration.BinaryCommandLine)
                        throw new InvalidOperationException("The newly created registration changed before rollback.");
                    ValidateOwnedRegistration(partial, requireDescription: false);
                    RunSc("config", configuration.ServiceName, "start=", "disabled");
                    Delete(); VerifyAbsentAndCleanOwnedResources();
                }
            }
            catch (Exception cleanup) { throw new AggregateException("Service registration metadata failed and its owned registration could not be removed.", original, cleanup); }
            throw;
        }
    }

    public void DisableRecovery()
    {
        // SCM may already have queued a restart. Disabling startup prevents that
        // queued action from relaunching the collector during update/uninstall.
        RunSc("config", InstallationConfiguration.ProductionServiceName, "start=", "disabled");
        RunSc("failure", InstallationConfiguration.ProductionServiceName, "reset=", "0", "actions=", "none/0");
        RunSc("failureflag", InstallationConfiguration.ProductionServiceName, "0");
    }

    public void Stop()
    {
        var registration = ReadService();
        if (registration is null) return;
        ValidateOwnedRegistration(registration);
        stoppedProcessId = registration.ProcessId;
        using var service = new ServiceController(InstallationConfiguration.ProductionServiceName);
        service.Refresh();
        if (service.Status != ServiceControllerStatus.Stopped)
        {
            if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        }
        if (stoppedProcessId != 0)
        {
            try
            {
                using var process = Process.GetProcessById((int)stoppedProcessId);
                if (!process.WaitForExit(10000)) throw new InvalidOperationException("The owned collector process did not exit after stopping.");
            }
            catch (ArgumentException) { }
        }
    }

    public void Delete() => RunSc("delete", InstallationConfiguration.ProductionServiceName);

    public void VerifyAbsentAndCleanOwnedResources()
    {
        var deadline = Stopwatch.StartNew();
        while (ReadService() is not null)
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(15))
                throw new InvalidOperationException("SCM still has the production service registration after deletion.");
            Thread.Sleep(100);
        }
        // Cleanup obtains the existing protected mutex and verifies the exact
        // session GUID, realtime mode and name before stopping a stale session.
        if (!CollectorRuntime.CleanupOwnedSession()) throw new InvalidOperationException("The exact owned ETW session remained after cleanup.");
        foreach (var process in Process.GetProcessesByName("NetTrace.AppUsageService"))
            using (process) throw new InvalidOperationException("A collector process remains after uninstall; refusing to claim cleanup succeeded.");
        if (ScmNative.PipeExists(ServicePipeSecurity.DefaultPipeName))
            throw new InvalidOperationException("The owned collector IPC pipe remains after uninstall.");
    }

    public async Task StartAndVerifyAsync(InstallationConfiguration configuration, CancellationToken cancellationToken)
    {
        RunSc("start", configuration.ServiceName);
        using (var service = new ServiceController(configuration.ServiceName))
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        var deadline = Stopwatch.StartNew();
        IpcState last = IpcState.CollectorUnavailable;
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadService() is not { Running: true }) throw new InvalidOperationException("The production service stopped before IPC became available.");
            await using var client = new IpcClient(ServicePipeSecurity.DefaultPipeName, serviceName: configuration.ServiceName);
            var connected = await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            last = connected.State;
            if (connected.State == IpcState.Connected)
            {
                var result = await client.GetAppUsageRangeAsync(AppUsageRange.Today, cancellationToken).ConfigureAwait(false);
                last = result.State;
                if (result.State == IpcState.Connected && result.Value?.Availability == AppUsageAvailability.Available)
                { ScmNative.VerifyConfiguration(configuration); return; }
            }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException($"The production service did not provide a verified IPC v2 usage query within 30 seconds ({last}).");
    }

    public object ReadStatus() => ScmNative.ReadDetailed(InstallationConfiguration.ProductionServiceName);

    private static string CurrentSid() => WindowsIdentity.GetCurrent().User?.Value
        ?? throw new UnauthorizedAccessException("The current Windows user SID is unavailable.");

    private static SortedDictionary<string, string> ReadKit(string source)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("The service source directory does not exist.");
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        long total = 0;
        foreach (string path in Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories))
        {
            VerifyNoReparse(path);
            if (Directory.Exists(path)) continue;
            string relative = Path.GetRelativePath(source, path);
            if (relative.StartsWith("installation-0051", StringComparison.Ordinal)) throw new ArgumentException("The source directory must be a build/publish kit, not an installed version.");
            if (++total > 10000) throw new ArgumentException("The source kit contains too many entries.");
            result.Add(relative, FileHash(path));
        }
        string[] required = ["NetTrace.AppUsageService.exe", "NetTrace.AppUsageService.dll",
            "NetTrace.AppUsageService.deps.json", "NetTrace.AppUsageService.runtimeconfig.json", "AppUsageCollectorCore.dll",
            "AppUsageIpc.dll", "AppUsageStorage.dll", "Microsoft.Data.Sqlite.dll", "Microsoft.Diagnostics.Tracing.TraceEvent.dll"];
        if (required.Any(name => !result.ContainsKey(name)) || !result.Keys.Any(name => name.EndsWith("e_sqlite3.dll", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The source directory is not a complete service build/publish kit (managed and native SQLite dependencies are required).");
        return result;
    }

    private static string KitVersion(IReadOnlyDictionary<string, string> files)
        => CollectorPackageVersion.Compute(files);

    private static string ClientManifestName(string clientSid) => "installation-0051-"
        + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clientSid))).ToLowerInvariant() + ".json";

    private static void VerifyManifest(string directory, IReadOnlyDictionary<string, string> files)
    {
        if (files.Count == 0 || KitVersion(files) != Path.GetFileName(directory))
            throw new InvalidOperationException("The installed binary inventory does not match the version identity.");
        foreach (var entry in files)
        {
            string path = Path.GetFullPath(Path.Combine(directory, entry.Key));
            if (!IsWithin(path, directory)) throw new InvalidOperationException("The installation manifest contains an unsafe file path.");
            VerifyNoUntrustedWriters(path);
            if (FileHash(path) != entry.Value) throw new InvalidOperationException("The installed service files do not match the protected manifest.");
        }
    }

    private static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool IsWithin(string path, string root) => Path.GetFullPath(path)
        .StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void VerifyExistingProtectedTree(string path)
    {
        VerifyNoReparse(path);
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if (Directory.Exists(current) && Path.GetFileName(current).Equals("NetTrace", StringComparison.OrdinalIgnoreCase))
            { VerifyNoUntrustedWriters(current); break; }
            if (Directory.Exists(current)) VerifyNoUntrustedWriters(current);
            current = Path.GetDirectoryName(current);
            if (current is not null && (current.Equals(Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase)
                || current.Equals(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), StringComparison.OrdinalIgnoreCase)
                || current.Equals(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase))) break;
        }
    }

    private static void VerifyNoReparse(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException($"Refusing a reparse point in the installation path: {current}");
            current = Path.GetDirectoryName(current);
        }
    }

    private static void VerifyNoUntrustedWriters(string path)
    {
        VerifyNoReparse(path);
        FileSystemSecurity security = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();
        string owner = (security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value
            ?? throw new UnauthorizedAccessException("The installation path owner is unavailable.");
        using var identity = WindowsIdentity.GetCurrent();
        if (!TrustedWriters.Contains(owner) && owner != identity.User?.Value)
            throw new UnauthorizedAccessException($"The installation path has an untrusted owner: {path}");
        const FileSystemRights dangerous = FileSystemRights.WriteData | FileSystemRights.AppendData
            | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete
            | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & dangerous) != 0
                && !TrustedWriters.Contains(((SecurityIdentifier)rule.IdentityReference).Value))
                throw new UnauthorizedAccessException($"The installation path is writable by a non-service/non-administrator principal: {path}");
    }

    private static void ProtectDirectory(string path)
    {
        VerifyNoReparse(path);
        var security = new DirectorySecurity();
        security.SetOwner(AdministratorsSid);
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { SystemSid, AdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (Directory.Exists(path)) VerifyNoUntrustedWriters(path);
        else new DirectoryInfo(path).Create(security);
        VerifyNoReparse(path);
        VerifyNoUntrustedWriters(path);
        new DirectoryInfo(path).SetAccessControl(security);
        VerifyNoUntrustedWriters(path);
    }

    private static void ProtectFile(string path)
    {
        VerifyNoReparse(path);
        var security = new FileSecurity();
        security.SetOwner(AdministratorsSid);
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { SystemSid, AdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static void WriteProtectedJson(string path, InstalledManifest value)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value));
        ProtectFile(temporary);
        File.Move(temporary, path, overwrite: true);
        ProtectFile(path);
    }

    private static void RunSc(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe"))
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("SCM command could not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000))
        {
            // Stop only this owned command helper. A late SCM mutation must not
            // race the engine's rollback after its deadline has expired.
            if (!process.HasExited) process.Kill(entireProcessTree: false);
            if (!process.WaitForExit(5000)) throw new InvalidOperationException("The timed-out SCM helper did not exit; further service mutation is unsafe.");
            throw new InvalidOperationException("SCM command exceeded its deadline; its owned helper was stopped.");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException($"SCM {arguments[0]} failed ({process.ExitCode}): {output.GetAwaiter().GetResult().Trim()} {errors.GetAwaiter().GetResult().Trim()}");
    }
}
