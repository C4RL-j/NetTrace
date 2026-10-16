using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NetTrace.Services;

// Installed VPN interfaces can retain their link/IP when only a route disappears.
// Observe both route families without adding a poller or sending any traffic.
public sealed class WindowsVpnRouteNotifications : IVpnRouteChangeSource
{
    // If Windows refuses cancellation, retain the delegate until process exit;
    // a late native callback must never target a collected managed delegate.
    private static readonly ConcurrentBag<RouteChangeCallback> FailedCancellationCallbacks = new();
    private readonly object _gate = new();
    private readonly RouteChangeCallback _callback;
    private IntPtr _handle;
    private bool _started;
    private volatile bool _disposed;

    public event EventHandler? Changed;

    public WindowsVpnRouteNotifications() => _callback = OnRouteChanged;

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _started = true;
            try
            {
                var result = NotifyRouteChange2(0, _callback, IntPtr.Zero, false, out _handle); // AF_UNSPEC
                if (result != 0) Trace.TraceWarning($"Windows VPN route notifications are unavailable: {new Win32Exception((int)result).Message}");
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or
                ExternalException or PlatformNotSupportedException)
            {
                Trace.TraceWarning($"Windows VPN route notifications are unavailable: {exception.Message}");
            }
        }
    }

    private void OnRouteChanged(IntPtr context, IntPtr row, uint notificationType)
    {
        // The callback row is incomplete and owned by Windows. Refresh the shared
        // inventory later rather than reading, retaining or freeing the pointer.
        if (_disposed || Changed is not { } changed) return;
        foreach (EventHandler handler in changed.GetInvocationList())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception exception) { Trace.TraceWarning($"Could not queue VPN route refresh: {exception.Message}"); }
        }
    }

    public void Dispose()
    {
        IntPtr handle;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            handle = _handle;
            _handle = IntPtr.Zero;
            Changed = null;
        }
        if (handle == IntPtr.Zero) return;
        // Cancellation waits for callbacks. Hold neither this gate nor the VPN
        // monitor gate, since a callback may currently be queueing its refresh.
        var result = CancelMibChangeNotify2(handle);
        if (result != 0)
        {
            FailedCancellationCallbacks.Add(_callback);
            Trace.TraceWarning($"Windows VPN route notification cancellation failed: {new Win32Exception((int)result).Message}");
        }
        GC.KeepAlive(_callback);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void RouteChangeCallback(IntPtr context, IntPtr row, uint notificationType);

    // https://learn.microsoft.com/windows/win32/api/netioapi/nf-netioapi-notifyroutechange2
    [DllImport("iphlpapi.dll")]
    private static extern uint NotifyRouteChange2(ushort family, RouteChangeCallback callback, IntPtr context,
        [MarshalAs(UnmanagedType.U1)] bool initialNotification, out IntPtr handle);

    [DllImport("iphlpapi.dll")]
    private static extern uint CancelMibChangeNotify2(IntPtr handle);
}
