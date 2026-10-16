using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace NetTrace.Services;

// NotifyIcon mouse coordinates and the monitor work area are in native screen
// coordinates. Place the measured HWND in those same units, including on scaled
// or negative-coordinate monitors, rather than treating pixels as WPF DIPs.
internal static class TrayPopupPlacement
{
    public static System.Drawing.Rectangle? GetTaskbarBounds(System.Drawing.Point anchor)
    {
        var screen = Forms.Screen.FromPoint(anchor).Bounds;
        System.Drawing.Rectangle? taskbar = null;
        // This also finds the monitor's taskbar when the clicked icon is inside
        // Windows' overflow flyout, which has its own top-level window.
        EnumWindows((handle, _) =>
        {
            var name = new StringBuilder(64);
            GetClassName(handle, name, name.Capacity);
            if (name.ToString() is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                || !GetWindowRect(handle, out var rectangle)) return true;
            var area = System.Drawing.Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
            if (!area.IntersectsWith(screen)) return true;
            taskbar = System.Drawing.Rectangle.Intersect(area, screen);
            return false;
        }, IntPtr.Zero);
        return taskbar;
    }

    public static void Show(Window popup, System.Drawing.Point anchor, System.Drawing.Rectangle? taskbar)
    {
        var screen = Forms.Screen.FromPoint(anchor);
        var monitorWorkArea = screen.WorkingArea;
        var work = monitorWorkArea;
        var bounds = screen.Bounds;
        if (taskbar is { Width: > 0, Height: > 0 } area)
        {
            // Auto-hidden taskbars are absent from WorkingArea even while exposed.
            if (area.Width >= area.Height)
                work = area.Top >= bounds.Top + bounds.Height / 2
                    ? System.Drawing.Rectangle.FromLTRB(work.Left, work.Top, work.Right, Math.Min(work.Bottom, area.Top))
                    : System.Drawing.Rectangle.FromLTRB(work.Left, Math.Max(work.Top, area.Bottom), work.Right, work.Bottom);
            else
                work = area.Left >= bounds.Left + bounds.Width / 2
                    ? System.Drawing.Rectangle.FromLTRB(work.Left, work.Top, Math.Min(work.Right, area.Left), work.Bottom)
                    : System.Drawing.Rectangle.FromLTRB(Math.Max(work.Left, area.Right), work.Top, work.Right, work.Bottom);
        }
        // A close initial position also places the window on the intended monitor
        // before WPF measures it at that monitor's effective DPI.
        var source = HwndSource.FromHwnd(new WindowInteropHelper(popup).EnsureHandle());
        var scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        popup.Left = work.Left / scale;
        popup.Top = work.Top / scale;
        popup.Show();
        popup.UpdateLayout();
        var handle = new WindowInteropHelper(popup).Handle;
        if (GetWindowRect(handle, out var rectangle))
        {
            var width = rectangle.Right - rectangle.Left;
            var height = rectangle.Bottom - rectangle.Top;
            var gap = (int)Math.Round(8 * (source?.CompositionTarget?.TransformToDevice.M11 ?? scale));
            var left = monitorWorkArea.Right - width;
            var top = work.Bottom - height - gap;
            if (work.Top > bounds.Top && anchor.Y < work.Top)
                top = work.Top + gap;
            else if (work.Right < bounds.Right && anchor.X >= work.Right)
            {
                top = anchor.Y - height / 2;
            }
            else if (work.Left > bounds.Left && anchor.X < work.Left)
            {
                top = anchor.Y - height / 2;
            }
            left = Math.Clamp(left, monitorWorkArea.Left, Math.Max(monitorWorkArea.Left, monitorWorkArea.Right - width));
            top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - height));
            SetWindowPos(handle, IntPtr.Zero, left, top, 0, 0, 0x0015); // NOSIZE | NOZORDER | NOACTIVATE
        }
        SetForegroundWindow(handle);
        popup.Activate();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int maximum);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
