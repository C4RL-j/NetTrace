using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NetTrace;
using NetTrace.Models;
using NetTrace.Services;

internal static class NavigationChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(string root, string adapterId, Action<bool, string> check)
    {
        var store = new UsageStore(System.IO.Path.Combine(root, "navigation"));
        var history = new ConnectionHistoryStore(store);
        var vpnHistory = new VpnSessionStore(store);
        var profiles = new ProfileStore(store);
        var profile = profiles.CreateProfile("Navigation ISP", "Owned router");
        profiles.AssignMapping(profile.Id, "WiFi", "NAVIGATION_NETWORK");
        var clock = new NavigationClock();
        var counters = new OwnedCounters(adapterId, clock.GetUtcNow());
        var pings = new OwnedPing();
        using var icmp = new IcmpMonitor(pings, interval: TimeSpan.FromMilliseconds(40));
        using var monitoring = new MonitoringService(store, new OwnedIdentity(adapterId),
            vpnVerifier: new VpnRouteVerifier(new VpnDetectionService(new OwnedTunnel()), new OwnedRoutes()),
            networkMonitor: counters, timeProvider: clock, icmpMonitor: icmp);
        var startup = new OwnedStartup();
        var windowsCreated = 0;
        var exits = 0;
        using var context = new TrayApplicationContext(Application.Current, monitoring,
            dashboardFactory: service =>
            {
                windowsCreated++;
                return new MainWindow(service, startup)
                    { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            }, shutdownAction: () => exits++);

        context.Start();
        StopSampler(monitoring);
        var window = context.Dashboard!;
        StopViewTimer(window);
        Wait(() => history.GetActiveSession() is not null && vpnHistory.GetActiveSession() is not null);
        window.RefreshDashboard();
        Pump();
        var navigation = Control<TabControl>(window, "MainNavigation");
        var names = new[] { "Dashboard", "Health", "Connections", "History", "Settings" };
        var tabs = names.Select(name => Control<TabItem>(window, name + "Tab")).ToArray();
        var content = tabs.Select(tab => tab.Content).ToArray();

        check(navigation.Items.Count == 5 && tabs.Select(tab => tab.Header?.ToString()).SequenceEqual(names),
            "Navigation exposes Dashboard, Health, Connections, History and Settings in the expected order");
        check(ReferenceEquals(navigation.SelectedItem, tabs[0]) && tabs[0].IsSelected,
            "A newly created dashboard defaults to the Dashboard page");

        for (var i = 0; i < tabs.Length; i++)
        {
            navigation.SelectedItem = tabs[i];
            window.UpdateLayout();
            Pump();
            check(tabs.Count(tab => tab.IsSelected) == 1 && tabs[i].IsSelected &&
                content.OfType<FrameworkElement>().Count(page => page.IsVisible) == 1 &&
                content[i] is FrameworkElement { IsVisible: true },
                $"Selecting {names[i]} shows one exclusive page with one selected navigation entry");
            check(tabs.All(tab => SelectedIndicatorMatches(tab)),
                $"The {names[i]} navigation selection has the accent underline while other entries remain unselected");
        }

        check(tabs.All(tab => HitBelongsToTab(tab, new Point(3, tab.ActualHeight / 2))),
            "Each navigation entry accepts hit tests in its header padding, not only over the text");

        check(OnPage(tabs[0], Control<TextBlock>(window, "DownloadRateText")) &&
            OnPage(tabs[0], Control<TextBlock>(window, "TodayTotalText")) &&
            OnPage(tabs[0], Control<TextBlock>(window, "DashboardProfileText")),
            "Dashboard retains live traffic, data usage and the compact current-connection summary");
        check(OnPage(tabs[2], Control<ComboBox>(window, "AdapterComboBox")) &&
            OnPage(tabs[2], Control<Button>(window, "ManageProfilesButton")) &&
            OnPage(tabs[2], Control<Button>(window, "AssignProfileButton")) &&
            OnPage(tabs[2], Control<TextBlock>(window, "LinkRateLabel")),
            "Connections retains adapter selection, profile management, assignment and local link-rate details");
        check(OnPage(tabs[3], Control<Button>(window, "ViewHistoryButton")),
            "History retains the existing connection-history action");
        check(OnPage(tabs[4], Control<CheckBox>(window, "StartWithWindowsCheckBox")) &&
            OnPage(tabs[4], Control<TextBlock>(window, "DatabasePathText")),
            "Settings contains startup registration and the existing local database path");
        check(OnPage(tabs[1], Control<TextBlock>(window, "HealthPlaceholderText")) &&
            Text(window, "HealthPlaceholderText") == "Health dashboard coming later" &&
            !LogicalDescendants((DependencyObject)tabs[1].Content).OfType<Button>().Any(),
            "Health remains a placeholder without adding probe controls or health classifications");

        monitoring.SampleNow();
        clock.Advance(TimeSpan.FromSeconds(1));
        counters.Advance(2048, 1024, clock.GetUtcNow());
        monitoring.SampleNow();
        navigation.SelectedItem = tabs[0];
        window.RefreshDashboard();
        Pump();
        var before = monitoring.GetSnapshot();
        var totals = monitoring.GetUsageTotals();
        var baseline = Baseline(monitoring);
        var physicalId = history.GetActiveSession()!.Id;
        var vpnId = vpnHistory.GetActiveSession()!.Id;
        var eventCount = history.GetRecentEvents().Count;
        var captureCount = counters.Captures;
        check(before.SessionDownloaded == 2048 && before.SessionUploaded == 1024 &&
            before.Session is { DownloadBytes: 2048, UploadBytes: 1024 },
            "The navigation fixture records the same deterministic bytes in app and physical session totals");
        check(MirrorsCurrentConnection(window),
            "Dashboard mirrors the current profile, physical network, duration and connection-session usage");
        RenderPreviews(window, tabs, names, System.IO.Path.Combine(root, "navigation-previews"));

        for (var round = 0; round < 3; round++)
        {
            foreach (var tab in tabs)
            {
                navigation.SelectedItem = tab;
                window.UpdateLayout();
                Pump();
            }
        }
        var after = monitoring.GetSnapshot();
        check(tabs.Select(tab => tab.Content).Zip(content).All(pair => ReferenceEquals(pair.First, pair.Second)),
            "Repeated page selection preserves each page instance");
        check(ReferenceEquals(baseline, Baseline(monitoring)) && captureCount == counters.Captures &&
            before.SessionDownloaded == after.SessionDownloaded && before.SessionUploaded == after.SessionUploaded &&
            before.DownloadBytesPerSecond == after.DownloadBytesPerSecond &&
            before.UploadBytesPerSecond == after.UploadBytesPerSecond && totals == monitoring.GetUsageTotals(),
            "Navigation leaves the sampler baseline, live rates and recorded usage unchanged");
        check(history.GetActiveSession()!.Id == physicalId && vpnHistory.GetActiveSession()!.Id == vpnId &&
            history.GetRecentEvents().Count == eventCount,
            "Navigation neither splits physical/VPN sessions nor manufactures connection events");
        check(startup.EnableCalls == 0 && startup.DisableCalls == 0 && !startup.Enabled,
            "Visiting Settings reads startup state without changing registration");

        navigation.SelectedItem = tabs[4];
        var checkbox = Control<CheckBox>(window, "StartWithWindowsCheckBox");
        checkbox.IsChecked = true;
        checkbox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        check(startup.Enabled && checkbox.IsChecked == true && startup.EnableCalls == 1,
            "The Settings startup checkbox retains immediate enable behavior");
        checkbox.IsChecked = false;
        checkbox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        check(!startup.Enabled && checkbox.IsChecked == false && startup.DisableCalls == 1,
            "The Settings startup checkbox retains immediate disable behavior");
        navigation.SelectedItem = tabs[0];
        startup.Enabled = true; // Simulate another settings UI changing registration.
        navigation.SelectedItem = tabs[4];
        Pump();
        var externalEnableVisible = checkbox.IsChecked == true;
        navigation.SelectedItem = tabs[0];
        startup.Enabled = false;
        navigation.SelectedItem = tabs[4];
        Pump();
        check(externalEnableVisible && checkbox.IsChecked == false && startup.EnableCalls == 1 && startup.DisableCalls == 1,
            "Returning to Settings reads externally changed startup state without writing it back");

        navigation.SelectedItem = tabs[2];
        OpenAndCloseDialog<ProfileManagerWindow>(window, Control<Button>(window, "ManageProfilesButton"),
            dialog => Control<TextBlock>(dialog, "HeadingText").Text.Contains("Connection profiles", StringComparison.Ordinal) &&
                Control<ListBox>(dialog, "ProfilesList").Items.OfType<ConnectionProfile>().Any(item => item.Id == profile.Id),
            check, "Connections opens the existing profile manager without replacing its workflow");
        navigation.SelectedItem = tabs[3];
        OpenAndCloseDialog<ConnectionHistoryWindow>(window, Control<Button>(window, "ViewHistoryButton"),
            dialog => Control<DataGrid>(dialog, "SessionsGrid").Items.Count >= 1 &&
                Control<DataGrid>(dialog, "EventsGrid").Items.Count >= 1,
            check, "History opens the existing session and event history window");
        check(history.GetActiveSession()!.Id == physicalId && vpnHistory.GetActiveSession()!.Id == vpnId &&
            monitoring.GetUsageTotals() == totals && ReferenceEquals(baseline, Baseline(monitoring)),
            "Existing profile/history actions keep monitoring state and session identity intact");

        navigation.SelectedItem = tabs[0];
        window.RefreshDashboard();
        Pump();
        check(MirrorsCurrentConnection(window), "Dashboard summaries remain synchronized after navigating away and returning");
        window.Close();
        var previousResult = monitoring.LatestIcmpMeasurement;
        Wait(() => monitoring.LatestIcmpMeasurement is not null &&
            !ReferenceEquals(previousResult, monitoring.LatestIcmpMeasurement));
        clock.Advance(TimeSpan.FromSeconds(1));
        counters.Advance(4096, 2048, clock.GetUtcNow());
        monitoring.SampleNow();
        check(context.Dashboard is null && monitoring.GetSnapshot().IsRunning &&
            monitoring.GetSnapshot().SessionDownloaded == 6144 && history.GetActiveSession()!.Id == physicalId &&
            vpnHistory.GetActiveSession()!.Id == vpnId,
            "Closing the navigation shell leaves bandwidth, ICMP and physical/VPN monitoring running in the tray");
        context.ShowDashboard();
        var recreated = context.Dashboard!;
        StopViewTimer(recreated);
        recreated.RefreshDashboard();
        Pump();
        check(windowsCreated == 2 && !ReferenceEquals(window, recreated) &&
            Control<TabItem>(recreated, "DashboardTab").IsSelected &&
            Text(recreated, "DashboardProfileText") == "Navigation ISP — Owned router" && MirrorsCurrentConnection(recreated),
            "Tray Open recreates the navigation shell on Dashboard with current monitoring state");
        context.Exit();
        icmp.Completion.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        check(exits == 1 && !monitoring.GetSnapshot().IsRunning && history.GetActiveSession() is null &&
            vpnHistory.GetActiveSession() is null,
            "Tray Exit still stops the navigation shell and closes physical/VPN history cleanly");
        CheckAssignmentFromConnections(root, adapterId, check);
    }

    private static void CheckAssignmentFromConnections(string root, string adapterId, Action<bool, string> check)
    {
        var store = new UsageStore(System.IO.Path.Combine(root, "navigation-assignment"));
        var profiles = new ProfileStore(store);
        var existing = profiles.CreateProfile("Existing ISP", "Existing device");
        var clock = new NavigationClock();
        using var monitoring = new MonitoringService(store, new OwnedIdentity(adapterId),
            vpnVerifier: new VpnRouteVerifier(new VpnDetectionService(new OwnedTunnel()), new OwnedRoutes()),
            networkMonitor: new OwnedCounters(adapterId, clock.GetUtcNow()), timeProvider: clock,
            icmpMonitor: new IcmpMonitor(new OwnedPing()));
        using var context = new TrayApplicationContext(Application.Current, monitoring,
            dashboardFactory: service => new MainWindow(service, new OwnedStartup())
                { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 }, shutdownAction: () => { });
        context.Start();
        StopSampler(monitoring);
        var window = context.Dashboard!;
        StopViewTimer(window);
        Wait(() => monitoring.GetSnapshot().Connection.CanAssign && monitoring.GetSnapshot().Session is not null);
        window.RefreshDashboard();
        var sessionId = monitoring.GetSnapshot().Session!.Id;
        Control<TabControl>(window, "MainNavigation").SelectedItem = Control<TabItem>(window, "ConnectionsTab");
        Pump();
        var button = Control<Button>(window, "AssignProfileButton");
        check(button.IsVisible && Text(window, "CurrentProfileText") == "Unassigned network",
            "Connections exposes the existing assignment action for a valid unassigned physical network");
        OpenAndCloseDialog<ProfileManagerWindow>(window, button, dialog =>
        {
            var assignment = Control<Button>(dialog, "AssignNetworkButton");
            var correctWorkflow = Text(dialog, "HeadingText") == "Assign a connection profile" &&
                Text(dialog, "AssignmentText").Contains("NAVIGATION_NETWORK", StringComparison.Ordinal) &&
                assignment.IsVisible && assignment.IsEnabled;
            assignment.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            return correctWorkflow && profiles.ResolveProfile("WiFi", "NAVIGATION_NETWORK")?.Id == existing.Id;
        }, check, "Connections assignment opens the existing workflow and maps the network to an existing profile");
        Wait(() => monitoring.GetSnapshot().Connection.Profile?.Id == existing.Id);
        window.RefreshDashboard();
        Control<TabControl>(window, "MainNavigation").SelectedItem = Control<TabItem>(window, "DashboardTab");
        Pump();
        check(monitoring.GetSnapshot().Session is { } current && current.Id == sessionId && current.ProfileId == existing.Id &&
            profiles.GetProfiles().Count == 1 && profiles.GetMappings(existing.Id).Count == 1 &&
            Text(window, "DashboardProfileText") == "Existing ISP — Existing device",
            "Assignment preserves the active physical session and updates Dashboard without creating a duplicate profile");
        context.Exit();
    }

    private static bool SelectedIndicatorMatches(TabItem tab)
    {
        if (tab.Template.FindName("ActiveIndicator", tab) is not Border { Background: SolidColorBrush brush })
            return false;
        return tab.IsSelected ? brush.Color == Color.FromRgb(56, 189, 248) : brush.Color.A == 0;
    }

    private static bool HitBelongsToTab(TabItem tab, Point point)
    {
        var current = VisualTreeHelper.HitTest(tab, point)?.VisualHit;
        while (current is not null)
        {
            if (ReferenceEquals(current, tab)) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private static void RenderPreviews(MainWindow window, TabItem[] tabs, string[] names, string directory)
    {
        System.IO.Directory.CreateDirectory(directory);
        var navigation = Control<TabControl>(window, "MainNavigation");
        foreach (var size in new[] { (Width: 920, Height: 740), (Width: 760, Height: 600) })
        {
            window.Width = size.Width;
            window.Height = size.Height;
            for (var i = 0; i < tabs.Length; i++)
            {
                navigation.SelectedItem = tabs[i];
                window.UpdateLayout();
                Pump();
                var content = (FrameworkElement)window.Content;
                var margin = content.Margin;
                var width = (int)Math.Ceiling(content.ActualWidth + margin.Left + margin.Right);
                var height = (int)Math.Ceiling(content.ActualHeight + margin.Top + margin.Bottom);
                var offset = VisualTreeHelper.GetOffset(content);
                // VisualBrush's default viewbox follows the painted descendant bounds.
                // Sparse pages must retain their layout whitespace instead of stretching
                // those painted bounds to fill the whole window content rectangle.
                var contentBrush = new VisualBrush(content)
                {
                    ViewboxUnits = BrushMappingMode.Absolute,
                    // The arranged root's visual offset already includes its margin;
                    // crop that offset here, then apply the margin once below.
                    Viewbox = new Rect(offset.X, offset.Y, content.ActualWidth, content.ActualHeight),
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top,
                };
                var drawing = new DrawingVisual();
                using (var context = drawing.RenderOpen())
                {
                    context.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
                    context.DrawRectangle(contentBrush, null,
                        new Rect(margin.Left, margin.Top, content.ActualWidth, content.ActualHeight));
                }
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(drawing);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = System.IO.File.Create(System.IO.Path.Combine(directory,
                    $"{size.Width}x{size.Height}-{names[i].ToLowerInvariant()}.png"));
                encoder.Save(output);
            }
        }
        window.Width = 920;
        window.Height = 740;
        navigation.SelectedItem = tabs[0];
        window.UpdateLayout();
        Pump();
    }

    private static bool MirrorsCurrentConnection(MainWindow window) =>
        Text(window, "DashboardProfileText") == Text(window, "CurrentProfileText") &&
        Text(window, "DashboardNetworkText") == Text(window, "CurrentNetworkText") &&
        Text(window, "DashboardSessionText") == Text(window, "CurrentSessionText") &&
        Text(window, "DashboardSessionUsageText") == Text(window, "CurrentSessionUsageText");

    private static bool OnPage(TabItem tab, DependencyObject control) =>
        tab.Content is DependencyObject root && LogicalDescendants(root).Contains(control);

    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in LogicalDescendants(child))
                yield return descendant;
    }

    private static void OpenAndCloseDialog<T>(MainWindow window, Button button,
        Func<T, bool> validate, Action<bool, string> check, string description) where T : Window
    {
        var seen = false;
        var valid = false;
        Exception? error = null;
        var operation = window.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
        {
            var dialog = window.OwnedWindows.OfType<T>().SingleOrDefault();
            if (dialog is null) return;
            seen = true;
            dialog.Opacity = 0;
            dialog.ShowActivated = false;
            dialog.ShowInTaskbar = false;
            try { valid = validate(dialog); }
            catch (Exception exception) { error = exception; }
            finally { if (dialog.IsVisible) dialog.Close(); }
        }));
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        operation.Abort();
        if (error is not null) throw new InvalidOperationException(description, error);
        check(seen && valid, description);
    }

    private static T Control<T>(Window window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"Missing navigation control {name}.");

    private static string Text(Window window, string name) => Control<TextBlock>(window, name).Text;
    private static object? Baseline(MonitoringService monitoring) =>
        typeof(MonitoringService).GetField("_previousSnapshot", PrivateInstance)!.GetValue(monitoring);
    private static void StopSampler(MonitoringService monitoring) =>
        ((System.Threading.Timer)typeof(MonitoringService).GetField("_timer", PrivateInstance)!.GetValue(monitoring)!)
            .Change(Timeout.Infinite, Timeout.Infinite);
    private static void StopViewTimer(MainWindow window) =>
        ((DispatcherTimer)typeof(MainWindow).GetField("_timer", PrivateInstance)!.GetValue(window)!).Stop();

    private static void Wait(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Owned navigation state did not become ready.");
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

    private sealed class OwnedIdentity(string adapterId) : INetworkIdentityService
    {
        public event EventHandler? NetworkChanged { add { } remove { } }
        public NetworkIdentityResult GetCurrentNetwork() => new(NetworkIdentityAvailability.Available,
            new NetworkIdentity("WiFi", "NAVIGATION_NETWORK", "NAVIGATION_NETWORK", adapterId), null);
        public void Dispose() { }
    }

    private sealed class OwnedStartup : IWindowsStartupRegistration
    {
        public bool Enabled;
        public int EnableCalls;
        public int DisableCalls;
        public bool IsEnabled() => Enabled;
        public void Enable() { EnableCalls++; Enabled = true; }
        public void Disable() { DisableCalls++; Enabled = false; }
    }

    private sealed class OwnedPing : IPingSource
    {
        public Task<IcmpPingReply> SendAsync(IPAddress target, TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new IcmpPingReply(IPStatus.Success, 12));
        }
    }

    private sealed class NavigationClock : TimeProvider
    {
        private long _utcTicks = DateTimeOffset.UtcNow.Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _utcTicks, duration.Ticks);
    }

    private sealed class OwnedCounters(string adapterId, DateTimeOffset initial) : INetworkMonitor
    {
        private NetworkSnapshot _snapshot = new(1000, 500, initial);
        public int Captures { get; private set; }
        public IReadOnlyList<AdapterInfo> GetActiveAdapters() =>
            [new(adapterId, "Owned navigation adapter", "Injected physical adapter", "Wireless80211", 86_000_000, "192.0.2.10", true)];
        public NetworkSnapshot? Capture(string requestedAdapter)
        {
            Captures++;
            return requestedAdapter == adapterId ? _snapshot : null;
        }
        public void Advance(long downloaded, long uploaded, DateTimeOffset timestamp) =>
            _snapshot = new(_snapshot.BytesReceived + downloaded, _snapshot.BytesSent + uploaded, timestamp);
    }

    private sealed class OwnedTunnel : IVpnInterfaceSource
    {
        public static readonly Guid Id = Guid.Parse("dbcedd3a-8121-4bca-96b9-a5c7eefc35de");
        public VpnInterfaceInventory Read() => new(
            [new VpnInterfaceEvidence
            {
                InterfaceIndex = 7001, InterfaceGuid = Id, InterfaceName = "Owned navigation VPN",
                Description = "Injected navigation tunnel", InterfaceType = 53, DriverComponentId = "wintun",
                MediaConnectState = 1, OperationalStatus = OperationalStatus.Up,
            }], []);
    }

    private sealed class OwnedRoutes : IVpnRouteSource
    {
        public VpnRouteInventory Read(AddressFamily family) => family == AddressFamily.InterNetwork
            ? new(family, [new VpnRouteEvidence(7001, OwnedTunnel.Id, IPAddress.Any, 0,
                IPAddress.Parse("192.0.2.1"), 1, 1, true)], Availability: IpFamilyAvailability.Usable)
            : new(family, [], Availability: IpFamilyAvailability.Unavailable);
    }
}
