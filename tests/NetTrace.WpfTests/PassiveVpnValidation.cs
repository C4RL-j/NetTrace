using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NetTrace;
using NetTrace.Models;
using NetTrace.Services;

// Explicit opt-in local observation. Rendering uses captured real routing evidence
// and visibly reported simulated health transports, never the user's database.
internal static class PassiveVpnValidation
{
    public static int Run(string[] args)
    {
        try
        {
            VpnInterfaceInventory interfaces;
            try { interfaces = new WindowsVpnNative().Read(); }
            catch (Exception exception) { interfaces = new([], [], false, exception.Message); }
            var routes = new WindowsVpnRouteNative();
            var ipv4 = Read(routes, AddressFamily.InterNetwork);
            var ipv6 = Read(routes, AddressFamily.InterNetworkV6);
            var snapshot = VpnRouteClassifier.Classify(interfaces, ipv4, ipv6, DateTimeOffset.UtcNow);
            Console.WriteLine($"Passive local observation: VPN / tunnel {snapshot.Status}; IPv4 route {RouteName(snapshot.IPv4RouteKind)}; IPv6 route {RouteName(snapshot.IPv6RouteKind)}.");
            Console.WriteLine("Effective interface: " + (snapshot.InterfaceName ?? "Unavailable"));
            Console.WriteLine("Route evidence: " + snapshot.Reason);
            Console.WriteLine($"Local capture: {interfaces.Interfaces.Count} interfaces; IPv4 rows {ipv4.Routes.Count}; IPv6 rows {ipv6.Routes.Count}; checked {snapshot.CheckedAtUtc:O}.");
            Console.WriteLine("Only existing local Windows interface and route readers were used. No probes, network changes or real NetTrace database access occurred.");
            if (snapshot.Status != VpnTunnelStatus.Active)
            {
                Console.WriteLine("Live active-VPN validation unavailable: the current local effective route is not conclusively VPN-active. The network was left unchanged.");
                return 0;
            }
            if (args.Contains("--render-health", StringComparer.Ordinal))
                RenderCapturedRoute(interfaces, ipv4, ipv6, snapshot);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Passive VPN validation failed: " + exception.Message);
            return 1;
        }
    }

    private static VpnRouteInventory Read(WindowsVpnRouteNative source, AddressFamily family)
    {
        try { return source.Read(family); }
        catch (Exception exception) { return new(family, [], false, exception.Message); }
    }
    private static string RouteName(InternetRouteKind route) => route == InternetRouteKind.Vpn ? "VPN" : route.ToString();

