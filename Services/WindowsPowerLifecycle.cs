using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NetTrace.Services;

// One application-lifetime native window receives Windows power messages even without
// a dashboard. The window overload remains available to other window-owned callers.
public sealed class WindowsPowerLifecycle : IDisposable
{
    private readonly Window? _window;
    private readonly bool _ownsSource;
    private HwndSource? _source;
    private IntPtr _registration;
    private bool _suspended;

    public event EventHandler? Suspending;
    public event EventHandler? Resuming;
    public bool IsRegistered => _registration != IntPtr.Zero;
    public IntPtr Handle => _source?.Handle ?? IntPtr.Zero;

    public WindowsPowerLifecycle()
    {
        _source = new HwndSource(new HwndSourceParameters("NetTrace lifecycle")
        {
            WindowStyle = 0,
            Width = 0,
            Height = 0,
            ParentWindow = IntPtr.Zero
        });
        _ownsSource = true;
        RegisterSource();
    }

    public WindowsPowerLifecycle(Window window)
    {
        _window = window;
        _window.SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs args)
    {
        var handle = new WindowInteropHelper(_window!).Handle;
        _source = HwndSource.FromHwnd(handle);
        RegisterSource();
    }

    private void RegisterSource()
    {
        _source?.AddHook(WindowProc);
        // Explicitly opt in for connected/Modern Standby as well as classic broadcasts.
        _registration = RegisterSuspendResumeNotification(Handle, 0); // DEVICE_NOTIFY_WINDOW_HANDLE
        if (_registration == IntPtr.Zero)
            Trace.TraceError($"Could not register Windows suspend/resume notifications: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0218) return IntPtr.Zero; // WM_POWERBROADCAST
        switch (wParam.ToInt32())
        {
            case 4 when !_suspended: // PBT_APMSUSPEND
                _suspended = true;
                Suspending?.Invoke(this, EventArgs.Empty);
                break;
            case 18 or 7 when _suspended: // AUTOMATIC then optional user-input resume
                _suspended = false;
                Resuming?.Invoke(this, EventArgs.Empty);
                break;
            default:
                return IntPtr.Zero;
        }
        handled = true;
        return new IntPtr(1);
    }

    public void Dispose()
    {
        if (_window is not null) _window.SourceInitialized -= OnSourceInitialized;
        if (_registration != IntPtr.Zero)
        {
            if (!UnregisterSuspendResumeNotification(_registration))
                Trace.TraceWarning($"Could not unregister Windows power notifications: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            _registration = IntPtr.Zero;
        }
        _source?.RemoveHook(WindowProc);
        if (_ownsSource) _source?.Dispose();
        _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterSuspendResumeNotification(IntPtr recipient, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterSuspendResumeNotification(IntPtr registration);
}
