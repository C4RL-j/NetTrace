using System.Security.Principal;

namespace NetTrace.AppUsageInstaller;

public sealed record RecoveryAction(string Kind, int DelayMilliseconds);

public sealed record RecoveryPolicy(int ResetPeriodSeconds, bool RecoverNonCrashFailures,
    IReadOnlyList<RecoveryAction> Actions)
{
    public static RecoveryPolicy Production { get; } = new(86400, true,
        Array.AsReadOnly(new[] { new RecoveryAction("Restart", 5000), new("Restart", 15000),
            new("Restart", 60000), new("None", 0) }));
}

public sealed record InstallationConfiguration(string InstallationRoot, string BinaryDirectory,
    string ExecutablePath, string DataDirectory, string DatabasePath, string LogPath, string ClientSid)
{
    public const string ProductionServiceName = "NetTraceAppUsageCollector";
    public const string OwnershipDescription = "NetTrace App Usage production collector; installation format 005.1.";
    public string ServiceName => ProductionServiceName;
    public string Identity => "LocalSystem";
    public string StartupMode => "Automatic (Delayed Start)";
    public RecoveryPolicy Recovery => RecoveryPolicy.Production;
    public string BinaryCommandLine => $"\"{ExecutablePath}\" --client-sid {ClientSid} --service-name {ServiceName} --log \"{LogPath}\"";

    public static InstallationConfiguration Create(string programFilesRoot, string commonDataRoot,
        string clientSid, string binaryVersion)
    {
        ValidateClientSid(clientSid);
        if (!Path.IsPathFullyQualified(programFilesRoot) || !Path.IsPathFullyQualified(commonDataRoot))
            throw new ArgumentException("Installation roots must be absolute paths.");
        if (binaryVersion.Length != 64 || binaryVersion.Any(value => !Uri.IsHexDigit(value)))
            throw new ArgumentException("A SHA-256 binary version is required.");
        string root = Path.Combine(Path.GetFullPath(programFilesRoot), "NetTrace", "AppUsageCollector");
        string binary = Path.Combine(root, "Versions", binaryVersion.ToLowerInvariant());
        string data = Path.Combine(Path.GetFullPath(commonDataRoot), "NetTrace");
        return new(root, binary, Path.Combine(binary, "NetTrace.AppUsageService.exe"), data,
            Path.Combine(data, "app-usage.db"), Path.Combine(data, "AppUsageService", "service.log"), clientSid);
    }

    public static void ValidateClientSid(string sid)
    {
        var identity = new SecurityIdentifier(sid);
        if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) || sid.Split('-').Length != 8
            || identity.AccountDomainSid is null)
            throw new ArgumentException("The intended client must be a Windows user SID.");
    }
}

public sealed record ServiceRegistration(string ServiceName, string BinaryCommandLine,
    string Identity, string Description, bool Running, uint ProcessId = 0);
public enum InstallDecision { Create, Update }
public static class InstallationDecision
{
    public static InstallDecision ForInstall(ServiceRegistration? existing, bool owned)
    {
        if (existing is null) return InstallDecision.Create;
        if (!owned || existing.ServiceName != InstallationConfiguration.ProductionServiceName)
            throw new InvalidOperationException("The production service name is occupied by an unowned registration.");
        return InstallDecision.Update;
    }
}

public sealed record InstallationResult(string Operation, string ServiceName, bool ServiceRunning,
    bool IpcAvailable, bool DatabasePreserved, InstallDecision? Decision = null);

public sealed record InstallerInvocation(string Operation, string? SourceDirectory, string? ClientSid)
{
    public static InstallerInvocation Parse(string[] arguments)
    {
        if (arguments.Length == 0 || arguments[0] is not ("install" or "uninstall" or "status" or "clear-history"))
            throw new ArgumentException("Use install --source ABSOLUTE_DIRECTORY [--client-sid SID], uninstall, clear-history, or status.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 1; index < arguments.Length; index++)
        {
            string option = arguments[index];
            if (arguments[0] != "install" || option is not ("--source" or "--client-sid")
                || ++index >= arguments.Length || arguments[index].StartsWith("--", StringComparison.Ordinal)
                || !values.TryAdd(option, arguments[index])) throw new ArgumentException("Invalid installer options.");
        }
        string? source = values.GetValueOrDefault("--source"), sid = values.GetValueOrDefault("--client-sid");
        if (arguments[0] == "install" && (source is null || !Path.IsPathFullyQualified(source)))
            throw new ArgumentException("Install requires --source with an absolute service build/publish directory.");
        if (sid is not null) InstallationConfiguration.ValidateClientSid(sid);
        return new(arguments[0], source is null ? null : Path.GetFullPath(source), sid);
    }
}