    private static void RenderCapturedRoute(VpnInterfaceInventory interfaces, VpnRouteInventory ipv4, VpnRouteInventory ipv6,
        VpnRouteSnapshot capturedSnapshot)
    {
        if (Application.Current is null) _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var root = System.IO.Path.Combine(AppContext.BaseDirectory, "vpn-passive-render-" + Guid.NewGuid().ToString("N"));
        var store = new UsageStore(root);
        var evidence = new CapturedEvidence(interfaces, ipv4, ipv6);
        using var monitoring = new MonitoringService(store, new ValidationIdentity(),
            vpnVerifier: new VpnRouteVerifier(new VpnDetectionService(evidence), evidence),
            networkMonitor: new ValidationCounters(),
            icmpMonitor: new IcmpMonitor(new SimulatedBlockedPing(), interval: TimeSpan.FromMilliseconds(30)),
            reachabilityMonitor: new InternetReachabilityMonitor(new SimulatedAvailableReachability()));
        MainWindow? window = null;
        try
        {
            monitoring.Start();
            window = new MainWindow(monitoring, new ReadOnlyStartup())
                { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            window.Show();
            Wait(() => monitoring.GetSnapshot().VpnRoute.Status == VpnTunnelStatus.Active &&
                monitoring.LatestInternetReachability?.Status == InternetReachabilityStatus.Available &&
                monitoring.RollingIcmpSamples.Count >= 12);
            Control<TabControl>(window, "MainNavigation").SelectedItem = Control<TabItem>(window, "ConnectionsTab");
            window.RefreshDashboard();
            window.UpdateLayout();
            var currentRoute = monitoring.GetSnapshot().VpnRoute;
            var connectionVpn = Control<TextBlock>(window, "ConnectionsVpnStatusText");
            if (currentRoute.IPv4RouteKind != capturedSnapshot.IPv4RouteKind || currentRoute.IPv6RouteKind != capturedSnapshot.IPv6RouteKind ||
                connectionVpn.Text != "Active" || !ReferenceEquals(connectionVpn.Foreground, window.FindResource("StatusInfoBrush")) ||
                !FamilyRouteMatches(window, "IPv4RouteText", capturedSnapshot.IPv4RouteKind) ||
                !FamilyRouteMatches(window, "IPv6RouteText", capturedSnapshot.IPv6RouteKind))
                throw new InvalidOperationException("Connections did not independently preserve the captured IPv4/IPv6 route values and semantic resources.");
            var connectionPreview = VpnRouteAwarenessChecks.SavePreview(window, root, "captured-vpn-family-routes");
            Console.WriteLine($"Connections presentation passed with REAL captured family routing: IPv4 {RouteName(capturedSnapshot.IPv4RouteKind)}; IPv6 {RouteName(capturedSnapshot.IPv6RouteKind)}. VPN rows are informational; Direct, Unknown and Unavailable rows are neutral.");
            Console.WriteLine("Physical identity, profile and bandwidth in this Connections preview are owned validation placeholders.");
            Console.WriteLine("Owned Connections preview: " + connectionPreview);
            Control<TabControl>(window, "MainNavigation").SelectedItem = Control<TabItem>(window, "HealthTab");
            window.RefreshDashboard();
            window.UpdateLayout();
            var available = Control<TextBlock>(window, "InternetReachabilityText");
            var vpn = Control<TextBlock>(window, "HealthVpnStatusText");
            var quality = Control<TextBlock>(window, "IcmpQualityText");
            if (available.Text != "Available" || vpn.Text != "Active" || quality.Text != "Unavailable" ||
                !ReferenceEquals(available.Foreground, window.FindResource("StatusPositiveBrush")) ||
                !ReferenceEquals(vpn.Foreground, window.FindResource("StatusInfoBrush")) ||
                !ReferenceEquals(quality.Foreground, window.FindResource("StatusNeutralBrush")))
                throw new InvalidOperationException("Captured active VPN routing and simulated independent health evidence did not retain their separate presentation.");
            var preview = VpnRouteAwarenessChecks.SavePreview(window, root, "captured-vpn-simulated-health");
            Console.WriteLine("Health presentation passed with REAL captured local routing and SIMULATED health evidence: Available DNS/HTTPS, Active VPN, Unavailable ICMP with 100% loss.");
            Console.WriteLine("These health results are presentation validation inputs; they are not measurements of the user's current internet or ICMP behavior.");
            Console.WriteLine("Owned preview: " + preview);
        }
        finally { window?.Close(); }
    }

    private static bool FamilyRouteMatches(Window window, string controlName, InternetRouteKind route)
    {
        var control = Control<TextBlock>(window, controlName);
        return control.Text == RouteName(route) && ReferenceEquals(control.Foreground,
            window.FindResource(route == InternetRouteKind.Vpn ? "StatusInfoBrush" : "StatusNeutralBrush"));
    }
    private static T Control<T>(Window window, string name) where T : class => window.FindName(name) as T
        ?? throw new InvalidOperationException($"Missing passive validation control {name}.");
    private static void Wait(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Captured-route presentation did not reach its expected state.");
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }
    private sealed class CapturedEvidence(VpnInterfaceInventory interfaces, VpnRouteInventory ipv4, VpnRouteInventory ipv6)
        : IVpnInterfaceSource, IVpnRouteSource
    {
        public VpnInterfaceInventory Read() => interfaces;
        public VpnRouteInventory Read(AddressFamily family) => family == AddressFamily.InterNetwork ? ipv4 : ipv6;
    }
    private sealed class ValidationIdentity : INetworkIdentityService
    {
        public event EventHandler? NetworkChanged { add { } remove { } }
        public NetworkIdentityResult GetCurrentNetwork() => new(NetworkIdentityAvailability.Available,
            new NetworkIdentity("WiFi", "PASSIVE_RENDER_PLACEHOLDER", "Owned validation placeholder", "passive-render-adapter"), null);
        public void Dispose() { }
    }
    private sealed class ValidationCounters : INetworkMonitor
    {
        public IReadOnlyList<AdapterInfo> GetActiveAdapters() =>
            [new("passive-render-adapter", "Validation adapter", "Owned fake bandwidth source", "Wireless80211", 0, "Unavailable", true)];
        public NetworkSnapshot? Capture(string adapterId) => new(0, 0, DateTimeOffset.UtcNow);
    }
    private sealed class SimulatedBlockedPing : IPingSource
    {
        public Task<IcmpPingReply> SendAsync(IPAddress target, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new IcmpPingReply(IPStatus.TimedOut));
    }
    private sealed class SimulatedAvailableReachability : IInternetReachabilityProbeSource
    {
        public Task<bool> ResolveDnsAsync(string hostname, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> ProbeHttpsAsync(Uri endpoint, CancellationToken cancellationToken) => Task.FromResult(true);
    }
    private sealed class ReadOnlyStartup : IWindowsStartupRegistration
    {
        public bool IsEnabled() => false;
        public void Enable() => throw new InvalidOperationException("Passive validation does not change startup registration.");
        public void Disable() => throw new InvalidOperationException("Passive validation does not change startup registration.");
    }
}
