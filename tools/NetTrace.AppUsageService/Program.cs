using System.ServiceProcess;
using System.Security.Principal;
using AppUsageIpc;
using AppUsageEtwProbe;
using AppUsageCollectorCore;

namespace NetTrace.AppUsageService;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.SequenceEqual(["--help"]))
        {
            Console.WriteLine("NetTrace.AppUsageService --client-sid SID [--pipe NAME] [--log FILE] [--report FILE]");
            Console.WriteLine("SCM-only development service. Administrator-only --cleanup-owned-session manages the exact owned ETW identity.");
            return 0;
        }
        if (args.SequenceEqual(["--cleanup-owned-session"]))
        {
            if (!EtwCapture.IsElevated) return 2;
            try { return CollectorRuntime.CleanupOwnedSession() ? 0 : 1; }
            catch (Exception exception) { Console.Error.WriteLine($"Exact-session cleanup failed: {exception.GetType().Name}: {exception.Message}"); return 1; }
        }
        BoundedLifecycleLog? log = null;
        try
        {
            var options = ServiceOptions.Parse(args);
            using var identity = WindowsIdentity.GetCurrent();
            var serviceIdentity = identity.User?.Value switch
            {
                "S-1-5-19" => "LocalService",
                "S-1-5-18" => "LocalSystem",
                _ => throw new UnauthorizedAccessException("Run the collector only through SCM under a built-in service identity.")
            };
            log = new(options.LogPath);
            log.Write($"Service process started; identity={serviceIdentity}; administrator={EtwCapture.IsElevated}; PID={Environment.ProcessId}.");
            using var host = new CollectorServiceHost(options, serviceIdentity, log);
            ServiceBase.Run(host);
            return host.ExitCode == 0 ? 0 : 1;
        }
        catch (Exception exception)
        {
            log?.Write($"Service initialization failed: {exception.GetType().Name}: {exception.Message}");
            Console.Error.WriteLine($"Service initialization failed: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
        finally { log?.Dispose(); }
    }
}

public sealed record ServiceOptions(string ClientSid, string PipeName, string? LogPath, string? ReportPath)
{
    public static ServiceOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < args.Length; index++)
        {
            string name = args[index];
            if (name is not ("--client-sid" or "--pipe" or "--log" or "--report" or "--service-name")
                || ++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal)
                || !values.TryAdd(name, args[index])) throw new ArgumentException("Invalid service options.");
        }
        if (!values.TryGetValue("--client-sid", out var clientSid)) throw new ArgumentException("An intended client SID is required.");
        _ = new SecurityIdentifier(clientSid);
        if (!clientSid.StartsWith("S-1-5-21-", StringComparison.Ordinal))
            throw new ArgumentException("The intended client must be a Windows user SID.");
        if (values.TryGetValue("--service-name", out var serviceName)) ServicePipeSecurity.ValidateServiceName(serviceName);
        string pipe = values.GetValueOrDefault("--pipe") ?? ServicePipeSecurity.DefaultPipeName;
        NativePipeSecurity.ValidatePipeName(pipe);
        return new(clientSid, pipe, FullPath("--log"), FullPath("--report"));
        string? FullPath(string name) => values.TryGetValue(name, out var path) ? Path.GetFullPath(path) : null;
    }
}

public sealed class BoundedLifecycleLog : IDisposable
{
    private readonly object gate = new();
    private readonly StreamWriter? writer;
    private int lines;
    public BoundedLifecycleLog(string? path)
    {
        if (path is null) return;
        // Validation chooses a protected temporary directory. Keep it bounded.
        var info = new FileInfo(path);
        if (info.Exists && info.Length >= 1024 * 1024) File.WriteAllText(path, "");
        writer = new StreamWriter(path, append: true) { AutoFlush = true };
    }
    public void Write(string message)
    {
        lock (gate)
        {
            if (++lines <= 200) writer?.WriteLine($"{DateTime.UtcNow:O} {message}");
        }
    }
    public void Dispose() { lock (gate) writer?.Dispose(); }
}
