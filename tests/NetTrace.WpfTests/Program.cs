using System.Diagnostics;
using System.Reflection;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using NetTrace;
using NetTrace.Models;
using NetTrace.Services;
using Microsoft.Data.Sqlite;

internal static class Program
{
    private static int _passed;
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Dictionary<MainWindow, TrayApplicationContext> OwnedContexts = new();

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--background-exit-child")
            return RunBackgroundExitChild(args[1], args[2]);
        if (args.Length >= 3 && args[0] == "--launch-child")
            return RunLaunchChild(args[1], args[2], args.Skip(3).ToArray());
        var ownedRoot = System.IO.Path.Combine(AppContext.BaseDirectory, "owned-data-" + Guid.NewGuid().ToString("N"));
        MainWindow? window = null;
        try
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var adapter = new NetworkMonitor().GetActiveAdapters().FirstOrDefault(a => a.Type is "Wireless80211" or "Ethernet")
                ?? throw new InvalidOperationException("The physical-adapter smoke requires an active Wi-Fi or Ethernet adapter.");
            var store = new UsageStore(ownedRoot);
            var history = new ConnectionHistoryStore(store);
            var profiles = new ProfileStore(store);
            var profile = profiles.CreateProfile("DITO", "D2 Router");
            profiles.AssignMapping(profile.Id, "WiFi", "SMOKE_A");
            var fake = new FakeIdentity(Network("SMOKE_A", adapter.Id));
            window = OwnedWindow(store, fake);
            var initialPowerLifecycle = Power(window);
            Check(initialPowerLifecycle.IsRegistered,
                "Owned WPF HWND registers native suspend/resume notifications successfully");
            Await(() => Tracker(window).Current?.NetworkIdentifier == "SMOKE_A");
            Check(Text(window, "CurrentProfileText") == "DITO — D2 Router" &&
                Text(window, "CurrentSessionText") != "No active session", "Current profile and session UI bind to recognized connection");
            Check(((Button)window.FindName("AssignProfileButton")).Visibility == Visibility.Collapsed,
                "Recognized connection hides assignment workflow");
            var duplicateBlocked = false;
            var duplicateIdentity = new FakeIdentity(Network("SMOKE_A", adapter.Id));
            try { _ = new MonitoringService(store, duplicateIdentity, reachabilityMonitor: TestReachability.Create()); }
            catch (InvalidOperationException exception) { duplicateBlocked = exception.Message.Contains("already monitoring", StringComparison.Ordinal); }
            Check(duplicateBlocked && history.GetActiveSession() is { EndedAtUtc: null },
                "A second same-thread window is blocked without recovering the live session");
            Check(duplicateIdentity.Disposed,
                "Rejected duplicate window disposes its injected identity service");
            var beforeRejectedSession = history.GetActiveSession();
            var beforeRejectedEvents = history.GetRecentEvents().Count;
            Exception? threadedFailure = null;
            var threadedIdentity = new FakeIdentity(Network("SMOKE_A", adapter.Id));
            var duplicateThread = new Thread(() =>
            {
                try { _ = new MonitoringService(store, threadedIdentity, reachabilityMonitor: TestReachability.Create()); }
                catch (Exception exception) { threadedFailure = exception; }
            });
            duplicateThread.SetApartmentState(ApartmentState.STA);
            duplicateThread.Start();
            Check(duplicateThread.Join(TimeSpan.FromSeconds(5)) &&
                threadedFailure is InvalidOperationException && threadedIdentity.Disposed &&
                history.GetActiveSession() == beforeRejectedSession && history.GetRecentEvents().Count == beforeRejectedEvents,
                "Second STA-thread window cannot recover or modify the live session");
            var factoryRejected = false;
            var rejectedFactoryIdentity = new FakeIdentity(Network("SMOKE_A", adapter.Id));
            try { _ = MonitoringService.CreateForDataRoot(ownedRoot, rejectedFactoryIdentity, reachabilityMonitor: TestReachability.Create()); }
            catch (InvalidOperationException exception) { factoryRejected = exception.Message.Contains("already monitoring", StringComparison.Ordinal); }
            Check(factoryRejected && rejectedFactoryIdentity.Disposed &&
                history.GetActiveSession() == beforeRejectedSession && history.GetRecentEvents().Count == beforeRejectedEvents,
                "Startup factory rejects duplicate ownership before database initialization or recovery");
            var firstId = Tracker(window).Current!.Id;
            Tick(window);
            Check(Field<NetworkSnapshot?>(window, "_previousSnapshot") is not null &&
                Tracker(window).Current!.DownloadBytes == 0, "First physical sample establishes baseline without manufacturing usage");
            PrimeDelta(window, adapter.Id, 1000, 500);
            Tick(window);
            Check(Tracker(window).Current!.DownloadBytes >= 1000 &&
                Tracker(window).Current!.UploadBytes >= 500 &&
                Tracker(window).Current!.DownloadBytes == Field<long>(window, "_sessionDownloaded") &&
                Tracker(window).Current!.UploadBytes == Field<long>(window, "_sessionUploaded"),
                "Dashboard and session consume exactly the same physical delta");

            var preservedBaseline = Field<NetworkSnapshot?>(window, "_previousSnapshot");
            profiles.UpdateProfile(profile.Id, "DITO", "Home Router");
            Refresh(window);
            Await(() => Text(window, "CurrentProfileText") == "DITO — Home Router");
            Check(Tracker(window).Current!.Id == firstId &&
                ReferenceEquals(preservedBaseline, Field<NetworkSnapshot?>(window, "_previousSnapshot")),
                "Profile rename preserves active session and sampler baseline");
            Check(history.GetRecentEvents().Count(e => e.EventType == "ProfileChanged") == 1,
                "Profile rename records one meaningful event");

            fake.Set(Network("SMOKE_B", adapter.Id));
            Await(() => Tracker(window).Current?.NetworkIdentifier == "SMOKE_B");
            Check(Tracker(window).Current!.Id != firstId && Tracker(window).Current!.DownloadBytes == 0 &&
                Field<NetworkSnapshot?>(window, "_previousSnapshot") is null,
                "SSID change on the same adapter starts a clean session and resets sampling baseline");
            Check(history.GetRecentSessions().Single(s => s.Id == firstId).EndReason == "NetworkChanged",
                "Old SSID session ends with NetworkChanged");
            var beforeBaseline = Field<long>(window, "_sessionDownloaded");
            Tick(window);
            Check(Field<long>(window, "_sessionDownloaded") == beforeBaseline &&
                Tracker(window).Current!.DownloadBytes == 0, "First sample after same-adapter SSID transition attributes no stale traffic");

            // Identity transitions must reach the core even while no dashboard work is pumped.
            var expectedCount = history.GetRecentSessions().Count + 3;
            foreach (var ssid in new[] { "SMOKE_A", "SMOKE_B", "SMOKE_A" })
            {
                var previous = Monitor(window).LastObservationSequence;
                fake.Set(Network(ssid, adapter.Id));
                WaitWithoutUi(() => Monitor(window).LastObservationSequence > previous);
            }
            Await(() => history.GetRecentSessions().Count == expectedCount);
            Check(history.GetRecentSessions().Take(3).Select(s => s.NetworkIdentifier).SequenceEqual(
                new[] { "SMOKE_A", "SMOKE_B", "SMOKE_A" }),
                "Queued A to B to A observations retain all physical transitions");
            var eventCount = history.GetRecentEvents().Count(e => !e.EventType.StartsWith("Vpn", StringComparison.Ordinal));
            fake.Set(Network("SMOKE_A", adapter.Id));
            AwaitObservation(window);
            Check(history.GetRecentEvents().Count(e => !e.EventType.StartsWith("Vpn", StringComparison.Ordinal)) == eventCount && history.GetRecentSessions().Count == expectedCount,
                "Repeated native identity observation creates no session or event duplicates");

            var sessionBeforeManual = Tracker(window).Current!;
            var fakeAdapter = new AdapterInfo("smoke-virtual-interface", "Virtual smoke adapter", "Owned fake selection", "Ethernet", 0, "—", false);
            typeof(MonitoringService).GetField("_adapters", PrivateInstance)!.SetValue(Service(window),
                Service(window).GetSnapshot().Adapters.Concat(new[] { fakeAdapter }).ToArray());
            var combo = (ComboBox)window.FindName("AdapterComboBox");
            combo.ItemsSource = new[] { fakeAdapter };
            combo.SelectedItem = fakeAdapter;
            Tick(window);
            Check(Tracker(window).Current!.DownloadBytes == sessionBeforeManual.DownloadBytes &&
                Text(window, "CurrentSessionUsageText").Contains("Select the current connection's adapter", StringComparison.Ordinal),
                "Manual nonphysical adapter selection adds no physical-session bytes and shows guidance");
            Check(!Tracker(window).RecordSample(sessionBeforeManual.Id, fakeAdapter.Id, long.MaxValue,
                DateTimeOffset.UtcNow, 9000, 9000), "Virtual-interface sample is rejected by physical-session attribution");
            var beforeIdleConfirmation = history.GetActiveSession()!.LastConfirmedAtUtc;
            typeof(MonitoringService).GetField("_lastFlush", PrivateInstance)!.SetValue(Service(window), DateTimeOffset.Now.AddSeconds(-31));
            Tick(window);
            Await(() => history.GetActiveSession()!.LastConfirmedAtUtc > beforeIdleConfirmation);
            Check(history.GetActiveSession()!.DownloadBytes == Tracker(window).Current!.DownloadBytes &&
                Tracker(window).Current!.DownloadBytes == sessionBeforeManual.DownloadBytes,
                "Thirty-second batch refresh confirms idle manual-selection connection without attributing virtual traffic");
            Invoke(window, "LoadAdapters", adapter.Id);

