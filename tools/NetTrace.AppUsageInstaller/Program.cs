using System.Security.Principal;
using System.Text.Json;
using AppUsageIpc;

namespace NetTrace.AppUsageInstaller;

public static class Program
{
    public static async Task<int> Main(string[] arguments)
    {
        if (arguments.SequenceEqual(["--help"]))
        {
            Console.WriteLine("NetTrace.AppUsageInstaller install --source ABSOLUTE_SERVICE_BUILD_OR_PUBLISH_DIRECTORY [--client-sid SID]");
            Console.WriteLine("NetTrace.AppUsageInstaller uninstall | clear-history | status");
            Console.WriteLine("Install/uninstall/clear-history require an elevated Administrator terminal; the installer never triggers UAC. Elevate the same account that runs NetTrace.");
            Console.WriteLine("Service: NetTraceAppUsageCollector; LocalSystem; Automatic (Delayed Start). Uninstall retains app-usage.db and protected binary versions.");
            return 0;
        }
        try
        {
            var invocation = InstallerInvocation.Parse(arguments);
            var system = new WindowsInstallationSystem();
            if (invocation.Operation == "status")
            { Console.WriteLine(JsonSerializer.Serialize(system.ReadStatus(), new JsonSerializerOptions { WriteIndented = true })); return 0; }
            if (invocation.Operation == "clear-history")
            {
                var clear = await new HistoryClearEngine(system).ClearAsync();
                Console.WriteLine(JsonSerializer.Serialize(clear, new JsonSerializerOptions { WriteIndented = true }));
                return clear.StorageExists ? 0 : 3;
            }
            var engine = new InstallationEngine(system);
            var result = invocation.Operation == "install"
                ? await engine.InstallAsync(invocation.SourceDirectory!, invocation.ClientSid ?? WindowsIdentity.GetCurrent().User!.Value)
                : await engine.UninstallAsync();
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (UnauthorizedAccessException exception) { Console.Error.WriteLine(exception.Message); return 2; }
        catch (ArgumentException exception) { Console.Error.WriteLine(exception.Message); return 2; }
        catch (AppUsageHistoryManagementUnavailableException exception) { Console.Error.WriteLine(exception.Message); return 4; }
        catch (Exception exception) { Console.Error.WriteLine($"Service operation failed: {exception.Message}"); return 1; }
    }
}
