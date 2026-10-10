using System.IO;
using Microsoft.Win32;

namespace NetTrace.Services;

// The current user's Run entry needs no elevation and launches the ordinary tray host.
public sealed class WindowsStartupRegistration : IWindowsStartupRegistration
{
    public const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "NetTrace";

    private readonly string _registrySubKey;

    public string ExecutablePath { get; }
    public string Command => $"\"{ExecutablePath}\" --background";

    public WindowsStartupRegistration(string? executablePath = null, string registrySubKey = RunRegistryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registrySubKey);
        _registrySubKey = registrySubKey;
        ExecutablePath = ValidateExecutablePath(executablePath ?? ResolveExecutablePath());
    }

    public bool IsEnabled()
    {
        // Reading the setting must not create a key or alter another startup entry.
        using var key = Registry.CurrentUser.OpenSubKey(_registrySubKey, writable: false);
        if (key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string command)
            return false;
        if (key.GetValueKind(ValueName) != RegistryValueKind.String)
            return false;

        var quotedPath = $"\"{ExecutablePath}\"";
        return command.Length == Command.Length
            && command.StartsWith(quotedPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(command[quotedPath.Length..], " --background", StringComparison.Ordinal);
    }

    public void Enable()
    {
        // Validate again in case the executable was moved after the setting was displayed.
        ValidateExecutablePath(ExecutablePath);
        using var key = Registry.CurrentUser.CreateSubKey(_registrySubKey, writable: true)
            ?? throw new InvalidOperationException("Windows could not open the current-user startup settings.");
        key.SetValue(ValueName, Command, RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_registrySubKey, writable: true);
        // Leave the Run key and every other application's registration untouched.
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string ResolveExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (processPath is not null
            && string.Equals(Path.GetFileName(processPath), "NetTrace.exe", StringComparison.OrdinalIgnoreCase))
            return processPath;

        // Under dotnet/test hosts, use the apphost rather than registering dotnet.exe.
        var appHostPath = Path.Combine(AppContext.BaseDirectory, "NetTrace.exe");
        if (File.Exists(appHostPath))
            return appHostPath;

        throw new InvalidOperationException(
            "NetTrace.exe could not be located. Start with Windows requires the NetTrace executable.");
    }

    private static string ValidateExecutablePath(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (executablePath.Contains('"') || executablePath.Any(char.IsControl)
            || !Path.IsPathFullyQualified(executablePath)
            || !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Startup requires an absolute executable path without quotes or control characters.", nameof(executablePath));

        var fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The NetTrace startup executable does not exist.", fullPath);
        return fullPath;
    }
}
