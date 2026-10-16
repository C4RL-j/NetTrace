using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NetTrace;
using NetTrace.Controls;
using NetTrace.Models;
using NetTrace.Presentation;
using NetTrace.Services;
using NetTrace.Utils;

internal static class TrayPopupChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    public static void Run(string root, Action<bool, string> check)
    {
        var previews = System.IO.Path.Combine(root, "tray-popup-previews");
        CheckLiveData(root, previews, check);
        CheckTrayInteraction(root, previews, check);
        Console.WriteLine("Owned tray popup previews: " + previews);
    }

    private static void CheckLiveData(string root, string previews, Action<bool, string> check)
    {
        using var fixture = new Fixture(System.IO.Path.Combine(root, "tray-popup-data"));
        var monitoring = fixture.Monitoring;
        var today = DateOnly.FromDateTime(fixture.Clock.GetLocalNow().DateTime);
        fixture.Store.AddUsage(today, 1_000_000_000, 250_000_000);
        fixture.Store.AddUsage(today.AddDays(-1), 9_000_000_000, 4_000_000_000);
        fixture.Store.AddUsage(today.AddDays(1), 8_000_000_000, 3_000_000_000);
        fixture.Start();
        var popup = new TrayPopupWindow(monitoring);
        try
        {
            popup.Show();
            Layout(popup);
            check(popup.Width == 340 && Math.Abs(popup.ActualWidth - 340) < 0.5 &&
                popup.WindowStyle == WindowStyle.None && !popup.ShowInTaskbar && popup.ResizeMode == ResizeMode.NoResize,
                "The native tray popup is a compact 340-DIP borderless window outside the taskbar");
            check(Text(popup, "DownloadSpeedText") == "0 B/s" && Text(popup, "UploadSpeedText") == "0 B/s" &&
                Control<DashboardTrafficChart>(popup, "TrafficChart").Data.Samples.Count <= 1,
                "A new popup renders truthful idle rates without inventing traffic");
            Task.Run(() => fixture.Step(1_420_000, 28_800)).GetAwaiter().GetResult();
            Wait(() => Text(popup, "DownloadSpeedText") == ByteRateFormatter.Format(1_420_000));
            var snapshot = monitoring.GetSnapshot();
            var totals = monitoring.GetUsageTotals().Today;
            check(Text(popup, "DownloadSpeedText") == ByteRateFormatter.Format(snapshot.DownloadBytesPerSecond) &&
                Text(popup, "UploadSpeedText") == ByteRateFormatter.Format(snapshot.UploadBytesPerSecond),
                "A background sampler notification updates the popup dispatcher with the same raw directional live rates");
            check(snapshot.PendingDownloaded == 1_420_000 && snapshot.PendingUploaded == 28_800 &&
                fixture.Store.GetTotals(today, today) == (1_000_000_000L, 250_000_000L) &&
                Text(popup, "TodayTotalText") == ByteFormatter.Format(totals.Downloaded + totals.Uploaded) &&
                Text(popup, "TodayDownloadText").Contains(ByteFormatter.Format(totals.Downloaded), StringComparison.Ordinal) &&
                Text(popup, "TodayUploadText").Contains(ByteFormatter.Format(totals.Uploaded), StringComparison.Ordinal),
                "Today includes existing unflushed monitor bytes exactly once and excludes adjacent date buckets");

            for (var second = 0; second < 135; second++)
                fixture.Step((second % 7 + 1) * 100_000, (second % 5 + 1) * 20_000);
            popup.RefreshSnapshot();
            Layout(popup);
            var chart = Control<DashboardTrafficChart>(popup, "TrafficChart");
            snapshot = monitoring.GetSnapshot();
            var retained = snapshot.TrafficSamples.TakeLast(60).ToArray();
            check(chart.Data.WindowEndUtc - chart.Data.WindowStartUtc == TimeSpan.FromSeconds(60) &&
                chart.Data.Samples.Count == 60 && chart.Data.Samples.SequenceEqual(retained) &&
                chart.Data.DownloadSegments.Count > 0 && chart.Data.UploadSegments.Count > 0 &&
                chart.Data.Samples[^1].DownloadBytesPerSecond == snapshot.DownloadBytesPerSecond &&
                chart.Data.Samples[^1].UploadBytesPerSecond == snapshot.UploadBytesPerSecond,
                "The compact live graph projects exactly the last 60 existing directional one-second samples");
            var dashboardData = DashboardTrafficChartData.Create(snapshot.TrafficSamples, chart.Data.WindowEndUtc);
            check(snapshot.TrafficSamples.Count == 130 && dashboardData.Samples.Count == 130 &&
                dashboardData.WindowEndUtc - dashboardData.WindowStartUtc == TimeSpan.FromSeconds(130),
                "Opening the compact graph preserves the monitor history and the dashboard's existing 130-second projection");

            var workers = new[] { "_timer", "_profileMonitor", "_icmpMonitor", "_reachabilityMonitor", "_vpnSessions" };
            var identities = workers.Select(name => Field<object>(monitoring, name)).ToArray();
            var baseline = Field<object?>(monitoring, "_previousSnapshot");
            var captures = fixture.Counters.Captures;
            var usage = monitoring.GetUsageTotals();
            var session = monitoring.GetSnapshot().Session;
            for (var refresh = 0; refresh < 8; refresh++) popup.RefreshSnapshot();
            Layout(popup);
            check(captures == fixture.Counters.Captures && usage == monitoring.GetUsageTotals() &&
                session == monitoring.GetSnapshot().Session && ReferenceEquals(baseline, Field<object?>(monitoring, "_previousSnapshot")) &&
                workers.Select((name, index) => ReferenceEquals(identities[index], Field<object>(monitoring, name))).All(value => value) &&
                !typeof(TrayPopupWindow).GetFields(PrivateInstance).Any(field =>
                    field.FieldType == typeof(System.Threading.Timer) || field.FieldType == typeof(DispatcherTimer)),
                "Popup rendering reuses existing services, session, samples and accounting without an additional timer or counter read");

            var observer = Field<VpnSessionMonitor>(monitoring, "_vpnSessions");
            Field<System.Threading.Timer>(observer, "_timer").Change(Timeout.Infinite, Timeout.Infinite);
            var cached = observer.LatestRouteSnapshot;
            var key = Control<FrameworkElement>(popup, "VpnKeyIndicator");
            var speedBounds = Bounds(Control<FrameworkElement>(popup, "DownloadSpeedText"), (FrameworkElement)popup.Content);
            foreach (var names in new IReadOnlyList<string>[] { [], ["Owned private split VPN"], ["Owned RAS VPN", "Owned second tunnel"], [] })
            {
                typeof(VpnSessionMonitor).GetField("_latestRouteSnapshot", PrivateInstance)!.SetValue(observer,
                    cached with { Status = VpnTunnelStatus.Inactive, RouteKind = InternetRouteKind.Direct, ConnectedTunnelNames = names });
                popup.RefreshSnapshot();
                Layout(popup);
                check(key.IsVisible == (names.Count > 0) &&
                    Bounds(Control<FrameworkElement>(popup, "DownloadSpeedText"), (FrameworkElement)popup.Content) == speedBounds,
                    "The header key follows connected tunnel evidence, including split VPN, without moving the compact contents");
                if (names.Count > 0)
                    check(names.All(name => Control<TextBlock>(popup, "VpnKeyTooltipText").Text.Contains(name, StringComparison.Ordinal)),
                        "The popup VPN key identifies every existing cached connected tunnel");
            }
            typeof(VpnSessionMonitor).GetField("_latestRouteSnapshot", PrivateInstance)!.SetValue(observer, cached);
            monitoring.Suspend();
            popup.RefreshSnapshot();
            Layout(popup);
            snapshot = monitoring.GetSnapshot();
            check(Text(popup, "MonitoringStatusText") == snapshot.Status && snapshot.IsSuspended &&
                SameBrush(Control<Ellipse>(popup, "MonitoringStatusDot").Fill,
                    (Brush)popup.FindResource(StatusPresentation.ForMonitoring(snapshot))),
                "Monitoring status and its shared theme color reflect the existing sleep-paused monitor state");
            CheckLayout(popup, check);
            HistoryOverviewChecks.SavePreview(popup, System.IO.Path.Combine(previews, "340-dip-paused.png"));
            popup.Hide();
            Layout(popup);
            check(!HasSubscriber(monitoring, popup), "Hiding the popup detaches its monitor notification subscription");
        }
        finally
        {
            popup.Close();
            if (ReferenceEquals(Application.Current.MainWindow, popup)) Application.Current.MainWindow = null;
        }
        check(!HasSubscriber(monitoring, popup), "Closing the popup releases its monitor subscription");
    }

    private static void CheckTrayInteraction(string root, string previews, Action<bool, string> check)
    {
        using var fixture = new Fixture(System.IO.Path.Combine(root, "tray-popup-interaction"));
        var shutdownCount = 0;
        var createdDashboards = 0;
        using var context = new TrayApplicationContext(Application.Current, fixture.Monitoring,
            dashboardFactory: service => { createdDashboards++; return HistoryOverviewChecks.CreateWindow(service); },
            shutdownAction: () => shutdownCount++);
        context.Start(background: true);
        fixture.StopSamplerAndWait();
        var sampler = Field<object>(fixture.Monitoring, "_timer");
        var tray = Field<object>(context, "_trayIcon");
        var icon = Field<object>(tray, "_icon");
        var menu = Field<object>(tray, "_menu");
        check(MenuItems(menu).Select(item => (string)item.GetType().GetProperty("Text")!.GetValue(item)!)
            .SequenceEqual(["Open NetTrace", "Settings", "Exit"]),
            "The native tray context menu exposes Open NetTrace, Settings and Exit");
        GetCursorPos(out var previousCursor);
        Window? outside = null;
        try
        {
            GetMonitorInfo(MonitorFromPoint(previousCursor, 2), out var monitor);
            SetCursorPos(monitor.Work.Left + (monitor.Work.Right - monitor.Work.Left) / 2, monitor.Work.Bottom - 8);
            SendTrayMouse(icon, 0x0201);
            SendTrayMouse(icon, 0x0202);
            check(context.Popup is null && context.Dashboard is null,
                "The first native left-click callback waits for the Windows double-click interval");
            Wait(() => context.Popup is { IsVisible: true }, TimeSpan.FromMilliseconds(GetDoubleClickTime() + 3000));
            var popup = context.Popup!;
            Layout(popup);
            GetWindowRect(new WindowInteropHelper(popup).Handle, out var bounds);
            check(context.Dashboard is null && Application.Current.MainWindow is null && createdDashboards == 0 &&
                bounds.Left >= monitor.Work.Left && bounds.Right == monitor.Work.Right &&
                bounds.Top >= monitor.Work.Top && bounds.Bottom <= monitor.Work.Bottom &&
                monitor.Work.Bottom - bounds.Bottom is >= 0 and <= 20,
                "Single left-click aligns the popup to its monitor's working-area right edge independently of icon X and preserves bottom spacing");
            CheckLayout(popup, check);
            HistoryOverviewChecks.SavePreview(popup, System.IO.Path.Combine(previews, "native-tray-popup.png"));
            context.TogglePopup();
            check(context.Popup is null && Application.Current.MainWindow is null &&
                !popup.IsVisible && !HasSubscriber(fixture.Monitoring, popup),
                "A second popup toggle closes its native window and releases its monitor subscription");

            SendTrayMouse(icon, 0x0201);
            SendTrayMouse(icon, 0x0202);
            SendTrayMouse(icon, 0x0203);
            SendTrayMouse(icon, 0x0202);
            Wait(() => context.Dashboard is { IsVisible: true });
            PumpFor(TimeSpan.FromMilliseconds(GetDoubleClickTime() + 100));
            check(context.Popup is null && createdDashboards == 1 && context.Dashboard!.IsVisible &&
                !Field<DispatcherTimer>(tray, "_singleClickTimer").IsEnabled,
                "A native left double-click cancels the pending single action and opens one main window");
            context.Dashboard!.Close();

            outside = new Window { Width = 170, Height = 100, Left = SystemParameters.WorkArea.Left + 20,
                Top = SystemParameters.WorkArea.Top + 20, ShowInTaskbar = false, Topmost = true,
                Content = new TextBlock { Text = "Owned outside-click target", Background = Brushes.DarkSlateGray } };
            outside.Show();
            context.TogglePopup();
            popup = context.Popup!;
            Wait(() => popup.IsActive);
            GetWindowRect(new WindowInteropHelper(popup).Handle, out var popupBounds);
            var target = outside.PointToScreen(new Point(60, 50));
            SetCursorPos((int)target.X, (int)target.Y);
            GetCursorPos(out var clickPoint);
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            Wait(() => context.Popup is null);
            check(!popup.IsVisible && context.Dashboard is null &&
                (clickPoint.X < popupBounds.Left || clickPoint.X >= popupBounds.Right ||
                    clickPoint.Y < popupBounds.Top || clickPoint.Y >= popupBounds.Bottom),
                "An actual native cursor click outside the active popup closes it without opening the dashboard");
            context.TogglePopup();
            Wait(() => context.Popup!.IsActive);
            keybd_event(0x1B, 0, 0, UIntPtr.Zero);
            keybd_event(0x1B, 0, 2, UIntPtr.Zero);
            Wait(() => context.Popup is null);
            check(context.Dashboard is null, "Native Escape closes the focused tray popup");

            context.TogglePopup();
            menu.GetType().GetMethod("OnOpening", PrivateInstance)!.Invoke(menu, [new CancelEventArgs()]);
            check(context.Popup is null, "Opening the native right-click menu closes an existing popup");
            ClickMenu(menu, "Settings");
            var dashboard = context.Dashboard!;
            HistoryOverviewChecks.StopViewTimer(dashboard);
            check(Control<TabItem>(dashboard, "SettingsTab").IsSelected && createdDashboards == 2,
                "The tray Settings command creates the main window directly on its existing Settings page");
            dashboard.WindowState = WindowState.Minimized;
            ClickMenu(menu, "Open NetTrace");
            check(ReferenceEquals(context.Dashboard, dashboard) && dashboard.WindowState == WindowState.Normal,
                "Open NetTrace restores and reuses the existing main window");
            dashboard.Close();
            context.TogglePopup();
            check(ReferenceEquals(sampler, Field<object>(fixture.Monitoring, "_timer")) && fixture.Monitoring.GetSnapshot().IsRunning,
                "Closing and reopening tray and main windows preserves the existing sampler and live monitor");
            var lastPopup = context.Popup!;
            ClickMenu(menu, "Exit");
            check(context.Popup is null && context.Dashboard is null && !lastPopup.IsVisible &&
                !fixture.Monitoring.GetSnapshot().IsRunning && shutdownCount == 1 &&
                !(bool)icon.GetType().GetProperty("Visible")!.GetValue(icon)! &&
                !Field<DispatcherTimer>(tray, "_singleClickTimer").IsEnabled,
                "Tray Exit closes popup and dashboard, removes its icon and timer and stops the existing monitor once");
            context.Exit();
            check(shutdownCount == 1, "Repeated Exit does not request application shutdown twice");
        }
        finally
        {
            outside?.Close();
            SetCursorPos(previousCursor.X, previousCursor.Y);
        }
    }

    private static void CheckLayout(Window window, Action<bool, string> check)
    {
        Layout(window);
        var content = (FrameworkElement)window.Content;
        var elements = new[] { "DownloadSpeedText", "UploadSpeedText", "TrafficChart", "TodayTotalText",
            "TodayDownloadText", "TodayUploadText", "MonitoringStatusText" };
        check(window.ActualHeight is > 180 and < 500 && elements.All(name =>
        {
            var element = Control<FrameworkElement>(window, name);
            var bounds = Bounds(element, content);
            return element.IsVisible && bounds.Left >= -0.5 && bounds.Top >= -0.5 &&
                bounds.Right <= content.ActualWidth + 0.5 && bounds.Bottom <= content.ActualHeight + 0.5;
        }) && !HistoryOverviewChecks.Descendants(content).OfType<ScrollViewer>().Any(scroll => scroll.ScrollableWidth > 0.5),
            "All required live values, Today breakdown, graph and monitoring status fit the compact popup without scrolling");
    }

    private static IEnumerable<object> MenuItems(object menu) =>
        ((System.Collections.IEnumerable)menu.GetType().GetProperty("Items")!.GetValue(menu)!).Cast<object>();
    private static void ClickMenu(object menu, string text)
    {
        var item = MenuItems(menu).Single(item => (string)item.GetType().GetProperty("Text")!.GetValue(item)! == text);
        item.GetType().GetMethod("PerformClick")!.Invoke(item, null);
    }
    private static void SendTrayMouse(object icon, int mouseMessage)
    {
        var window = Field<object>(icon, "_window");
        var handle = (IntPtr)window.GetType().GetProperty("Handle")!.GetValue(window)!;
        var callback = Convert.ToUInt32(icon.GetType().GetField("WM_TRAYMOUSEMESSAGE", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue());
        SendMessage(handle, callback, new IntPtr(Convert.ToInt64(Field<object>(icon, "_id"))), new IntPtr(mouseMessage));
        HistoryOverviewChecks.Pump();
    }
    private static bool HasSubscriber(MonitoringService monitoring, object target) =>
        typeof(MonitoringService).GetFields(PrivateInstance).Where(field => typeof(Delegate).IsAssignableFrom(field.FieldType))
            .Select(field => field.GetValue(monitoring)).OfType<Delegate>()
            .SelectMany(handler => handler.GetInvocationList()).Any(handler => ReferenceEquals(handler.Target, target));
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, PrivateInstance)!.GetValue(value)!;
    private static T Control<T>(Window window, string name) where T : class => HistoryOverviewChecks.Control<T>(window, name);
    private static string Text(Window window, string name) => Control<TextBlock>(window, name).Text;
    private static Rect Bounds(FrameworkElement element, FrameworkElement parent) =>
        new(element.TranslatePoint(new Point(), parent), new Size(element.ActualWidth, element.ActualHeight));
    private static bool SameBrush(Brush? first, Brush? second) => first is SolidColorBrush left &&
        second is SolidColorBrush right && left.Color == right.Color;
    private static void Layout(Window window) { window.UpdateLayout(); HistoryOverviewChecks.Pump(); window.UpdateLayout(); }
    private static void Wait(Func<bool> ready, TimeSpan? timeout = null)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready())
        {
            if (elapsed.Elapsed > (timeout ?? TimeSpan.FromSeconds(5))) throw new TimeoutException("Owned tray popup state was not ready.");
            HistoryOverviewChecks.Pump();
        }
        HistoryOverviewChecks.Pump();
    }
    private static void PumpFor(TimeSpan duration)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < duration) HistoryOverviewChecks.Pump();
    }

    private sealed class Fixture : IDisposable
    {
        public OwnedClock Clock { get; } = new();
        public UsageStore Store { get; }
        public HistoryOverviewChecks.OwnedCounters Counters { get; }
        public MonitoringService Monitoring { get; }
        public Fixture(string root)
        {
            Store = new UsageStore(root);
            Counters = new HistoryOverviewChecks.OwnedCounters(Clock);
            Monitoring = HistoryOverviewChecks.CreateMonitoring(Store, Clock,
                new HistoryOverviewChecks.OwnedIdentity(new NetworkIdentity("WiFi", "Owned popup network", "Owned popup network",
                    HistoryOverviewChecks.AdapterId)), Counters);
        }
        public void Start() { Monitoring.Start(); StopSamplerAndWait(); Monitoring.SampleNow(); }
        public void StopSamplerAndWait()
        {
            Field<System.Threading.Timer>(Monitoring, "_timer").Change(Timeout.Infinite, Timeout.Infinite);
            Wait(() => Monitoring.GetSnapshot().Session is not null);
        }
        public void Step(long download, long upload)
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            Counters.Advance(download, upload);
            Monitoring.SampleNow();
        }
        public void Dispose() => Monitoring.Dispose();
    }
    private sealed class OwnedClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(new DateTime(2026, 10, 15, 12, 0, 0),
            TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 15, 12, 0, 0))).UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public int Flags;
    }
    private static bool GetMonitorInfo(IntPtr handle, out MonitorInfo info)
    {
        info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return ReadMonitorInfo(handle, ref info);
    }
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
}
