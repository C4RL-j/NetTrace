using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NetTrace;
using NetTrace.Models;
using NetTrace.Presentation;
using NetTrace.Services;

// Owned native inventories and counters exercise the existing observer and window.
// Every transport value comes from the existing observer, without sockets or probes.
internal static class VpnTransportChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string AdapterId = "vpn-transport-owned-wifi";
    private const string TunnelName = "Owned transport tunnel";
    private static readonly Guid WifiGuid = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid TunnelGuid = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

    public static void Run(string root, Action<bool, string> check)
    {
        using var fixture = new Fixture(System.IO.Path.Combine(root, "vpn-transport-owned"));
        var window = fixture.Window;
        Select(window, "Connections");
        var grid = Control<Grid>(window, "ConnectionRouteGrid");
        check(Control<StackPanel>(window, "ConnectionsContent").Children.OfType<Border>().Count() == 2 &&
            grid.RowDefinitions.Count == 5 && grid.Children.OfType<FrameworkElement>()
                .Where(cell => Grid.GetColumn(cell) == 0 && Grid.GetRow(cell) == 1)
                .Select(cell => cell is TextBlock label ? label.Text :
                    (cell as Panel)?.Children.OfType<TextBlock>().SingleOrDefault()?.Text)
                .SingleOrDefault() == "VPN transport" &&
            new[] { "ConnectionsVpnStatusText", "VpnTransportText", "IPv4RouteText", "IPv6RouteText", "PhysicalAdapterText" }
                .Select(name => Grid.GetRow(Control<TextBlock>(window, name))).SequenceEqual(new[] { 0, 1, 2, 3, 4 }),
            "VPN transport occupies one compact row between tunnel status and the independent family routes inside the existing card");

        foreach (var family in new[] { VpnTransportFamily.Inactive, VpnTransportFamily.IPv4,
            VpnTransportFamily.IPv6, VpnTransportFamily.Mixed, VpnTransportFamily.Unknown })
        {
            fixture.Set(family);
            check(Text(window, "VpnTransportText") == family.ToString() &&
                Text(window, "ConnectionsVpnStatusText") == (family == VpnTransportFamily.Inactive ? "Inactive" : "Active"),
                $"Connections displays {family} VPN transport with its independently observed tunnel status");
            check(Semantic(window, "VpnTransportText", family is VpnTransportFamily.IPv4 or VpnTransportFamily.IPv6 or VpnTransportFamily.Mixed
                    ? "StatusInfoBrush" : "StatusNeutralBrush"),
                $"VPN transport {family} uses the shared informational or neutral resource without assigning health significance");
            VpnRouteAwarenessChecks.SavePreview(window, root, "transport-" + family.ToString().ToLowerInvariant());
        }
        check(Control<TextBlock>(window, "VpnTransportText").ToolTip is string tooltip &&
            tooltip.Contains("outer connection", StringComparison.Ordinal) &&
            tooltip.Contains("separate from ordinary application routes", StringComparison.Ordinal) &&
            tooltip.Contains(fixture.Monitoring.GetSnapshot().VpnRoute.TransportReason, StringComparison.Ordinal),
            "VPN transport tooltip explains outer transport separately and uses the safe cached evidence reason");
        var invalidTransport = fixture.Monitoring.GetSnapshot().VpnRoute with { TransportFamily = (VpnTransportFamily)99 };
        var snapshot = fixture.Monitoring.GetSnapshot();
        check(VpnRoutePresentation.Create(invalidTransport, snapshot.Connection.Network, snapshot.Adapters).VpnTransport == "Unknown" &&
            StatusPresentation.ForVpnTransport(invalidTransport.TransportFamily) == StatusPresentation.NeutralBrushKey,
            "An unsupported transport enum value remains explicit neutral Unknown instead of exposing a numeric state");

        foreach (var routeCase in new[]
        {
            (VpnTransportFamily.IPv6, InternetRouteKind.Vpn, InternetRouteKind.Direct, "ipv6-v4-vpn-v6-direct"),
            (VpnTransportFamily.IPv6, InternetRouteKind.Vpn, InternetRouteKind.Vpn, "ipv6-both-vpn"),
            (VpnTransportFamily.IPv4, InternetRouteKind.Vpn, InternetRouteKind.Unavailable, "ipv4-v6-unavailable"),
            (VpnTransportFamily.Unknown, InternetRouteKind.Vpn, InternetRouteKind.Direct, "unknown-v4-vpn-v6-direct"),
            (VpnTransportFamily.Mixed, InternetRouteKind.Vpn, InternetRouteKind.Direct, "mixed-v4-vpn-v6-direct")
        })
        {
            fixture.Set(routeCase.Item1, routeCase.Item2, routeCase.Item3);
            check(Text(window, "VpnTransportText") == routeCase.Item1.ToString() &&
                FamilyRoutes(window, fixture.Monitoring, routeCase.Item2, routeCase.Item3),
                $"Transport {routeCase.Item1} remains independent of IPv4 {routeCase.Item2} and IPv6 {routeCase.Item3}, including neutral direct or unavailable IPv6");
            check(fixture.AccountingIntact(),
                $"Transport {routeCase.Item1} and family-route changes retain Wi-Fi selection, physical session totals, counters and sampler baseline");
            VpnRouteAwarenessChecks.SavePreview(window, root, "transport-routes-" + routeCase.Item4);
        }

        var observer = Observer(fixture.Monitoring);
        var routeTimer = Field<System.Threading.Timer>(observer, "_timer");
        var sampler = Field<System.Threading.Timer>(fixture.Monitoring, "_timer");
        var icmpWorker = fixture.Icmp.Completion;
        var reachabilityWorker = fixture.Reachability.Completion;
        var viewTimer = Field<DispatcherTimer>(window, "_timer");
        foreach (var transition in new[]
        {
            (VpnTransportFamily.Inactive, VpnTransportFamily.IPv6),
            (VpnTransportFamily.IPv6, VpnTransportFamily.Inactive),
            (VpnTransportFamily.IPv4, VpnTransportFamily.IPv6),
            (VpnTransportFamily.Unknown, VpnTransportFamily.IPv6),
            (VpnTransportFamily.IPv6, VpnTransportFamily.Unknown)
        })
        {
            fixture.Set(transition.Item1);
            fixture.Set(transition.Item2);
            check(Text(window, "VpnTransportText") == transition.Item2.ToString() && fixture.AccountingIntact() &&
                FamilyRoutes(window, fixture.Monitoring,
                    transition.Item2 == VpnTransportFamily.Inactive ? InternetRouteKind.Direct : InternetRouteKind.Vpn,
                    InternetRouteKind.Direct),
                $"Transport transition {transition.Item1} to {transition.Item2} refreshes Connections without mutating independently classified family routes or physical accounting");
            check(ReferenceEquals(observer, Observer(fixture.Monitoring)) &&
                ReferenceEquals(routeTimer, Field<System.Threading.Timer>(observer, "_timer")) &&
                ReferenceEquals(sampler, Field<System.Threading.Timer>(fixture.Monitoring, "_timer")) &&
                ReferenceEquals(viewTimer, Field<DispatcherTimer>(window, "_timer")) &&
                ReferenceEquals(icmpWorker, fixture.Icmp.Completion) &&
                ReferenceEquals(reachabilityWorker, fixture.Reachability.Completion),
                $"Transport transition {transition.Item1} to {transition.Item2} reuses the existing route, sampler, WPF, ICMP and reachability workers");
        }

        fixture.Set(VpnTransportFamily.IPv6);
        window.Width = 760;
        window.Height = 600;
        foreach (var pageName in new[] { "Connections", "Health", "Dashboard" })
        {
            Select(window, pageName);
            var page = (FrameworkElement)Control<TabItem>(window, pageName + "Tab").Content;
            check(Descendants(page).OfType<ScrollViewer>().All(scroll => scroll.ScrollableWidth < 0.01) &&
                (pageName != "Connections" || new[] { "ConnectionsVpnStatusText", "VpnTransportText", "IPv4RouteText", "IPv6RouteText", "PhysicalAdapterText" }
                    .All(name => Control<TextBlock>(window, name).ActualHeight >= 14)) &&
                (pageName != "Health" || !Descendants(page).OfType<TextBlock>().Any(text => text.Text == "VPN transport")),
                $"At 760 by 600, {pageName} remains readable without horizontal overflow or an added Health transport row");
            VpnRouteAwarenessChecks.SavePreview(window, root, "transport-760x600-" + pageName.ToLowerInvariant());
        }

        Select(window, "Health");
        var healthText = Text(window, "HealthVpnStatusText");
        var healthTooltip = Control<TextBlock>(window, "HealthVpnStatusText").ToolTip;
        var disclaimer = Text(window, "HealthDisclaimerText");
        fixture.Set(VpnTransportFamily.IPv4);
        check(Text(window, "HealthVpnStatusText") == healthText &&
            Equals(Control<TextBlock>(window, "HealthVpnStatusText").ToolTip, healthTooltip) &&
            Text(window, "HealthDisclaimerText") == disclaimer,
            "A transport-family change leaves Health status, route tooltip and ICMP/reachability explanation unchanged");

        Select(window, "Connections");
        var visibleTransport = Text(window, "VpnTransportText");
        fixture.Context.Dashboard!.Close();
        fixture.Set(VpnTransportFamily.IPv6, render: false);
        check(fixture.Context.Dashboard is null && Text(window, "VpnTransportText") == visibleTransport &&
            fixture.Monitoring.GetSnapshot().VpnRoute.TransportFamily == VpnTransportFamily.IPv6 &&
            ReferenceEquals(routeTimer, Field<System.Threading.Timer>(observer, "_timer")) && fixture.AccountingIntact(),
            "Tray-only monitoring admits fresh transport through the existing observer without rendering a closed dashboard or resetting totals");
        fixture.Context.ShowDashboard();
        window = fixture.Context.Dashboard!;
        Field<DispatcherTimer>(window, "_timer").Stop();
        Select(window, "Connections");
        check(Text(window, "VpnTransportText") == "IPv6" && Semantic(window, "VpnTransportText", "StatusInfoBrush") &&
            FamilyRoutes(window, fixture.Monitoring, InternetRouteKind.Vpn, InternetRouteKind.Direct) && fixture.AccountingIntact(),
            "Opening a recreated dashboard renders the latest cached IPv6 transport and independent routes without replacing monitoring");
    }

    private static bool FamilyRoutes(Window window, MonitoringService monitoring, InternetRouteKind ipv4, InternetRouteKind ipv6)
    {
        var snapshot = monitoring.GetSnapshot().VpnRoute;
        return snapshot.IPv4RouteKind == ipv4 && snapshot.IPv6RouteKind == ipv6 &&
            Text(window, "IPv4RouteText") == RouteName(ipv4) && Text(window, "IPv6RouteText") == RouteName(ipv6) &&
            Semantic(window, "IPv4RouteText", ipv4 == InternetRouteKind.Vpn ? "StatusInfoBrush" : "StatusNeutralBrush") &&
            Semantic(window, "IPv6RouteText", ipv6 == InternetRouteKind.Vpn ? "StatusInfoBrush" : "StatusNeutralBrush");
    }
    private static string RouteName(InternetRouteKind route) => route == InternetRouteKind.Vpn ? "VPN" : route.ToString();
    private static bool Semantic(Window window, string name, string resource) =>
        ReferenceEquals(Control<TextBlock>(window, name).Foreground, window.FindResource(resource));
    private static string Text(Window window, string name) => Control<TextBlock>(window, name).Text;
    private static T Control<T>(Window window, string name) where T : class => window.FindName(name) as T
        ?? throw new InvalidOperationException($"Missing transport control {name}.");
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, PrivateInstance)!.GetValue(owner)!;
    private static VpnSessionMonitor Observer(MonitoringService monitoring) => Field<VpnSessionMonitor>(monitoring, "_vpnSessions");
    private static void Select(MainWindow window, string page)
    {
        Control<TabControl>(window, "MainNavigation").SelectedItem = Control<TabItem>(window, page + "Tab");
        window.RefreshDashboard();
        window.UpdateLayout();
        Pump();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static void Wait(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Owned transport observation did not reach its expected state.");
            Pump();
        }
        Pump();
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private sealed record State(VpnTransportFamily Family, InternetRouteKind IPv4, InternetRouteKind IPv6);
    private sealed class OwnedEvidence : IVpnInterfaceSource, IVpnRouteSource
    {
        private State _state = new(VpnTransportFamily.Inactive, InternetRouteKind.Direct, InternetRouteKind.Direct);
        public void Set(State state) => Volatile.Write(ref _state, state);
        public VpnInterfaceInventory Read()
        {
            var state = Volatile.Read(ref _state);
            var wifi = new VpnInterfaceEvidence { InterfaceIndex = 4, InterfaceGuid = WifiGuid,
                InterfaceName = "Owned Wi-Fi", Description = "Owned physical adapter", InterfaceType = 71,
                OperationalStatus = OperationalStatus.Up, MediaConnectState = 1, StatusFlags = 1 };
            if (state.Family == VpnTransportFamily.Inactive) return new([wifi], []);
            var tunnel = new VpnInterfaceEvidence { InterfaceIndex = 41, InterfaceGuid = TunnelGuid,
                InterfaceName = TunnelName, Description = TunnelName,
                InterfaceType = state.Family == VpnTransportFamily.Mixed ? 131u : 23u,
                TunnelType = state.Family == VpnTransportFamily.Mixed ? 1u : 0u,
                OperationalStatus = OperationalStatus.Up, MediaConnectState = 1 };
            if (state.Family == VpnTransportFamily.Mixed)
                return new([wifi, tunnel],
                [
                    new VpnRasConnectionEvidence("Owned IPv4 session", "Owned IPv4 device", "vpn", 0x2000)
                        { RemoteEndpoint = IPAddress.Parse("198.51.100.55") },
                    new VpnRasConnectionEvidence("Owned IPv6 session", "Owned IPv6 device", "vpn", 0x2000)
                        { RemoteEndpoint = IPAddress.Parse("2001:db8:55::1") }
                ]);
            IPAddress? endpoint = state.Family switch
            {
                VpnTransportFamily.IPv4 => IPAddress.Parse("198.51.100.55"),
                VpnTransportFamily.IPv6 => IPAddress.Parse("2001:db8:55::1"),
                _ => null
            };
            return new([wifi, tunnel], [new VpnRasConnectionEvidence(TunnelName, TunnelName, "vpn", 0x2000)
                { RemoteEndpoint = endpoint }]);
        }
        public VpnRouteInventory Read(AddressFamily family)
        {
            var state = Volatile.Read(ref _state);
            var route = family == AddressFamily.InterNetwork ? state.IPv4 : state.IPv6;
            if (route == InternetRouteKind.Unavailable) return new(family, [], Availability: IpFamilyAvailability.Unavailable);
            var zero = family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any;
            var physicalHop = IPAddress.Parse(family == AddressFamily.InterNetwork ? "192.0.2.1" : "fe80::1");
            var rows = new List<VpnRouteEvidence> { new(4, WifiGuid, zero, 0, physicalHop, 100, 10, true) };
            if (route == InternetRouteKind.Vpn) rows.Add(new(41, TunnelGuid, zero, 0, zero, 1, 1, true));
            return new(family, rows, Availability: IpFamilyAvailability.Usable);
        }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly ControlledClock _clock = new();
        private readonly OwnedEvidence _evidence = new();
        private readonly OwnedCounters _counters;
        private readonly long _sessionId;
        private readonly object? _baseline;
        private readonly int _captures;
        public IcmpMonitor Icmp { get; }
        public InternetReachabilityMonitor Reachability { get; }
        public MonitoringService Monitoring { get; }
        public TrayApplicationContext Context { get; }
        public MainWindow Window => Context.Dashboard!;
        public Fixture(string root)
        {
            _counters = new(_clock);
            Icmp = new(new OwnedPing());
            Reachability = TestReachability.Create();
            Monitoring = new(new UsageStore(root), new OwnedIdentity(),
                vpnVerifier: new(new VpnDetectionService(_evidence), _evidence), networkMonitor: _counters,
                timeProvider: _clock, icmpMonitor: Icmp, reachabilityMonitor: Reachability);
            Context = new(Application.Current, Monitoring,
                dashboardFactory: service => new MainWindow(service, new ReadOnlyStartup())
                    { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 }, shutdownAction: () => { });
            Context.Start();
            Field<System.Threading.Timer>(Monitoring, "_timer").Change(Timeout.Infinite, Timeout.Infinite);
            Field<DispatcherTimer>(Window, "_timer").Stop();
            Wait(() => Monitoring.GetSnapshot().Session is not null && Monitoring.GetSnapshot().VpnRoute.Status == VpnTunnelStatus.Inactive);
            Monitoring.SampleNow();
            _clock.Advance();
            _counters.Advance();
            Monitoring.SampleNow();
            _sessionId = Monitoring.GetSnapshot().Session!.Id;
            _baseline = Field<NetworkSnapshot?>(Monitoring, "_previousSnapshot");
            _captures = _counters.Captures;
        }
        public void Set(VpnTransportFamily family, InternetRouteKind? ipv4 = null,
            InternetRouteKind ipv6 = InternetRouteKind.Direct, bool render = true)
        {
            var previous = Monitoring.GetSnapshot().VpnRoute.CheckedAtUtc;
            _clock.Advance();
            _evidence.Set(new(family,
                ipv4 ?? (family == VpnTransportFamily.Inactive ? InternetRouteKind.Direct : InternetRouteKind.Vpn), ipv6));
            Observer(Monitoring).Refresh();
            Wait(() => Monitoring.GetSnapshot().VpnRoute.CheckedAtUtc > previous);
            if (render && Context.Dashboard is { } window) { window.RefreshDashboard(); window.UpdateLayout(); Pump(); }
        }
        public bool AccountingIntact()
        {
            var snapshot = Monitoring.GetSnapshot();
            return snapshot.SelectedAdapter?.Id == AdapterId && snapshot.Session?.Id == _sessionId &&
                snapshot.SessionDownloaded == 2048 && snapshot.SessionUploaded == 1024 && _counters.Captures == _captures &&
                ReferenceEquals(_baseline, Field<NetworkSnapshot?>(Monitoring, "_previousSnapshot"));
        }
        public void Dispose() => Context.Dispose();
    }
    private sealed class ControlledClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 10, 11, 5, 6, 7, TimeSpan.Zero).Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance() => Interlocked.Add(ref _ticks, TimeSpan.TicksPerSecond);
    }
    private sealed class OwnedCounters(ControlledClock clock) : INetworkMonitor
    {
        private long _downloaded = 1000, _uploaded = 500;
        public int Captures { get; private set; }
        public IReadOnlyList<AdapterInfo> GetActiveAdapters() =>
            [new(AdapterId, "Owned Wi-Fi", "Owned transport accounting adapter", "Wireless80211", 86_000_000, "192.0.2.10", true)];
        public NetworkSnapshot? Capture(string adapterId) { Captures++; return new(_downloaded, _uploaded, clock.GetUtcNow()); }
        public void Advance() { _downloaded += 2048; _uploaded += 1024; }
    }
    private sealed class OwnedIdentity : INetworkIdentityService
    {
        public event EventHandler? NetworkChanged { add { } remove { } }
        public NetworkIdentityResult GetCurrentNetwork() => new(NetworkIdentityAvailability.Available,
            new NetworkIdentity("WiFi", "VPN_TRANSPORT_OWNED", "Owned hotspot", AdapterId), null);
        public void Dispose() { }
    }
    private sealed class OwnedPing : IPingSource
    {
        public Task<IcmpPingReply> SendAsync(IPAddress target, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new IcmpPingReply(IPStatus.TimedOut));
    }
    private sealed class ReadOnlyStartup : IWindowsStartupRegistration
    {
        public bool IsEnabled() => false;
        public void Enable() => throw new InvalidOperationException("Transport checks do not write startup registration.");
        public void Disable() => throw new InvalidOperationException("Transport checks do not write startup registration.");
    }
}
