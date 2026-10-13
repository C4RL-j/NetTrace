using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("NetTrace.AppUsageServiceTests")]

namespace AppUsageIpc;

// Deliberately separate from the read-only v2 query protocol. Only a fixed
// clear-history request is supported; no paths, SQL, service actions or options.
public static class AppUsageHistoryManagement
{
    public const string PipeName = "NetTrace.AppUsage0054.Management.938F90CEB2A34EF7A67BA5A43521EE77";
    public const string SecurityDescriptorSddl = "O:SYD:P(A;;GA;;;SY)(A;;GA;;;BA)S:(ML;;NW;;;HI)";
    internal static readonly byte[] Request = [0x4e, 0x54, 0x48, 0x01, 0x01];
    internal const int ResponseLength = 9;

    public static bool IsClearRequest(ReadOnlySpan<byte> request) => request.SequenceEqual(Request);
    public static bool IsAdministratorIdentity(WindowsIdentity identity, bool elevated) => elevated
        && new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
}

public sealed class AppUsageHistoryManagementUnavailableException(string message, Exception? inner = null)
    : IOException(message, inner);

public sealed class AppUsageHistoryManagementClient
{
    public async Task<int> ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await using var pipe = NativePipeSecurity.CreateClient(AppUsageHistoryManagement.PipeName);
        try { await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AppUsageHistoryManagementUnavailableException("Update App Tracking before clearing history; this running collector does not expose the management endpoint.", exception);
        }
        if (!NativePipeSecurity.VerifyServiceServer(pipe.SafePipeHandle, ServicePipeSecurity.ProductionServiceName))
            throw new UnauthorizedAccessException("The history management server is not the registered SYSTEM collector.");
        byte[] frame = new byte[4 + AppUsageHistoryManagement.Request.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(frame, AppUsageHistoryManagement.Request.Length);
        AppUsageHistoryManagement.Request.CopyTo(frame, 4);
        await pipe.WriteAsync(frame, timeout.Token).ConfigureAwait(false);
        // Once admitted, the transaction finishes even if the calling window
        // closes. Do not abandon an in-flight clear and report a false failure.
        byte[] response = new byte[AppUsageHistoryManagement.ResponseLength];
        using var acknowledgementTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await pipe.ReadExactlyAsync(response, acknowledgementTimeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException exception)
        { throw new IOException("The clear result could not be confirmed. The collector may still be completing it; retry after checking app usage.", exception); }
        if (!response.AsSpan(0, 4).SequenceEqual(AppUsageHistoryManagement.Request.AsSpan(0, 4)))
            throw new IOException("The history management acknowledgement is invalid.");
        // Complete the small acknowledgement exchange before a caller can
        // immediately retry against this retained first-instance listener.
        using (var closingTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            try
            {
                await pipe.WriteAsync(new byte[1], closingTimeout.Token).ConfigureAwait(false);
                await pipe.ReadAsync(new byte[1], closingTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException) { }
        }
        if (response[4] != 0) throw new InvalidOperationException("The collector could not clear application usage history.");
        int rows = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(5));
        if (rows < 0) throw new IOException("The history clear result is invalid.");
        return rows;
    }
}

public sealed class AppUsageHistoryManagementServer : IAsyncDisposable
{
    private readonly NamedPipeServerStream pipe;
    private readonly Func<CancellationToken, Task<int>> clear;
    private readonly Func<NamedPipeServerStream, bool> verify;
    private readonly CancellationTokenSource stopping = new();
    private Task? running;
    private int disposed;

    public AppUsageHistoryManagementServer(Func<CancellationToken, Task<int>> clear)
        : this(clear, CreateServer(), VerifyAdministrator) { }

    // Test-only injection cannot be selected through the service or installer CLI.
    internal AppUsageHistoryManagementServer(Func<CancellationToken, Task<int>> clear,
        NamedPipeServerStream pipe, Func<NamedPipeServerStream, bool> verify)
    { this.clear = clear; this.pipe = pipe; this.verify = verify; }

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (running is not null) throw new InvalidOperationException("History management is already listening.");
        return running = RunCoreAsync(cancellationToken);
    }

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        while (!linked.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(linked.Token).ConfigureAwait(false);
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                byte[] header = new byte[4];
                await pipe.ReadExactlyAsync(header, readTimeout.Token).ConfigureAwait(false);
                if (System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header)
                    != AppUsageHistoryManagement.Request.Length) continue;
                byte[] request = new byte[AppUsageHistoryManagement.Request.Length];
                await pipe.ReadExactlyAsync(request, readTimeout.Token).ConfigureAwait(false);
                // Identity inspection happens only after the bounded read and
                // always reverts before invoking any privileged operation.
                if (!AppUsageHistoryManagement.IsClearRequest(request) || !verify(pipe)) continue;
                byte[] response = new byte[AppUsageHistoryManagement.ResponseLength];
                AppUsageHistoryManagement.Request.AsSpan(0, 4).CopyTo(response);
                try
                {
                    int rows = await clear(CancellationToken.None).ConfigureAwait(false);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(5), rows);
                }
                catch { response[4] = 1; }
                using var responseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await pipe.WriteAsync(response, responseTimeout.Token).ConfigureAwait(false);
                // Client confirms response consumption, then waits for EOF.
                // This preserves the acknowledgement and prevents a rapid retry
                // attaching before the retained listener has disconnected.
                try { await pipe.ReadAsync(new byte[1], responseTimeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
            finally
            {
                try { if (pipe.IsConnected) pipe.Disconnect(); }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException) { }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stopping.Cancel();
        // The service retains its storage lease until this admitted transaction
        // has completed; a failed bounded join is a lifecycle failure.
        if (running is not null) await running.WaitAsync(TimeSpan.FromSeconds(12)).ConfigureAwait(false);
        pipe.Dispose();
        stopping.Dispose();
    }

    private static NamedPipeServerStream CreateServer()
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(AppUsageHistoryManagement.SecurityDescriptorSddl,
            1, out var descriptor, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateNamedPipe(@"\\.\pipe\" + AppUsageHistoryManagement.PipeName,
                3 | 0x40000000 | NativePipeSecurity.FirstInstanceFlag, NativePipeSecurity.RejectRemoteClientsFlag,
                1, 64, 64, 0, ref attributes);
            if (handle.IsInvalid)
            { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(descriptor); }
    }

    private static bool VerifyAdministrator(NamedPipeServerStream pipe)
    {
        if (!ImpersonateNamedPipeClient(pipe.SafePipeHandle)) return false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            if (!OpenThreadToken(GetCurrentThread(), 0x0008, true, out var token)) return false;
            using (token)
            {
                IntPtr buffer = Marshal.AllocHGlobal(sizeof(int));
                try
                {
                    bool elevated = GetTokenInformation(token, 20, buffer, sizeof(int), out _)
                        && Marshal.ReadInt32(buffer) != 0;
                    return AppUsageHistoryManagement.IsAdministratorIdentity(identity, elevated);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or Win32Exception) { return false; }
        finally
        {
            if (!RevertToSelf()) Environment.FailFast("History management identity inspection could not restore the SYSTEM token.");
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW")]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateNamedPipeW")]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outputBuffer, uint inputBuffer, uint timeout, ref SecurityAttributes security);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool ImpersonateNamedPipeClient(SafePipeHandle pipe);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool RevertToSelf();
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenThreadToken(IntPtr thread, uint access,
        bool openAsSelf, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token,
        int informationClass, IntPtr information, int length, out int returnedLength);
}
