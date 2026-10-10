using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NetTrace;
using NetTrace.Models;
using NetTrace.Services;

internal static class ConnectionDetailsChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string WifiId = "connections-owned-wifi";
    private const string EthernetId = "connections-owned-ethernet";
    private static readonly string[] ValueNames =
    [
        "AdapterNameText", "AdapterStatusText", "AdapterNetworkText", "AdapterProfileText", "AdapterTypeText",
        "Ipv4Text", "Ipv6Text", "DefaultGatewayText", "DnsText", "LinkSpeedText"
    ];

    public static void Run(string root, Action<bool, string> check)
    {
        var store = new UsageStore(System.IO.Path.Combine(root, "connection-details"));
        var history = new ConnectionHistoryStore(store);
        var profile = StoreProfile(store);
        var identity = new OwnedIdentity(Network("OWNED_WIFI", "OPPO A3x"));
        var counters = new OwnedCounters();
        var connected = Details(WifiId, OperationalStatus.Up,
            ["127.0.0.1", "0.0.0.0", "192.0.2.10", "fe80::1%7", "2001:db8:2::10", "2001:db8:1::10"],
            ["fe80::1%7", "192.0.2.1"], ["8.8.8.8", "1.1.1.1"], 86_000_000);
        counters.Set(connected);
        counters.Set(Details(EthernetId, OperationalStatus.Up, ["198.51.100.20", "2001:db8:3::20"],
            ["198.51.100.1"], ["9.9.9.9"], 1_000_000_000));
        using var icmp = new IcmpMonitor(new OwnedPing());
        using var reachability = TestReachability.Create();
        using var monitoring = new MonitoringService(store, identity,
            vpnVerifier: new VpnRouteVerifier(new VpnDetectionService(new NoTunnel()), new NoRoutes()),
            networkMonitor: counters, icmpMonitor: icmp, reachabilityMonitor: reachability);
        var created = 0;
        var exits = 0;
        using var context = new TrayApplicationContext(Application.Current, monitoring,
            dashboardFactory: service =>
            {
                created++;
                return new MainWindow(service, new ReadOnlyStartup())
                    { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            }, shutdownAction: () => exits++);
        context.Start(background: true);
        StopSampler(monitoring);
        Wait(() => history.GetActiveSession() is not null && monitoring.GetSnapshot().Connection.Profile?.Id == profile.Id);
        var sampler = Field<System.Threading.Timer>(monitoring, "_timer");
        var profileTimer = Field<System.Threading.Timer>(Field<ConnectionProfileMonitor>(monitoring, "_profileMonitor"), "_refreshTimer");
        var icmpWorker = icmp.Completion;
        var reachabilityWorker = reachability.Completion;
        check(context.Dashboard is null && monitoring.GetSnapshot().SelectedAdapterDetails == connected,
            "Connections details are observed in background mode without creating a dashboard or another monitor");

        context.ShowDashboard();
        var window = context.Dashboard!;
        var viewTimer = ViewTimer(window);
        viewTimer.Stop();
        var navigation = Control<TabControl>(window, "MainNavigation");
        var connections = Control<TabItem>(window, "ConnectionsTab");
        var dashboard = Control<TabItem>(window, "DashboardTab");
        var health = Control<TabItem>(window, "HealthTab");
        var reads = counters.DetailReads;
        var captures = counters.Captures;
        Select(navigation, connections, window);
        check(Values(window).SequenceEqual(new[]
            { "Owned Wi-Fi", "Connected", "OPPO A3x", "DITO — Owned router", "Wi-Fi", "192.0.2.10", "2001:db8:1::10", "192.0.2.1", "1.1.1.1, 8.8.8.8", "86 Mbps" }) &&
            Text(window, "LinkRateLabel") == "Wi-Fi link rate" && reads == counters.DetailReads && captures == counters.Captures,
            "Entering Connections immediately renders all cached connected Wi-Fi fields without capturing traffic or rediscovering the adapter");
        var content = Control<StackPanel>(window, "ConnectionsContent");
        var grid = Control<Grid>(window, "AdapterDetailsGrid");
        check(content.Children.OfType<Border>().Count() == 2 &&
            grid.Children.OfType<TextBlock>().Where(block => Grid.GetColumn(block) == 0).OrderBy(Grid.GetRow)
                .Select(block => block.Text).SequenceEqual(new[]
                { "Adapter", "Status", "Network", "Profile", "Type", "IPv4", "IPv6", "Default gateway", "DNS", "Wi-Fi link rate" }) &&
            ValueNames.Select(name => Control<TextBlock>(window, name)).Select(Grid.GetRow).SequenceEqual(Enumerable.Range(0, 10)),
            "Connections preserves its two cards and aligns the ten monitored-adapter fields in the requested order");
        check(Text(window, "CurrentProfileText") == "DITO — Owned router" &&
            Text(window, "CurrentSessionText") != "No active session" &&
            Control<Button>(window, "ManageProfilesButton").IsVisible && Control<ComboBox>(window, "AdapterComboBox").IsVisible,
            "The existing current-connection profile, session, Profiles action and adapter selector remain visible");
        SavePreview(window, root, "connected-wifi");

        var missingIpv6 = connected with { UnicastAddresses = Addresses("192.0.2.10") };
        Observe(monitoring, counters, identity, missingIpv6);
        check(Text(window, "Ipv6Text") == "Unavailable" && Text(window, "Ipv4Text") == "192.0.2.10" &&
            Text(window, "DefaultGatewayText") == "192.0.2.1" && Text(window, "DnsText") == "1.1.1.1, 8.8.8.8" &&
            Text(window, "LinkSpeedText") == "86 Mbps", "Missing IPv6 renders Unavailable while the remaining Wi-Fi rows stay valid");
        SavePreview(window, root, "missing-ipv6");
        Observe(monitoring, counters, identity, connected with { UnicastAddresses = Addresses("192.0.2.10", "fe80::1%7") });
        check(Text(window, "Ipv6Text") == "Unavailable", "Link-local-only IPv6 follows the documented global IPv6 Unavailable presentation");
        Observe(monitoring, counters, identity, connected with { GatewayAddresses = [] });
        check(Text(window, "DefaultGatewayText") == "Unavailable" && Text(window, "Ipv6Text") == "2001:db8:1::10" &&
            Text(window, "DnsText") == "1.1.1.1, 8.8.8.8", "A missing gateway renders Unavailable without hiding IPv6 or DNS");
        Observe(monitoring, counters, identity, connected with { DnsAddresses = [] });
        check(Text(window, "DnsText") == "Unavailable" && Text(window, "DefaultGatewayText") == "192.0.2.1",
            "Missing configured DNS renders Unavailable while gateway data stays valid");
        var manyDns = connected with { DnsAddresses = Addresses("2001:4860:4860::8888", "9.9.9.9", "8.8.8.8", "1.1.1.1", "8.8.8.8") };
        Observe(monitoring, counters, identity, manyDns);
        check(Text(window, "DnsText") == "1.1.1.1, 8.8.8.8 (+2 more)",
            "Multiple DNS servers render a compact deterministic pair and count after duplicate removal");
        SavePreview(window, root, "multiple-dns");
        Observe(monitoring, counters, identity, connected with
        {
            UnicastAddresses = connected.UnicastAddresses.Reverse().ToArray(),
            GatewayAddresses = connected.GatewayAddresses.Reverse().ToArray(),
            DnsAddresses = connected.DnsAddresses.Reverse().ToArray()
        });
        check(Text(window, "Ipv4Text") == "192.0.2.10" && Text(window, "Ipv6Text") == "2001:db8:1::10" &&
            Text(window, "DefaultGatewayText") == "192.0.2.1" && Text(window, "DnsText") == "1.1.1.1, 8.8.8.8",
            "Address enumeration order does not change the usable IPv4, global IPv6, gateway or DNS presentation");

        monitoring.SampleNow();
        var baseline = Field<NetworkSnapshot?>(monitoring, "_previousSnapshot");
        var physicalId = history.GetActiveSession()!.Id;
        var beforeRaw = monitoring.GetSnapshot();
        var events = history.GetRecentEvents().Count;
        var updated = Details(WifiId, OperationalStatus.Up, ["203.0.113.25", "2001:db8:4::25"],
            ["203.0.113.1"], ["4.4.4.4"], 100_000_000);
        Observe(monitoring, counters, identity, updated);
        check(Text(window, "Ipv4Text") == "203.0.113.25" && Text(window, "Ipv6Text") == "2001:db8:4::25" &&
            Text(window, "DefaultGatewayText") == "203.0.113.1" && Text(window, "DnsText") == "4.4.4.4" &&
            Text(window, "LinkSpeedText") == "100 Mbps" && history.GetActiveSession()!.Id == physicalId &&
            history.GetRecentEvents().Count == events && ReferenceEquals(baseline, Field<NetworkSnapshot?>(monitoring, "_previousSnapshot")) &&
            monitoring.GetSnapshot().SessionDownloaded == beforeRaw.SessionDownloaded,
            "An unchanged network identity still refreshes IP, gateway, DNS and link rate without splitting the session or resetting counters");
        monitoring.Profiles.UpdateProfile(profile.Id, "DITO", "Renamed router");
        RefreshObservation(monitoring);
        check(Text(window, "AdapterProfileText") == "DITO — Renamed router" && history.GetActiveSession()!.Id == physicalId,
            "Profile edits refresh the adapter profile through the existing resolver while preserving the physical session");
        identity.Set(Network("OWNED_UNASSIGNED", "Unmapped hotspot"));
        Wait(() => monitoring.GetSnapshot().Connection.Network?.NetworkIdentifier == "OWNED_UNASSIGNED");
        Pump();
        check(Text(window, "AdapterNetworkText") == "Unmapped hotspot" && Text(window, "AdapterProfileText") == "Unassigned",
            "An unmapped selected connection displays its network name and the Unassigned profile state");
        physicalId = history.GetActiveSession()!.Id;
        monitoring.Profiles.AssignMapping(profile.Id, "WiFi", "OWNED_UNASSIGNED");
        RefreshObservation(monitoring);
        check(Text(window, "AdapterProfileText") == "DITO — Renamed router" && history.GetActiveSession()!.Id == physicalId,
            "Assigning an existing profile updates the adapter profile without creating a second physical session");

        var combo = Control<ComboBox>(window, "AdapterComboBox");
        combo.SelectedItem = monitoring.GetSnapshot().Adapters.Single(adapter => adapter.Id == EthernetId);
        check(Values(window).SequenceEqual(new[]
            { "Owned Ethernet", "Connected", "Unavailable", "Unassigned", "Ethernet", "198.51.100.20", "2001:db8:3::20", "198.51.100.1", "9.9.9.9", "1 Gbps" }) &&
            !Text(window, "LinkRateLabel").Contains("Wi-Fi", StringComparison.Ordinal) &&
            monitoring.GetSnapshot().SelectedAdapter?.Id == EthernetId && history.GetActiveSession()!.Id == physicalId &&
            monitoring.GetSnapshot().SessionDownloaded == beforeRaw.SessionDownloaded,
            "Selecting Ethernet immediately refreshes every row without carrying over Wi-Fi network, profile, addresses, DNS or link wording");
        combo.SelectedItem = monitoring.GetSnapshot().Adapters.Single(adapter => adapter.Id == WifiId);
        check(Text(window, "AdapterNameText") == "Owned Wi-Fi" && Text(window, "AdapterNetworkText") == "Unmapped hotspot" &&
            Text(window, "AdapterProfileText") == "DITO — Renamed router" && Text(window, "Ipv4Text") == "203.0.113.25" &&
            Text(window, "DnsText") == "4.4.4.4" && Text(window, "LinkRateLabel") == "Wi-Fi link rate",
            "Returning to Wi-Fi restores the selected adapter's latest details and existing mapped profile");

        Observe(monitoring, counters, identity, updated with { Status = OperationalStatus.Down });
        check(Text(window, "AdapterStatusText") == "Disconnected" && Text(window, "AdapterNetworkText") == "Unavailable" &&
            Text(window, "AdapterProfileText") == "Unassigned" && ValueNames.Skip(5).All(name => Text(window, name) == "Unavailable"),
            "A disconnected adapter clears stale connection, IP, gateway, DNS and link data without exposing raw operational enums");
        SavePreview(window, root, "disconnected");
        counters.Set(null, WifiId);
        RefreshObservation(monitoring);
        check(Text(window, "AdapterStatusText") == "Unknown" && Text(window, "AdapterNameText") == "Owned Wi-Fi" &&
            Text(window, "AdapterNetworkText") == "Unavailable" && Text(window, "AdapterProfileText") == "Unassigned" &&
            ValueNames.Skip(5).All(name => Text(window, name) == "Unavailable") &&
            Values(window).All(value => !string.IsNullOrWhiteSpace(value)),
            "Unavailable raw adapter details keep identity rows readable and use consistent Unknown, Unassigned and Unavailable values");
        SavePreview(window, root, "unavailable");

        var longIpv6 = "2001:db8:abcd:1234:5678:90ab:cdef:1234";
        var longDetails = manyDns with { UnicastAddresses = Addresses("192.0.2.10", longIpv6) };
        Select(navigation, dashboard, window);
        var hiddenValues = Values(window);
        Observe(monitoring, counters, identity, longDetails);
        window.RefreshDashboard();
        check(Values(window).SequenceEqual(hiddenValues) && monitoring.GetSnapshot().SelectedAdapterDetails == longDetails,
            "Connections presentation stays idle on Dashboard while the existing monitor accepts current adapter details");
        reads = counters.DetailReads;
        captures = counters.Captures;
        Select(navigation, connections, window);
        check(Text(window, "Ipv6Text") == longIpv6 && Text(window, "DnsText") == "1.1.1.1, 8.8.8.8 (+2 more)" &&
            reads == counters.DetailReads && captures == counters.Captures,
            "Returning to Connections immediately uses the latest cached IPv6 and DNS data without another monitoring cycle");
        SavePreview(window, root, "long-ipv6");
        CheckMinimumLayout(window, root, check);

        var page = connections.Content;
        for (var round = 0; round < 3; round++)
        {
            foreach (var tab in new[] { dashboard, health, connections }) Select(navigation, tab, window);
        }
        check(ReferenceEquals(page, connections.Content) && ReferenceEquals(viewTimer, ViewTimer(window)) &&
            ReferenceEquals(sampler, Field<System.Threading.Timer>(monitoring, "_timer")) &&
            ReferenceEquals(profileTimer, Field<System.Threading.Timer>(Field<ConnectionProfileMonitor>(monitoring, "_profileMonitor"), "_refreshTimer")) &&
            ReferenceEquals(icmpWorker, icmp.Completion) && ReferenceEquals(reachabilityWorker, reachability.Completion) &&
            counters.Captures == captures && context.Dashboard == window && created == 1,
            "Repeated Dashboard, Health and Connections navigation retains the page, single view timer and existing monitoring workers");

        window.Hide();
        Pump();
        hiddenValues = Values(window);
        Observe(monitoring, counters, identity, updated);
        window.RefreshDashboard();
        check(!viewTimer.IsEnabled && Values(window).SequenceEqual(hiddenValues),
            "A hidden window stops its presentation timer and leaves connection rows idle while details continue refreshing");
        window.Show();
        window.UpdateLayout();
        Pump();
        check(viewTimer.IsEnabled && Text(window, "Ipv4Text") == "203.0.113.25" && Text(window, "DnsText") == "4.4.4.4",
            "Showing the hidden Connections page immediately displays the current cached adapter details");
        viewTimer.Stop();
        window.WindowState = WindowState.Minimized;
        Pump();
        hiddenValues = Values(window);
        Observe(monitoring, counters, identity, connected);
        window.RefreshDashboard();
        check(!viewTimer.IsEnabled && Values(window).SequenceEqual(hiddenValues),
            "A minimized Connections page suppresses detail rendering without stopping the monitoring core");
        window.WindowState = WindowState.Normal;
        window.UpdateLayout();
        Pump();
        check(viewTimer.IsEnabled && Text(window, "Ipv4Text") == "192.0.2.10" && Text(window, "Ipv6Text") == "2001:db8:1::10",
            "Restoring Connections immediately refreshes its current addresses through the existing view lifecycle");
        viewTimer.Stop();

        window.Close();
        hiddenValues = Values(window);
        Observe(monitoring, counters, identity, updated);
        window.RefreshDashboard();
        check(context.Dashboard is null && !viewTimer.IsEnabled && Values(window).SequenceEqual(hiddenValues) && monitoring.GetSnapshot().IsRunning,
            "Closing Connections detaches its view while application-owned adapter observation continues in the tray");
        context.ShowDashboard();
        var recreated = context.Dashboard!;
        ViewTimer(recreated).Stop();
        Select(Control<TabControl>(recreated, "MainNavigation"), Control<TabItem>(recreated, "ConnectionsTab"), recreated);
        check(created == 2 && Text(recreated, "Ipv4Text") == "203.0.113.25" && Text(recreated, "DnsText") == "4.4.4.4" &&
            ReferenceEquals(icmpWorker, icmp.Completion) && ReferenceEquals(reachabilityWorker, reachability.Completion) &&
            history.GetActiveSession()!.Id == physicalId,
            "Tray Open recreates Connections using current cached data and the unchanged physical session and workers");
        context.Exit();
        check(exits == 1 && !monitoring.GetSnapshot().IsRunning && history.GetActiveSession() is null && !ViewTimer(recreated).IsEnabled,
            "Connections shutdown retains the existing clean tray-exit and timer lifecycle");
    }

    private static ConnectionProfile StoreProfile(UsageStore store)
    {
        var profiles = new ProfileStore(store);
        var profile = profiles.CreateProfile("DITO", "Owned router");
        profiles.AssignMapping(profile.Id, "WiFi", "OWNED_WIFI");
        return profile;
    }

    private static void CheckMinimumLayout(MainWindow window, string root, Action<bool, string> check)
    {
        window.Width = 760;
        window.Height = 600;
        window.UpdateLayout();
        Pump();
        var ipv6 = Control<TextBlock>(window, "Ipv6Text");
        var dns = Control<TextBlock>(window, "DnsText");
        var scroll = VisualAncestors(ipv6).OfType<ScrollViewer>().First();
        scroll.ScrollToBottom();
        window.UpdateLayout();
        Pump();
        var first = Control<TextBlock>(window, "AdapterNameText").TranslatePoint(new Point(), scroll);
        var last = Control<TextBlock>(window, "LinkSpeedText");
        var lastPosition = last.TranslatePoint(new Point(), scroll);
        check(window.MinWidth == 760 && window.MinHeight == 600 &&
            scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && scroll.ScrollableWidth < 0.01 &&
            ipv6.ActualWidth > 200 && dns.ActualWidth > 200 && ipv6.TextTrimming == TextTrimming.CharacterEllipsis &&
            first.Y >= 0 && lastPosition.Y + last.ActualHeight <= scroll.ActualHeight &&
            ValueNames.All(name => Control<TextBlock>(window, name).ActualHeight >= 14),
            "At the 760 by 600 minimum window size, long IPv6 and compact DNS remain aligned and readable without horizontal scrolling");
        SavePreview(window, root, "760x600-long-ipv6-multiple-dns", includeAdapterPreview: false);
        window.Width = 920;
        window.Height = 740;
        scroll.ScrollToTop();
        window.UpdateLayout();
        Pump();
    }

    private static NetworkIdentityResult Network(string identifier, string name) =>
        new(NetworkIdentityAvailability.Available, new NetworkIdentity("WiFi", identifier, name, WifiId), null);
    private static IPAddress[] Addresses(params string[] values) => values.Select(IPAddress.Parse).ToArray();
    private static NetworkAdapterDetails Details(string id, OperationalStatus status, string[] unicast,
        string[] gateways, string[] dns, long speed) => new(id, status, Addresses(unicast), Addresses(gateways), Addresses(dns), speed);
    private static void Observe(MonitoringService monitoring, OwnedCounters counters, OwnedIdentity identity, NetworkAdapterDetails details)
    {
        counters.Set(details);
        RefreshObservation(monitoring, identity.RaiseChanged);
    }
    private static void RefreshObservation(MonitoringService monitoring, Action? trigger = null)
    {
        var previous = Field<long>(monitoring, "_lastHandledObservationSequence");
        if (trigger is null) monitoring.RefreshRecognition(); else trigger();
        Wait(() =>
        {
            // Wait for the observation's gated snapshot update, not merely the
            // sequence assignment at the beginning of its background callback.
            _ = monitoring.GetSnapshot();
            return Field<long>(monitoring, "_lastHandledObservationSequence") > previous;
        });
        Pump();
    }
    private static void Select(TabControl navigation, TabItem tab, MainWindow window)
    {
        navigation.SelectedItem = tab;
        window.UpdateLayout();
        Pump();
    }
    private static string[] Values(MainWindow window) => ValueNames.Select(name => Text(window, name)).ToArray();
    private static string Text(MainWindow window, string name) => Control<TextBlock>(window, name).Text;
    private static T Control<T>(Window window, string name) where T : class => window.FindName(name) as T
        ?? throw new InvalidOperationException($"Missing Connections check control {name}.");
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, PrivateInstance)!.GetValue(owner)!;
    private static DispatcherTimer ViewTimer(MainWindow window) => Field<DispatcherTimer>(window, "_timer");
    private static void StopSampler(MonitoringService monitoring) => Field<System.Threading.Timer>(monitoring, "_timer").Change(Timeout.Infinite, Timeout.Infinite);
    private static IEnumerable<DependencyObject> VisualAncestors(DependencyObject child)
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent is not null; parent = VisualTreeHelper.GetParent(parent)) yield return parent;
    }
    private static void Wait(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Connections fixture did not reach its expected state.");
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
    private static void SavePreview(MainWindow window, string root, string name, bool includeAdapterPreview = true)
    {
        SaveCurrentPreview(window, root, name);
        if (!includeAdapterPreview) return;
        var scroll = VisualAncestors(Control<TextBlock>(window, "Ipv6Text")).OfType<ScrollViewer>().First();
        var originalOffset = scroll.VerticalOffset;
        try
        {
            scroll.ScrollToBottom();
            SaveCurrentPreview(window, root, name + "-adapter");
        }
        finally
        {
            scroll.ScrollToVerticalOffset(originalOffset);
            window.UpdateLayout();
            Pump();
        }
    }
    private static void SaveCurrentPreview(MainWindow window, string root, string name)
    {
        window.UpdateLayout();
        Pump();
        var content = (FrameworkElement)window.Content;
        var margin = content.Margin;
        var width = content.ActualWidth + margin.Left + margin.Right;
        var height = content.ActualHeight + margin.Top + margin.Bottom;
        var offset = VisualTreeHelper.GetOffset(content);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(content)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(offset.X, offset.Y, content.ActualWidth, content.ActualHeight),
                Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top
            }, null, new Rect(margin.Left, margin.Top, content.ActualWidth, content.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var path = System.IO.Path.Combine(root, "connection-details-previews", name + ".png");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = System.IO.File.Create(path);
        encoder.Save(output);
    }

    private sealed class OwnedCounters : INetworkMonitor
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, NetworkAdapterDetails?> _details = new(StringComparer.Ordinal);
        private int _reads;
        private int _captures;
        public int DetailReads => Volatile.Read(ref _reads);
        public int Captures => Volatile.Read(ref _captures);
        public IReadOnlyList<AdapterInfo> GetActiveAdapters() =>
        [
            new(WifiId, "Owned Wi-Fi", "Injected Wi-Fi adapter", "Wireless80211", 86_000_000, "192.0.2.10", true),
            new(EthernetId, "Owned Ethernet", "Injected Ethernet adapter", "Ethernet", 1_000_000_000, "198.51.100.20", false)
        ];
        public void Set(NetworkAdapterDetails? details, string? id = null)
        {
            lock (_gate) _details[id ?? details!.AdapterId] = details;
        }
        public NetworkAdapterDetails? GetAdapterDetails(string adapterId)
        {
            Interlocked.Increment(ref _reads);
            lock (_gate) return _details.GetValueOrDefault(adapterId);
        }
        public NetworkSnapshot? Capture(string adapterId)
        {
            Interlocked.Increment(ref _captures);
            return adapterId is WifiId or EthernetId ? new(1000, 500, DateTimeOffset.UtcNow) : null;
        }
    }
    private sealed class OwnedIdentity(NetworkIdentityResult initial) : INetworkIdentityService
    {
        private volatile NetworkIdentityResult _current = initial;
        public event EventHandler? NetworkChanged;
        public NetworkIdentityResult GetCurrentNetwork() => _current;
        public void Set(NetworkIdentityResult current) { _current = current; RaiseChanged(); }
        public void RaiseChanged() => NetworkChanged?.Invoke(this, EventArgs.Empty);
        public void Dispose() { }
    }
    private sealed class OwnedPing : IPingSource
    {
        public Task<IcmpPingReply> SendAsync(IPAddress target, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new IcmpPingReply(IPStatus.Success, 12));
    }
    private sealed class ReadOnlyStartup : IWindowsStartupRegistration
    {
        public bool IsEnabled() => false;
        public void Enable() => throw new InvalidOperationException("Connections checks do not write startup registration.");
        public void Disable() => throw new InvalidOperationException("Connections checks do not write startup registration.");
    }
    private sealed class NoTunnel : IVpnInterfaceSource { public VpnInterfaceInventory Read() => new([], []); }
    private sealed class NoRoutes : IVpnRouteSource
    {
        public VpnRouteInventory Read(AddressFamily family) => new(family, [], Availability: IpFamilyAvailability.Unavailable);
    }
}
