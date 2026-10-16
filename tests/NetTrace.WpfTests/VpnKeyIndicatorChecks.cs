using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NetTrace;
using NetTrace.Models;
using NetTrace.Services;

internal static class VpnKeyIndicatorChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly string[] StableElements =
    ["StatusDot", "StatusText", "MainNavigation", "DashboardUsageSelectorButton", "DashboardUsageCard",
        "DashboardTrafficChart", "DownloadRateText", "UploadRateText", "DashboardProfileText"];

    public static void Run(string root, Action<bool, string> check)
    {
        var previews = System.IO.Path.Combine(root, "vpn-key-previews");
        var store = new UsageStore(System.IO.Path.Combine(root, "vpn-key-owned"));
        var counters = new HistoryOverviewChecks.OwnedCounters(TimeProvider.System);
        var interfaces = new OwnedInterfaces();
        var routes = new OwnedRoutes();
        using var monitoring = new MonitoringService(store, new HistoryOverviewChecks.OwnedIdentity(),
            vpnVerifier: new VpnRouteVerifier(new VpnDetectionService(interfaces), routes), networkMonitor: counters,
            icmpMonitor: new IcmpMonitor(new NoPing()), reachabilityMonitor: TestReachability.Create());
        var window = HistoryOverviewChecks.CreateWindow(monitoring);
        try
        {
            window.Show();
            HistoryOverviewChecks.StopViewTimer(window);
            monitoring.Start();
            Field<System.Threading.Timer>(monitoring, "_timer").Change(Timeout.Infinite, Timeout.Infinite);
            Wait(() => monitoring.LatestInternetReachability?.Status == InternetReachabilityStatus.Unavailable &&
                monitoring.GetSnapshot().VpnRoute.CheckedAtUtc is not null);
            window.RefreshDashboard();
            Layout(window);
            var indicator = Control<Border>(window, "VpnKeyIndicator");
            var key = Control<System.Windows.Shapes.Path>(window, "VpnKeyPath");
            var tooltip = Control<ToolTip>(window, "VpnKeyToolTip");
            var tooltipText = Control<TextBlock>(window, "VpnKeyTooltipText");
            check(indicator.Visibility == Visibility.Collapsed && !indicator.IsVisible && indicator.Width <= 20 &&
                indicator.Height <= 20 && ReferenceEquals(indicator.ToolTip, tooltip),
                "The header key is a small collapsed native-tooltip indicator before any connected tunnel is confirmed");
            var accent = (Brush)window.FindResource("AccentBrush");
            check((SameBrush(key.Stroke, accent) || SameBrush(key.Fill, accent)) && key.Data is not null &&
                (AutomationProperties.GetName(indicator).Contains("VPN", StringComparison.OrdinalIgnoreCase) ||
                    AutomationProperties.GetName(key).Contains("VPN", StringComparison.OrdinalIgnoreCase)),
                "The compact key uses the existing cyan accent and an accessible VPN identity");
            var monitoringText = Control<TextBlock>(window, "StatusText").Text;
            var monitoringBrush = Control<TextBlock>(window, "StatusText").Foreground;
            var dotBrush = Control<Ellipse>(window, "StatusDot").Fill;
            var usageBefore = monitoring.GetUsageTotals();
            var captureCount = counters.Captures;
            var routeReads = routes.Reads;
            var interfaceReads = interfaces.Reads;
            var baseline = Field<object?>(monitoring, "_previousSnapshot");
            var observer = Field<VpnSessionMonitor>(monitoring, "_vpnSessions");
            var cachedSnapshot = observer.LatestRouteSnapshot;
            typeof(VpnSessionMonitor).GetField("_latestRouteSnapshot", PrivateInstance)!
                .SetValue(observer, Connected(["Owned cached split VPN"]));
            window.RefreshDashboard();
            Layout(window);
            check(indicator.IsVisible && tooltipText.Text.Contains("Owned cached split VPN", StringComparison.Ordinal) &&
                monitoring.GetSnapshot().VpnRoute.Status == VpnTunnelStatus.Inactive &&
                monitoring.LatestInternetReachability?.Status == InternetReachabilityStatus.Unavailable,
                "The real Dashboard refresh reads connected split-tunnel names from its existing cached observer independently of general routing and failed reachability");
            typeof(VpnSessionMonitor).GetField("_latestRouteSnapshot", PrivateInstance)!.SetValue(observer, cachedSnapshot);
            window.RefreshDashboard();
            Layout(window);
            var workerNames = new[] { "_timer", "_icmpMonitor", "_reachabilityMonitor", "_vpnSessions" };
            var workers = workerNames.Select(name => Field<object>(monitoring, name)).ToArray();
            var viewTimer = Field<DispatcherTimer>(window, "_timer");
            var routeTimer = Field<System.Threading.Timer>(observer, "_timer");
            var samples = new[]
            {
                (Name: "no-vpn", Snapshot: VpnRouteSnapshot.Unknown),
                (Name: "connected-split", Snapshot: Connected(["Owned split VPN"])),
                (Name: "connected-ras", Snapshot: Connected(["Owned RAS VPN"]) with
                    { Status = VpnTunnelStatus.Unknown, RouteKind = InternetRouteKind.Unknown }),
                (Name: "multiple", Snapshot: Connected(["Owned RAS VPN", "Owned split VPN", "Owned second tunnel"])),
                (Name: "inactive", Snapshot: Connected([])),
                (Name: "unknown", Snapshot: VpnRouteSnapshot.Unknown)
            };
            foreach (var (width, height) in new[] { (760d, 600d), (920d, 740d) })
            {
                window.Width = width;
                window.Height = height;
                Apply(window, VpnRouteSnapshot.Unknown);
                Layout(window);
                var geometry = Geometry(window);
                foreach (var sample in samples)
                {
                    Apply(window, sample.Snapshot);
                    Layout(window);
                    var active = sample.Snapshot.ConnectedTunnelNames.Count > 0;
                    check(indicator.Visibility == (active ? Visibility.Visible : Visibility.Collapsed) &&
                        indicator.IsVisible == active && SameGeometry(Geometry(window), geometry),
                        $"At {width} DIP {sample.Name} changes only key visibility and never moves Monitoring, navigation, graph or surrounding cards");
                    if (active)
                    {
                        var rootElement = (FrameworkElement)window.Content;
                        var keyBounds = Bounds(indicator, rootElement);
                        var dotBounds = Bounds(Control<FrameworkElement>(window, "StatusDot"), rootElement);
                        check(keyBounds.Right <= dotBounds.Left && dotBounds.Left - keyBounds.Right is >= 0 and <= 12 &&
                            Math.Abs(keyBounds.Y + keyBounds.Height / 2 - dotBounds.Y - dotBounds.Height / 2) <= 5,
                            $"At {width} DIP {sample.Name} places a small key immediately left of the existing monitoring dot");
                        check(tooltipText.Text.StartsWith("VPN Connected", StringComparison.Ordinal) &&
                            sample.Snapshot.ConnectedTunnelNames.All(name => tooltipText.Text.Contains(name, StringComparison.Ordinal)) &&
                            !tooltipText.Text.Contains("encrypted", StringComparison.OrdinalIgnoreCase) &&
                            !tooltipText.Text.Contains("secure", StringComparison.OrdinalIgnoreCase),
                            $"At {width} DIP {sample.Name} tooltip identifies every available connected tunnel without claiming encryption or security");
                    }
                    HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, $"{width}x{height}-{sample.Name}.png"));
                }
            }
            Apply(window, Connected(["Owned split VPN", "Owned RAS VPN"]));
            Layout(window);
            CheckActualHover(window, indicator, tooltip, tooltipText, previews, check);
            tooltip.IsOpen = false;
            Apply(window, Connected([]));
            Layout(window);
            check(!indicator.IsVisible && !tooltip.IsOpen &&
                Control<TextBlock>(window, "StatusText").Text == monitoringText &&
                ReferenceEquals(Control<TextBlock>(window, "StatusText").Foreground, monitoringBrush) &&
                ReferenceEquals(Control<Ellipse>(window, "StatusDot").Fill, dotBrush) &&
                monitoring.LatestInternetReachability?.Status == InternetReachabilityStatus.Unavailable,
                "Removing connected evidence hides the key while Monitoring and independently unavailable internet reachability retain their existing meaning");
            check(counters.Captures == captureCount && interfaces.Reads == interfaceReads && routes.Reads == routeReads &&
                monitoring.GetUsageTotals() == usageBefore && ReferenceEquals(baseline, Field<object?>(monitoring, "_previousSnapshot")) &&
                workerNames.Select((name, index) => ReferenceEquals(workers[index], Field<object>(monitoring, name))).All(value => value) &&
                ReferenceEquals(viewTimer, Field<DispatcherTimer>(window, "_timer")) &&
                ReferenceEquals(routeTimer, Field<System.Threading.Timer>(observer, "_timer")) &&
                typeof(MainWindow).GetFields(PrivateInstance).Count(field => field.FieldType == typeof(DispatcherTimer)) == 1,
                "Repeated key presentation reads only cached evidence and adds no native query, sampler, worker, timer, usage write or accounting change");
        }
        finally { window.Close(); }
        Console.WriteLine("Owned VPN header key WPF previews: " + previews);
    }

    private static void CheckActualHover(MainWindow window, Border indicator, ToolTip tooltip, TextBlock tooltipText,
        string previews, Action<bool, string> check)
    {
        GetCursorPos(out var previousCursor);
        var opacity = window.Opacity;
        var topmost = window.Topmost;
        try
        {
            window.Opacity = 1;
            window.Topmost = true;
            ToolTipService.SetInitialShowDelay(indicator, 0);
            ToolTipService.SetBetweenShowDelay(indicator, 0);
            Layout(window);
            var target = indicator.PointToScreen(new Point(indicator.ActualWidth / 2, indicator.ActualHeight / 2));
            SetCursorPos((int)target.X + 60, (int)target.Y + 30);
            HistoryOverviewChecks.Pump();
            SetCursorPos((int)target.X, (int)target.Y);
            Wait(() => tooltip.IsOpen && tooltip.IsVisible, TimeSpan.FromSeconds(4));
            check(indicator.IsMouseOver && tooltip.IsOpen && tooltip.IsVisible &&
                tooltipText.Text.Contains("Owned split VPN", StringComparison.Ordinal) &&
                tooltipText.Text.Contains("Owned RAS VPN", StringComparison.Ordinal),
                "Actual pointer hover over the header key opens the native tooltip with all connected names");
            HistoryUsageChecks.SaveTooltip(tooltip, System.IO.Path.Combine(previews, "native-connected-hover-tooltip.png"));
        }
        finally
        {
            tooltip.IsOpen = false;
            SetCursorPos(previousCursor.X, previousCursor.Y);
            window.Topmost = topmost;
            window.Opacity = opacity;
        }
    }

    private static VpnRouteSnapshot Connected(IReadOnlyList<string> names) => new(VpnTunnelStatus.Inactive,
        InternetRouteKind.Direct, null, null, DateTimeOffset.UtcNow, "Owned private split tunnel does not own general routes.")
    {
        IPv4RouteKind = InternetRouteKind.Direct,
        IPv6RouteKind = InternetRouteKind.Unavailable,
        ConnectedTunnelNames = names
    };

    private static void Apply(MainWindow window, VpnRouteSnapshot snapshot) =>
        typeof(MainWindow).GetMethod("ApplyVpnKeyIndicator", PrivateInstance)!.Invoke(window, new object[] { snapshot });
    private static T Control<T>(Window window, string name) where T : class => HistoryOverviewChecks.Control<T>(window, name);
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, PrivateInstance)!.GetValue(instance)!;
    private static Rect Bounds(FrameworkElement element, FrameworkElement parent) => new(element.TranslatePoint(new Point(), parent),
        new Size(element.ActualWidth, element.ActualHeight));
    private static Rect[] Geometry(MainWindow window) => StableElements.Select(name =>
        Bounds(Control<FrameworkElement>(window, name), (FrameworkElement)window.Content)).ToArray();
    private static bool SameGeometry(Rect[] actual, Rect[] expected) => actual.Zip(expected, (left, right) =>
        Math.Abs(left.X - right.X) < 0.01 && Math.Abs(left.Y - right.Y) < 0.01 &&
        Math.Abs(left.Width - right.Width) < 0.01 && Math.Abs(left.Height - right.Height) < 0.01).All(value => value);
    private static bool SameBrush(Brush? left, Brush? right) => left is SolidColorBrush first &&
        right is SolidColorBrush second && first.Color == second.Color;
    private static void Layout(Window window) { window.UpdateLayout(); HistoryOverviewChecks.Pump(); window.UpdateLayout(); }
    private static void Wait(Func<bool> ready, TimeSpan? timeout = null)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready())
        {
            if (elapsed.Elapsed > (timeout ?? TimeSpan.FromSeconds(5))) throw new TimeoutException("The owned VPN key state was not ready.");
            HistoryOverviewChecks.Pump();
        }
        HistoryOverviewChecks.Pump();
    }

    private sealed class OwnedInterfaces : IVpnInterfaceSource
    {
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public VpnInterfaceInventory Read() { Interlocked.Increment(ref _reads); return new([], []); }
    }
    private sealed class OwnedRoutes : IVpnRouteSource
    {
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public VpnRouteInventory Read(AddressFamily family)
        {
            Interlocked.Increment(ref _reads);
            return new(family, [], Availability: IpFamilyAvailability.Unavailable);
        }
    }
    private sealed class NoPing : IPingSource
    {
        public Task<IcmpPingReply> SendAsync(IPAddress target, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new IcmpPingReply(IPStatus.TimedOut));
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint { public int X; public int Y; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
}
