using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Data.Sqlite;
using NetTrace;
using NetTrace.Controls;
using NetTrace.Models;
using NetTrace.Presentation;
using NetTrace.Services;
using NetTrace.Utils;

internal static class DashboardSpeedChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly string[] PreservedLabels =
    ["DownloadPeakText", "UploadPeakText", "DownloadSessionText", "UploadSessionText", "DashboardUsageTitleText",
        "DashboardUsageTotalText", "DashboardUsageBreakdownText", "DashboardProfileText", "DashboardNetworkText"];

    public static void Run(string root, Action<bool, string> check)
    {
        var previews = System.IO.Path.Combine(root, "dashboard-speed-previews");
        CheckPresentation(root, previews, check);
        CheckAccounting(root, previews, check);
        Console.WriteLine("Dashboard adaptive-speed previews: " + previews);
    }

    private static void CheckPresentation(string root, string previews, Action<bool, string> check)
    {
        var store = new UsageStore(System.IO.Path.Combine(root, "dashboard-speed-presentation"));
        using var monitoring = HistoryOverviewChecks.CreateMonitoring(store);
        var window = HistoryOverviewChecks.CreateWindow(monitoring);
        try
        {
            window.Show();
            HistoryOverviewChecks.StopViewTimer(window);
            Layout(window);
            check(Control<TabItem>(window, "DashboardTab").IsSelected && Text(window, "DownloadRateText") == "0 B/s" &&
                Text(window, "UploadRateText") == "0 B/s" && Text(window, "DownloadMbpsText") == "0.00 Mbps" &&
                Text(window, "UploadMbpsText") == "0.00 Mbps",
                "Dashboard opens with prominent adaptive idle bytes and both existing idle Mbps readings");
            var preserved = PreservedLabels.Select(name => Text(window, name)).ToArray();
            foreach (var direction in new[] { "Download", "Upload" })
            {
                var primary = Control<TextBlock>(window, direction + "RateText");
                var secondary = Control<TextBlock>(window, direction + "MbpsText");
                var panel = (StackPanel)primary.Parent;
                check(ReferenceEquals(primary.Style, window.FindResource("LiveRatePrimaryTextStyle")) &&
                    ReferenceEquals(secondary.Style, window.FindResource("LiveRateSecondaryTextStyle")) &&
                    ReferenceEquals(secondary.Style.BasedOn, window.FindResource("MutedText")) && primary.FontSize > secondary.FontSize &&
                    primary.FontWeight.ToOpenTypeWeight() > secondary.FontWeight.ToOpenTypeWeight() &&
                    ReferenceEquals(primary.Parent, secondary.Parent) && panel.Children.IndexOf(secondary) == panel.Children.IndexOf(primary) + 1,
                    $"{direction} uses shared semantic primary/secondary styles with muted smaller Mbps directly below byte rate");
            }
            var initialCuePositions = new[] { "DownloadRateHelp", "UploadRateHelp" }
                .Select(name => Control<NetworkHelpButton>(window, name).TranslatePoint(new Point(), Card(Control<TextBlock>(window, name.StartsWith("Download", StringComparison.Ordinal) ? "DownloadRateText" : "UploadRateText"))))
                .ToArray();
            var cases = new[]
            {
                new SpeedCase("idle", 0, 0, "0 B/s", "0 B/s", "0.00 Mbps", "0.00 Mbps"),
                new SpeedCase("download-KB-upload-near-zero", 28_800, 8, "28.8 KB/s", "8 B/s", "0.23 Mbps", "0.00 Mbps"),
                new SpeedCase("download-MB-upload-KB", 1_420_000, 1_250, "1.42 MB/s", "1.25 KB/s", "11.36 Mbps", "0.01 Mbps"),
                new SpeedCase("download-MB-upload-GB", 38_400_000, 1_100_000_000, "38.4 MB/s", "1.10 GB/s", "307.20 Mbps", "8800.00 Mbps"),
                new SpeedCase("long-MB-values", 999_999_999, 123_456_789, "1000 MB/s", "123 MB/s", "8000.00 Mbps", "987.65 Mbps")
            };
            foreach (var sample in cases)
            {
                Apply(window, sample.Download, sample.Upload);
                Layout(window);
                check(Text(window, "DownloadRateText") == sample.DownloadText && Text(window, "UploadRateText") == sample.UploadText &&
                    Text(window, "DownloadMbpsText") == sample.DownloadMbps && Text(window, "UploadMbpsText") == sample.UploadMbps,
                    $"Dashboard {sample.Name} renders each byte/Mbps pair from its raw directional sample");
                CheckLayout(window, check, sample.Name);
                HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, "760x600-" + sample.Name + ".png"));
            }
            check(PreservedLabels.Select(name => Text(window, name)).SequenceEqual(preserved),
                "Applying adaptive live speed leaves peak/session/data-usage/current-connection labels unchanged");
            check(new[] { "DownloadRateHelp", "UploadRateHelp" }.Select((name, index) =>
                Control<NetworkHelpButton>(window, name).TranslatePoint(new Point(), Card(Control<TextBlock>(window,
                    index == 0 ? "DownloadRateText" : "UploadRateText"))) == initialCuePositions[index]).All(value => value),
                "Transfer-rate help cues stay beside the stable direction labels as numeric values and units change");

            // Presentation does not depend on usage queries. Remove only an owned
            // test table temporarily; the real database is never opened here.
            RenameOwnedTable(store.DatabasePath, "daily_usage", "owned_speed_usage");
            try
            {
                Apply(window, 5_000_000, 1_000_000);
                check(Text(window, "DownloadRateText") == "5.00 MB/s" && Text(window, "DownloadMbpsText") == "40.00 Mbps" &&
                    Text(window, "UploadRateText") == "1.00 MB/s" && Text(window, "UploadMbpsText") == "8.00 Mbps",
                    "One speed presentation update derives both values without querying even the owned usage table");
            }
            finally { RenameOwnedTable(store.DatabasePath, "owned_speed_usage", "daily_usage"); }
            Apply(window, 28_800, 1_420_000);
            window.Width = 920;
            window.Height = 740;
            Layout(window);
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, "920x740-asymmetric-units.png"));
            window.Width = 760;
            window.Height = 600;
            Layout(window);
            var page = Page(window);
            page.ScrollToEnd();
            Layout(window);
            var connection = Control<TextBlock>(window, "DashboardProfileText");
            var position = connection.TranslatePoint(new Point(), page);
            check(page.VerticalScrollBarVisibility == ScrollBarVisibility.Auto && page.ScrollableHeight > 0 &&
                position.Y >= -0.5 && position.Y + connection.ActualHeight <= page.ActualHeight + 0.5,
                "The minimum Dashboard still scrolls vertically to its existing data usage and current connection");
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, "760x600-dashboard-bottom.png"));
            page.ScrollToTop();
            Layout(window);
            CheckHelp(window, previews, check);
        }
        finally { window.Close(); }
    }

    private static void CheckHelp(MainWindow window, string previews, Action<bool, string> check)
    {
        var download = Control<NetworkHelpButton>(window, "DownloadRateHelp");
        var upload = Control<NetworkHelpButton>(window, "UploadRateHelp");
        foreach (var help in new[] { download, upload })
        {
            var text = (TextBlock)help.HelpToolTip.Content;
            check(help.Term == "Transfer rate" && ReferenceEquals(help.ToolTip, help.HelpToolTip) &&
                text.Text == NetworkHelpText.Get("Transfer rate") && text.Text.Contains("1 MB/s", StringComparison.Ordinal) &&
                text.Text.Contains("8 Mbps", StringComparison.Ordinal) && text.Text.Contains("bytes", StringComparison.OrdinalIgnoreCase) &&
                text.TextWrapping == TextWrapping.Wrap && text.MaxWidth is >= 280 and <= 320 &&
                double.IsFinite(help.HelpToolTip.MaxWidth) && help.Focusable &&
                AutomationProperties.GetName(help) == "Help for Transfer rate",
                "The Dashboard transfer-rate cue reuses centralized accessible wrapped beginner help");
        }
        var extent = Page(window).ExtentWidth;
        download.HelpToolTip.IsOpen = true;
        HistoryOverviewChecks.Pump();
        download.HelpToolTip.UpdateLayout();
        check(download.HelpToolTip.IsVisible && ((TextBlock)download.HelpToolTip.Content).ActualWidth <= 300.5 &&
            ((TextBlock)download.HelpToolTip.Content).ActualHeight > 30 && Math.Abs(Page(window).ExtentWidth - extent) < 0.01,
            "The registered transfer-rate hover popup wraps without widening the 760x600 Dashboard");
        HistoryUsageChecks.SaveTooltip(download.HelpToolTip, System.IO.Path.Combine(previews, "transfer-rate-help-tooltip.png"));
        HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, "760x600-transfer-rate-help-page.png"));
        download.HelpToolTip.IsOpen = false;
        Keyboard.Focus(download);
        HistoryOverviewChecks.Pump();
        check(download.HelpToolTip.IsOpen, "Keyboard focus opens the same transfer-rate explanation");
        download.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Escape)
            { RoutedEvent = Keyboard.KeyDownEvent });
        check(!download.HelpToolTip.IsOpen, "Escape dismisses the focused transfer-rate help");
        typeof(NetworkHelpButton).GetMethod("OnClick", PrivateInstance)!.Invoke(download, null);
        check(download.HelpToolTip.IsOpen, "Click opens transfer-rate help without a new dialog or documentation page");
        Keyboard.Focus(upload);
        HistoryOverviewChecks.Pump();
        check(!download.HelpToolTip.IsOpen && upload.HelpToolTip.IsOpen &&
            ((TextBlock)download.HelpToolTip.Content).Text == ((TextBlock)upload.HelpToolTip.Content).Text,
            "Both stable direction cues share one definition and dismiss the previous help when focus moves");
        Keyboard.Focus(Control<TabItem>(window, "DashboardTab"));
        HistoryOverviewChecks.Pump();
        check(!upload.HelpToolTip.IsOpen, "Leaving the transfer-rate cue closes its native help popup");
    }

    private static void CheckAccounting(string root, string previews, Action<bool, string> check)
    {
        var store = new UsageStore(System.IO.Path.Combine(root, "dashboard-speed-accounting"));
        var clock = new OwnedClock();
        var counters = new CountingCounters(clock);
        using var monitoring = new MonitoringService(store, new HistoryOverviewChecks.OwnedIdentity(
            new NetworkIdentity("WiFi", "Owned speed network", "Owned speed network", HistoryOverviewChecks.AdapterId)),
            vpnVerifier: new VpnRouteVerifier(new VpnDetectionService(new NoTunnel()), new NoRoutes()),
            networkMonitor: counters, timeProvider: clock, icmpMonitor: new IcmpMonitor(new NoPing()),
            reachabilityMonitor: TestReachability.Create());
        var window = HistoryOverviewChecks.CreateWindow(monitoring);
        try
        {
            window.Show();
            HistoryOverviewChecks.StopViewTimer(window);
            monitoring.Start();
            StopSampler(monitoring);
            Wait(() => monitoring.GetSnapshot().Session is not null);
            monitoring.SampleNow();
            clock.Advance(TimeSpan.FromSeconds(2));
            counters.Advance(10_000_000, 2_000_000);
            monitoring.SampleNow();
            window.RefreshDashboard();
            Layout(window);
            var snapshot = monitoring.GetSnapshot();
            check(snapshot.DownloadBytesPerSecond == 5_000_000 && snapshot.UploadBytesPerSecond == 1_000_000 &&
                Text(window, "DownloadRateText") == "5.00 MB/s" && Text(window, "DownloadMbpsText") == "40.00 Mbps" &&
                Text(window, "UploadRateText") == "1.00 MB/s" && Text(window, "UploadMbpsText") == "8.00 Mbps",
                "A single owned two-second physical delta feeds the exact 5 MB/s/40 Mbps and 1 MB/s/8 Mbps pairs");
            check(snapshot.SessionDownloaded == 10_000_000 && snapshot.SessionUploaded == 2_000_000 &&
                snapshot.Session is { DownloadBytes: 10_000_000, UploadBytes: 2_000_000 } &&
                Text(window, "DownloadSessionText") == "App session ↓ " + ByteFormatter.Format(10_000_000) &&
                Text(window, "UploadSessionText") == "App session ↑ " + ByteFormatter.Format(2_000_000) &&
                Text(window, "DownloadPeakText") == "Peak today ↓ 40.00 Mbps" && Text(window, "UploadPeakText") == "Peak today ↑ 8.00 Mbps",
                "Adaptive live units preserve physical/app session byte accounting and existing Mbps peak presentation");
            foreach (var rangeTitle in new[] { "TODAY", "LAST 7 DAYS", "LAST 30 DAYS" })
            {
                check(Text(window, "DashboardUsageRangeText") == rangeTitle &&
                    Text(window, "DashboardUsageTotalText") == ByteFormatter.Format(12_000_000) &&
                    Text(window, "DashboardUsageBreakdownText") == "↓ " + ByteFormatter.Format(10_000_000) + "   ↑ " + ByteFormatter.Format(2_000_000),
                    $"Dashboard {rangeTitle} summary remains a cumulative byte quantity rather than live speed units");
                typeof(Button).GetMethod("OnClick", PrivateInstance)!.Invoke(Control<Button>(window, "DashboardUsageSelectorButton"), null);
            }
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, "760x600-owned-same-sample.png"));
            var reads = counters.Reads;
            var viewTimer = Field<object>(window, "_timer");
            var workerNames = new[] { "_timer", "_profileMonitor", "_icmpMonitor", "_reachabilityMonitor", "_vpnSessions" };
            var workers = workerNames.Select(name => Field<object>(monitoring, name)).ToArray();
            var baseline = Field<object>(monitoring, "_previousSnapshot");
            var usage = monitoring.GetUsageTotals();
            var eventCount = monitoring.History.GetRecentEvents().Count;
            for (var i = 0; i < 12; i++)
            {
                Apply(window, i * 1_000_000d, i * 1000d);
                window.RefreshDashboard();
            }
            Layout(window);
            var after = monitoring.GetSnapshot();
            check(counters.Reads == reads && ReferenceEquals(viewTimer, Field<object>(window, "_timer")) &&
                workerNames.Select((name, index) => ReferenceEquals(workers[index], Field<object>(monitoring, name))).All(value => value) &&
                ReferenceEquals(baseline, Field<object>(monitoring, "_previousSnapshot")) &&
                typeof(MainWindow).GetFields(PrivateInstance).Count(field => field.FieldType == typeof(System.Windows.Threading.DispatcherTimer)) == 1,
                "Repeated presentation refresh reuses the existing timer/workers and performs no additional adapter measurement queries");
            check(after.DownloadBytesPerSecond == snapshot.DownloadBytesPerSecond && after.UploadBytesPerSecond == snapshot.UploadBytesPerSecond &&
                after.SessionDownloaded == snapshot.SessionDownloaded && after.SessionUploaded == snapshot.SessionUploaded &&
                after.Session!.Id == snapshot.Session!.Id && monitoring.History.GetRecentSessions().Count == 1 &&
                monitoring.History.GetRecentEvents().Count == eventCount && monitoring.GetUsageTotals() == usage,
                "Refreshing both speed labels leaves raw rates, session identity, daily accounting and history records unchanged");
            HistoryOverviewChecks.SelectHistory(window);
            var chart = Control<HistoryUsageChart>(window, "HistoryUsageChart");
            check(Text(window, "HistoryTodayDownloadText") == ByteFormatter.Format(10_000_000) &&
                Text(window, "HistoryTodayUploadText") == ByteFormatter.Format(2_000_000) &&
                chart.Data.Points[^1] is { DownloadBytes: 10_000_000, UploadBytes: 2_000_000 } &&
                Control<ItemsControl>(window, "HistoryRecentSessions").Items.Count == 1,
                "History 001/002 continue to consume the same unmodified active-session bytes");
            Control<TabControl>(window, "MainNavigation").SelectedItem = Control<TabItem>(window, "DashboardTab");
            clock.Advance(TimeSpan.FromSeconds(1));
            monitoring.SampleNow();
            window.RefreshDashboard();
            Layout(window);
            check(Text(window, "DownloadRateText") == "0 B/s" && Text(window, "DownloadMbpsText") == "0.00 Mbps" &&
                Text(window, "UploadRateText") == "0 B/s" && Text(window, "UploadMbpsText") == "0.00 Mbps" &&
                Text(window, "DownloadPeakText") == "Peak today ↓ 40.00 Mbps" &&
                monitoring.GetSnapshot().SessionDownloaded == 10_000_000,
                "A later idle sample resets both live formats together while leaving peaks and cumulative usage intact");
        }
        finally { window.Close(); }
    }

    private static void CheckLayout(MainWindow window, Action<bool, string> check, string state)
    {
        var page = Page(window);
        var download = Control<TextBlock>(window, "DownloadRateText");
        var upload = Control<TextBlock>(window, "UploadRateText");
        var cards = new[] { Card(download), Card(upload) };
        var root = Control<StackPanel>(window, "DashboardContent");
        var roots = cards.Select(card => card.TransformToAncestor(root).TransformBounds(new Rect(0, 0, card.ActualWidth, card.ActualHeight))).ToArray();
        var valuesFit = new[] { "DownloadRateText", "DownloadMbpsText", "UploadRateText", "UploadMbpsText", "DownloadPeakText", "UploadPeakText" }
            .All(name =>
            {
                var value = Control<TextBlock>(window, name);
                var card = name.StartsWith("Download", StringComparison.Ordinal) ? cards[0] : cards[1];
                var bounds = value.TransformToAncestor(card).TransformBounds(new Rect(0, 0, value.ActualWidth, value.ActualHeight));
                value.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                return bounds.Left >= -0.5 && bounds.Right <= card.ActualWidth + 0.5 && value.DesiredSize.Width <= value.ActualWidth + 0.5;
            });
        check(page.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && page.ScrollableWidth < 0.01 && valuesFit &&
            Math.Abs(cards[0].ActualWidth - cards[1].ActualWidth) < 0.5 && Math.Abs(cards[0].ActualHeight - cards[1].ActualHeight) < 0.5 &&
            Math.Abs(roots[0].Top - roots[1].Top) < 0.5 &&
            ((Grid)cards[0].Parent).Children.OfType<Border>().Count() == 2,
            $"Dashboard {state} keeps two aligned readable speed cards at 760x600 without horizontal overflow");
        Layout(window);
    }
    private static Border Card(TextBlock value) => (Border)((StackPanel)value.Parent).Parent;
    private static void Apply(MainWindow window, double download, double upload) =>
        typeof(MainWindow).GetMethod("ApplyLiveSpeed", PrivateInstance)!.Invoke(window, new object[] { download, upload });
    private static void RenameOwnedTable(string path, string from, string to)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {from} RENAME TO {to};";
        command.ExecuteNonQuery();
    }
    private static T Control<T>(Window window, string name) where T : class => HistoryOverviewChecks.Control<T>(window, name);
    private static string Text(Window window, string name) => Control<TextBlock>(window, name).Text;
    private static ScrollViewer Page(MainWindow window) => (ScrollViewer)Control<TabItem>(window, "DashboardTab").Content;
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, PrivateInstance)!.GetValue(instance)!;
    private static void StopSampler(MonitoringService monitoring) => Field<System.Threading.Timer>(monitoring, "_timer").Change(Timeout.Infinite, Timeout.Infinite);
    private static void Layout(Window window) { window.UpdateLayout(); HistoryOverviewChecks.Pump(); }
    private static void Wait(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Owned speed sample was not ready.");
            HistoryOverviewChecks.Pump();
        }
        HistoryOverviewChecks.Pump();
    }
    private sealed record SpeedCase(string Name, double Download, double Upload, string DownloadText, string UploadText,
        string DownloadMbps, string UploadMbps);
    private sealed class OwnedClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(new DateTime(2026, 10, 12, 12, 0, 0),
            TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 12))).UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }
    private sealed class CountingCounters(TimeProvider clock) : INetworkMonitor
    {
        private long _download;
        private long _upload;
        public int Reads { get; private set; }
        public void Advance(long download, long upload) { _download += download; _upload += upload; }
        public IReadOnlyList<AdapterInfo> GetActiveAdapters()
        {
            Reads++;
            return [new(HistoryOverviewChecks.AdapterId, "Owned speed adapter", "Deterministic speed counters", "Ethernet", 1_000_000_000, "192.0.2.10", true)];
        }
        public NetworkSnapshot? Capture(string adapterId)
        {
            Reads++;
            return new(_download, _upload, clock.GetUtcNow());
        }
        public NetworkAdapterDetails? GetAdapterDetails(string adapterId)
        {
            Reads++;
            return new(adapterId, OperationalStatus.Up, [IPAddress.Parse("192.0.2.10")], [], [], 1_000_000_000);
        }
    }
    private sealed class NoTunnel : IVpnInterfaceSource { public VpnInterfaceInventory Read() => new([], []); }
    private sealed class NoRoutes : IVpnRouteSource { public VpnRouteInventory Read(AddressFamily family) => new(family, [], Availability: IpFamilyAvailability.Unavailable); }
    private sealed class NoPing : IPingSource
    {
        public Task<IcmpPingReply> SendAsync(IPAddress target, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new IcmpPingReply(IPStatus.TimedOut));
    }
}
