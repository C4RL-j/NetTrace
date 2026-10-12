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

    // Explicit client rights exclude FILE_CREATE_PIPE_INSTANCE (0x4), DELETE,
    // WRITE_DAC and WRITE_OWNER. GenericWrite would accidentally grant 0x4.
    public const uint ServiceClientAccessMask = 0x0012019b;
    public static string ServiceSecurityDescriptorSddl(string clientSid, string serviceIdentitySid, string serviceSid)
    {
        _ = new SecurityIdentifier(clientSid); _ = new SecurityIdentifier(serviceIdentitySid);
        _ = new SecurityIdentifier(serviceSid);
        if (serviceIdentitySid is not ("S-1-5-18" or "S-1-5-19")
            || !serviceSid.StartsWith("S-1-5-80-", StringComparison.Ordinal)
            || !clientSid.StartsWith("S-1-5-21-", StringComparison.Ordinal))
            throw new ArgumentException("Only a built-in collector identity and an intended user are supported.");
        return $"O:{serviceIdentitySid}D:P(A;;GA;;;SY)(A;;GA;;;{serviceSid})(A;;0x{ServiceClientAccessMask:x};;;{clientSid})S:(ML;;NW;;;ME)";
    }

    public static void ValidatePipeName(string pipeName)
    {
        if (string.IsNullOrEmpty(pipeName) || pipeName.Length > 120
            || pipeName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            throw new ArgumentException("Use a short local pipe name containing only letters, digits, dot, dash or underscore.", nameof(pipeName));
    }

    public static NamedPipeServerStream CreateServer(string pipeName, bool firstInstance, int maxInstances = MaximumClients,
        ServicePipeSecurity? serviceSecurity = null)
    {
        ValidatePipeName(pipeName);
        if (maxInstances is < 1 or > MaximumClients) throw new ArgumentOutOfRangeException(nameof(maxInstances));
        string descriptorText;
        if (serviceSecurity is null) descriptorText = SecurityDescriptorSddl(CurrentUserSid());
        else
        {
            ServicePipeSecurity.ValidateServiceName(serviceSecurity.ServiceName);
            descriptorText = ServiceSecurityDescriptorSddl(serviceSecurity.AllowedClientSid, CurrentUserSid(),
                ServicePipeSecurity.ServiceSid(serviceSecurity.ServiceName));
        }
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(descriptorText, 1, out var descriptor, out _))
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
    public static bool VerifyClient(SafePipeHandle pipe, string allowedSid) => VerifyPeer(pipe, server: false, allowedSid);
    public static bool VerifyServer(SafePipeHandle pipe) => VerifyPeer(pipe, server: true);
    public static bool VerifyServiceServer(SafePipeHandle pipe, string serviceName) => ServicePeerVerification.Verify(pipe, serviceName);

    public static bool VerifyServiceClient(SafePipeHandle pipe, string allowedSid)
    {
        // The first bounded request has been read. SQOS Identification lets us
        // inspect its authenticated token; no privileged action runs impersonated.
        if (!ImpersonateNamedPipeClient(pipe)) return false;
        try
        {
            if (!OpenThreadToken(GetCurrentThread(), 0x0008, true, out var token)) return false;
            using (token) return ReadSid(token) == allowedSid;
        }
        finally
        {
            // Remaining code must never execute with the client's identity.
            if (!RevertToSelf()) Environment.FailFast("Pipe identity inspection could not restore the service token.");
        }
    }

    public static async Task<NamedPipeClientStream> ConnectServiceClientAsync(string pipeName, CancellationToken cancellationToken)
    {
        ValidatePipeName(pipeName);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            const uint overlapped = 0x40000000, sqosPresent = 0x00100000, identification = 0x00010000;
            var handle = CreateFile(@"\\.\pipe\" + pipeName, ServiceClientAccessMask, 0, IntPtr.Zero, 3,
                overlapped | sqosPresent | identification, IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                try { return new NamedPipeClientStream(PipeDirection.InOut, true, true, handle); }
                catch { handle.Dispose(); throw; }
            }
            int error = Marshal.GetLastWin32Error(); handle.Dispose();
            if (error == 5) throw new UnauthorizedAccessException("The service pipe denied access.");
            if (error is not (2 or 231)) throw new IOException("The service pipe could not be opened.", new Win32Exception(error));
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool VerifyPeer(SafePipeHandle pipe, bool server, string? allowedSid = null)
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
                return sid == (allowedSid ?? CurrentUserSid()) && (!server || ReadElevated(token));
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
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafePipeHandle CreateFile(string path, uint access, uint sharing, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool ImpersonateNamedPipeClient(SafePipeHandle pipe);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool RevertToSelf();
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenThreadToken(IntPtr thread, uint access, bool openAsSelf, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        IntPtr information, int length, out int returnedLength);
}