            var mappingCount = profiles.GetMappings(profile.Id).Count;
            fake.Set(new NetworkIdentityResult(NetworkIdentityAvailability.Unavailable,
                new NetworkIdentity("WiFi", "", "", adapter.Id), "Windows denied SSID access. Enable location access for desktop apps in Windows Settings."));
            Await(() => Text(window, "CurrentProfileText") == "Network identity unavailable");
            Tick(window);
            Check(Tracker(window).Current is null && ((Button)window.FindName("AssignProfileButton")).Visibility == Visibility.Collapsed &&
                Text(window, "CurrentNetworkText").Contains("Windows denied", StringComparison.Ordinal),
                "Permission failure closes identity observation and hides fake assignment");
            Check(profiles.GetMappings(profile.Id).Count == mappingCount &&
                history.GetRecentSessions().All(s => !string.IsNullOrEmpty(s.NetworkIdentifier)) &&
                Text(window, "StatusText") == "Monitoring", "SSID failure preserves bandwidth dashboard and creates no fake mapping or session");

            fake.Set(Network("SMOKE_UNASSIGNED", adapter.Id));
            Await(() => Tracker(window).Current?.NetworkIdentifier == "SMOKE_UNASSIGNED");
            var unassignedId = Tracker(window).Current!.Id;
            Check(Tracker(window).Current!.ProfileId is null &&
                ((Button)window.FindName("AssignProfileButton")).Visibility == Visibility.Visible,
                "Valid unknown connection gets a nullable-profile session and assignment workflow");
            profiles.AssignMapping(profile.Id, "WiFi", "SMOKE_UNASSIGNED");
            Refresh(window);
            Await(() => Tracker(window).Current?.ProfileId == profile.Id);
            Check(Tracker(window).Current!.Id == unassignedId &&
                history.GetRecentEvents().Count(e => e.EventType == "ProfileAssigned") == 1 &&
                history.GetRecentSessions().Where(s => s.NetworkIdentifier == "SMOKE_B").All(s => s.ProfileId is null),
                "Assignment updates only the active session and leaves completed unknown history unchanged");

            var handle = Power(window).Handle;
            Tick(window);
            PrimeDelta(window, adapter.Id, 400, 200);
            Tick(window);
            var retainedDownloaded = Field<long>(window, "_pendingDownloaded");
            Sql(store.DatabasePath, "CREATE TRIGGER fail_lifecycle_usage BEFORE INSERT ON daily_usage BEGIN SELECT RAISE(ABORT, 'owned usage save failure'); END;");
            SendMessage(handle, 0x0218, new IntPtr(4), IntPtr.Zero);
            SendMessage(handle, 0x0218, new IntPtr(4), IntPtr.Zero);
            Check(Tracker(window).Current is null && Tracker(window).IsSleeping &&
                Text(window, "CurrentSessionText") == "Paused for sleep" &&
                history.GetRecentEvents().Count(e => e.EventType == "Sleep") == 1,
                "Owned suspend notification ends session once and pauses UI");
            Check(Field<long>(window, "_pendingDownloaded") == retainedDownloaded &&
                Text(window, "SessionMessageText").Contains("Usage history could not be saved", StringComparison.Ordinal),
                "Daily-usage persistence failure retains its batch and does not break sleep lifecycle");
            Sql(store.DatabasePath, "DROP TRIGGER fail_lifecycle_usage;");
            Invoke(window, "FlushPending");
            Check(Field<long>(window, "_pendingDownloaded") == 0,
                "Retained failed usage batch succeeds on retry without double counting");
            SendMessage(handle, 0x0218, new IntPtr(18), IntPtr.Zero);
            SendMessage(handle, 0x0218, new IntPtr(7), IntPtr.Zero);
            // This fixture drives samples manually; pause before waiting for recognition
            // so an automatic tick cannot establish a baseline ahead of the assertion.
            StopSampler(window);
            Await(() => Tracker(window).Current is { } resumed && resumed.Id != unassignedId);
            Field<DispatcherTimer>(window, "_timer").Stop();
            Check(Tracker(window).Current!.NetworkIdentifier == "SMOKE_UNASSIGNED" &&
                Tracker(window).Current!.DownloadBytes == 0 &&
                history.GetRecentSessions().Single(s => s.Id == unassignedId).EndReason == "Sleep" &&
                history.GetRecentEvents().Count(e => e.EventType == "Resume") == 1,
                "Automatic plus user-input resume creates one new same-network session and one Resume event");
            Check(Field<NetworkSnapshot?>(window, "_previousSnapshot") is null,
                "Resume establishes fresh sampler baseline");

            var historyWindow = new ConnectionHistoryWindow(history) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            _ = new WindowInteropHelper(historyWindow).EnsureHandle();
            Check(((DataGrid)historyWindow.FindName("SessionsGrid")).Items.Count == history.GetRecentSessions().Count &&
                ((DataGrid)historyWindow.FindName("EventsGrid")).Items.Count == history.GetRecentEvents().Count &&
                ((TextBlock)historyWindow.FindName("HistoryMessageText")).Text.Contains("local time", StringComparison.Ordinal),
                "Minimal history window loads persisted sessions and events in local display time");
            historyWindow.Close();

            Tick(window);
            PrimeDelta(window, adapter.Id, 700, 300);
            Tick(window);
            var expectedDownload = Field<long>(window, "_sessionDownloaded");
            var expectedUpload = Field<long>(window, "_sessionUploaded");
            var normalSessionId = Tracker(window).Current!.Id;
            CloseRun(window);
            Check(!initialPowerLifecycle.IsRegistered,
                "Normal close unregisters native suspend/resume notifications and clears registration state");
            var normalSession = history.GetRecentSessions().Single(s => s.Id == normalSessionId);
            var today = DateOnly.FromDateTime(DateTime.Now);
            Check(history.GetActiveSession() is null && normalSession.EndReason == "ApplicationExit" &&
                normalSession.LastConfirmedAtUtc == normalSession.EndedAtUtc,
                "Explicit application exit ends session cleanly and confirms final boundary");
            Check(store.GetTotals(today, today) == (expectedDownload, expectedUpload),
                "Normal close flushes exactly the dashboard's pending usage totals");

