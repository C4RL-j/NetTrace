using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AppUsageIpc;

public static class NativePipeSecurity
{
    public const uint RejectRemoteClientsFlag = 0x00000008;
    public const uint FirstInstanceFlag = 0x00080000;
    public const int MaximumClients = 3;
    public static string DefaultPipeName => "NetTrace.AppUsage002." +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CurrentUserSid())))[..24];

    public static string CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new UnauthorizedAccessException("The current user SID is unavailable.");
    }

    public static string SecurityDescriptorSddl(string sid)
    {
        _ = new SecurityIdentifier(sid);
        // Only this user is allowed. Medium mandatory integrity permits the normal
        // tray/client token to write to a pipe created by the elevated same-user helper.
        return $"D:P(A;;GA;;;{sid})S:(ML;;NW;;;ME)";
    }

    public static void ValidatePipeName(string pipeName)
    {
        if (string.IsNullOrEmpty(pipeName) || pipeName.Length > 120
            || pipeName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            throw new ArgumentException("Use a short local pipe name containing only letters, digits, dot, dash or underscore.", nameof(pipeName));
    }

    public static NamedPipeServerStream CreateServer(string pipeName, bool firstInstance, int maxInstances = MaximumClients)
    {
        ValidatePipeName(pipeName);
        if (maxInstances is < 1 or > MaximumClients) throw new ArgumentOutOfRangeException(nameof(maxInstances));
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(SecurityDescriptorSddl(CurrentUserSid()), 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            const uint duplex = 3, overlapped = 0x40000000;
            var handle = CreateNamedPipe(@"\\.\pipe\" + pipeName,
                duplex | overlapped | (firstInstance ? FirstInstanceFlag : 0),
                RejectRemoteClientsFlag, (uint)maxInstances, 65536, 4096, 0, ref attributes);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error(); handle.Dispose();
                throw new Win32Exception(error, "The guarded local pipe could not be created; an existing owner or permissions may block it.");
            }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(descriptor); }
    }

    public static NamedPipeClientStream CreateClient(string pipeName)
    {
        ValidatePipeName(pipeName);
        // SQOS Identification prevents a pipe server from impersonating this client
        // for privileged operations. We authenticate by process token, never impersonation.
        return new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
    }

    public static bool VerifyClient(SafePipeHandle pipe) => VerifyPeer(pipe, server: false);
    public static bool VerifyServer(SafePipeHandle pipe) => VerifyPeer(pipe, server: true);

    private static bool VerifyPeer(SafePipeHandle pipe, bool server)
    {
        try
        {
            bool success = server ? GetNamedPipeServerProcessId(pipe, out uint pid) : GetNamedPipeClientProcessId(pipe, out pid);
            if (!success || pid == 0) return false;
            using var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
            if (process.IsInvalid || !OpenProcessToken(process, 0x0008, out var token)) return false; // TOKEN_QUERY
            using (token)
            {
                string sid = ReadSid(token);
                return sid == CurrentUserSid() && (!server || ReadElevated(token));
            }
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException or UnauthorizedAccessException
            or System.Security.SecurityException) { return false; }
    }

    private static string ReadSid(SafeAccessTokenHandle token)
    {
        GetTokenInformation(token, 1, IntPtr.Zero, 0, out int size); // TokenUser
        if (size <= 0 || size > 65536) throw new UnauthorizedAccessException("Peer token user is unavailable.");
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, 1, buffer, size, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool ReadElevated(SafeAccessTokenHandle token)
    {
        IntPtr buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            return GetTokenInformation(token, 20, buffer, sizeof(int), out _) && Marshal.ReadInt32(buffer) != 0;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW")]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string descriptor, uint revision, out IntPtr result, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateNamedPipeW")]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances, uint outputBuffer,
        uint inputBuffer, uint timeout, ref SecurityAttributes security);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr value);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        IntPtr information, int length, out int returnedLength);
}
