using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using NetTrace;
using NetTrace.Controls;
using NetTrace.Models;
using NetTrace.Services;

internal static class VpnRouteAwarenessChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string AdapterId = "vpn-awareness-owned-wifi";
    private static readonly Guid WifiGuid = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TunnelGuid = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid OtherTunnelGuid = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    public static void Run(string root, Action<bool, string> check)
    {
        using var fixture = new Fixture(System.IO.Path.Combine(root, "vpn-route-awareness"));
        var window = fixture.Window;
        var icmpWorker = fixture.Icmp.Completion;
        var reachabilityWorker = fixture.Reachability.Completion;
        var observer = Observer(fixture.Monitoring);
        var routeTimer = Field<System.Threading.Timer>(observer, "_timer");
        var sampler = Field<System.Threading.Timer>(fixture.Monitoring, "_timer");
        var viewTimer = ViewTimer(window);
        Select(window, "Connections");
        check(Text(window, "ConnectionsVpnStatusText") == "Inactive" && FamilyRoutes(window, fixture.Monitoring,
                InternetRouteKind.Direct, InternetRouteKind.Direct) &&
            Text(window, "PhysicalAdapterText") == "Owned Wi-Fi" && Text(window, "AdapterNameText") == "Owned Wi-Fi" &&
            Semantic(window, "ConnectionsVpnStatusText", "StatusNeutralBrush") &&
            Semantic(window, "AdapterStatusText", "StatusPositiveBrush"),
            "Physical default routes display neutral Direct for both IPv4 and IPv6 with inactive VPN while connected monitored Wi-Fi uses the positive resource");
        var connections = Control<StackPanel>(window, "ConnectionsContent");
        var routeGrid = Control<Grid>(window, "ConnectionRouteGrid");
        check(connections.Children.OfType<Border>().Count() == 2 &&
            Grid.GetRow(Control<TextBlock>(window, "ConnectionsVpnStatusText")) < Grid.GetRow(Control<TextBlock>(window, "IPv4RouteText")) &&
            Grid.GetRow(Control<TextBlock>(window, "IPv4RouteText")) < Grid.GetRow(Control<TextBlock>(window, "IPv6RouteText")) &&
            Grid.GetRow(Control<TextBlock>(window, "IPv6RouteText")) < Grid.GetRow(Control<TextBlock>(window, "PhysicalAdapterText")) &&
            routeGrid.Children.OfType<TextBlock>().Any(text => text.Text == "IPv4 route") &&
            routeGrid.Children.OfType<TextBlock>().Any(text => text.Text == "IPv6 route") &&
            routeGrid.Children.OfType<TextBlock>().Any(text => text.Text == "Physical adapter"),
            "Connections keeps ordered compact VPN, separate IPv4/IPv6 route and physical adapter rows inside its existing current-connection card");
        SavePreview(window, root, "connections-direct");

        fixture.SetRoute(Scenario.InstalledInactive);
        check(Text(window, "ConnectionsVpnStatusText") == "Inactive" && FamilyRoutes(window, fixture.Monitoring,
                InternetRouteKind.Direct, InternetRouteKind.Direct),
            "An installed disconnected tunnel does not become an active internet VPN");
        SavePreview(window, root, "connections-installed-inactive");
        fixture.SetRoute(Scenario.VirtualInternal);
        check(Text(window, "ConnectionsVpnStatusText") == "Inactive" && FamilyRoutes(window, fixture.Monitoring,
                InternetRouteKind.Direct, InternetRouteKind.Direct),
            "An operational Hyper-V internal adapter without an effective general route remains inactive VPN context");
        fixture.SetRoute(Scenario.Unknown);
        check(Text(window, "ConnectionsVpnStatusText") == "Unknown" && FamilyRoutes(window, fixture.Monitoring,
                InternetRouteKind.Unknown, InternetRouteKind.Unknown) &&
            Semantic(window, "ConnectionsVpnStatusText", "StatusNeutralBrush"),
            "Incomplete native routing produces explicit neutral Unknown VPN and separate Unknown family-route values");
        SavePreview(window, root, "connections-unknown");
        fixture.SetRoute(Scenario.ActiveMixed);
        check(Text(window, "ConnectionsVpnStatusText") == "Active" && FamilyRoutes(window, fixture.Monitoring,
                InternetRouteKind.Vpn, InternetRouteKind.Direct) &&
            fixture.Monitoring.GetSnapshot().VpnRoute.Reason.Contains("IPv4", StringComparison.Ordinal) &&
            fixture.Monitoring.GetSnapshot().VpnRoute.Reason.Contains("IPv6", StringComparison.Ordinal) &&
            Text(window, "PhysicalAdapterText") == "Owned Wi-Fi" && fixture.Monitoring.GetSnapshot().SelectedAdapter?.Id == AdapterId &&
            Semantic(window, "ConnectionsVpnStatusText", "StatusInfoBrush"),
            "Mixed routing independently displays cyan IPv4 VPN and neutral IPv6 Direct while monitored accounting stays on physical Wi-Fi");
        SavePreview(window, root, "connections-vpn-active");
        foreach (var familyCase in new[]
        {
            (Scenario.ActiveBoth, InternetRouteKind.Vpn, InternetRouteKind.Vpn, "Active", "connections-both-vpn"),
            (Scenario.ActiveV4Only, InternetRouteKind.Vpn, InternetRouteKind.Unavailable, "Active", "connections-v4-vpn-v6-unavailable"),
            (Scenario.ActiveV6, InternetRouteKind.Direct, InternetRouteKind.Vpn, "Active", "connections-v4-direct-v6-vpn"),
            (Scenario.DirectUnknownV6, InternetRouteKind.Direct, InternetRouteKind.Unknown, "Unknown", "connections-v4-direct-v6-unknown"),
            (Scenario.ActiveUnknownV6, InternetRouteKind.Vpn, InternetRouteKind.Unknown, "Active", "connections-v4-vpn-v6-unknown")
        })
        {
            fixture.SetRoute(familyCase.Item1);
            check(FamilyRoutes(window, fixture.Monitoring, familyCase.Item2, familyCase.Item3) &&
                Text(window, "ConnectionsVpnStatusText") == familyCase.Item4 &&
                Semantic(window, "ConnectionsVpnStatusText", familyCase.Item4 == "Active" ? "StatusInfoBrush" : "StatusNeutralBrush") &&
                fixture.Monitoring.GetSnapshot().SelectedAdapter?.Id == AdapterId &&
                fixture.Monitoring.GetSnapshot().Session!.Id == fixture.SessionId &&
                ReferenceEquals(fixture.Baseline, Baseline(fixture.Monitoring)) && fixture.Counters.Captures == fixture.CaptureCount &&
                fixture.Monitoring.GetSnapshot().SessionDownloaded == 2048 && fixture.Monitoring.GetSnapshot().SessionUploaded == 1024,
                $"Independent IPv4 {familyCase.Item2} / IPv6 {familyCase.Item3} route values and semantic brushes refresh without changing physical accounting or its baseline");
            SavePreview(window, root, familyCase.Item5);
        }
        fixture.SetRoute(Scenario.MultipleTunnels);
        check(fixture.Monitoring.GetSnapshot().VpnRoute.InterfaceName == "Owned routed tunnel" &&
            Text(window, "ConnectionsVpnStatusText") == "Active" && FamilyRoutes(window, fixture.Monitoring,
                InternetRouteKind.Vpn, InternetRouteKind.Direct),
            "With several tunnel candidates the effective general-route participant supplies the active VPN context");
        fixture.SetRoute(Scenario.Direct);
        check(Text(window, "ConnectionsVpnStatusText") == "Inactive" && FamilyRoutes(window, fixture.Monitoring,
                InternetRouteKind.Direct, InternetRouteKind.Direct) &&
            fixture.Monitoring.GetSnapshot().Session!.Id == fixture.SessionId &&
            ReferenceEquals(fixture.Baseline, Baseline(fixture.Monitoring)) && fixture.Counters.Captures == fixture.CaptureCount &&
            fixture.Monitoring.GetSnapshot().SessionDownloaded == 2048 && fixture.Monitoring.GetSnapshot().SessionUploaded == 1024,
            "VPN to Direct transitions refresh Connections without restarting, changing the monitored adapter or resetting physical usage and sampler baseline");
        SavePreview(window, root, "connections-vpn-inactive");

        Select(window, "Health");
        check(Text(window, "InternetReachabilityText") == "Checking" && Text(window, "IcmpQualityText") == "Measuring" &&
            Text(window, "HealthVpnStatusText") == "Inactive" && Semantic(window, "InternetReachabilityText", "StatusNeutralBrush") &&
            Semantic(window, "IcmpQualityText", "StatusInfoBrush") &&
            Grid.GetRow(Control<TextBlock>(window, "InternetReachabilityText")) < Grid.GetRow(Control<TextBlock>(window, "HealthVpnStatusText")) &&
            Grid.GetRow(Control<TextBlock>(window, "HealthVpnStatusText")) < Grid.GetRow(Control<TextBlock>(window, "IcmpQualityText")) &&
            Control<StackPanel>(window, "HealthContent").Children.OfType<Border>().Count() == 2,
            "Health places neutral Checking, neutral inactive VPN and informational Measuring in the existing card without another chart or card");
        fixture.CompleteReachability(true, true);
        fixture.FillIcmp(12, cycles: 3);
        window.RefreshDashboard();
        check(Text(window, "InternetReachabilityText") == "Available" && Text(window, "HealthVpnStatusText") == "Inactive" &&
            Text(window, "IcmpQualityText") == "Excellent" && Semantic(window, "InternetReachabilityText", "StatusPositiveBrush") &&
            Semantic(window, "IcmpQualityText", "StatusPositiveBrush") && GeneralDisclaimer(window),
            "Health case A independently renders Available internet and Excellent ICMP positively with inactive VPN and the DNS/HTTPS disclaimer");
        SavePreview(window, root, "health-a-direct-excellent");
        fixture.FillIcmp(null);
        window.RefreshDashboard();
        var rawIcmp = fixture.Monitoring.LatestIcmpMeasurement;
        var rawSamples = fixture.Monitoring.RollingIcmpSamples;
        var line = Control<IcmpStabilityChart>(window, "IcmpStabilityChart");
        var loss = Control<IcmpPacketLossChart>(window, "IcmpPacketLossChart");
        var chartData = line.Data;
        var quality = Field<object>(window, "_qualitySummary");
        fixture.SetRoute(Scenario.ActiveMixed);
        check(Text(window, "InternetReachabilityText") == "Available" && Text(window, "HealthVpnStatusText") == "Active" &&
            Text(window, "IcmpQualityText") == "Unavailable" && Text(window, "IcmpPacketLossText") == "Unavailable" &&
            loss.Data.PacketLossPoints.TakeLast(12).All(point => point.Percent is null) &&
            Semantic(window, "InternetReachabilityText", "StatusPositiveBrush") && Semantic(window, "HealthVpnStatusText", "StatusInfoBrush") &&
            Semantic(window, "IcmpQualityText", "StatusNeutralBrush") && VpnDisclaimer(window) &&
            Text(window, "HealthDisclaimerText").Contains("available", StringComparison.OrdinalIgnoreCase) &&
            Text(window, "HealthDisclaimerText").Contains("Packet loss can't be measured for these samples.", StringComparison.Ordinal) &&
            Text(window, "HealthDisclaimerText").Contains("Ping replies may be blocked by the active VPN.", StringComparison.Ordinal),
            "Health case B shows unavailable packet loss without false bars for Available internet and an active VPN, with a qualified blocked-ping explanation");
        check(ReferenceEquals(rawIcmp, fixture.Monitoring.LatestIcmpMeasurement) && rawSamples.SequenceEqual(fixture.Monitoring.RollingIcmpSamples) &&
            ReferenceEquals(chartData, line.Data) && ReferenceEquals(line.Data, loss.Data) &&
            ReferenceEquals(quality, Field<object>(window, "_qualitySummary")),
            "A route-only change updates Health context without another ICMP completion or recalculating unchanged quality and chart history");
        SavePreview(window, root, "health-b-vpn-icmp-unavailable");
        fixture.SetRoute(Scenario.Direct);
        check(Text(window, "HealthVpnStatusText") == "Inactive" && GeneralDisclaimer(window) &&
            !Text(window, "HealthDisclaimerText").Contains("active VPN", StringComparison.OrdinalIgnoreCase) &&
            ReferenceEquals(rawIcmp, fixture.Monitoring.LatestIcmpMeasurement),
            "VPN disconnection immediately restores the general Health disclaimer while the same unavailable ICMP evidence remains independent");
        fixture.SetRoute(Scenario.ActiveMixed);
        fixture.CompleteReachability(false, false);
        window.RefreshDashboard();
        check(Text(window, "InternetReachabilityText") == "Unavailable" && Text(window, "HealthVpnStatusText") == "Active" &&
            Text(window, "IcmpPacketLossText") == "100%" && loss.Data.PacketLossPoints.TakeLast(12).All(point => point.Percent == 100) &&
            Text(window, "IcmpQualityText") == "Unavailable" && Semantic(window, "InternetReachabilityText", "StatusNegativeBrush") &&
            Semantic(window, "HealthVpnStatusText", "StatusInfoBrush") && Semantic(window, "IcmpQualityText", "StatusNeutralBrush") &&
            !Text(window, "HealthDisclaimerText").Contains("remains available", StringComparison.OrdinalIgnoreCase),
            "Health case C colors failed DNS/HTTPS reachability negatively while active VPN and unavailable ICMP retain independent semantics");
        SavePreview(window, root, "health-c-vpn-internet-unavailable");
        fixture.CompleteReachability(true, true);
        fixture.FillIcmp(250);
        window.RefreshDashboard();
        check(Text(window, "InternetReachabilityText") == "Available" && Text(window, "HealthVpnStatusText") == "Active" &&
            Text(window, "IcmpQualityText") == "Poor" && Semantic(window, "InternetReachabilityText", "StatusPositiveBrush") &&
            Semantic(window, "HealthVpnStatusText", "StatusInfoBrush") && Semantic(window, "IcmpQualityText", "StatusNegativeBrush"),
            "Health case D renders Available internet positive and active VPN informational while Poor ICMP alone uses the negative resource");
        SavePreview(window, root, "health-d-vpn-icmp-poor");
        fixture.CompleteReachability(true, false);
        window.RefreshDashboard();
        check(Text(window, "InternetReachabilityText") == "Limited" && Semantic(window, "InternetReachabilityText", "StatusCautionBrush"),
            "Limited reachability uses the shared caution resource without changing VPN or ICMP classifications");
        fixture.FillIcmp(75);
        window.RefreshDashboard();
        check(Text(window, "IcmpQualityText") == "Good" && Semantic(window, "IcmpQualityText", "StatusPositiveBrush"),
            "Good ICMP quality reuses the positive semantic resource");
        fixture.FillIcmp(150);
        window.RefreshDashboard();
        check(Text(window, "IcmpQualityText") == "Fair" && Semantic(window, "IcmpQualityText", "StatusCautionBrush"),
            "Fair ICMP quality reuses the caution semantic resource");
        fixture.SetRoute(Scenario.Unknown);
        check(Text(window, "HealthVpnStatusText") == "Unknown" && Semantic(window, "HealthVpnStatusText", "StatusNeutralBrush"),
            "Unknown VPN state is explicitly neutral on Health");

        Select(window, "Connections");
        fixture.Counters.Status = OperationalStatus.Down;
        fixture.Monitoring.RefreshRecognition();
        Wait(() => Text(window, "AdapterStatusText") == "Disconnected");
        check(Semantic(window, "AdapterStatusText", "StatusNegativeBrush") &&
            !ReferenceEquals(Control<TextBlock>(window, "Ipv4Text").Foreground, window.FindResource("StatusNegativeBrush")),
            "Disconnected adapter status uses the negative resource while address data remains ordinary text");
        fixture.Counters.Status = OperationalStatus.Up;
        fixture.Monitoring.RefreshRecognition();
        Wait(() => Text(window, "AdapterStatusText") == "Connected");
        Select(window, "Dashboard");
        check(Text(window, "StatusText") == "Monitoring" && Semantic(window, "StatusText", "StatusPositiveBrush") &&
            ReferenceEquals(Control<Ellipse>(window, "StatusDot").Fill, window.FindResource("StatusPositiveBrush")),
            "Dashboard Monitoring text and indicator share the centralized positive status brush");
        check(new[] { ("PositiveBrush", "StatusPositiveBrush"), ("CautionBrush", "StatusCautionBrush"),
                ("WarningBrush", "StatusNegativeBrush"), ("AccentBrush", "StatusInfoBrush"), ("SubtleBrush", "StatusNeutralBrush") }
                .All(pair => ReferenceEquals(window.FindResource(pair.Item1), window.FindResource(pair.Item2))),
            "Legacy NetTrace brush names alias the same centralized semantic resource objects");
        SavePreview(window, root, "dashboard-monitoring");
        CheckMinimumPages(fixture, root, check);

        Select(window, "Health");
        fixture.SetRoute(Scenario.Direct);
        var hiddenHealth = Text(window, "HealthVpnStatusText");
        Select(window, "Dashboard");
        fixture.SetRoute(Scenario.ActiveMixed);
        window.RefreshDashboard();
        check(Text(window, "HealthVpnStatusText") == hiddenHealth && fixture.Monitoring.GetSnapshot().VpnRoute.Status == VpnTunnelStatus.Active,
            "Hidden Health presentation stays idle while the existing route observer continues accepting VPN changes");
        var reads = fixture.Evidence.Reads;
        Select(window, "Health");
        check(Text(window, "HealthVpnStatusText") == "Active" && fixture.Evidence.Reads == reads,
            "Entering Health immediately displays the latest cached VPN state without a new native route query");
        window.Hide();
        Pump();
        fixture.SetRoute(Scenario.Direct);
        window.RefreshDashboard();
        check(!viewTimer.IsEnabled && Text(window, "HealthVpnStatusText") == "Active",
            "Hidden dashboards stop their view timer and skip VPN presentation while route monitoring continues");
        window.Show();
        window.UpdateLayout();
        Pump();
        check(viewTimer.IsEnabled && Text(window, "HealthVpnStatusText") == "Inactive",
            "Showing Health immediately consumes its current Direct route snapshot through the existing view lifecycle");
        viewTimer.Stop();
        window.WindowState = WindowState.Minimized;
        Pump();
        fixture.SetRoute(Scenario.ActiveMixed);
        check(!viewTimer.IsEnabled && Text(window, "HealthVpnStatusText") == "Inactive",
            "Minimization suppresses route presentation while the VPN snapshot stays current");
        window.WindowState = WindowState.Normal;
        window.UpdateLayout();
        Pump();
        viewTimer.Stop();
        check(Text(window, "HealthVpnStatusText") == "Active" &&
            ReferenceEquals(observer, Observer(fixture.Monitoring)) && ReferenceEquals(routeTimer, Field<System.Threading.Timer>(observer, "_timer")) &&
            ReferenceEquals(sampler, Field<System.Threading.Timer>(fixture.Monitoring, "_timer")) && ReferenceEquals(viewTimer, ViewTimer(window)) &&
            ReferenceEquals(icmpWorker, fixture.Icmp.Completion) && ReferenceEquals(reachabilityWorker, fixture.Reachability.Completion) &&
            fixture.Pings.MaximumConcurrent == 1 && fixture.Probes.MaximumConcurrent == 4 &&
            fixture.Monitoring.GetSnapshot().Session!.Id == fixture.SessionId &&
            fixture.Monitoring.GetSnapshot().SessionDownloaded == 2048 && ReferenceEquals(fixture.Baseline, Baseline(fixture.Monitoring)),
            "Restoration and route changes retain the single existing observer/timers and health workers while physical session accounting stays unchanged");

        fixture.Identity.SetUnavailable();
        Wait(() => fixture.Monitoring.GetSnapshot().Connection.Status == ConnectionStatus.ConnectedIdentityUnavailable);
        fixture.SetRoute(Scenario.ActiveMixed);
        Select(window, "Connections");
        check(Text(window, "ConnectionsVpnStatusText") == "Active" && FamilyRoutes(window, fixture.Monitoring,
                InternetRouteKind.Vpn, InternetRouteKind.Direct) &&
            Text(window, "PhysicalAdapterText") == "Unavailable" && fixture.Monitoring.GetSnapshot().SelectedAdapter?.Id == AdapterId,
            "Proven local VPN routing remains independent of unavailable physical network identity and does not switch the monitored adapter");
        fixture.Identity.SetAvailable();
        Wait(() => fixture.Monitoring.GetSnapshot().Session is not null);
        fixture.SessionId = fixture.Monitoring.GetSnapshot().Session!.Id;

        window.Close();
        fixture.SetRoute(Scenario.Direct);
        check(fixture.Context.Dashboard is null && !viewTimer.IsEnabled && fixture.Monitoring.GetSnapshot().IsRunning,
            "Closing the dashboard leaves route awareness current in tray mode without a presentation window");
        fixture.Context.ShowDashboard();
        var recreated = fixture.Context.Dashboard!;
        ViewTimer(recreated).Stop();
        Select(recreated, "Connections");
        check(Text(recreated, "ConnectionsVpnStatusText") == "Inactive" && FamilyRoutes(recreated, fixture.Monitoring,
                InternetRouteKind.Direct, InternetRouteKind.Direct) &&
            ReferenceEquals(observer, Observer(fixture.Monitoring)) && ReferenceEquals(icmpWorker, fixture.Icmp.Completion) &&
            fixture.Monitoring.GetSnapshot().Session!.Id == fixture.SessionId,
            "Tray Open recreates Connections with current cached routing and the same existing monitoring services and physical session");
        fixture.Monitoring.Suspend();
        fixture.Evidence.Set(Scenario.ActiveMixed);
        observer.Refresh();
        Pump();
        check(fixture.Monitoring.GetSnapshot().IsSuspended && fixture.Monitoring.GetSnapshot().VpnRoute == VpnRouteSnapshot.Unknown &&
            fixture.Monitoring.GetSnapshot().Session is null,
            "Suspend invalidates cached route context and cannot publish new partial route state while asleep");
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Monitoring.Resume();
        StopSampler(fixture.Monitoring);
        Wait(() => fixture.Monitoring.GetSnapshot().VpnRoute.Status == VpnTunnelStatus.Active &&
            fixture.Monitoring.GetSnapshot().Session is not null && fixture.Monitoring.GetSnapshot().Session!.Id != fixture.SessionId);
        recreated.RefreshDashboard();
        check(Text(recreated, "ConnectionsVpnStatusText") == "Active" && FamilyRoutes(recreated, fixture.Monitoring,
                InternetRouteKind.Vpn, InternetRouteKind.Direct) &&
            ReferenceEquals(observer, Observer(fixture.Monitoring)) && ReferenceEquals(routeTimer, Field<System.Threading.Timer>(observer, "_timer")),
            "Resume promptly refreshes VPN routing through the existing observer while normal physical-session resume remains intact");
        fixture.Context.Exit();
        fixture.Icmp.Completion.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        fixture.Reachability.Completion.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        check(!fixture.Monitoring.GetSnapshot().IsRunning && fixture.History.GetActiveSession() is null &&
            !ViewTimer(recreated).IsEnabled && fixture.Pings.Active == 0 && fixture.Probes.Active == 0,
            "Tray Exit cleanly stops route observation, view refresh and bounded health transports and closes the physical session");
    }

    private static void CheckMinimumPages(Fixture fixture, string root, Action<bool, string> check)
    {
        var window = fixture.Window;
        fixture.SetRoute(Scenario.ActiveMixed);
        fixture.CompleteReachability(true, true);
        fixture.FillIcmp(null);
        window.Width = 760;
        window.Height = 600;
        foreach (var name in new[] { "Connections", "Health", "Dashboard" })
        {
            Select(window, name);
            var page = Control<TabItem>(window, name + "Tab").Content as FrameworkElement;
            check(page is not null && VisualDescendants(page).OfType<ScrollViewer>().All(scroll => scroll.ScrollableWidth < 0.01) &&
                (name != "Connections" || new[] { "ConnectionsVpnStatusText", "IPv4RouteText", "IPv6RouteText" }
                    .All(control => Control<TextBlock>(window, control).ActualHeight >= 14)) &&
                (name != "Health" || Control<TextBlock>(window, "HealthVpnStatusText").ActualHeight >= 14),
                $"At 760 by 600, {name} retains compact readable status rows and its existing vertical scroll without horizontal overflow");
            SavePreview(window, root, "760x600-" + name.ToLowerInvariant());
        }
        window.Width = 920;
        window.Height = 740;
        window.UpdateLayout();
        Pump();
    }
    private static bool Semantic(Window window, string control, string resource) =>
        ReferenceEquals(Control<TextBlock>(window, control).Foreground, window.FindResource(resource));
    private static bool FamilyRoutes(Window window, MonitoringService monitoring, InternetRouteKind ipv4, InternetRouteKind ipv6)
    {
        var snapshot = monitoring.GetSnapshot().VpnRoute;
        return snapshot.IPv4RouteKind == ipv4 && snapshot.IPv6RouteKind == ipv6 &&
            Text(window, "IPv4RouteText") == RouteName(ipv4) && Text(window, "IPv6RouteText") == RouteName(ipv6) &&
            Semantic(window, "IPv4RouteText", ipv4 == InternetRouteKind.Vpn ? "StatusInfoBrush" : "StatusNeutralBrush") &&
            Semantic(window, "IPv6RouteText", ipv6 == InternetRouteKind.Vpn ? "StatusInfoBrush" : "StatusNeutralBrush");
    }
    private static string RouteName(InternetRouteKind route) => route == InternetRouteKind.Vpn ? "VPN" : route.ToString();
    private static bool GeneralDisclaimer(Window window)
    {
        var text = Text(window, "HealthDisclaimerText");
        return text.Contains("does not determine internet availability", StringComparison.OrdinalIgnoreCase) &&
            text.Contains("DNS", StringComparison.Ordinal) && text.Contains("HTTPS", StringComparison.Ordinal);
    }
    private static bool VpnDisclaimer(Window window)
    {
        var text = Text(window, "HealthDisclaimerText");
        return text.Contains("may be blocked or filtered", StringComparison.OrdinalIgnoreCase) &&
            text.Contains("active VPN", StringComparison.OrdinalIgnoreCase) &&
            text.Contains("DNS", StringComparison.Ordinal) && text.Contains("HTTPS", StringComparison.Ordinal);
    }
    private static void Select(MainWindow window, string page)
    {
        Control<TabControl>(window, "MainNavigation").SelectedItem = Control<TabItem>(window, page + "Tab");
        window.UpdateLayout();
        Pump();
    }
    private static string Text(Window window, string name) => Control<TextBlock>(window, name).Text;
    private static T Control<T>(Window window, string name) where T : class => window.FindName(name) as T
        ?? throw new InvalidOperationException($"Missing VPN awareness control {name}.");
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, PrivateInstance)!.GetValue(owner)!;
    private static VpnSessionMonitor Observer(MonitoringService monitoring) => Field<VpnSessionMonitor>(monitoring, "_vpnSessions");
    private static DispatcherTimer ViewTimer(MainWindow window) => Field<DispatcherTimer>(window, "_timer");
    private static object? Baseline(MonitoringService monitoring) => Field<NetworkSnapshot?>(monitoring, "_previousSnapshot");
    private static void StopSampler(MonitoringService monitoring) => Field<System.Threading.Timer>(monitoring, "_timer").Change(Timeout.Infinite, Timeout.Infinite);
    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in VisualDescendants(child)) yield return nested;
        }
    }
    private static void Wait(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("VPN awareness fixture did not reach its expected state.");
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
    internal static string SavePreview(MainWindow window, string root, string name)
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
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(offset.X, offset.Y, content.ActualWidth, content.ActualHeight),
                Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top
            }, null, new Rect(margin.Left, margin.Top, content.ActualWidth, content.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var path = System.IO.Path.Combine(root, "vpn-route-awareness-previews", name + ".png");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = System.IO.File.Create(path);
        encoder.Save(output);
        return path;
    }

    private enum Scenario
    {
        Direct, ActiveMixed, InstalledInactive, VirtualInternal, Unknown, MultipleTunnels,
        ActiveBoth, ActiveV4Only, ActiveV6, DirectUnknownV6, ActiveUnknownV6
    }
    private sealed class OwnedEvidence : IVpnInterfaceSource, IVpnRouteSource
    {
        private int _scenario;
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public void Set(Scenario scenario) => Volatile.Write(ref _scenario, (int)scenario);
        public VpnInterfaceInventory Read()
        {
            Interlocked.Increment(ref _reads);
            var scenario = (Scenario)Volatile.Read(ref _scenario);
            var wifi = new VpnInterfaceEvidence { InterfaceIndex = 4, InterfaceGuid = WifiGuid,
                InterfaceName = "Owned Wi-Fi", Description = "Owned physical adapter", InterfaceType = 71,
                OperationalStatus = OperationalStatus.Up, MediaConnectState = 1, StatusFlags = 1 };
            var tunnel = Tunnel(41, TunnelGuid, "Owned routed tunnel");
            return scenario switch
            {
                Scenario.ActiveMixed or Scenario.ActiveBoth or Scenario.ActiveV4Only or Scenario.ActiveV6 or
                    Scenario.ActiveUnknownV6 => new([wifi, tunnel], []),
                Scenario.InstalledInactive => new([wifi, tunnel with { OperationalStatus = OperationalStatus.Down, MediaConnectState = 2 }], []),
                Scenario.VirtualInternal => new([wifi, tunnel with { InterfaceType = 6, TunnelType = 0, DriverService = "vms_mp", InterfaceName = "Hyper-V internal adapter" }], []),
                Scenario.MultipleTunnels => new([wifi, Tunnel(42, OtherTunnelGuid, "Owned unrelated tunnel"), tunnel], []),
                _ => new([wifi], [])
            };
        }
        public VpnRouteInventory Read(AddressFamily family)
        {
            var scenario = (Scenario)Volatile.Read(ref _scenario);
            if (scenario == Scenario.Unknown) return new(family, [], false, "Owned route query incomplete.");
            if (family == AddressFamily.InterNetworkV6 && scenario is Scenario.DirectUnknownV6 or Scenario.ActiveUnknownV6)
                return new(family, [], false, "Owned IPv6 route query incomplete; IPv4 evidence remains independent.");
            if (family == AddressFamily.InterNetworkV6 && scenario == Scenario.ActiveV4Only)
                return new(family, [], Availability: IpFamilyAvailability.Unavailable);
            var zero = family == AddressFamily.InterNetwork ? "0.0.0.0" : "::";
            var physical = Route(4, WifiGuid, zero, 0, family == AddressFamily.InterNetwork ? "192.0.2.1" : "fe80::1", 50);
            var routes = new List<VpnRouteEvidence> { physical };
            if (family == AddressFamily.InterNetworkV6 && scenario is Scenario.ActiveBoth or Scenario.ActiveV6)
            {
                routes.Add(Route(41, TunnelGuid, "::", 1, "fe80::2", 1));
                routes.Add(Route(41, TunnelGuid, "8000::", 1, "fe80::2", 1));
            }
            if (family == AddressFamily.InterNetwork && scenario is Scenario.ActiveMixed or Scenario.MultipleTunnels or
                Scenario.ActiveBoth or Scenario.ActiveV4Only or Scenario.ActiveUnknownV6)
            {
                routes.Add(Route(41, TunnelGuid, "0.0.0.0", 1, "10.0.0.1", 1));
                routes.Add(Route(41, TunnelGuid, "128.0.0.0", 1, "10.0.0.1", 1));
            }
            if (family == AddressFamily.InterNetwork && scenario == Scenario.MultipleTunnels)
                routes.Add(Route(42, OtherTunnelGuid, "10.10.0.0", 16, "10.10.0.1", 1));
            return new(family, routes, Availability: IpFamilyAvailability.Usable);
        }
        private static VpnInterfaceEvidence Tunnel(uint index, Guid guid, string name) => new()
        {
            InterfaceIndex = index, InterfaceGuid = guid, InterfaceName = name, Description = "Owned encapsulation tunnel",
            InterfaceType = 131, TunnelType = 1, OperationalStatus = OperationalStatus.Up, MediaConnectState = 1, DriverService = "wintun"
        };
        private static VpnRouteEvidence Route(uint index, Guid guid, string prefix, byte length, string hop, uint metric) =>
            new(index, guid, IPAddress.Parse(prefix), length, IPAddress.Parse(hop), metric, 1, true);
    }
    private sealed class Fixture : IDisposable
    {
        public ControlledClock Clock { get; } = new();
        public OwnedEvidence Evidence { get; } = new();
        public OwnedIdentity Identity { get; } = new();
        public OwnedCounters Counters { get; }
        public ControlledPing Pings { get; } = new();
        public ControlledProbes Probes { get; } = new();
        public IcmpMonitor Icmp { get; }
        public InternetReachabilityMonitor Reachability { get; }
        public MonitoringService Monitoring { get; }
        public ConnectionHistoryStore History { get; }
        public TrayApplicationContext Context { get; }
        public MainWindow Window { get; }
        public long SessionId { get; set; }
        public object? Baseline { get; }
        public int CaptureCount { get; }
        public Fixture(string root)
        {
            var store = new UsageStore(root);
            var profiles = new ProfileStore(store);
            var profile = profiles.CreateProfile("Owned ISP", "Owned router");
            profiles.AssignMapping(profile.Id, "WiFi", "VPN_ROUTE_NETWORK");
            History = new ConnectionHistoryStore(store);
            Counters = new OwnedCounters(Clock);
            Icmp = new IcmpMonitor(Pings, interval: TimeSpan.FromMilliseconds(30), timeout: TimeSpan.FromMinutes(5), timeProvider: Clock);
            Reachability = new InternetReachabilityMonitor(Probes, interval: TimeSpan.FromMilliseconds(30), timeout: TimeSpan.FromMinutes(5), timeProvider: Clock);
            Monitoring = new MonitoringService(store, Identity, vpnVerifier: new VpnRouteVerifier(new VpnDetectionService(Evidence), Evidence),
                networkMonitor: Counters, timeProvider: Clock, icmpMonitor: Icmp, reachabilityMonitor: Reachability);
            Context = new TrayApplicationContext(Application.Current, Monitoring,
                dashboardFactory: service => new MainWindow(service, new ReadOnlyStartup())
                    { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 }, shutdownAction: () => { });
            Context.Start();
            StopSampler(Monitoring);
            Window = Context.Dashboard!;
            ViewTimer(Window).Stop();
            Wait(() => Monitoring.GetSnapshot().Session is not null && Monitoring.GetSnapshot().VpnRoute.RouteKind == InternetRouteKind.Direct &&
                Pings.Active == 1 && Probes.Active == 4);
            Monitoring.SampleNow();
            Clock.Advance(TimeSpan.FromSeconds(1));
            Counters.Advance(2048, 1024);
            Monitoring.SampleNow();
            Monitoring.Flush();
            SessionId = Monitoring.GetSnapshot().Session!.Id;
            Baseline = VpnRouteAwarenessChecks.Baseline(Monitoring);
            CaptureCount = Counters.Captures;
        }
        public void SetRoute(Scenario scenario)
        {
            var previous = Monitoring.GetSnapshot().VpnRoute.CheckedAtUtc;
            Clock.Advance(TimeSpan.FromSeconds(1));
            Evidence.Set(scenario);
            Observer(Monitoring).Refresh();
            Wait(() => Monitoring.GetSnapshot().VpnRoute.CheckedAtUtc > previous);
        }
        public void FillIcmp(long? latency, int cycles = 12)
        {
            for (var cycle = 0; cycle < cycles; cycle++)
            {
                Wait(() => Pings.Active == 1);
                var previous = Monitoring.LatestIcmpMeasurement;
                Clock.Advance(TimeSpan.FromSeconds(15));
                Pings.Queue(latency);
                Wait(() => Monitoring.LatestIcmpMeasurement is not null && !ReferenceEquals(previous, Monitoring.LatestIcmpMeasurement) && Pings.Active == 1);
            }
        }
        public void CompleteReachability(bool dns, bool https)
        {
            Wait(() => Probes.Active == 4);
            var previous = Monitoring.LatestInternetReachability;
            Clock.Advance(TimeSpan.FromSeconds(60));
            Probes.Queue(dns, https);
            Wait(() => Monitoring.LatestInternetReachability is not null && !ReferenceEquals(previous, Monitoring.LatestInternetReachability) && Probes.Active == 4);
        }
        public void Dispose() => Context.Dispose();
    }
    private sealed class ControlledClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 10, 11, 4, 5, 6, TimeSpan.Zero).Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }
    private sealed class ControlledPing : IPingSource
    {
        private readonly Channel<IcmpPingReply> _replies = Channel.CreateUnbounded<IcmpPingReply>();
        private int _active;
        private int _maximum;
        public int Active => Volatile.Read(ref _active);
        public int MaximumConcurrent => Volatile.Read(ref _maximum);
        public void Queue(long? latency)
        {
            for (var i = 0; i < 4; i++) _replies.Writer.TryWrite(latency is { } value ? new(IPStatus.Success, value) : new(IPStatus.TimedOut));
        }
        public async Task<IcmpPingReply> SendAsync(IPAddress target, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            Interlocked.Exchange(ref _maximum, Math.Max(active, MaximumConcurrent));
            try { return await _replies.Reader.ReadAsync(cancellationToken).ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
    private sealed class ControlledProbes : IInternetReachabilityProbeSource
    {
        private readonly Channel<bool> _dns = Channel.CreateUnbounded<bool>();
        private readonly Channel<bool> _https = Channel.CreateUnbounded<bool>();
        private int _active;
        private int _maximum;
        public int Active => Volatile.Read(ref _active);
        public int MaximumConcurrent => Volatile.Read(ref _maximum);
        public void Queue(bool dns, bool https)
        {
            for (var i = 0; i < 2; i++) { _dns.Writer.TryWrite(dns); _https.Writer.TryWrite(https); }
        }
        public Task<bool> ResolveDnsAsync(string hostname, CancellationToken cancellationToken) => Read(_dns, cancellationToken);
        public Task<bool> ProbeHttpsAsync(Uri endpoint, CancellationToken cancellationToken) => Read(_https, cancellationToken);
        private async Task<bool> Read(Channel<bool> source, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            while (true)
            {
                var previous = MaximumConcurrent;
                if (active <= previous || Interlocked.CompareExchange(ref _maximum, active, previous) == previous) break;
            }
            try { return await source.Reader.ReadAsync(cancellationToken).ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
    private sealed class OwnedIdentity : INetworkIdentityService
    {
        private volatile bool _available = true;
        public event EventHandler? NetworkChanged;
        public NetworkIdentityResult GetCurrentNetwork() => _available
            ? new(NetworkIdentityAvailability.Available, new NetworkIdentity("WiFi", "VPN_ROUTE_NETWORK", "Owned hotspot", AdapterId), null)
            : new(NetworkIdentityAvailability.Unavailable, null, "Owned unavailable physical identity.");
        public void SetUnavailable() { _available = false; NetworkChanged?.Invoke(this, EventArgs.Empty); }
        public void SetAvailable() { _available = true; NetworkChanged?.Invoke(this, EventArgs.Empty); }
        public void Dispose() { }
    }
    private sealed class OwnedCounters(ControlledClock clock) : INetworkMonitor
    {
        private long _downloaded = 1000;
        private long _uploaded = 500;
        public int Captures { get; private set; }
        public OperationalStatus Status { get; set; } = OperationalStatus.Up;
        public IReadOnlyList<AdapterInfo> GetActiveAdapters() =>
            [new(AdapterId, "Owned Wi-Fi", "Owned physical accounting adapter", "Wireless80211", 86_000_000, "192.0.2.10", true)];
        public NetworkSnapshot? Capture(string adapterId) { Captures++; return adapterId == AdapterId ? new(_downloaded, _uploaded, clock.GetUtcNow()) : null; }
        public void Advance(long downloaded, long uploaded) { _downloaded += downloaded; _uploaded += uploaded; }
        public NetworkAdapterDetails? GetAdapterDetails(string adapterId) => adapterId == AdapterId
            ? new(AdapterId, Status, [IPAddress.Parse("192.0.2.10"), IPAddress.Parse("2001:db8::10")],
                [IPAddress.Parse("192.0.2.1")], [IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8")], 86_000_000) : null;
    }
    private sealed class ReadOnlyStartup : IWindowsStartupRegistration
    {
        public bool IsEnabled() => false;
        public void Enable() => throw new InvalidOperationException("VPN awareness checks do not write startup registration.");
        public void Disable() => throw new InvalidOperationException("VPN awareness checks do not write startup registration.");
    }
}