            var normalRecoveryCount = history.GetRecentEvents().Count(e => e.EventType == "SessionRecoveredAfterUnexpectedExit");
            fake = new FakeIdentity(Network("SMOKE_UNASSIGNED", adapter.Id));
            window = OwnedWindow(store, fake);
            Await(() => Tracker(window).Current is not null);
            Check(history.GetRecentEvents().Count(e => e.EventType == "SessionRecoveredAfterUnexpectedExit") == normalRecoveryCount,
                "Clean restart starts a new session without a false crash-recovery event");
            Tick(window);
            PrimeDelta(window, adapter.Id, 800, 400);
            Tick(window);
            Invoke(window, "FlushPending");
            var stale = history.GetActiveSession()!;
            SimulateUnexpectedExit(window);
            Check(history.GetActiveSession()?.Id == stale.Id, "Unexpected-exit simulation leaves one unfinished persisted session");
            fake = new FakeIdentity(Network("SMOKE_UNASSIGNED", adapter.Id));
            window = OwnedWindow(store, fake);
            Await(() => Tracker(window).Current is not null);
            var recovered = history.GetRecentSessions().Single(s => s.Id == stale.Id);
            Check(recovered.EndReason == "UnexpectedExit" && recovered.EndedAtUtc == stale.LastConfirmedAtUtc &&
                recovered.DownloadBytes == stale.DownloadBytes && Tracker(window).Current!.Id != stale.Id,
                "Restart recovers stale session at last persisted confirmation and preserves its usage");
            Check(history.GetRecentSessions().Count(s => s.EndedAtUtc is null) == 1 &&
                history.GetRecentEvents().Count(e => e.EventType == "SessionRecoveredAfterUnexpectedExit") == normalRecoveryCount + 1,
                "Recovery creates one event and leaves exactly one new active session");
            CloseRun(window);
            window = null;
            CheckLeaseFailurePaths(ownedRoot, adapter.Id);
            CheckVpnIntegration(ownedRoot, adapter.Id);
            CheckVpnLifecycle(ownedRoot, adapter.Id);
            CheckBackgroundLifecycle(ownedRoot, adapter.Id);
            CheckActualBackgroundProcessExit(ownedRoot, adapter.Id);
            CheckStartupLaunchModes(ownedRoot, adapter.Id);
            CheckStartupSetting(ownedRoot, adapter.Id);
            CheckActualLaunchProcess(ownedRoot, adapter.Id, background: false);
            CheckActualLaunchProcess(ownedRoot, adapter.Id, background: true);
            CheckIcmpBackgroundLifecycle(ownedRoot, adapter.Id);
            NavigationChecks.Run(ownedRoot, adapter.Id, Check);
            IcmpPresentationChecks.Run(ownedRoot, adapter.Id, Check);
            IcmpStabilityChartChecks.Run(ownedRoot, adapter.Id, Check);
            IcmpPacketLossChartChecks.Run(ownedRoot, adapter.Id, Check);
            IcmpQualityChecks.Run(ownedRoot, adapter.Id, Check);
            InternetReachabilityChecks.Run(ownedRoot, adapter.Id, Check);
            Console.WriteLine($"{_passed}/{_passed} WPF integration checks passed. Isolated data: {ownedRoot}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL after {_passed} checks: {exception}");
            try { if (window is not null) CloseRun(window); } catch { }
            return 1;
        }
    }

    private static void CheckLeaseFailurePaths(string ownedRoot, string adapterId)
    {
        var crossThreadDatabase = System.IO.Path.Combine(ownedRoot, "cross-thread", "usage.db");
        var crossThreadLease = Task.Run(() => new DatabaseRunLease(crossThreadDatabase)).GetAwaiter().GetResult();
        Check(crossThreadLease.IsHeld,
            "Run lease can be acquired by a worker thread without UI-thread ownership");
        Task.Run(crossThreadLease.Dispose).GetAwaiter().GetResult();
        using (var next = new DatabaseRunLease(crossThreadDatabase))
            Check(next.IsHeld && !crossThreadLease.IsHeld,
                "Cross-thread lease disposal releases ownership for the next instance");

        var failedRoot = System.IO.Path.Combine(ownedRoot, "constructor-failure");
        var failedStore = new UsageStore(failedRoot);
        Sql(failedStore.DatabasePath, "ALTER TABLE daily_usage RENAME TO owned_inaccessible_usage;");
        var failedIdentity = new FakeIdentity(Network("SMOKE_FAILURE", adapterId));
        var failedView = OwnedWindow(failedStore, failedIdentity);
        Await(() => Tracker(failedView).Current is not null);
        Check(Text(failedView, "SessionMessageText").Contains("Usage history could not be read", StringComparison.Ordinal) &&
            Service(failedView).GetSnapshot().IsRunning && !failedIdentity.Disposed,
            "A dashboard usage-read failure reports a useful message without stopping application-owned monitoring");
        CloseRun(failedView);
        using (var next = new DatabaseRunLease(failedStore.DatabasePath))
            Check(failedIdentity.Disposed && next.IsHeld,
                "Application shutdown after a dashboard read failure disposes native service and releases ownership");
        Sql(failedStore.DatabasePath, "ALTER TABLE owned_inaccessible_usage RENAME TO daily_usage;");
        var repairedWindow = OwnedWindow(failedStore, new FakeIdentity(Network("SMOKE_FAILURE", adapterId)));
        Await(() => Tracker(repairedWindow).Current?.NetworkIdentifier == "SMOKE_FAILURE");
        CloseRun(repairedWindow);
        Check(new ConnectionHistoryStore(failedStore).GetActiveSession() is null,
            "Repaired startup succeeds after constructor failure and closes its session normally");

        var invalidRoot = System.IO.Path.Combine(ownedRoot, "initialization-failure");
        var invalidPath = System.IO.Path.Combine(invalidRoot, "NetTrace", "usage.db");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(invalidPath)!);
        System.IO.File.WriteAllText(invalidPath, "Owned invalid SQLite database for startup failure coverage.");
        var invalidIdentity = new FakeIdentity(Network("SMOKE_FAILURE", adapterId));
        Exception? initializationFailure = null;
        try { _ = MonitoringService.CreateForDataRoot(invalidRoot, invalidIdentity, reachabilityMonitor: TestReachability.Create()); }
        catch (Exception exception) { initializationFailure = exception; }
        using (var next = new DatabaseRunLease(invalidPath))
            Check(initializationFailure is InvalidOperationException && invalidIdentity.Disposed && next.IsHeld,
                "Database initialization failure releases startup ownership and injected resources");
        var throwingIdentity = new FakeIdentity(Network("SMOKE_FAILURE", adapterId)) { ThrowOnDispose = true };
        Exception? throwingFailure = null;
        try { _ = MonitoringService.CreateForDataRoot(invalidRoot, throwingIdentity, reachabilityMonitor: TestReachability.Create()); }
        catch (Exception exception) { throwingFailure = exception; }
        using (var next = new DatabaseRunLease(invalidPath))
            Check(throwingIdentity.Disposed && next.IsHeld &&
                throwingFailure is InvalidOperationException { InnerException: SqliteException } &&
                throwingFailure.Message.Contains("Could not initialize NetTrace history", StringComparison.Ordinal),
                "Startup cleanup failure preserves initialization error and releases ownership");
    }

    private static NetworkIdentityResult Network(string ssid, string adapterId) => new(
        NetworkIdentityAvailability.Available, new NetworkIdentity("WiFi", ssid, ssid, adapterId), null);

    private static void CheckVpnIntegration(string ownedRoot, string adapterId)
    {
        var store = new UsageStore(System.IO.Path.Combine(ownedRoot, "vpn-integration"));
        var vpnStore = new VpnSessionStore(store);
        var history = new ConnectionHistoryStore(store);
        var evidence = new OwnedVpnEvidence();
        var routeSource = new OwnedVpnRoutes();
        var identity = new FakeIdentity(Network("SMOKE_VPN_A", adapterId));
        var window = CreateOwnedWindow(store, identity,
            new VpnRouteVerifier(new VpnDetectionService(evidence), routeSource));
        try
        {
            Await(() => Tracker(window).Current?.NetworkIdentifier == "SMOKE_VPN_A");
            var parentId = Tracker(window).Current!.Id;
            var initialPhysicalEvents = history.GetRecentEvents().Where(e => !e.EventType.StartsWith("Vpn", StringComparison.Ordinal)).ToArray();
            Check(vpnStore.GetActiveSession() is null && vpnStore.GetRecentSessions().Count == 0,
                "Injected NoVpn leaves physical WPF session active without creating VPN history");
            evidence.Enabled = true;
            Monitor(window).Refresh();
            Await(() => vpnStore.GetActiveSession() is { RoutingMode: VpnRoutingMode.FullTunnel });
            var firstVpnId = vpnStore.GetActiveSession()!.Id;
            Check(vpnStore.GetActiveSession()!.ConnectionSessionId == parentId && Tracker(window).Current!.Id == parentId,
                "VPN ON creates a child interval without replacing the physical WPF session");
            var connected = history.GetRecentEvents().Single(e => e.EventType == "VpnConnected");
            Check(connected.SessionId == parentId && connected.NetworkIdentifier == "SMOKE_VPN_A" &&
                connected.Message.Contains("Owned WPF tunnel", StringComparison.Ordinal) &&
                connected.Message.Contains("FullTunnel", StringComparison.Ordinal),
                "WPF VPN ON records one connected event with physical parent and readable VPN/routing details");
            var previousConfirmation = vpnStore.GetActiveSession()!.LastConfirmedAtUtc;
            Monitor(window).Refresh();
            Await(() => vpnStore.GetActiveSession()!.LastConfirmedAtUtc > previousConfirmation);
            Check(vpnStore.GetActiveSession()!.Id == firstVpnId && vpnStore.GetRecentSessions().Count == 1,
                "Repeated WPF VPN observations confirm the same child interval");
            Check(history.GetRecentEvents().Count(e => e.EventType.StartsWith("Vpn", StringComparison.Ordinal)) == 1,
                "Repeated identical WPF VPN state adds no duplicate connected or route event");
            routeSource.Split = true;
            Monitor(window).Refresh();
            Await(() => vpnStore.GetActiveSession() is { RoutingMode: VpnRoutingMode.SplitTunnel });
            Check(vpnStore.GetActiveSession()!.Id == firstVpnId && Tracker(window).Current!.Id == parentId,
                "WPF route-mode changes update the active VPN row in place");
            var routeChanged = history.GetRecentEvents().Single(e => e.EventType == "VpnRouteChanged");
            Check(routeChanged.SessionId == parentId && routeChanged.Message.Contains("FullTunnel → SplitTunnel", StringComparison.Ordinal) &&
                routeChanged.Message.Contains("Owned WPF tunnel", StringComparison.Ordinal),
                "WPF routing mode transition records one route-change event with old/new mode and physical parent");
            var splitConfirmation = vpnStore.GetActiveSession()!.LastConfirmedAtUtc;
            Monitor(window).Refresh();
            Await(() => vpnStore.GetActiveSession()!.LastConfirmedAtUtc > splitConfirmation);
            Check(history.GetRecentEvents().Count(e => e.EventType == "VpnRouteChanged") == 1,
                "Repeated SplitTunnel observations do not duplicate WPF routing events");
            evidence.Enabled = false;
            Monitor(window).Refresh();
            Await(() => vpnStore.GetActiveSession() is null);
            Check(vpnStore.GetRecentSessions().Single().EndReason == "VpnDisconnected" &&
                Tracker(window).Current!.Id == parentId && Tracker(window).Current!.DownloadBytes == 0 &&
                Tracker(window).Current!.UploadBytes == 0,
                "VPN OFF closes only the VPN interval and leaves physical byte counters unchanged");
            var disconnected = history.GetRecentEvents().Single(e => e.EventType == "VpnDisconnected");
            Check(disconnected.SessionId == parentId && disconnected.Message.Contains("SplitTunnel", StringComparison.Ordinal) &&
                disconnected.Message.Contains("VpnDisconnected", StringComparison.Ordinal),
                "WPF VPN OFF records one disconnected event with physical parent and end reason");
            Check(history.GetRecentEvents().Where(e => !e.EventType.StartsWith("Vpn", StringComparison.Ordinal)).SequenceEqual(initialPhysicalEvents) &&
                history.GetRecentSessions().Count == 1 && Tracker(window).Current!.Id == parentId,
                "VPN connected/route/off events preserve the exact existing physical event rows and physical session");
            evidence.Enabled = true;
            Monitor(window).Refresh();
            Await(() => vpnStore.GetActiveSession() is not null);
            identity.Set(new(NetworkIdentityAvailability.Disconnected, null, null));
            Await(() => Tracker(window).Current is null);
            Check(vpnStore.GetActiveSession() is null &&
                vpnStore.GetRecentSessions().First().EndReason == "PhysicalSessionEnded",
                "A WPF physical disconnect atomically closes its active VPN interval");
            Check(history.GetRecentEvents().Count(e => e.EventType == "VpnDisconnected" && e.SessionId == parentId) == 2 &&
                history.GetRecentEvents().Count(e => e.EventType == "Disconnected" && e.SessionId == parentId) == 1,
                "Physical disconnect adds one VPN interval-end event alongside the unchanged physical disconnect event");
            identity.Set(Network("SMOKE_VPN_B", adapterId));
            Await(() => Tracker(window).Current?.NetworkIdentifier == "SMOKE_VPN_B" &&
                vpnStore.GetActiveSession() is not null);
            var finalParent = Tracker(window).Current!.Id;
            CloseRun(window);
            Check(history.GetActiveSession() is null && vpnStore.GetActiveSession() is null &&
                vpnStore.GetRecentSessions().First().ConnectionSessionId == finalParent,
                "Normal WPF shutdown quiesces VPN observation then closes physical and VPN intervals");
            Check(history.GetRecentEvents().Count(e => e.EventType == "VpnDisconnected" && e.SessionId == finalParent) == 1,
                "Normal WPF shutdown records exactly one VPN interval-end event for its physical parent");
            using var nextOwner = new DatabaseRunLease(store.DatabasePath);
            Check(nextOwner.IsHeld, "VPN integration shutdown releases database run ownership cleanly");
        }
        finally { CloseRun(window); }
    }

    private sealed class OwnedVpnEvidence : IVpnInterfaceSource
    {
        public volatile bool Enabled;
        public int ReadCount;
        public VpnInterfaceInventory Read()
        {
            Interlocked.Increment(ref ReadCount);
            return new(
            Enabled ? new[] { new VpnInterfaceEvidence {
                InterfaceIndex = 5001, InterfaceGuid = OwnedVpnRoutes.TunnelGuid,
                InterfaceName = "Owned WPF tunnel", Description = "Injected test-only tunnel",
                InterfaceType = 53, DriverComponentId = "wintun", MediaConnectState = 1,
                OperationalStatus = OperationalStatus.Up
            }} : Array.Empty<VpnInterfaceEvidence>(), Array.Empty<VpnRasConnectionEvidence>());
        }
    }

    private static void CheckVpnLifecycle(string ownedRoot, string adapterId)
    {
        var store = new UsageStore(System.IO.Path.Combine(ownedRoot, "vpn-power-lifecycle"));
        var history = new ConnectionHistoryStore(store);
        var vpnStore = new VpnSessionStore(store);
        var evidence = new OwnedVpnEvidence { Enabled = true };
        var identity = new FakeIdentity(Network("SMOKE_VPN_POWER", adapterId));
        var window = OwnedVpnWindow(store, identity, evidence);
        try
        {
            Await(() => vpnStore.GetActiveSession() is not null);
            var firstVpn = vpnStore.GetActiveSession()!;
            var firstParent = Tracker(window).Current!.Id;
            var handle = Power(window).Handle;
            SendMessage(handle, 0x0218, new IntPtr(4), IntPtr.Zero);
            SendMessage(handle, 0x0218, new IntPtr(4), IntPtr.Zero);
            var sleptVpn = vpnStore.GetRecentSessions().Single(s => s.Id == firstVpn.Id);
            var sleptParent = history.GetRecentSessions().Single(s => s.Id == firstParent);
            Check(sleptVpn.EndReason == "Sleep" && sleptParent.EndReason == "Sleep" &&
                sleptVpn.EndedAtUtc == sleptParent.EndedAtUtc && vpnStore.GetActiveSession() is null &&
                Tracker(window).IsSleeping,
                "Native WPF sleep closes VPN with Sleep before its physical parent at the same clean boundary");
            Check(history.GetRecentEvents().Count(e => e.EventType == "VpnDisconnected" && e.SessionId == firstParent) == 1 &&
                history.GetRecentEvents().Count(e => e.EventType == "Sleep" && e.SessionId == firstParent) == 1,
                "Duplicate native suspend broadcasts produce one physical sleep and one VPN disconnect event");
            var asleepReads = Volatile.Read(ref evidence.ReadCount);
            Field<VpnSessionMonitor>(window, "_vpnSessions").Refresh();
            var asleepDeadline = Stopwatch.StartNew();
            while (asleepDeadline.Elapsed < TimeSpan.FromMilliseconds(400)) PumpOnce();
            Check(vpnStore.GetActiveSession() is null && Volatile.Read(ref evidence.ReadCount) == asleepReads,
                "VPN observer remains quiescent during suspend despite an explicit refresh");

            SendMessage(handle, 0x0218, new IntPtr(18), IntPtr.Zero);
            SendMessage(handle, 0x0218, new IntPtr(7), IntPtr.Zero);
            Await(() => Tracker(window).Current is { } current && current.Id != firstParent &&
                vpnStore.GetActiveSession() is { } vpn && vpn.ConnectionSessionId == current.Id);
            Field<DispatcherTimer>(window, "_timer").Stop();
            StopSampler(window);
            var resumedParent = Tracker(window).Current!;
            var resumedVpn = vpnStore.GetActiveSession()!;
            Check(resumedVpn.Id != firstVpn.Id && resumedVpn.ConnectionSessionId == resumedParent.Id &&
                resumedParent.NetworkIdentifier == "SMOKE_VPN_POWER" && resumedVpn.StartedAtUtc >= resumedParent.StartedAtUtc,
                "Native resume redetects the same physical network and VPN into a new child interval");
            Check(Volatile.Read(ref evidence.ReadCount) > asleepReads &&
                history.GetRecentEvents().Count(e => e.EventType == "Resume") == 1 &&
                history.GetRecentEvents().Count(e => e.EventType == "VpnConnected") == 2,
                "Automatic and user-input resume reuse fresh VPN evidence without duplicate resume or connect events");

            SendMessage(handle, 0x0218, new IntPtr(4), IntPtr.Zero);
            evidence.Enabled = false;
            var readsBeforeOffResume = Volatile.Read(ref evidence.ReadCount);
            SendMessage(handle, 0x0218, new IntPtr(18), IntPtr.Zero);
            SendMessage(handle, 0x0218, new IntPtr(7), IntPtr.Zero);
            Await(() => Tracker(window).Current is { } current && current.Id != resumedParent.Id &&
                Volatile.Read(ref evidence.ReadCount) > readsBeforeOffResume);
            Field<DispatcherTimer>(window, "_timer").Stop();
            StopSampler(window);
            Check(vpnStore.GetActiveSession() is null && vpnStore.GetRecentSessions().Count == 2 &&
                history.GetRecentEvents().Count(e => e.EventType == "VpnConnected") == 2,
                "Resume with fresh NoVpn evidence starts the physical session without inventing a VPN interval");
            Check(vpnStore.GetRecentSessions().All(s => s.EndedAtUtc is not null) &&
                history.GetRecentSessions().Count(s => s.EndedAtUtc is null) == 1,
                "Sleep/resume leaves no active VPN child of a closed physical parent");

            evidence.Enabled = true;
            Monitor(window).Refresh();
            Await(() => vpnStore.GetActiveSession() is not null);
            var exitVpn = vpnStore.GetActiveSession()!;
            var exitParent = Tracker(window).Current!.Id;
            CloseRun(window);
            var exitedVpn = vpnStore.GetRecentSessions().Single(s => s.Id == exitVpn.Id);
            var exitedParent = history.GetRecentSessions().Single(s => s.Id == exitParent);
            Check(exitedVpn.EndReason == "ApplicationExit" && exitedVpn.EndedAtUtc == exitedParent.EndedAtUtc &&
                exitedParent.EndReason == "ApplicationExit" && vpnStore.GetActiveSession() is null && history.GetActiveSession() is null,
                "Normal WPF shutdown flushes and closes VPN with ApplicationExit alongside its physical parent");
            Service(window).Stop();
            Check(history.GetRecentEvents().Count(e => e.EventType == "VpnDisconnected" && e.SessionId == exitParent) == 1,
                "Repeated normal shutdown cleanup does not duplicate the VPN interval-end event");
            using (var lease = new DatabaseRunLease(store.DatabasePath))
                Check(lease.IsHeld, "VPN power lifecycle cleanup releases database ownership after all writers stop");
        }
        finally { CloseRun(window); }

        var crashStore = new UsageStore(System.IO.Path.Combine(ownedRoot, "vpn-crash-lifecycle"));
        var crashHistory = new ConnectionHistoryStore(crashStore);
        var crashVpnStore = new VpnSessionStore(crashStore);
        var crashEvidence = new OwnedVpnEvidence { Enabled = true };
        var crashIdentity = new FakeIdentity(Network("SMOKE_VPN_CRASH", adapterId));
        window = OwnedVpnWindow(crashStore, crashIdentity, crashEvidence);
        try
        {
            Await(() => crashVpnStore.GetActiveSession() is not null);
            Field<VpnSessionMonitor>(window, "_vpnSessions").Dispose();
            var staleVpn = crashVpnStore.GetActiveSession()!;
            Monitor(window).Refresh();
            Await(() => Tracker(window).Current!.LastConfirmedAtUtc > staleVpn.LastConfirmedAtUtc);
            Tracker(window).Flush();
            var staleParent = crashHistory.GetActiveSession()!;
            Check(staleParent.LastConfirmedAtUtc > staleVpn.LastConfirmedAtUtc,
                "Crash fixture has an independently newer persisted physical confirmation than its VPN confirmation");
            SimulateUnexpectedExit(window);
            Check(crashVpnStore.GetActiveSession()?.Id == staleVpn.Id && crashHistory.GetActiveSession()?.Id == staleParent.Id,
                "Owned abrupt-exit simulation leaves one unfinished VPN and physical parent without a clean close");
            var restartedEvidence = new OwnedVpnEvidence { Enabled = true };
            // Production recovers in the service constructor before Start admits live observations.
            // Hold that boundary explicitly so an idempotent recovery retry cannot close a fresh session.
            window = OwnedVpnWindow(crashStore, new FakeIdentity(Network("SMOKE_VPN_CRASH", adapterId)), restartedEvidence,
                start: false);
            var recoveredVpn = crashVpnStore.GetRecentSessions().Single(s => s.Id == staleVpn.Id);
            var recoveredParent = crashHistory.GetRecentSessions().Single(s => s.Id == staleParent.Id);
            Check(recoveredVpn.EndedAtUtc == staleVpn.LastConfirmedAtUtc &&
                recoveredVpn.LastConfirmedAtUtc == staleVpn.LastConfirmedAtUtc && recoveredVpn.EndReason == "UnexpectedExit",
                "WPF startup recovery closes stale VPN exactly at its own persisted confirmation without extending downtime");
            Check(recoveredParent.EndedAtUtc == staleParent.LastConfirmedAtUtc && recoveredParent.EndReason == "UnexpectedExit" &&
                recoveredVpn.EndedAtUtc < recoveredParent.EndedAtUtc,
                "VPN crash recovery preserves the independent existing physical last-confirmed recovery boundary");
            var recoveredDisconnect = crashHistory.GetRecentEvents().Single(e => e.EventType == "VpnDisconnected" && e.SessionId == staleParent.Id);
            Check(recoveredDisconnect.OccurredAtUtc == staleVpn.LastConfirmedAtUtc &&
                recoveredDisconnect.Message.Contains("UnexpectedExit", StringComparison.Ordinal),
                "Recovered VPN emits one disconnect at its exact persisted confirmation with explicit crash reason");
            var vpnEventsBeforeRetry = crashHistory.GetRecentEvents().Count(e => e.EventType == "VpnDisconnected" && e.SessionId == staleParent.Id);
            crashHistory.RecoverUnfinishedSessions(DateTimeOffset.UtcNow);
            Check(crashHistory.GetRecentEvents().Count(e => e.EventType == "VpnDisconnected" && e.SessionId == staleParent.Id) == vpnEventsBeforeRetry &&
                crashHistory.GetRecentEvents().Count(e => e.EventType == "SessionRecoveredAfterUnexpectedExit") == 1 &&
                !Service(window).GetSnapshot().IsRunning && Volatile.Read(ref restartedEvidence.ReadCount) == 0,
                "Recovery retry before fresh physical admission adds neither duplicate VPN disconnect nor physical recovery event");
            Service(window).Start();
            StopSampler(window);
            Await(() => crashVpnStore.GetActiveSession() is { } currentVpn && currentVpn.Id != staleVpn.Id &&
                Tracker(window).Current is { } currentParent && currentParent.Id != staleParent.Id &&
                currentVpn.ConnectionSessionId == currentParent.Id);
            var freshVpn = crashVpnStore.GetActiveSession()!;
            Check(Volatile.Read(ref restartedEvidence.ReadCount) > 0 && freshVpn.StartedAtUtc > staleVpn.LastConfirmedAtUtc &&
                freshVpn.StartedAtUtc >= Tracker(window).Current!.StartedAtUtc,
                "Fresh startup VPN evidence creates a new interval on the freshly detected physical parent");
            Check(crashVpnStore.GetRecentSessions().Count(s => s.EndedAtUtc is null) == 1 &&
                crashHistory.GetRecentSessions().Count(s => s.EndedAtUtc is null) == 1,
                "Recovery plus fresh admission leaves exactly one new active VPN and one new active physical parent");
            CloseRun(window);
            Check(crashVpnStore.GetActiveSession() is null && crashHistory.GetActiveSession() is null &&
                crashVpnStore.GetRecentSessions().All(s => s.EndedAtUtc is not null),
                "Recovery and cleanup leave neither multiple active VPNs nor an orphan child of a closed parent");
        }
        finally { CloseRun(window); }
    }

    private static MainWindow OwnedVpnWindow(UsageStore store, FakeIdentity identity, OwnedVpnEvidence evidence, bool start = true)
        => CreateOwnedWindow(store, identity,
            new VpnRouteVerifier(new VpnDetectionService(evidence), new OwnedVpnRoutes()), start);

    private static void CheckBackgroundLifecycle(string ownedRoot, string adapterId)
    {
        var store = new UsageStore(System.IO.Path.Combine(ownedRoot, "background-lifecycle"));
        var history = new ConnectionHistoryStore(store);
        var vpnStore = new VpnSessionStore(store);
        var identity = new FakeIdentity(Network("BACKGROUND_A", adapterId));
        var evidence = new OwnedVpnEvidence { Enabled = true };
        var routes = new OwnedVpnRoutes();
        var shutdownCount = 0;
        var windowCount = 0;
        using var monitoring = new MonitoringService(store, identity,
            vpnVerifier: new VpnRouteVerifier(new VpnDetectionService(evidence), routes),
            icmpMonitor: new IcmpMonitor(new FakePingSource()), reachabilityMonitor: TestReachability.Create());
        monitoring.Start();
        using var context = new TrayApplicationContext(Application.Current, monitoring,
            dashboardFactory: service =>
            {
                windowCount++;
                return new MainWindow(service, new FakeStartupRegistration()) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            }, shutdownAction: () => shutdownCount++);
        context.ShowDashboard();
        var firstWindow = context.Dashboard!;
        OwnedContexts.Add(firstWindow, context);
        Await(() => history.GetActiveSession() is not null && vpnStore.GetActiveSession() is not null);
        var physicalId = history.GetActiveSession()!.Id;
        var vpnId = vpnStore.GetActiveSession()!.Id;
        var snapshot = monitoring.GetSnapshot();
        var initialBaseline = Field<NetworkSnapshot?>(firstWindow, "_previousSnapshot");
        var initialPower = Power(firstWindow);
        var hiddenHandle = initialPower.Handle;
        var notifyIcon = NotifyIcon(context);
        Check((bool)notifyIcon.GetType().GetProperty("Visible")!.GetValue(notifyIcon)!,
            "Application lifetime owns a visible Windows system-tray icon");
        Check(TrayMenuLabels(context).SequenceEqual(new[] { "Open NetTrace", "Exit" }),
            "Tray menu contains only Open NetTrace and Exit");
        Check(Application.Current.ShutdownMode == ShutdownMode.OnExplicitShutdown,
            "Tray lifetime uses explicit application shutdown");

        firstWindow.Close();
        Check(context.Dashboard is null && Application.Current.MainWindow is null && shutdownCount == 0 &&
            monitoring.GetSnapshot().IsRunning && !Application.Current.Dispatcher.HasShutdownStarted,
            "Closing the dashboard destroys its window while the application and monitoring stay alive");
        Check(!Field<DispatcherTimer>(firstWindow, "_timer").IsEnabled &&
            !Application.Current.Windows.OfType<MainWindow>().Any(),
            "Background mode has no dashboard window or dashboard refresh timer");
        var subscribers = typeof(MonitoringService).GetField("Changed", PrivateInstance)!.GetValue(monitoring) as Delegate;
        Check(subscribers is null || subscribers.GetInvocationList().All(handler => !ReferenceEquals(handler.Target, firstWindow)),
            "A closed dashboard detaches its subscription from the application-owned monitor");
        Check(initialPower.IsRegistered && hiddenHandle != IntPtr.Zero,
            "Background mode retains its independently owned native power-notification HWND");
        Check(history.GetActiveSession()!.Id == physicalId && vpnStore.GetActiveSession()!.Id == vpnId &&
            history.GetRecentEvents().All(e => e.EventType != "VpnDisconnected"),
            "Dashboard closure preserves physical/VPN interval IDs without disconnect events");

        Await(() => Field<NetworkSnapshot?>(firstWindow, "_previousSnapshot") is { } current &&
            !ReferenceEquals(current, initialBaseline) &&
            monitoring.GetSnapshot().Session!.LastConfirmedAtUtc > snapshot.Session!.LastConfirmedAtUtc);
        Check(monitoring.GetSnapshot().Session!.Id == physicalId &&
            Field<NetworkSnapshot?>(firstWindow, "_previousSnapshot") is not null,
            "The independent one-second bandwidth sampler continues with no dashboard");
        var beforeVpnConfirmation = vpnStore.GetActiveSession()!.LastConfirmedAtUtc;
        monitoring.RefreshRecognition();
        Await(() => vpnStore.GetActiveSession()!.LastConfirmedAtUtc > beforeVpnConfirmation);
        Check(vpnStore.GetActiveSession()!.Id == vpnId && history.GetActiveSession()!.Id == physicalId,
            "VPN tracking and persistence continue in background mode without replacing sessions");

        var beforeReopen = monitoring.GetSnapshot();
        ClickTrayMenu(context, "Open NetTrace");
        var secondWindow = context.Dashboard!;
        OwnedContexts.Add(secondWindow, context);
        Check(!ReferenceEquals(firstWindow, secondWindow) && windowCount == 2 && secondWindow.IsVisible,
            "Tray Open recreates a fresh dashboard after the previous window was closed");
        Check(monitoring.GetSnapshot().SessionDownloaded >= beforeReopen.SessionDownloaded &&
            monitoring.GetSnapshot().SessionUploaded >= beforeReopen.SessionUploaded &&
            history.GetActiveSession()!.Id == physicalId && vpnStore.GetActiveSession()!.Id == vpnId,
            "Recreated dashboard reuses the existing sampler totals and physical/VPN sessions");
        ClickTrayMenu(context, "Open NetTrace");
        Check(ReferenceEquals(context.Dashboard, secondWindow) && windowCount == 2 &&
            Application.Current.Windows.OfType<MainWindow>().Count() == 1,
            "Repeated Tray Open reuses one existing dashboard");
        secondWindow.WindowState = WindowState.Minimized;
        ClickTrayMenu(context, "Open NetTrace");
        Check(secondWindow.WindowState == WindowState.Normal && ReferenceEquals(context.Dashboard, secondWindow),
            "Tray Open restores the existing minimized dashboard");

        secondWindow.Close();
        SendMessage(hiddenHandle, 0x0218, new IntPtr(4), IntPtr.Zero);
        SendMessage(hiddenHandle, 0x0218, new IntPtr(4), IntPtr.Zero);
        Check(history.GetActiveSession() is null && vpnStore.GetActiveSession() is null &&
            monitoring.GetSnapshot().IsSuspended &&
            history.GetRecentSessions().Single(s => s.Id == physicalId).EndReason == "Sleep" &&
            vpnStore.GetRecentSessions().Single(s => s.Id == vpnId).EndReason == "Sleep",
            "Native suspend closes physical and VPN history normally while no dashboard exists");
        var suspendedCapture = Field<NetworkSnapshot?>(firstWindow, "_previousSnapshot");
        var suspendedDeadline = Stopwatch.StartNew();
        while (suspendedDeadline.Elapsed < TimeSpan.FromMilliseconds(1100)) PumpOnce();
        Check(ReferenceEquals(Field<NetworkSnapshot?>(firstWindow, "_previousSnapshot"), suspendedCapture) &&
            history.GetRecentEvents().Count(e => e.EventType == "Sleep") == 1,
            "Background suspend pauses the sampler and deduplicates repeated power callbacks");
        SendMessage(hiddenHandle, 0x0218, new IntPtr(18), IntPtr.Zero);
        SendMessage(hiddenHandle, 0x0218, new IntPtr(7), IntPtr.Zero);
        Await(() => history.GetActiveSession() is { } physical && physical.Id != physicalId &&
            vpnStore.GetActiveSession() is { } vpn && vpn.Id != vpnId && vpn.ConnectionSessionId == physical.Id);
        Check(context.Dashboard is null && !monitoring.GetSnapshot().IsSuspended &&
            history.GetRecentEvents().Count(e => e.EventType == "Resume") == 1,
            "Background resume detects fresh physical/VPN sessions without creating a dashboard");

        monitoring.RefreshRecognition();
        Await(() => history.GetActiveSession()!.LastConfirmedAtUtc > history.GetActiveSession()!.StartedAtUtc);
        var exitParent = history.GetActiveSession()!.Id;
        var exitVpn = vpnStore.GetActiveSession()!.Id;
        var expectedTotals = monitoring.GetSnapshot();
        ClickTrayMenu(context, "Exit");
        Check(shutdownCount == 1 && !monitoring.GetSnapshot().IsRunning && context.Dashboard is null &&
            !initialPower.IsRegistered && history.GetActiveSession() is null && vpnStore.GetActiveSession() is null,
            "Tray Exit stops services, destroys native resources and requests application shutdown once");
        Check(history.GetRecentSessions().Single(s => s.Id == exitParent).EndReason == "ApplicationExit" &&
            vpnStore.GetRecentSessions().Single(s => s.Id == exitVpn).EndReason == "ApplicationExit" &&
            history.GetRecentEvents().Count(e => e.EventType == "VpnDisconnected" && e.SessionId == exitParent) == 1,
            "Tray Exit preserves existing clean physical/VPN shutdown reasons and deduplicated events");
        var today = DateOnly.FromDateTime(DateTime.Now);
        var persisted = store.GetTotals(today, today);
        Check(persisted.Downloaded >= expectedTotals.SessionDownloaded && persisted.Uploaded >= expectedTotals.SessionUploaded,
            "Tray Exit flushes accumulated bandwidth history with no dashboard");
        context.Exit();
        context.Dispose();
        Check(shutdownCount == 1, "Repeated tray exit and disposal perform no duplicate shutdown");
        using var next = new DatabaseRunLease(store.DatabasePath);
        Check(next.IsHeld, "Tray Exit releases database ownership after all writers have stopped");
    }

    private static object NotifyIcon(TrayApplicationContext context)
    {
        var tray = typeof(TrayApplicationContext).GetField("_trayIcon", PrivateInstance)!.GetValue(context)!;
        return tray.GetType().GetField("_icon", PrivateInstance)!.GetValue(tray)!;
    }

    private static IEnumerable<object> TrayMenuItems(TrayApplicationContext context)
    {
        var icon = NotifyIcon(context);
        var menu = icon.GetType().GetProperty("ContextMenuStrip")!.GetValue(icon)!;
        return ((System.Collections.IEnumerable)menu.GetType().GetProperty("Items")!.GetValue(menu)!).Cast<object>();
    }

    private static IEnumerable<string> TrayMenuLabels(TrayApplicationContext context) =>
        TrayMenuItems(context).Select(item => (string)item.GetType().GetProperty("Text")!.GetValue(item)!);

    private static void ClickTrayMenu(TrayApplicationContext context, string label)
    {
        var item = TrayMenuItems(context).Single(item =>
            string.Equals((string)item.GetType().GetProperty("Text")!.GetValue(item)!, label, StringComparison.Ordinal));
        item.GetType().GetMethod("PerformClick")!.Invoke(item, null);
        PumpOnce();
    }

    private static void CheckActualBackgroundProcessExit(string ownedRoot, string adapterId)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--background-exit-child");
        start.ArgumentList.Add(System.IO.Path.Combine(ownedRoot, "actual-tray-exit"));
        start.ArgumentList.Add(adapterId);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Owned WPF child did not start.");
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        var wait = Stopwatch.StartNew();
        while (!child.HasExited)
        {
            if (wait.Elapsed > TimeSpan.FromSeconds(20))
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit();
                throw new TimeoutException("Owned tray-exit child did not terminate.");
            }
            PumpOnce();
        }
        var childOutput = output.GetAwaiter().GetResult();
        var childError = error.GetAwaiter().GetResult();
        Check(child.ExitCode == 0 && childOutput.Contains("PASS actual background process exit", StringComparison.Ordinal),
            "An owned real WPF process stays alive after dashboard close and fully terminates through Tray Exit: " + childError);
    }

    private static int RunBackgroundExitChild(string root, string adapterId)
    {
        try
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var store = new UsageStore(root);
            var history = new ConnectionHistoryStore(store);
            var vpn = new VpnSessionStore(store);
            using var monitoring = new MonitoringService(store, new FakeIdentity(Network("CHILD_BACKGROUND", adapterId)),
                vpnVerifier: new VpnRouteVerifier(new VpnDetectionService(new OwnedVpnEvidence { Enabled = true }), new OwnedVpnRoutes()),
                icmpMonitor: new IcmpMonitor(new FakePingSource()), reachabilityMonitor: TestReachability.Create());
            monitoring.Start();
            using var context = new TrayApplicationContext(application, monitoring,
                dashboardFactory: service => new MainWindow(service, new FakeStartupRegistration()) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 });
            context.ShowDashboard();
            Await(() => history.GetActiveSession() is not null && vpn.GetActiveSession() is not null);
            var parentId = history.GetActiveSession()!.Id;
            var vpnId = vpn.GetActiveSession()!.Id;
            var confirmed = monitoring.GetSnapshot().Session!.LastConfirmedAtUtc;
            context.Dashboard!.Close();
            Await(() => monitoring.GetSnapshot().Session!.LastConfirmedAtUtc > confirmed);
            if (context.Dashboard is not null || !monitoring.GetSnapshot().IsRunning ||
                application.Dispatcher.HasShutdownStarted || history.GetActiveSession()?.Id != parentId || vpn.GetActiveSession()?.Id != vpnId)
                throw new InvalidOperationException("Closing the child dashboard stopped the owned process's monitoring.");
            context.ShowDashboard();
            application.Dispatcher.BeginInvoke(new Action(() =>
            {
                var exit = TrayMenuItems(context).Single(item =>
                    (string)item.GetType().GetProperty("Text")!.GetValue(item)! == "Exit");
                exit.GetType().GetMethod("PerformClick")!.Invoke(exit, null);
            }));
            application.Run();
            if (monitoring.GetSnapshot().IsRunning || history.GetActiveSession() is not null || vpn.GetActiveSession() is not null ||
                history.GetRecentSessions().Single(s => s.Id == parentId).EndReason != "ApplicationExit" ||
                vpn.GetRecentSessions().Single(s => s.Id == vpnId).EndReason != "ApplicationExit")
                throw new InvalidOperationException("Actual tray exit did not finish clean physical/VPN shutdown.");
            using var next = new DatabaseRunLease(store.DatabasePath);
            if (!next.IsHeld) throw new InvalidOperationException("Actual tray exit left the database owned.");
            Console.WriteLine("PASS actual background process exit");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void CheckStartupLaunchModes(string ownedRoot, string adapterId)
    {
        Check(!LaunchOptions.IsBackground(Array.Empty<string>()), "Normal launch arguments select the dashboard");
        Check(LaunchOptions.IsBackground(new[] { "--background" }) &&
            LaunchOptions.IsBackground(new[] { "unused", "--BACKGROUND", "--background" }),
            "Background flag supports argument ordering, casing and repetition");
        Check(!LaunchOptions.IsBackground(new[] { "--backgrounded", "--background=true", "background" }),
            "Only the complete background flag selects headless launch");
        foreach (var background in new[] { false, true })
        {
            var store = new UsageStore(System.IO.Path.Combine(ownedRoot, "launch-" + background));
            var history = new ConnectionHistoryStore(store);
            var vpn = new VpnSessionStore(store);
            using var monitoring = new MonitoringService(store, new FakeIdentity(Network("LAUNCH", adapterId)),
                vpnVerifier: new VpnRouteVerifier(new VpnDetectionService(new OwnedVpnEvidence { Enabled = true }), new OwnedVpnRoutes()),
                icmpMonitor: new IcmpMonitor(new FakePingSource()), reachabilityMonitor: TestReachability.Create());
            var factoryCalls = 0;
            var shutdownCalls = 0;
            using var context = new TrayApplicationContext(Application.Current, monitoring,
                dashboardFactory: service =>
                {
                    factoryCalls++;
                    return new MainWindow(service, new FakeStartupRegistration())
                        { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
                }, shutdownAction: () => shutdownCalls++);
            context.Start(background);
            Check(monitoring.GetSnapshot().IsRunning && (background
                ? context.Dashboard is null && factoryCalls == 0 && !Application.Current.Windows.OfType<MainWindow>().Any()
                : context.Dashboard is { IsVisible: true } && factoryCalls == 1),
                background ? "Background launch starts monitoring without constructing any dashboard"
                    : "Normal launch starts monitoring and opens a dashboard");
            Check((bool)NotifyIcon(context).GetType().GetProperty("Visible")!.GetValue(NotifyIcon(context))!,
                $"Launch mode background={background} creates a visible tray icon");
            WaitWithoutUi(() => history.GetActiveSession() is not null && vpn.GetActiveSession() is not null);
            var parent = history.GetActiveSession()!.Id;
            var child = vpn.GetActiveSession()!.Id;
            var firstConfirmation = monitoring.GetSnapshot().Session!.LastConfirmedAtUtc;
            WaitWithoutUi(() => monitoring.GetSnapshot().Session!.LastConfirmedAtUtc > firstConfirmation);
            Check(history.GetActiveSession()!.Id == parent && vpn.GetActiveSession()!.Id == child,
                $"Launch mode background={background} samples and retains physical/VPN sessions without dashboard refresh");
            context.Start(background);
            Check(factoryCalls == (background ? 0 : 1) && history.GetRecentSessions().Count == 1,
                $"Repeated launch initialization background={background} creates no duplicate windows or sessions");
            if (background)
            {
                ClickTrayMenu(context, "Open NetTrace");
                Check(context.Dashboard is { IsVisible: true } && factoryCalls == 1 &&
                    history.GetActiveSession()!.Id == parent && vpn.GetActiveSession()!.Id == child,
                    "Tray Open after initial background launch creates the first dashboard without restarting monitoring");
            }
            ClickTrayMenu(context, "Exit");
            Check(shutdownCalls == 1 && !monitoring.GetSnapshot().IsRunning &&
                history.GetActiveSession() is null && vpn.GetActiveSession() is null &&
                history.GetRecentSessions().Single().EndReason == "ApplicationExit" &&
                vpn.GetRecentSessions().Single().EndReason == "ApplicationExit",
                $"Launch mode background={background} exits with clean physical/VPN closure");
        }
    }

    private static void CheckStartupSetting(string ownedRoot, string adapterId)
    {
        var registration = new FakeStartupRegistration { Enabled = true };
        var store = new UsageStore(System.IO.Path.Combine(ownedRoot, "startup-setting"));
        using var monitoring = new MonitoringService(store, new FakeIdentity(Network("SETTING", adapterId)),
            vpnVerifier: new VpnRouteVerifier(new VpnDetectionService(new OwnedVpnEvidence()), new OwnedVpnRoutes()),
            icmpMonitor: new IcmpMonitor(new FakePingSource()), reachabilityMonitor: TestReachability.Create());
        using var context = new TrayApplicationContext(Application.Current, monitoring,
            dashboardFactory: service => new MainWindow(service, registration)
                { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 }, shutdownAction: () => { });
        context.Start();
        var window = context.Dashboard!;
        var checkbox = (CheckBox)window.FindName("StartWithWindowsCheckBox");
        Check(checkbox.IsChecked == true && registration.EnableCalls == 0 && registration.DisableCalls == 0,
            "Startup checkbox reflects registration on dashboard creation without changing it");
        var reads = registration.ReadCalls;
        window.RefreshDashboard();
        window.RefreshDashboard();
        Check(registration.ReadCalls == reads, "Dashboard refresh performs no recurring startup-registry reads");
        ToggleStartup(window, false);
        Check(!registration.Enabled && checkbox.IsChecked == false && registration.DisableCalls == 1,
            "Unchecking startup immediately disables registration and reads back its actual state");
        ToggleStartup(window, true);
        Check(registration.Enabled && checkbox.IsChecked == true && registration.EnableCalls == 1,
            "Checking startup immediately enables registration and reads back its actual state");
        registration.Enabled = false;
        RefreshStartupOnActivation(window);
        Check(checkbox.IsChecked == false && registration.EnableCalls == 1 && registration.DisableCalls == 1,
            "Dashboard activation reflects external registration changes without writing them");
        registration.FailEnable = true;
        ToggleStartup(window, true);
        Check(checkbox.IsChecked == false && Text(window, "StartupSettingMessageText").Contains("Could not update") &&
            monitoring.GetSnapshot().IsRunning,
            "Failed startup registration reports an inline error and restores actual unchecked state while monitoring continues");
        registration.ApplyBeforeFailure = true;
        ToggleStartup(window, true);
        Check(registration.Enabled && checkbox.IsChecked == true && Text(window, "StartupSettingMessageText").Contains("Could not update"),
            "Partial startup write failure displays the actual enabled registration");
        registration.FailEnable = false;
        registration.FailDisable = true;
        ToggleStartup(window, false);
        Check(registration.Enabled && checkbox.IsChecked == true && monitoring.GetSnapshot().IsRunning,
            "Failed disable preserves the actual enabled state and does not stop monitoring");
        registration.FailDisable = false;
        registration.FailRead = true;
        RefreshStartupOnActivation(window);
        Check(checkbox.IsChecked is null && !checkbox.IsEnabled && Text(window, "StartupSettingMessageText").Contains("unavailable") &&
            monitoring.GetSnapshot().IsRunning,
            "Unavailable startup registration is shown as unknown without pretending it is disabled");
        registration.FailRead = false;
        RefreshStartupOnActivation(window);
        Check(checkbox.IsChecked == true && checkbox.IsEnabled && Text(window, "StartupSettingMessageText") == "",
            "Startup setting recovers on activation after a transient read failure");
        registration.Enabled = false;
        window.Close();
        ClickTrayMenu(context, "Open NetTrace");
        Check(!ReferenceEquals(window, context.Dashboard) &&
            ((CheckBox)context.Dashboard!.FindName("StartWithWindowsCheckBox")).IsChecked == false &&
            monitoring.GetSnapshot().IsRunning,
            "Recreated dashboard reads current registration instead of retaining a stale checkbox value");
        context.Exit();
        Check(registration.EnableCalls == 3 && registration.DisableCalls == 2,
            "Tray exit leaves Windows startup registration untouched");
    }

    private static void RefreshStartupOnActivation(MainWindow window) =>
        typeof(MainWindow).GetMethod("OnActivated", PrivateInstance)!.Invoke(window, new object[] { EventArgs.Empty });

    private static void ToggleStartup(MainWindow window, bool value)
    {
        var checkbox = (CheckBox)window.FindName("StartWithWindowsCheckBox");
        checkbox.IsChecked = value;
        checkbox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }

    private static void CheckActualLaunchProcess(string ownedRoot, string adapterId, bool background)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--launch-child");
        start.ArgumentList.Add(System.IO.Path.Combine(ownedRoot, "actual-launch-" + background));
        start.ArgumentList.Add(adapterId);
        if (background) start.ArgumentList.Add("--background");
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Owned launch child did not start.");
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        var wait = Stopwatch.StartNew();
        while (!child.HasExited)
        {
            if (wait.Elapsed > TimeSpan.FromSeconds(20))
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit();
                throw new TimeoutException("Owned launch child did not terminate.");
            }
            PumpOnce();
        }
        Check(child.ExitCode == 0 && output.GetAwaiter().GetResult().Contains("PASS actual launch", StringComparison.Ordinal),
            $"Owned WPF process background={background} starts the correct view mode and fully exits via the tray: " + error.GetAwaiter().GetResult());
    }

    private static int RunLaunchChild(string root, string adapterId, string[] launchArgs)
    {
        try
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var store = new UsageStore(root);
            var history = new ConnectionHistoryStore(store);
            using var monitoring = new MonitoringService(store, new FakeIdentity(Network("CHILD_LAUNCH", adapterId)),
                vpnVerifier: new VpnRouteVerifier(new VpnDetectionService(new OwnedVpnEvidence()), new OwnedVpnRoutes()),
                icmpMonitor: new IcmpMonitor(new FakePingSource()), reachabilityMonitor: TestReachability.Create());
            var factoryCalls = 0;
            using var context = new TrayApplicationContext(application, monitoring,
                dashboardFactory: service =>
                {
                    factoryCalls++;
                    return new MainWindow(service, new FakeStartupRegistration())
                        { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
                });
            var background = LaunchOptions.IsBackground(launchArgs);
            application.Startup += (_, _) =>
            {
                context.Start(background);
                if (!monitoring.GetSnapshot().IsRunning || (background
                    ? context.Dashboard is not null || factoryCalls != 0 || application.Windows.OfType<MainWindow>().Any()
                    : context.Dashboard is not { IsVisible: true } || factoryCalls != 1))
                    throw new InvalidOperationException("Actual WPF startup created the wrong dashboard state.");
                WaitWithoutUi(() => history.GetActiveSession() is not null);
                var confirmed = monitoring.GetSnapshot().Session!.LastConfirmedAtUtc;
                WaitWithoutUi(() => monitoring.GetSnapshot().Session!.LastConfirmedAtUtc > confirmed);
                if (background && factoryCalls != 0)
                    throw new InvalidOperationException("Background sampling unexpectedly created a dashboard.");
                application.Dispatcher.BeginInvoke(new Action(() => ClickTrayMenu(context, "Exit")));
            };
            application.Run();
            if (monitoring.GetSnapshot().IsRunning || history.GetActiveSession() is not null ||
                history.GetRecentSessions().Single().EndReason != "ApplicationExit")
                throw new InvalidOperationException("Launch process did not close monitoring cleanly.");
            using var next = new DatabaseRunLease(store.DatabasePath);
            Console.WriteLine("PASS actual launch");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void CheckIcmpBackgroundLifecycle(string ownedRoot, string adapterId)
    {
        var store = new UsageStore(System.IO.Path.Combine(ownedRoot, "icmp-background"));
        var history = new ConnectionHistoryStore(store);
        var source = new FakePingSource();
        using var icmp = new IcmpMonitor(source, interval: TimeSpan.FromMilliseconds(60));
        using var monitoring = new MonitoringService(store, new FakeIdentity(Network("ICMP_BACKGROUND", adapterId)),
            vpnVerifier: new VpnRouteVerifier(new VpnDetectionService(new OwnedVpnEvidence()), new OwnedVpnRoutes()),
            icmpMonitor: icmp, reachabilityMonitor: TestReachability.Create());
        var windowsCreated = 0;
        var shutdownCalls = 0;
        using var context = new TrayApplicationContext(Application.Current, monitoring,
            dashboardFactory: service =>
            {
                windowsCreated++;
                return new MainWindow(service, new FakeStartupRegistration())
                    { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            }, shutdownAction: () => shutdownCalls++);
        context.Start(background: true);
        WaitWithoutUi(() => monitoring.LatestIcmpMeasurement is not null && history.GetActiveSession() is not null);
        var firstResult = monitoring.LatestIcmpMeasurement;
        WaitWithoutUi(() => source.Calls >= 8 && !ReferenceEquals(firstResult, monitoring.LatestIcmpMeasurement));
        var result = monitoring.LatestIcmpMeasurement!;
        var parentId = history.GetActiveSession()!.Id;
        Check(context.Dashboard is null && windowsCreated == 0 && !Application.Current.Windows.OfType<MainWindow>().Any() &&
            result.SuccessfulReplies == 4 && result.FailedReplies == 0 && result.AverageLatencyMs == 20 && result.PacketLossPercent == 0,
            "Tray-only launch completes repeated injected ICMP cycles without a dashboard or dispatcher refresh");
        Check(result.Target == "1.1.1.1" && result.TimestampUtc.Offset == TimeSpan.Zero && monitoring.GetSnapshot().IsRunning,
            "Application-owned monitoring exposes timestamped raw ICMP measurements for the configured public IP");
        ClickTrayMenu(context, "Open NetTrace");
        var firstWindow = context.Dashboard!;
        firstWindow.Close();
        var callsAfterClose = source.Calls;
        var resultAfterClose = monitoring.LatestIcmpMeasurement;
        WaitWithoutUi(() => source.Calls >= callsAfterClose + 4 &&
            !ReferenceEquals(resultAfterClose, monitoring.LatestIcmpMeasurement));
        Check(context.Dashboard is null && windowsCreated == 1 && monitoring.LatestIcmpMeasurement is not null &&
            history.GetActiveSession()!.Id == parentId,
            "Closing a recreated dashboard leaves ICMP monitoring and the existing physical session running");
        source.Fail = true;
        WaitWithoutUi(() => monitoring.LatestIcmpMeasurement is { FailedReplies: 4 });
        Check(monitoring.LatestIcmpMeasurement is { PacketLossPercent: 100, AverageLatencyMs: null } &&
            monitoring.GetSnapshot().Status == "Monitoring" && history.GetActiveSession()!.Id == parentId,
            "All ICMP failures report raw loss without declaring Internet Offline or closing the physical session");
        monitoring.Suspend();
        WaitWithoutUi(() => typeof(IcmpMonitor).GetField("_cycleCancellation", PrivateInstance)!.GetValue(icmp) is null);
        var callsAtSleep = source.Calls;
        var sleepWait = Stopwatch.StartNew();
        while (sleepWait.Elapsed < TimeSpan.FromMilliseconds(150)) Thread.Sleep(5);
        Check(source.Calls == callsAtSleep && monitoring.GetSnapshot().IsSuspended,
            "Existing application suspend pauses ICMP cycles without a second power lifecycle");
        monitoring.Resume();
        var resultAtResume = monitoring.LatestIcmpMeasurement;
        WaitWithoutUi(() => source.Calls >= callsAtSleep + 4 && history.GetActiveSession() is not null &&
            !ReferenceEquals(resultAtResume, monitoring.LatestIcmpMeasurement));
        Check(history.GetActiveSession()!.Id != parentId && !monitoring.GetSnapshot().IsSuspended,
            "Existing application resume restarts raw ICMP monitoring independently of the dashboard");
        ClickTrayMenu(context, "Exit");
        icmp.Completion.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        var callsAtExit = source.Calls;
        var exitWait = Stopwatch.StartNew();
        while (exitWait.Elapsed < TimeSpan.FromMilliseconds(100)) Thread.Sleep(5);
        Check(source.Calls == callsAtExit && shutdownCalls == 1 && !monitoring.GetSnapshot().IsRunning &&
            history.GetActiveSession() is null && history.GetRecentSessions().First().EndReason == "ApplicationExit",
            "Tray Exit cancels the application-owned ICMP worker and retains clean physical shutdown");
        using var next = new DatabaseRunLease(store.DatabasePath);
        Check(next.IsHeld, "ICMP shutdown leaves database ownership released for the next run");
    }

    private sealed class FakePingSource : IPingSource
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public volatile bool Fail;
        public Task<IcmpPingReply> SendAsync(IPAddress target, TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new IcmpPingReply(Fail ? IPStatus.TimedOut : IPStatus.Success, 20));
        }
    }

    private sealed class FakeStartupRegistration : IWindowsStartupRegistration
    {
        public bool Enabled;
        public bool FailRead;
        public bool FailEnable;
        public bool FailDisable;
        public bool ApplyBeforeFailure;
        public int ReadCalls;
        public int EnableCalls;
        public int DisableCalls;
        public bool IsEnabled()
        {
            ReadCalls++;
            if (FailRead) throw new InvalidOperationException("Owned startup read denied.");
            return Enabled;
        }
        public void Enable()
        {
            EnableCalls++;
            if (!FailEnable || ApplyBeforeFailure) Enabled = true;
            if (FailEnable) throw new InvalidOperationException("Owned startup write denied.");
        }
        public void Disable()
        {
            DisableCalls++;
            if (FailDisable) throw new InvalidOperationException("Owned startup delete denied.");
            Enabled = false;
        }
    }

    private sealed class OwnedVpnRoutes : IVpnRouteSource
    {
        public static readonly Guid TunnelGuid = Guid.Parse("aae68bce-af13-4456-9193-31ded9c79ab7");
        public volatile bool Split;
        public VpnRouteInventory Read(AddressFamily family)
        {
            if (family == AddressFamily.InterNetworkV6)
                return new(family, Array.Empty<VpnRouteEvidence>(), Availability: IpFamilyAvailability.Unavailable);
            var routes = Split ? new[] {
                new VpnRouteEvidence(5001, TunnelGuid, IPAddress.Parse("10.20.0.0"), 16,
                    IPAddress.Parse("10.0.0.1"), 1, 1, true),
                new VpnRouteEvidence(5002, Guid.Parse("c451d2b5-4d20-4695-a3e8-675b723ab014"),
                    IPAddress.Any, 0, IPAddress.Parse("192.168.1.1"), 10, 10, true)
            } : new[] {
                new VpnRouteEvidence(5001, TunnelGuid, IPAddress.Any, 0,
                    IPAddress.Parse("10.0.0.1"), 1, 1, true)
            };
            return new(family, routes, Availability: IpFamilyAvailability.Usable);
        }
    }

    private static MainWindow OwnedWindow(UsageStore store, FakeIdentity identity)
        => CreateOwnedWindow(store, identity);

    private static MainWindow CreateOwnedWindow(UsageStore store, FakeIdentity identity, VpnRouteVerifier? verifier = null,
        bool start = true)
    {
        // Physical-only fixtures must not inherit VPN events from the test machine.
        verifier ??= new VpnRouteVerifier(new VpnDetectionService(new OwnedVpnEvidence()), new OwnedVpnRoutes());
        var monitoring = new MonitoringService(store, identity, vpnVerifier: verifier,
            icmpMonitor: new IcmpMonitor(new FakePingSource()), reachabilityMonitor: TestReachability.Create());
        var context = new TrayApplicationContext(Application.Current, monitoring,
            dashboardFactory: service => new MainWindow(service, new FakeStartupRegistration())
                { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 },
            shutdownAction: () => { });
        context.ShowDashboard();
        var window = context.Dashboard!;
        OwnedContexts.Add(window, context);
        if (start) monitoring.Start();
        Field<DispatcherTimer>(window, "_timer").Stop();
        StopSampler(window);
        return window;
    }

    private static void PrimeDelta(MainWindow window, string adapterId, long received, long sent)
    {
        var actual = new NetworkMonitor().Capture(adapterId) ?? throw new InvalidOperationException("Owned physical adapter disappeared.");
        typeof(MonitoringService).GetField("_previousSnapshot", PrivateInstance)!.SetValue(Service(window),
            new NetworkSnapshot(Math.Max(0, actual.BytesReceived - received), Math.Max(0, actual.BytesSent - sent),
                actual.Timestamp.AddSeconds(-1)));
    }

    private static void Tick(MainWindow window)
    {
        Service(window).SampleNow();
        window.RefreshDashboard();
    }
    private static void Refresh(MainWindow window) => Monitor(window).Refresh();
    private static ConnectionProfileMonitor Monitor(MainWindow window) => Field<ConnectionProfileMonitor>(window, "_profileMonitor");
    private static ConnectionSessionTracker Tracker(MainWindow window) => Field<ConnectionSessionTracker>(window, "_sessions");
    private static string Text(MainWindow window, string name) => ((TextBlock)window.FindName(name)).Text;
    private static MonitoringService Service(MainWindow window) => OwnedContexts[window].Monitoring;
    private static WindowsPowerLifecycle Power(MainWindow window) =>
        (WindowsPowerLifecycle)typeof(TrayApplicationContext).GetField("_powerLifecycle", PrivateInstance)!.GetValue(OwnedContexts[window])!;
    private static T Field<T>(MainWindow window, string name)
    {
        var owner = name == "_timer" ? (object)window : Service(window);
        return (T)owner.GetType().GetField(name, PrivateInstance)!.GetValue(owner)!;
    }
    private static void StopSampler(MainWindow window)
    {
        var service = Service(window);
        var sampler = typeof(MonitoringService).GetField("_timer", PrivateInstance)!.GetValue(service) as System.Threading.Timer;
        sampler?.Change(Timeout.Infinite, Timeout.Infinite);
    }
    private static void CloseRun(MainWindow window) => OwnedContexts[window].Dispose();
    private static void Invoke(MainWindow window, string name, params object?[] arguments)
    {
        if (name == "FlushPending") Service(window).Flush();
        else
            typeof(MonitoringService).GetMethod(name == "LoadAdapters" ? "RefreshAdapters" : name,
                PrivateInstance)!.Invoke(Service(window), arguments);
        if (window.IsLoaded) window.RefreshDashboard();
    }

    private static void AwaitObservation(MainWindow window)
    {
        var sequence = Monitor(window).LastObservationSequence;
        Monitor(window).Refresh();
        Await(() => Monitor(window).LastObservationSequence > sequence);
        PumpOnce();
    }

    private static void Await(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Owned WPF condition did not become true.");
            PumpOnce();
        }
        PumpOnce();
    }

    private static void PumpOnce()
    {
        foreach (var window in OwnedContexts.Keys.Where(window => window.IsLoaded)) window.RefreshDashboard();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void WaitWithoutUi(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Owned identity observation timed out.");
            Thread.Sleep(10);
        }
    }

    private static void SimulateUnexpectedExit(MainWindow window)
    {
        // Stop only owned resources, bypassing a clean database close to exercise recovery.
        StopSampler(window);
        Field<DispatcherTimer>(window, "_timer").Stop();
        Power(window).Dispose();
        Field<IcmpMonitor>(window, "_icmpMonitor").Dispose();
        Field<VpnSessionMonitor>(window, "_vpnSessions").Dispose();
        Monitor(window).Dispose();
        Field<DatabaseRunLease>(window, "_runLease").Dispose();
        var tray = typeof(TrayApplicationContext).GetField("_trayIcon", PrivateInstance)!.GetValue(OwnedContexts[window]) as IDisposable;
        tray?.Dispose();
        window.Close();
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        _passed++;
        Console.WriteLine("PASS " + description);
    }

    private static void Sql(string databasePath, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam)
    {
        var result = NativeSendMessage(hwnd, message, wParam, lParam);
        foreach (var window in OwnedContexts.Keys.Where(window => window.IsLoaded)) window.RefreshDashboard();
        return result;
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr NativeSendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    private sealed class FakeIdentity(NetworkIdentityResult initial) : INetworkIdentityService
    {
        private volatile NetworkIdentityResult _result = initial;
        public bool Disposed { get; private set; }
        public bool ThrowOnDispose { get; init; }
        public event EventHandler? NetworkChanged;
        public NetworkIdentityResult GetCurrentNetwork() => _result;
        public void Set(NetworkIdentityResult result) { _result = result; NetworkChanged?.Invoke(this, EventArgs.Empty); }
        public void Dispose()
        {
            Disposed = true;
            if (ThrowOnDispose) throw new InvalidOperationException("Owned identity dispose failure.");
        }
    }
}
