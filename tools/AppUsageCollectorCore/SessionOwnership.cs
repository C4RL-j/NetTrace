using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using AppUsageEtwProbe;
using Microsoft.Win32.SafeHandles;

namespace AppUsageCollectorCore;

public enum StaleSessionDecision { Missing, RecoverOwned, RejectForeign }

public static class SessionOwnership
{
    public const string SessionName = "NetTrace-AppUsage003-Collector-938F90CEB2A34EF7A67BA5A43521EE77";
    public static readonly Guid SessionGuid = new("938f90ce-b2a3-4ef7-a67b-a5a43521ee77");
    public const string MutexName = "Global\\NetTrace-AppUsage003-938F90CEB2A34EF7A67BA5A43521EE77";

    public static StaleSessionDecision Evaluate(NativeEtwSessionDescriptor? session) => session is null
        ? StaleSessionDecision.Missing
        : session.Name == SessionName && session.SessionGuid == SessionGuid
          && NativeEtwController.IsExpectedRealtimeMode(session.LogFileMode)
          && !session.HasLogFile && session.SessionHandle != 0
            ? StaleSessionDecision.RecoverOwned : StaleSessionDecision.RejectForeign;

    // Called only while holding the guarded global mutex. No enumeration,
    // wildcard/prefix matching, provider reconfiguration or unrelated trace stops.
    public static bool RecoverExactSession()
    {
        var descriptor = NativeEtwController.Query(SessionName);
        switch (Evaluate(descriptor))
        {
            case StaleSessionDecision.Missing: return false;
            case StaleSessionDecision.RecoverOwned:
                NativeEtwController.StopVerified(descriptor!);
                if (NativeEtwController.Query(SessionName) is not null)
                    throw new InvalidOperationException("The verified owned ETW session remained after recovery.");
                return true;
            default: throw new InvalidOperationException("Exact ETW name belongs to a different session identity; refusing cleanup.");
        }
    }

    public static bool CleanupOwnedSession()
    {
        using var ownership = ServiceOwnershipLease.Acquire();
        RecoverExactSession();
        return NativeEtwController.Query(SessionName) is null;
    }
}

// A mutex owner is a Windows thread, so one quiet dedicated thread retains it
// across async capture continuations. Windows releases it after process death.
// Protected ACL permits only the two explicitly tested service identities.
public sealed class ServiceOwnershipLease : IDisposable
{
    public const string SecurityDescriptorSddl = "D:P(A;;GA;;;SY)(A;;GA;;;LS)";
    private readonly ManualResetEventSlim release = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private int disposed;
    private ServiceOwnershipLease()
    {
        thread = new Thread(Own) { IsBackground = true, Name = "NetTrace ETW ownership" };
        thread.Start();
    }
    public static ServiceOwnershipLease Acquire()
    {
        var lease = new ServiceOwnershipLease();
        try { lease.ready.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); return lease; }
        catch { lease.Dispose(); throw; }
    }
    private void Own()
    {
        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(SecurityDescriptorSddl, 1, out var descriptor, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            SafeWaitHandle mutex;
            try
            {
                var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
                mutex = CreateMutex(ref attributes, false, SessionOwnership.MutexName);
            }
            finally { LocalFree(descriptor); }
            using (mutex)
            {
                if (mutex.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                VerifyAcl(mutex);
                uint acquired = WaitForSingleObject(mutex, 0);
                if (acquired is not (0 or 128))
                    throw new InvalidOperationException("Another collector owns the protected machine-wide ETW mutex.");
                try { ready.TrySetResult(); release.Wait(); }
                finally
                {
                    if (!ReleaseMutex(mutex)) throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
        }
        catch (Exception exception) { ready.TrySetException(exception); }
    }

    private static void VerifyAcl(SafeWaitHandle handle)
    {
        GetKernelObjectSecurity(handle, 4, null, 0, out uint size);
        if (size == 0 || size > 65536) throw new UnauthorizedAccessException("Mutex security could not be inspected.");
        var bytes = new byte[size];
        if (!GetKernelObjectSecurity(handle, 4, bytes, size, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var descriptor = new RawSecurityDescriptor(bytes, 0);
        if ((descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0 || descriptor.DiscretionaryAcl?.Count != 2)
            throw new UnauthorizedAccessException("Existing mutex has an unexpected security descriptor.");
        var trustees = new HashSet<string>(StringComparer.Ordinal);
        foreach (GenericAce raw in descriptor.DiscretionaryAcl)
        {
            if (raw is not CommonAce ace || ace.AceQualifier != AceQualifier.AccessAllowed || ace.IsCallback
                || ace.AceFlags != AceFlags.None || ace.AccessMask is not (0x10000000 or 0x1f0001))
                throw new UnauthorizedAccessException("Existing mutex has an unexpected access rule.");
            trustees.Add(ace.SecurityIdentifier.Value);
        }
        if (!trustees.SetEquals(["S-1-5-18", "S-1-5-19"]))
            throw new UnauthorizedAccessException("Existing mutex trustees do not match the service identities.");
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        release.Set();
        if (thread.Join(TimeSpan.FromSeconds(1))) release.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW")]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateMutexW")]
    private static extern SafeWaitHandle CreateMutex(ref SecurityAttributes attributes, bool initialOwner, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReleaseMutex(SafeWaitHandle handle);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetKernelObjectSecurity(SafeWaitHandle handle, uint information, byte[]? descriptor, uint size, out uint required);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr pointer);
}
