using System.Diagnostics;
using System.Globalization;
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
using Microsoft.Data.Sqlite;
using NetTrace;
using NetTrace.Models;
using NetTrace.Services;
using NetTrace.Utils;

internal static class HistoryOverviewChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    internal const string AdapterId = "owned-history-adapter";

    public static void Run(string root, Action<bool, string> check)
    {
        var previews = System.IO.Path.Combine(root, "history-previews");
        CheckOverviewStates(root, previews, check);
        CheckLiveLifecycle(root, previews, check);
        CheckDetailedHistory(root, previews, check);
        Console.WriteLine("History overview previews: " + previews);
    }

    private static void CheckOverviewStates(string root, string previews, Action<bool, string> check)
    {
        var store = new UsageStore(System.IO.Path.Combine(root, "history-states"));
        var history = new ConnectionHistoryStore(store);
        var clock = new OwnedClock();
        using var monitoring = CreateMonitoring(store, clock);
        var window = CreateWindow(monitoring);
        try
        {
            window.Show();
            StopViewTimer(window);
            SelectHistory(window);
            check(Control<StackPanel>(window, "HistoryEmptyState").IsVisible &&
                !Control<FrameworkElement>(window, "HistoryTodayPanel").IsVisible &&
                Control<ItemsControl>(window, "HistoryRecentSessions").Items.Count == 0 &&
                Descendants(Control<StackPanel>(window, "HistoryEmptyState")).OfType<TextBlock>()
                    .Any(text => text.Text == "No connection history yet."),
                "Empty History shows its plain empty state without summary or fake session rows");
            check(Control<Button>(window, "ViewHistoryButton").Content?.ToString() == "View full history" &&
                Control<Button>(window, "ViewHistoryButton").IsVisible,
                "History keeps the existing detailed-window action with the View full history label");
            CheckLayout(window, check, "empty");
            SavePreview(window, System.IO.Path.Combine(previews, "760x600-empty.png"));

            var profiles = new ProfileStore(store);
            var profile = profiles.CreateProfile("DITO", "Owned router");
            var first = AddCompleted(history, clock.GetUtcNow().AddHours(-2), TimeSpan.FromMinutes(45),
                "Owned network", profile.Id, 24_040_000, 125_000_000);
            SelectOtherThenHistory(window);
            check(!Control<StackPanel>(window, "HistoryEmptyState").IsVisible &&
                Control<FrameworkElement>(window, "HistoryTodayPanel").IsVisible &&
                Text(window, "HistoryTodaySessionsText") == "1" &&
                Text(window, "HistoryTodayConnectedTimeText").Contains("45m", StringComparison.Ordinal) &&
                Text(window, "HistoryTodayDownloadText") == ByteFormatter.Format(first.DownloadBytes) &&
                Text(window, "HistoryTodayUploadText") == ByteFormatter.Format(first.UploadBytes),
                "Opening History immediately renders the stored day's four summary fields");
            var items = Control<ItemsControl>(window, "HistoryRecentSessions");
            check(items.Items.Count == 1 && Property<string>(items.Items[0], "ProfileName") == "DITO — Owned router" &&
                Property<string>(items.Items[0], "NetworkName").Contains("Owned network", StringComparison.Ordinal) &&
                !Property<bool>(items.Items[0], "IsActive"),
                "One completed recent session retains profile, network, usage and completed state");
            CheckLayout(window, check, "one-session");
            SavePreview(window, System.IO.Path.Combine(previews, "760x600-one-session.png"));

            for (var i = 0; i < 3; i++)
                AddCompleted(history, clock.GetUtcNow().AddMinutes(-100 + i * 20), TimeSpan.FromMinutes(10),
                    "Owned network " + (i + 2), profile.Id, 761_000 + i * 1_000_000, 28_960_000);
            SelectOtherThenHistory(window);
            check(items.Items.Count == 4 && items.Items.Cast<object>().Select(item => Property<long>(item, "Id"))
                .SequenceEqual(history.GetRecentSessions(4).Select(session => session.Id)),
                "Recent History renders four persisted sessions newest first");
            check(Text(window, "HistoryTodaySessionsText") == "4" &&
                Text(window, "HistoryTodayDownloadText") == ByteFormatter.Format(history.GetRecentSessions().Sum(session => session.DownloadBytes)) &&
                Text(window, "HistoryTodayUploadText") == ByteFormatter.Format(history.GetRecentSessions().Sum(session => session.UploadBytes)),
                "Multiple completed session rows contribute once to the summary byte totals");
            CheckLayout(window, check, "four-sessions");
            SavePreview(window, System.IO.Path.Combine(previews, "760x600-four-sessions.png"));
            SaveBottomPreview(window, System.IO.Path.Combine(previews, "760x600-four-sessions-bottom.png"));

            var longProfile = profiles.CreateProfile("A very long historical provider name with several descriptive words",
                "A very long owned router and device name that should wrap within the page");
            var longNetwork = "A long stored network identity with spaces and additional descriptive router information " + new string('W', 85);
            AddCompleted(history, clock.GetUtcNow().AddMinutes(-10), TimeSpan.FromMinutes(8),
                longNetwork, longProfile.Id, 5_370_000_000, 2_630_000_000);
            SelectOtherThenHistory(window);
            check(items.Items.Count == 4 && Text(window, "HistoryTodaySessionsText") == "5" &&
                Text(window, "HistoryTodayDownloadText").Contains("GB", StringComparison.Ordinal) &&
                Text(window, "HistoryTodayUploadText").Contains("GB", StringComparison.Ordinal),
                "History bounds its recent rows at four while all today's sessions and GB totals remain represented");
            check(!Descendants(items).OfType<TextBlock>().Any(text => text.Text.Contains("ApplicationExit", StringComparison.Ordinal)),
                "The compact recent overview does not expose internal session end reasons");
            CheckLayout(window, check, "long-names-and-GB");
            SavePreview(window, System.IO.Path.Combine(previews, "760x600-long-names-GB.png"));
            SaveBottomPreview(window, System.IO.Path.Combine(previews, "760x600-long-names-GB-bottom.png"));
            window.Width = 920;
            window.Height = 740;
            window.UpdateLayout();
            Pump();
            SavePreview(window, System.IO.Path.Combine(previews, "920x740-long-names-GB.png"));

            var latest = AddCompleted(history, clock.GetUtcNow().AddSeconds(-50), TimeSpan.FromSeconds(42),
                "Written while overview inputs are cached", null, 800_000, 500_000);
            window.RefreshDashboard();
            Pump();
            check(Text(window, "HistoryTodaySessionsText") == "5" && Property<long>(items.Items[0], "Id") != latest.Id,
                "An ordinary view tick reuses cached persisted History inputs instead of requerying history");
            SelectOtherThenHistory(window);
            check(Text(window, "HistoryTodaySessionsText") == "6" && Property<long>(items.Items[0], "Id") == latest.Id,
                "Returning to History forces a fresh persisted summary and recent-session query");

            Control<TabControl>(window, "MainNavigation").SelectedItem = Control<TabItem>(window, "DashboardTab");
            Pump();
            RenameOwnedSessionTable(store.DatabasePath, "ConnectionSessions", "OwnedHiddenSessions");
            try
            {
                typeof(MainWindow).GetMethod("Monitoring_OnChanged", PrivateInstance)!.Invoke(window,
                    new object[] { monitoring, EventArgs.Empty });
                window.RefreshDashboard();
                Pump();
                check(Text(window, "HistoryMessageText") == "" && Text(window, "HistoryTodaySessionsText") == "6",
                    "A hidden History page neither queries its temporarily unavailable owned table nor renders on monitor notifications");
            }
            finally { RenameOwnedSessionTable(store.DatabasePath, "OwnedHiddenSessions", "ConnectionSessions"); }
            SelectHistory(window);
        }
        finally { window.Close(); }
    }

    private static void CheckLiveLifecycle(string root, string previews, Action<bool, string> check)
    {
        var store = new UsageStore(System.IO.Path.Combine(root, "history-live-owned"));
        var clock = new OwnedClock();
        var identity = new OwnedIdentity(Network("Active owned network"));
        var counters = new OwnedCounters(clock);
        using var monitoring = CreateMonitoring(store, clock, identity, counters);
        var window = CreateWindow(monitoring);
        try
        {
            window.Show();
            StopViewTimer(window);
            SelectHistory(window);
            monitoring.Start();
            StopSampler(monitoring);
            Wait(() => monitoring.GetSnapshot().Session is not null);
            window.RefreshDashboard();
            Pump();
            var sessionId = monitoring.GetSnapshot().Session!.Id;
            var items = Control<ItemsControl>(window, "HistoryRecentSessions");
            check(items.Items.Count == 1 && Property<bool>(items.Items[0], "IsActive") &&
                Property<string>(items.Items[0], "TimeRange").Contains("Active", StringComparison.Ordinal) &&
                Text(window, "HistoryTodaySessionsText") == "1" &&
                !Control<StackPanel>(window, "HistoryEmptyState").IsVisible,
                "An active-only first day shows its current session and does not claim there is no history");

            monitoring.SampleNow();
            clock.Advance(TimeSpan.FromSeconds(42));
            counters.Advance(1_260_000, 662_000);
            monitoring.SampleNow();
            window.RefreshDashboard();
            Pump();
            check(Text(window, "HistoryTodayConnectedTimeText") == "42s" &&
                Text(window, "HistoryTodayDownloadText") == ByteFormatter.Format(1_260_000) &&
                Text(window, "HistoryTodayUploadText") == ByteFormatter.Format(662_000) &&
                Property<string>(items.Items[0], "DownloadText") == ByteFormatter.Format(1_260_000),
                "The existing view refresh updates active duration and current byte counters without duplicate history rows");
            check(monitoring.History.GetRecentSessions().Count == 1 && monitoring.History.GetRecentEvents()
                .Count(connectionEvent => connectionEvent.EventType == "Connected") == 1,
                "Rendering live History does not manufacture sessions or connection events");
            CheckLayout(window, check, "active-session");
            SavePreview(window, System.IO.Path.Combine(previews, "760x600-active.png"));

            var viewTimer = Field<object>(window, "_timer");
            var sampler = Field<object>(monitoring, "_timer");
            var profileMonitor = Field<object>(monitoring, "_profileMonitor");
            var icmp = Field<object>(monitoring, "_icmpMonitor");
            var reachability = Field<object>(monitoring, "_reachabilityMonitor");
            var vpn = Field<object>(monitoring, "_vpnSessions");
            var captures = counters.Captures;
            var eventCount = monitoring.History.GetRecentEvents().Count;
            for (var i = 0; i < 5; i++) SelectOtherThenHistory(window);
            check(ReferenceEquals(viewTimer, Field<object>(window, "_timer")) &&
                ReferenceEquals(sampler, Field<object>(monitoring, "_timer")) &&
                ReferenceEquals(profileMonitor, Field<object>(monitoring, "_profileMonitor")) &&
                ReferenceEquals(icmp, Field<object>(monitoring, "_icmpMonitor")) &&
                ReferenceEquals(reachability, Field<object>(monitoring, "_reachabilityMonitor")) &&
                ReferenceEquals(vpn, Field<object>(monitoring, "_vpnSessions")) && captures == counters.Captures &&
                eventCount == monitoring.History.GetRecentEvents().Count &&
                monitoring.GetSnapshot().Session?.Id == sessionId &&
                typeof(MainWindow).GetFields(PrivateInstance).Count(field => field.FieldType == typeof(DispatcherTimer)) == 1,
                "Repeated History navigation reuses the single view timer and existing workers without sampling or splitting sessions");

            Control<TabControl>(window, "MainNavigation").SelectedItem = Control<TabItem>(window, "DashboardTab");
            Pump();
            var hiddenText = Text(window, "HistoryTodayDownloadText");
            clock.Advance(TimeSpan.FromSeconds(18));
            counters.Advance(100_000, 200_000);
            monitoring.SampleNow();
            window.RefreshDashboard();
            Pump();
            check(Text(window, "HistoryTodayDownloadText") == hiddenText,
                "History does not render active usage while another navigation page is selected");
            SelectHistory(window);
            check(Text(window, "HistoryTodayDownloadText") == ByteFormatter.Format(1_360_000) &&
                Text(window, "HistoryTodayUploadText") == ByteFormatter.Format(862_000),
                "Returning to History immediately shows usage accumulated while its page was hidden");

            window.Hide();
            Pump();
            hiddenText = Text(window, "HistoryTodayDownloadText");
            clock.Advance(TimeSpan.FromSeconds(5));
            counters.Advance(30_000, 20_000);
            monitoring.SampleNow();
            window.RefreshDashboard();
            Pump();
            check(Text(window, "HistoryTodayDownloadText") == hiddenText &&
                monitoring.GetSnapshot().Session?.DownloadBytes == 1_390_000,
                "A hidden History window suppresses rendering while existing monitoring continues accounting bytes");
            window.Show();
            StopViewTimer(window);
            Pump();
            check(Text(window, "HistoryTodayDownloadText") == ByteFormatter.Format(1_390_000),
                "Restoring a visible History window immediately refreshes its current summary");

            identity.Set(Network("Second active owned network"));
            Wait(() => monitoring.GetSnapshot().Session is { } current && current.Id != sessionId &&
                current.NetworkIdentifier == "Second active owned network");
            Wait(() => items.Items.Count == 2);
            check(Property<bool>(items.Items[0], "IsActive") && !Property<bool>(items.Items[1], "IsActive") &&
                Text(window, "HistoryTodaySessionsText") == "2" &&
                Text(window, "HistoryTodayDownloadText") == ByteFormatter.Format(1_390_000),
                "Existing session-transition notifications immediately combine completed and current active History");
            var secondId = monitoring.GetSnapshot().Session!.Id;
            var profile = monitoring.Profiles.CreateProfile("Owned active provider", "Owned device");
            monitoring.Profiles.AssignMapping(profile.Id, "WiFi", "Second active owned network");
            monitoring.RefreshRecognition();
            Wait(() => monitoring.GetSnapshot().Connection.Profile?.Id == profile.Id);
            Wait(() => Property<string>(items.Items[0], "ProfileName") == "Owned active provider — Owned device");
            check(monitoring.GetSnapshot().Session?.Id == secondId && Text(window, "HistoryTodaySessionsText") == "2",
                "Profile resolution notifications refresh the active History identity without creating a session");
            monitoring.Profiles.UpdateProfile(profile.Id, "Renamed active provider", "Renamed device");
            monitoring.RefreshRecognition();
            Wait(() => Property<string>(items.Items[0], "ProfileName") == "Renamed active provider — Renamed device");
            check(monitoring.GetSnapshot().Session?.Id == secondId && items.Items.Count == 2,
                "Profile rename notifications refresh recent History while retaining the existing active session");
            monitoring.Suspend();
            Pump();
            check(items.Items.Cast<object>().All(item => !Property<bool>(item, "IsActive")) &&
                Text(window, "HistoryTodaySessionsText") == "2",
                "Existing sleep handling clears the active overview marker without counting sleep as another session");
            clock.Advance(TimeSpan.FromMinutes(5));
            monitoring.Resume();
            StopSampler(monitoring);
            Wait(() => monitoring.GetSnapshot().Session is { } current && current.Id != secondId);
            Wait(() => items.Items.Count == 3 && Property<bool>(items.Items[0], "IsActive"));
            check(Text(window, "HistoryTodaySessionsText") == "3",
                "Existing resume handling refreshes the newly persisted active session and counts each session once");
            monitoring.Stop();
            Pump();
            check(items.Items.Count == 3 && items.Items.Cast<object>().All(item => !Property<bool>(item, "IsActive")) &&
                Text(window, "HistoryTodaySessionsText") == "3",
                "Ending monitoring refreshes recent active markers without adding a duplicate session");
        }
        finally { window.Close(); }
    }

    private static void CheckDetailedHistory(string root, string previews, Action<bool, string> check)
    {
        var store = new UsageStore(System.IO.Path.Combine(root, "history-detailed"));
        var history = new ConnectionHistoryStore(store);
        var clock = new OwnedClock();
        for (var i = 0; i < 105; i++)
            AddCompleted(history, clock.GetUtcNow().AddDays(-1).AddMinutes(i), TimeSpan.FromSeconds(20),
                "Detailed network " + i, null, i * 761_000L, i * 200_000L);
        for (var i = 0; i < 205; i++)
            history.RecordEvent(clock.GetUtcNow().AddSeconds(i), "OwnedEvent", null, null, null, "Owned detail event " + i);
        var detailed = new ConnectionHistoryWindow(history)
            { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        try
        {
            detailed.Show();
            detailed.UpdateLayout();
            Pump();
            var sessions = Control<DataGrid>(detailed, "SessionsGrid");
            var events = Control<DataGrid>(detailed, "EventsGrid");
            var tabs = Descendants((DependencyObject)detailed.Content).OfType<TabControl>().Single();
            check(tabs.Items.OfType<TabItem>().Select(tab => tab.Header?.ToString()).SequenceEqual(new[] { "Sessions", "Events" }) &&
                sessions.Items.Count == 100 && events.Items.Count == 200 && sessions.IsReadOnly && events.IsReadOnly &&
                sessions.Columns.Count == 7 && events.Columns.Count == 3,
                "The existing detailed utility window preserves Sessions/Events tabs, read-only tables and 100/200 record limits");
            var expected = history.GetRecentSessions(100);
            check(sessions.Items.Cast<object>().Select(item => Property<string>(item, "Network"))
                .SequenceEqual(expected.Select(session => session.NetworkDisplayName)) &&
                events.Items.Cast<object>().Select(item => Property<string>(item, "Message"))
                .SequenceEqual(history.GetRecentEvents(200).Select(connectionEvent => connectionEvent.Message)),
                "Detailed session and event tables retain their newest-first stored record ordering");
            check(Property<string>(sessions.Items[0], "Started") == expected[0].StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") &&
                Property<string>(sessions.Items[0], "Ended") == expected[0].EndedAtUtc!.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") &&
                Property<string>(events.Items[0], "Time") == history.GetRecentEvents(1)[0].OccurredAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") &&
                Text(detailed, "HistoryMessageText").Contains("local time", StringComparison.Ordinal),
                "Detailed session start/end and event timestamps keep their established local-time presentation");
            SavePreview(detailed, System.IO.Path.Combine(previews, "detailed-sessions.png"));
            tabs.SelectedIndex = 1;
            detailed.UpdateLayout();
            Pump();
            check(events.IsVisible && !sessions.IsVisible, "The existing detailed Events tab remains selectable");
            SavePreview(detailed, System.IO.Path.Combine(previews, "detailed-events.png"));
            var newest = AddCompleted(history, clock.GetUtcNow().AddMinutes(1), TimeSpan.FromSeconds(10),
                "Newest after Refresh", null, 900_000, 800_000);
            history.RecordEvent(clock.GetUtcNow().AddMinutes(5), "OwnedRefresh", null, null, null, "Newest after Refresh");
            var refresh = Descendants((DependencyObject)detailed.Content).OfType<Button>()
                .Single(button => button.Content?.ToString() == "Refresh");
            refresh.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            check(sessions.Items.Count == 100 && events.Items.Count == 200 &&
                Property<string>(sessions.Items[0], "Network") == newest.NetworkDisplayName &&
                Property<string>(events.Items[0], "Message") == "Newest after Refresh",
                "Detailed Refresh reloads both bounded tables after additional persisted history appears");
        }
        finally { detailed.Close(); }

        using var monitoring = CreateMonitoring(store, clock);
        var main = CreateWindow(monitoring);
        try
        {
            main.Show();
            StopViewTimer(main);
            SelectHistory(main);
            var sessionCount = history.GetRecentSessions(1000).Count;
            var eventCount = history.GetRecentEvents(1000).Count;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var seen = false;
                var valid = false;
                Exception? failure = null;
                var close = main.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                {
                    var dialog = main.OwnedWindows.OfType<ConnectionHistoryWindow>().SingleOrDefault();
                    if (dialog is null) return;
                    seen = true;
                    dialog.Opacity = 0;
                    dialog.ShowInTaskbar = false;
                    try
                    {
                        valid = ReferenceEquals(dialog.Owner, main) && Control<DataGrid>(dialog, "SessionsGrid").Items.Count == 100 &&
                            Control<DataGrid>(dialog, "EventsGrid").Items.Count == 200;
                    }
                    catch (Exception exception) { failure = exception; }
                    finally { dialog.Close(); }
                }));
                Control<Button>(main, "ViewHistoryButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                close.Abort();
                if (failure is not null) throw failure;
                check(seen && valid && !main.OwnedWindows.OfType<ConnectionHistoryWindow>().Any(),
                    $"View full history safely opens and closes the preserved modal detailed window (attempt {attempt + 1})");
            }
            check(history.GetRecentSessions(1000).Count == sessionCount && history.GetRecentEvents(1000).Count == eventCount &&
                !monitoring.GetSnapshot().IsRunning,
                "Repeated full-history actions leave stored session/event records and monitoring state unchanged");
        }
        finally { main.Close(); }
    }

    private static void CheckLayout(MainWindow window, Action<bool, string> check, string state)
    {
        window.Width = 760;
        window.Height = 600;
        window.UpdateLayout();
        Pump();
        var page = (ScrollViewer)Control<TabItem>(window, "HistoryTab").Content;
        var content = Control<StackPanel>(window, "HistoryContent");
        var within = Descendants(content).OfType<FrameworkElement>().Where(element => element.IsVisible && element.ActualWidth > 0)
            .All(element =>
            {
                var bounds = element.TransformToAncestor(content).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                return bounds.Left >= -0.5 && bounds.Right <= content.ActualWidth + 0.5;
            });
        check(page.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && page.ScrollableWidth < 0.01 && within,
            $"History {state} stays inside the 760x600 page without horizontal overflow");
        var button = Control<Button>(window, "ViewHistoryButton");
        page.ScrollToEnd();
        window.UpdateLayout();
        Pump();
        var buttonBounds = button.TransformToAncestor(page).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
        check(page.VerticalScrollBarVisibility == ScrollBarVisibility.Auto &&
            buttonBounds.Top >= -0.5 && buttonBounds.Bottom <= page.ActualHeight + 0.5,
            $"History {state} permits vertical scrolling to its full-history action at the minimum window size");
        page.ScrollToTop();
        window.UpdateLayout();
        Pump();
    }

    private static void SaveBottomPreview(MainWindow window, string path)
    {
        var page = (ScrollViewer)Control<TabItem>(window, "HistoryTab").Content;
        page.ScrollToEnd();
        window.UpdateLayout();
        Pump();
        SavePreview(window, path);
        page.ScrollToTop();
        window.UpdateLayout();
        Pump();
    }

    internal static void SavePreview(Window window, string path)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var margin = content.Margin;
        var width = (int)Math.Ceiling(content.ActualWidth + margin.Left + margin.Right);
        var height = (int)Math.Ceiling(content.ActualHeight + margin.Top + margin.Bottom);
        var offset = VisualTreeHelper.GetOffset(content);
        var brush = new VisualBrush(content)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(offset.X, offset.Y, content.ActualWidth, content.ActualHeight),
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top
        };
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            context.DrawRectangle(brush, null, new Rect(margin.Left, margin.Top, content.ActualWidth, content.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = System.IO.File.Create(path);
        encoder.Save(output);
    }

    internal static MonitoringService CreateMonitoring(UsageStore store, TimeProvider? clock = null,
        OwnedIdentity? identity = null, OwnedCounters? counters = null) => new(store, identity ?? new OwnedIdentity(),
            vpnVerifier: new VpnRouteVerifier(new VpnDetectionService(new NoTunnel()), new NoRoutes()),
            networkMonitor: counters ?? new OwnedCounters(clock ?? TimeProvider.System), timeProvider: clock,
            icmpMonitor: new IcmpMonitor(new NoPing()), reachabilityMonitor: TestReachability.Create());

    internal static MainWindow CreateWindow(MonitoringService monitoring) => new(monitoring, new OwnedStartup(),
        new WifiRadioService(new NoRadio())) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Width = 760, Height = 600 };

    internal static void SelectHistory(MainWindow window)
    {
        Control<TabControl>(window, "MainNavigation").SelectedItem = Control<TabItem>(window, "HistoryTab");
        window.UpdateLayout();
        Pump();
    }

    private static void SelectOtherThenHistory(MainWindow window)
    {
        Control<TabControl>(window, "MainNavigation").SelectedItem = Control<TabItem>(window, "DashboardTab");
        Pump();
        SelectHistory(window);
    }

    private static ConnectionSession AddCompleted(ConnectionHistoryStore history, DateTimeOffset start,
        TimeSpan duration, string networkName, long? profileId, long download, long upload)
    {
        var session = history.Transition(null, start, null, 0, 0, Network(networkName), profileId,
            start, null, null)!;
        history.Transition(session.Id, start + duration, "ApplicationExit", download, upload, null, null,
            start + duration, null, null);
        return history.GetRecentSessions(1000).Single(item => item.Id == session.Id);
    }

    private static void RenameOwnedSessionTable(string path, string from, string to)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {from} RENAME TO {to};";
        command.ExecuteNonQuery();
    }

    private static NetworkIdentity Network(string name) => new("WiFi", name, name, AdapterId);
    internal static T Control<T>(Window window, string name) where T : class => window.FindName(name) as T ??
        throw new InvalidOperationException("Missing History control " + name);
    private static string Text(Window window, string name) => Control<TextBlock>(window, name).Text;
    private static T Property<T>(object instance, string name) => (T)(instance.GetType().GetProperty(name)!.GetValue(instance)
        ?? throw new InvalidOperationException("Missing History presentation property " + name));
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, PrivateInstance)!.GetValue(instance)!;
    internal static void StopViewTimer(MainWindow window) => Field<DispatcherTimer>(window, "_timer").Stop();
    private static void StopSampler(MonitoringService monitoring) => Field<System.Threading.Timer>(monitoring, "_timer")
        .Change(Timeout.Infinite, Timeout.Infinite);
    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var descendant in Descendants(VisualTreeHelper.GetChild(root, i))) yield return descendant;
    }
    private static void Wait(Func<bool> ready)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Owned History state was not ready.");
            Pump();
        }
        Pump();
    }
    internal static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private sealed class OwnedClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(new DateTime(2026, 10, 11, 14, 0, 0),
            TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 11))).UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan time) => Interlocked.Add(ref _ticks, time.Ticks);
    }
    internal sealed class OwnedIdentity(NetworkIdentity? initial = null) : INetworkIdentityService
    {
        private NetworkIdentity? _network = initial;
        public event EventHandler? NetworkChanged;
        public NetworkIdentityResult GetCurrentNetwork() => new(_network is null ? NetworkIdentityAvailability.Disconnected :
            NetworkIdentityAvailability.Available, _network, null);
        public void Set(NetworkIdentity network) { _network = network; NetworkChanged?.Invoke(this, EventArgs.Empty); }
        public void Dispose() { }
    }
    internal sealed class OwnedCounters(TimeProvider clock) : INetworkMonitor
    {
        private long _received;
        private long _sent;
        public int Captures { get; private set; }
        public void Advance(long download, long upload) { _received += download; _sent += upload; }
        public IReadOnlyList<AdapterInfo> GetActiveAdapters() => [new(AdapterId, "Owned Ethernet", "Owned History fixture",
            "Ethernet", 1_000_000_000, "192.0.2.10", true)];
        public NetworkSnapshot? Capture(string adapterId)
        {
            Captures++;
            return adapterId == AdapterId ? new(_received, _sent, clock.GetUtcNow()) : null;
        }
        public NetworkAdapterDetails? GetAdapterDetails(string adapterId) => adapterId == AdapterId ?
            new(AdapterId, OperationalStatus.Up, [IPAddress.Parse("192.0.2.10")], [], [], 1_000_000_000) : null;
    }
    private sealed class NoTunnel : IVpnInterfaceSource { public VpnInterfaceInventory Read() => new([], []); }
    private sealed class NoRoutes : IVpnRouteSource
    {
        public VpnRouteInventory Read(AddressFamily family) => new(family, [], Availability: IpFamilyAvailability.Unavailable);
    }
    private sealed class NoPing : IPingSource
    {
        public Task<IcmpPingReply> SendAsync(IPAddress target, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new IcmpPingReply(IPStatus.TimedOut));
    }
    private sealed class NoRadio : IWifiRadioSource { public WifiRadioSnapshot Read(string adapterId) => new(adapterId, false); }
    private sealed class OwnedStartup : IWindowsStartupRegistration
    {
        public bool IsEnabled() => false;
        public void Enable() => throw new InvalidOperationException("History verification cannot change startup settings.");
        public void Disable() => throw new InvalidOperationException("History verification cannot change startup settings.");
    }
}
