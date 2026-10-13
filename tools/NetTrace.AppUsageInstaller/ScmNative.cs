using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NetTrace.AppUsageInstaller;

// Reads SCM's actual configuration rather than parsing localized sc.exe output.
internal static class ScmNative
{
    internal static uint ReadCurrentState(string name)
    {
        using var manager = OpenSCManager(null, null, 1);
        Check(manager);
        using var service = OpenService(manager, name, 4);
        Check(service);
        return ReadStatus(service).CurrentState;
    }

    internal static ServiceRegistration? Read(string name)
    {
        using var manager = OpenSCManager(null, null, 1);
        Check(manager);
        using var service = OpenService(manager, name, 1 | 4);
        if (service.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 1060) return null;
            throw new Win32Exception(error);
        }
        var config = ReadConfig(service);
        var status = ReadStatus(service);
        string description = ReadConfig2(service, 1, pointer => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer)) ?? "");
        return new(name, config.BinaryCommandLine, config.Identity, description,
            status.CurrentState == 4, status.ProcessId);
    }

    internal static object ReadDetailed(string name)
    {
        var registration = Read(name);
        if (registration is null) return new { ServiceName = name, Installed = false, ScmError = 1060 };
        using var manager = OpenSCManager(null, null, 1);
        Check(manager);
        using var service = OpenService(manager, name, 1 | 4);
        Check(service);
        var config = ReadConfig(service);
        bool delayed = ReadConfig2(service, 3, pointer => Marshal.ReadInt32(pointer) != 0);
        var recovery = ReadRecovery(service);
        return new { ServiceName = name, Installed = true, registration.Running, registration.Identity,
            registration.BinaryCommandLine, registration.Description, registration.ProcessId,
            Automatic = config.StartType == 2, DelayedAutomaticStart = delayed,
            Recovery = recovery };
    }

    internal static void VerifyConfiguration(InstallationConfiguration expected)
    {
        using var manager = OpenSCManager(null, null, 1);
        Check(manager);
        using var service = OpenService(manager, expected.ServiceName, 1 | 4);
        Check(service);
        var config = ReadConfig(service);
        if (config.ServiceType != 0x10 || config.StartType != 2 || config.Identity != expected.Identity
            || config.BinaryCommandLine != expected.BinaryCommandLine
            || !ReadConfig2(service, 3, pointer => Marshal.ReadInt32(pointer) != 0))
            throw new InvalidOperationException("SCM did not retain the required own-process, LocalSystem, delayed automatic configuration.");
        var recovery = ReadRecovery(service);
        if (recovery.ResetPeriodSeconds != expected.Recovery.ResetPeriodSeconds
            || recovery.RecoverNonCrashFailures != expected.Recovery.RecoverNonCrashFailures
            || !recovery.Actions.SequenceEqual(expected.Recovery.Actions))
            throw new InvalidOperationException("SCM did not retain the required restart-on-failure policy.");
    }

    private static RecoveryPolicy ReadRecovery(ServiceHandle service)
    {
        var policy = ReadConfig2(service, 2, pointer =>
        {
            var native = Marshal.PtrToStructure<FailureActions>(pointer);
            if (native.Count > 16) throw new InvalidOperationException("Unexpected SCM recovery action count.");
            var actions = new List<RecoveryAction>();
            for (int index = 0; index < native.Count; index++)
            {
                var action = Marshal.PtrToStructure<ScAction>(native.Actions + index * Marshal.SizeOf<ScAction>());
                actions.Add(new(action.Type switch { 0 => "None", 1 => "Restart", 2 => "Reboot", 3 => "RunCommand", _ => "Unknown" }, checked((int)action.Delay)));
            }
            if (Marshal.PtrToStringUni(native.Command) is { Length: > 0 } || Marshal.PtrToStringUni(native.RebootMessage) is { Length: > 0 })
                throw new InvalidOperationException("SCM recovery has an unexpected command or reboot action.");
            return new RecoveryPolicy(checked((int)native.ResetPeriod), false, actions.AsReadOnly());
        });
        bool flag = ReadConfig2(service, 4, pointer => Marshal.ReadInt32(pointer) != 0);
        return policy with { RecoverNonCrashFailures = flag };
    }

    private sealed record Configuration(uint ServiceType, uint StartType, string BinaryCommandLine, string Identity);
    private static Configuration ReadConfig(ServiceHandle service)
    {
        QueryServiceConfig(service, IntPtr.Zero, 0, out uint required);
        if (required == 0 || required > 65536) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            if (!QueryServiceConfig(service, buffer, required, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var config = Marshal.PtrToStructure<QueryConfig>(buffer);
            return new(config.ServiceType, config.StartType, Marshal.PtrToStringUni(config.BinaryPathName) ?? "",
                Marshal.PtrToStringUni(config.ServiceStartName) ?? "");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static T ReadConfig2<T>(ServiceHandle service, uint level, Func<IntPtr, T> read)
    {
        QueryServiceConfig2(service, level, IntPtr.Zero, 0, out uint required);
        if (required == 0 || required > 65536) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            if (!QueryServiceConfig2(service, level, buffer, required, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return read(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static ServiceStatus ReadStatus(ServiceHandle service)
    {
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<ServiceStatus>());
        try
        {
            if (!QueryServiceStatusEx(service, 0, buffer, (uint)Marshal.SizeOf<ServiceStatus>(), out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return Marshal.PtrToStructure<ServiceStatus>(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static bool PipeExists(string name)
    {
        if (WaitNamedPipe(@"\\.\pipe\" + name, 0)) return true;
        int error = Marshal.GetLastWin32Error();
        if (error is 2 or 3) return false;
        if (error is 5 or 121 or 231) return true;
        throw new Win32Exception(error);
    }

    private static void Check(ServiceHandle handle)
    { if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus
    { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct QueryConfig
    { public uint ServiceType, StartType, ErrorControl; public IntPtr BinaryPathName, LoadOrderGroup; public uint TagId; public IntPtr Dependencies, ServiceStartName, DisplayName; }
    [StructLayout(LayoutKind.Sequential)] private struct FailureActions
    { public uint ResetPeriod; public IntPtr RebootMessage, Command; public uint Count; public IntPtr Actions; }
    [StructLayout(LayoutKind.Sequential)] private struct ScAction { public uint Type, Delay; }
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryServiceConfig(ServiceHandle service, IntPtr buffer, uint size, out uint required);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryServiceConfig2(ServiceHandle service, uint level, IntPtr buffer, uint size, out uint required);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatusEx(ServiceHandle service, uint level, IntPtr buffer, uint size, out uint required);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool WaitNamedPipe(string name, uint timeout);
}
