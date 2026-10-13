using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AppUsageIpc;

public sealed record ServicePipeSecurity(string AllowedClientSid,
    string ServiceName = ServicePipeSecurity.DefaultServiceName)
{
    public const string DefaultServiceName = "NetTraceAppUsageCollectorDev";
    public const string ProductionServiceName = "NetTraceAppUsageCollector";
    public static string DefaultPipeName => "NetTrace.AppUsage003.Service.938F90CEB2A34EF7A67BA5A43521EE77";
    public static void ValidateServiceName(string name)
    {
        if (name is not (DefaultServiceName or ProductionServiceName))
            throw new ArgumentException("Only the known NetTrace collector services are supported.");
    }
    public static string ServiceSid(string name)
    {
        ValidateServiceName(name);
        var hash = SHA1.HashData(Encoding.Unicode.GetBytes(name.ToUpperInvariant()));
        return "S-1-5-80-" + string.Join('-', Enumerable.Range(0, 5).Select(index =>
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(index * 4, 4))));
    }
}

// This verifier trusts SCM's protected configuration and running own-process PID,
// plus the kernel pipe owner. It does not need TOKEN_QUERY on a SYSTEM process.
internal static class ServicePeerVerification
{
    public static bool Verify(SafePipeHandle pipe, string serviceName)
    {
        try
        {
            ServicePipeSecurity.ValidateServiceName(serviceName);
            if (!GetNamedPipeServerProcessId(pipe, out uint pipePid) || pipePid == 0) return false;
            using var manager = OpenSCManager(null, null, 1); // SC_MANAGER_CONNECT
            if (manager.IsInvalid) return false;
            using var service = OpenService(manager, serviceName, 1 | 4); // QUERY_CONFIG | QUERY_STATUS
            if (service.IsInvalid || !ReadStatus(service, out var before)
                || before.CurrentState != 4 || before.ServiceType != 0x10 || before.ProcessId != pipePid) return false;
            QueryServiceConfig(service, IntPtr.Zero, 0, out uint required);
            if (required is 0 or > 65536) return false;
            var configBuffer = Marshal.AllocHGlobal((int)required);
            string? serviceAccount;
            try
            {
                if (!QueryServiceConfig(service, configBuffer, required, out _)) return false;
                serviceAccount = Marshal.PtrToStringUni(Marshal.PtrToStructure<QueryConfig>(configBuffer).ServiceStartName);
            }
            finally { Marshal.FreeHGlobal(configBuffer); }
            string? expectedOwner = serviceAccount?.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) == true
                ? "S-1-5-18" : serviceAccount?.Equals(@"NT AUTHORITY\LocalService", StringComparison.OrdinalIgnoreCase) == true
                ? "S-1-5-19" : null;
            if (expectedOwner is null) return false;
            if (GetSecurityInfo(pipe, 6, 1, out var owner, out _, out _, out _, out var descriptor) != 0) return false;
            try { if (new SecurityIdentifier(owner).Value != expectedOwner) return false; }
            finally { LocalFree(descriptor); }
            return ReadStatus(service, out var after) && after.ProcessId == pipePid
                && after.CurrentState == 4 && after.ServiceType == 0x10;
        }
        catch (Exception exception) when (exception is Win32Exception or ArgumentException
            or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }
    private static bool ReadStatus(ServiceHandle service, out ServiceStatus status)
    {
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<ServiceStatus>());
        try
        {
            if (!QueryServiceStatusEx(service, 0, buffer, (uint)Marshal.SizeOf<ServiceStatus>(), out _))
            { status = default; return false; }
            status = Marshal.PtrToStructure<ServiceStatus>(buffer); return true;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode,
            CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct QueryConfig
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
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool CloseServiceHandle(IntPtr service);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatusEx(ServiceHandle service,
        uint information, IntPtr buffer, uint size, out uint required);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryServiceConfig(ServiceHandle service, IntPtr config, uint size, out uint required);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(SafePipeHandle handle, int objectType,
        uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr value);
}
