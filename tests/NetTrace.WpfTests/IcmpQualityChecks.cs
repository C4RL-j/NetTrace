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
using System.Windows.Threading;
using NetTrace;
using NetTrace.Controls;
using NetTrace.Models;
using NetTrace.Presentation;
using NetTrace.Services;

internal static class IcmpQualityChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(string root, string adapterId, Action<bool, string> check)
    {
        var store = new UsageStore(System.IO.Path.Combine(root, "icmp-quality"));
        var history = new ConnectionHistoryStore(store);
        var clock = new ControlledClock();
        var source = new ControlledPingSource();
        var counters = new OwnedCounters(adapterId, clock);
        using var icmp = new IcmpMonitor(source, interval: TimeSpan.FromMilliseconds(40),
            timeout: TimeSpan.FromMinutes(5), timeProvider: clock);
        using var monitoring = new MonitoringService(store, new OwnedIdentity(adapterId),
            vpnVerifier: new VpnRouteVerifier(new VpnDetectionService(new NoTunnel()), new NoRoutes()),
            networkMonitor: counters, timeProvider: clock, icmpMonitor: icmp, reachabilityMonitor: TestReachability.Create());
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
        WaitWithoutUi(() => source.Calls == 1 && history.GetActiveSession() is not null);
        var physicalId = history.GetActiveSession()!.Id;
        var worker = icmp.Completion;
        monitoring.SampleNow();
        var baseline = Baseline(monitoring);
        var captures = counters.Captures;
        check(context.Dashboard is null && monitoring.RollingIcmpSamples.Count == 0 && source.Active == 1,
            "ICMP quality background startup collects through the existing worker without a dashboard or completed sample");

        context.ShowDashboard();
        var window = context.Dashboard!;
        var navigation = Control<TabControl>(window, "MainNavigation");
        var health = Control<TabItem>(window, "HealthTab");
        var timer = ViewTimer(window);
        timer.Stop();
        navigation.SelectedItem = health;
        window.UpdateLayout();
        Pump();
        var line = Control<IcmpStabilityChart>(window, "IcmpStabilityChart");
        var loss = Control<IcmpPacketLossChart>(window, "IcmpPacketLossChart");
        var quality = Control<TextBlock>(window, "IcmpQualityText");
        var content = Control<StackPanel>(window, "HealthContent");
        check(quality.Text == "Measuring" && Summary(window) is { Quality: IcmpQuality.Measuring, SampleCount: 0 } &&
            Control<TextBlock>(window, "HealthWaitingText").Visibility == Visibility.Visible,
            "Entering Health immediately displays Measuring before any completed ICMP cycle");
        var cards = content.Children.OfType<Border>().ToArray();
        check(cards.Length == 2 && VisualAncestors(quality).Contains(cards[0]) &&
            FindText(cards[0]).Any(text => text == "ICMP quality") && quality.FontWeight == FontWeights.SemiBold &&
            Equals(quality.Foreground, window.FindResource("AccentBrush")) && quality.IsVisible,
            "ICMP quality is a readable text field using the existing accent in the upper card without adding another card");
        SavePreview(window, root, "measuring");

        CompleteCycle(monitoring, source, clock, 40);
        window.RefreshDashboard();
        check(quality.Text == "Measuring" && Summary(window).SampleCount == 1 && line.Data.Count == 1,
            "One completed successful cycle still displays Measuring while preserving the existing chart sample");
        CompleteCycle(monitoring, source, clock, 40);
        window.RefreshDashboard();
        check(quality.Text == "Measuring" && Summary(window).SampleCount == 2 && line.Data.Count == 2,
            "Two completed successful cycles still display Measuring without prematurely classifying ICMP quality");
        timer.Start();
        CompleteCycle(monitoring, source, clock, 40);
        Wait(() => quality.Text == "Excellent");
        check(Summary(window) is { Quality: IcmpQuality.Excellent, SampleCount: 3, RecentLatencyMs: 40,
                RecentJitterMs: 0, RecentPacketLossPercent: 0 } && SharedHistory(monitoring, line, loss),
            "The existing view timer displays Excellent at three completed cycles using the same history as both charts");
        SavePreview(window, root, "excellent");
        timer.Stop();
        var stableSummary = Summary(window);
        var stableChart = line.Data;
        for (var repeat = 0; repeat < 4; repeat++) window.RefreshDashboard();
        check(ReferenceEquals(stableSummary, Summary(window)) && ReferenceEquals(stableChart, line.Data) &&
            ReferenceEquals(timer, ViewTimer(window)),
            "Identical visible refreshes reuse the quality summary and chart snapshot without another presentation timer");

        FillWindow(monitoring, source, clock, 75);
        window.RefreshDashboard();
        check(quality.Text == "Good" && Summary(window) is { Quality: IcmpQuality.Good, SampleCount: 12, RecentLatencyMs: 75 } &&
            Control<TextBlock>(window, "IcmpLatencyText").Text == "75 ms" && SharedHistory(monitoring, line, loss),
            "A recent twelve-cycle Good window updates quality while preserving raw latency and synchronized charts");
        SavePreview(window, root, "good");
        FillWindow(monitoring, source, clock, 150);
        window.RefreshDashboard();
        check(quality.Text == "Fair" && Summary(window) is { Quality: IcmpQuality.Fair, SampleCount: 12, RecentLatencyMs: 150 },
            "A recent twelve-cycle Fair window displays the human-readable classification in the existing Health card");
        SavePreview(window, root, "fair");
        FillWindow(monitoring, source, clock, 250);
        window.RefreshDashboard();
        check(quality.Text == "Poor" && Summary(window) is { Quality: IcmpQuality.Poor, SampleCount: 12, RecentLatencyMs: 250 },
            "A recent twelve-cycle Poor window displays Poor without an internet connectivity label");
        SavePreview(window, root, "poor");
        FillWindow(monitoring, source, clock, null);
        window.RefreshDashboard();
        check(quality.Text == "Unavailable" && Summary(window) is { Quality: IcmpQuality.Unavailable, SampleCount: 12,
                RecentLatencyMs: null, RecentJitterMs: null, RecentPacketLossPercent: 100 } &&
            Control<TextBlock>(window, "IcmpLatencyText").Text == "Unavailable" &&
            Control<TextBlock>(window, "IcmpJitterText").Text == "Unavailable" &&
            Control<TextBlock>(window, "IcmpPacketLossText").Text == "100%" &&
            Control<TextBlock>(window, "IcmpRepliesText").Text == "0 / 4",
            "Twelve all-failed cycles display Unavailable rather than Poor, retain 100% loss and never manufacture latency");
        SavePreview(window, root, "unavailable");
        check(FindText(cards[0]).Any(text => text.Contains("ICMP replies can be blocked", StringComparison.Ordinal) &&
                text.Contains("does not determine internet availability", StringComparison.Ordinal)) &&
            !FindText(cards[0]).Any(text => text is "Offline" or "Online" or "No Internet" or "Internet quality" or "Internet status"),
            "All ICMP quality states retain the blocked-replies disclaimer and avoid internet availability terminology");

        var inactiveSummary = Summary(window);
        var inactiveChart = line.Data;
        navigation.SelectedItem = Control<TabItem>(window, "ConnectionsTab");
        Pump();
        FillWindow(monitoring, source, clock, 40);
        window.RefreshDashboard();
        Pump();
        check(ReferenceEquals(inactiveSummary, Summary(window)) && ReferenceEquals(inactiveChart, line.Data) &&
            monitoring.RollingIcmpSamples.Count == 60 && monitoring.RollingIcmpSamples[^1].AverageLatencyMs == 40,
            "Inactive Health avoids quality evaluation and chart presentation while tray monitoring keeps its bounded sixty-cycle history");
        var historyBeforeReturn = monitoring.RollingIcmpSamples;
        navigation.SelectedItem = health;
        window.UpdateLayout();
        Pump();
        check(quality.Text == "Excellent" && Summary(window) is { SampleCount: 12, RecentLatencyMs: 40, RecentPacketLossPercent: 0 } &&
            SharedHistory(monitoring, line, loss) && monitoring.RollingIcmpSamples.Any(sample => sample.PacketLossPercent == 100),
            "Returning to Health immediately classifies only the newest twelve cycles while older failed cycles remain in sixty-cycle chart history");
        var returnedChart = line.Data;
        for (var repeat = 0; repeat < 3; repeat++)
        {
            navigation.SelectedItem = Control<TabItem>(window, "DashboardTab");
            Pump();
            navigation.SelectedItem = health;
            Pump();
        }
        check(quality.Text == "Excellent" && ReferenceEquals(returnedChart, line.Data) &&
            monitoring.RollingIcmpSamples.SequenceEqual(historyBeforeReturn) && source.Active == 1,
            "Repeated page changes neither alter raw history nor restart probes or rebuild unchanged chart data");

        var hiddenSummary = Summary(window);
        window.Hide();
        Pump();
        FillWindow(monitoring, source, clock, 75);
        window.RefreshDashboard();
        Pump();
        check(!timer.IsEnabled && ReferenceEquals(hiddenSummary, Summary(window)) &&
            quality.Text == "Excellent" && monitoring.RollingIcmpSamples[^1].AverageLatencyMs == 75,
            "Hidden dashboards stop presentation work while the existing ICMP worker continues measuring new history");
        window.Show();
        window.UpdateLayout();
        Pump();
        check(timer.IsEnabled && quality.Text == "Good" && Summary(window).RecentLatencyMs == 75 && SharedHistory(monitoring, line, loss),
            "Showing a hidden Health page immediately displays Good from the latest completed history");
        var minimizedSummary = Summary(window);
        window.WindowState = WindowState.Minimized;
        Pump();
        FillWindow(monitoring, source, clock, 150);
        window.RefreshDashboard();
        Pump();
        check(!timer.IsEnabled && ReferenceEquals(minimizedSummary, Summary(window)) && quality.Text == "Good" &&
            monitoring.RollingIcmpSamples[^1].AverageLatencyMs == 150,
            "Minimized dashboards skip quality evaluation while new completed measurements remain available in memory");
        window.WindowState = WindowState.Normal;
        window.UpdateLayout();
        Pump();
        check(timer.IsEnabled && quality.Text == "Fair" && Summary(window).RecentLatencyMs == 150 && SharedHistory(monitoring, line, loss),
            "Restoring a minimized Health page immediately reflects its latest Fair window without new probes");
        CheckSmallLayout(window, quality, line, loss, root, check);

        var closedSummary = Summary(window);
        var closedChart = line.Data;
        window.Close();
        FillWindow(monitoring, source, clock, 250);
        window.RefreshDashboard();
        Pump();
        check(context.Dashboard is null && !timer.IsEnabled && ReferenceEquals(closedSummary, Summary(window)) &&
            ReferenceEquals(closedChart, line.Data) && history.GetActiveSession()!.Id == physicalId,
            "Closing the dashboard freezes its quality field and charts while tray monitoring retains the physical connection session");
        context.ShowDashboard();
        var recreated = context.Dashboard!;
        Control<TabControl>(recreated, "MainNavigation").SelectedItem = Control<TabItem>(recreated, "HealthTab");
        recreated.UpdateLayout();
        Pump();
        check(created == 2 && Control<TextBlock>(recreated, "IcmpQualityText").Text == "Poor" &&
            Summary(recreated) is { SampleCount: 12, RecentLatencyMs: 250 } &&
            SharedHistory(monitoring, Control<IcmpStabilityChart>(recreated, "IcmpStabilityChart"),
                Control<IcmpPacketLossChart>(recreated, "IcmpPacketLossChart")),
            "Tray Open recreates Health with its current Poor classification and existing sixty-cycle chart history");
        check(ReferenceEquals(worker, icmp.Completion) && source.MaximumConcurrent == 1 &&
            ReferenceEquals(baseline, Baseline(monitoring)) && counters.Captures == captures &&
            history.GetActiveSession()!.Id == physicalId && monitoring.GetSnapshot().SessionDownloaded == 0 &&
            typeof(MainWindow).GetFields(PrivateInstance).Count(field => field.FieldType == typeof(DispatcherTimer)) == 1,
            "Quality navigation and tray recreation preserve one ICMP worker, one view timer, bandwidth baseline and physical session");
        context.Exit();
        icmp.Completion.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        WaitWithoutUi(() => source.Active == 0);
        check(exits == 1 && !monitoring.GetSnapshot().IsRunning && history.GetActiveSession() is null &&
            history.GetRecentSessions().Single().EndReason == "ApplicationExit" && !ViewTimer(recreated).IsEnabled,
            "Tray Exit cancels the existing ICMP worker and closes monitoring cleanly without a quality-specific lifecycle");
    }

    private static void CheckSmallLayout(MainWindow window, TextBlock quality, IcmpStabilityChart line,
        IcmpPacketLossChart loss, string root, Action<bool, string> check)
    {
        window.Width = 760;
        window.Height = 600;
        window.UpdateLayout();
        Pump();
        var scroll = VisualAncestors(quality).OfType<ScrollViewer>().First();
        scroll.ScrollToTop();
        window.UpdateLayout();
        Pump();
        var position = quality.TranslatePoint(new Point(), scroll);
        check(quality.ActualWidth >= 100 && quality.ActualHeight >= 14 && position.X >= 0 && position.Y >= 0 &&
            position.Y + quality.ActualHeight <= scroll.ActualHeight &&
            scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && scroll.ScrollableWidth < 0.01 &&
            Math.Abs(line.ActualWidth - loss.ActualWidth) < 0.01 && line.Height == 110 && loss.Height == 70,
            "At 760 by 600 ICMP quality remains readable in the original two-card vertical layout without horizontal overflow");
        SavePreview(window, root, "760x600-health");
        scroll.ScrollToBottom();
        window.UpdateLayout();
        Pump();
        var stripPosition = loss.TranslatePoint(new Point(), scroll);
        check(stripPosition.Y >= 0 && stripPosition.Y + loss.ActualHeight <= scroll.ActualHeight && loss.ActualWidth >= 400,
            "The existing small-window vertical scroll still exposes the complete compact packet-loss strip below the line chart");
        window.Width = 920;
        window.Height = 740;
        scroll.ScrollToTop();
        window.UpdateLayout();
        Pump();
    }

    private static void SavePreview(MainWindow window, string root, string name)
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
                Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
            }, null, new Rect(margin.Left, margin.Top, content.ActualWidth, content.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var path = System.IO.Path.Combine(root, "icmp-quality-previews", name + ".png");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = System.IO.File.Create(path);
        encoder.Save(output);
    }

    private static bool SharedHistory(MonitoringService monitoring, IcmpStabilityChart line, IcmpPacketLossChart loss)
        => ReferenceEquals(line.Data, loss.Data) && line.Data.Samples.SequenceEqual(monitoring.RollingIcmpSamples);
    private static IcmpQualitySummary Summary(MainWindow window) =>
        typeof(MainWindow).GetField("_qualitySummary", PrivateInstance)!.GetValue(window) as IcmpQualitySummary
        ?? throw new InvalidOperationException("Health has no ICMP quality presentation summary.");
    private static T Control<T>(Window window, string name) where T : class => window.FindName(name) as T
        ?? throw new InvalidOperationException($"Missing ICMP quality check control {name}.");
    private static DispatcherTimer ViewTimer(MainWindow window) =>
        (DispatcherTimer)typeof(MainWindow).GetField("_timer", PrivateInstance)!.GetValue(window)!;
    private static object? Baseline(MonitoringService monitoring) =>
        typeof(MonitoringService).GetField("_previousSnapshot", PrivateInstance)!.GetValue(monitoring);
    private static void StopSampler(MonitoringService monitoring) =>
        ((System.Threading.Timer)typeof(MonitoringService).GetField("_timer", PrivateInstance)!.GetValue(monitoring)!)
            .Change(Timeout.Infinite, Timeout.Infinite);

    private static void FillWindow(MonitoringService monitoring, ControlledPingSource source, ControlledClock clock, long? latency)
    {
        for (var cycle = 0; cycle < 12; cycle++) CompleteCycle(monitoring, source, clock, latency);
    }

    private static void CompleteCycle(MonitoringService monitoring, ControlledPingSource source, ControlledClock clock, long? latency)
    {
        var previous = monitoring.LatestIcmpMeasurement;
        clock.Advance(TimeSpan.FromSeconds(15));
        var timestamp = clock.GetUtcNow();
        var reply = latency is { } value ? new IcmpPingReply(IPStatus.Success, value) : new IcmpPingReply(IPStatus.TimedOut, 9999);
        source.Queue(Enumerable.Repeat(reply, 4));
        WaitWithoutUi(() => monitoring.LatestIcmpMeasurement is { } latest &&
            !ReferenceEquals(previous, latest) && latest.TimestampUtc == timestamp && source.Active == 1);
    }

    private static IEnumerable<DependencyObject> VisualAncestors(DependencyObject child)
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            yield return parent;
    }

    private static IEnumerable<string> FindText(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is TextBlock text) yield return text.Text;
            foreach (var value in FindText(child)) yield return value;
        }
    }

    private static void Wait(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("The ICMP quality UI did not reach its expected state.");
            Pump();
        }
        Pump();
    }

    private static void WaitWithoutUi(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("The owned ICMP quality probe did not reach its expected state.");
            Thread.Sleep(5);
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private sealed class ControlledPingSource : IPingSource
    {
        private readonly Channel<IcmpPingReply> _replies = Channel.CreateUnbounded<IcmpPingReply>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });
        private int _calls;
        private int _active;
        private int _maximum;
        public int Calls => Volatile.Read(ref _calls);
        public int Active => Volatile.Read(ref _active);
        public int MaximumConcurrent => Volatile.Read(ref _maximum);
        public void Queue(IEnumerable<IcmpPingReply> replies)
        {
            foreach (var reply in replies)
                if (!_replies.Writer.TryWrite(reply)) throw new InvalidOperationException("The owned quality ping queue is closed.");
        }
        public async Task<IcmpPingReply> SendAsync(IPAddress target, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var active = Interlocked.Increment(ref _active);
            while (true)
            {
                var previous = Volatile.Read(ref _maximum);
                if (active <= previous || Interlocked.CompareExchange(ref _maximum, active, previous) == previous) break;
            }
            try { return await _replies.Reader.ReadAsync(cancellationToken).ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class ControlledClock : TimeProvider
    {
        private long _utcTicks = new DateTimeOffset(2026, 10, 11, 4, 5, 6, TimeSpan.Zero).Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _utcTicks, duration.Ticks);
    }

    private sealed class OwnedIdentity(string adapterId) : INetworkIdentityService
    {
        public event EventHandler? NetworkChanged { add { } remove { } }
        public NetworkIdentityResult GetCurrentNetwork() => new(NetworkIdentityAvailability.Available,
            new NetworkIdentity("WiFi", "ICMP_QUALITY", "ICMP_QUALITY", adapterId), null);
        public void Dispose() { }
    }

    private sealed class OwnedCounters(string adapterId, TimeProvider clock) : INetworkMonitor
    {
        public int Captures { get; private set; }
        public IReadOnlyList<AdapterInfo> GetActiveAdapters() =>
            [new(adapterId, "Owned ICMP quality adapter", "Injected physical adapter", "Wireless80211", 86_000_000, "192.0.2.10", true)];
        public NetworkSnapshot? Capture(string requestedAdapter)
        {
            Captures++;
            return requestedAdapter == adapterId ? new(1000, 500, clock.GetUtcNow()) : null;
        }
    }

    private sealed class ReadOnlyStartup : IWindowsStartupRegistration
    {
        public bool IsEnabled() => false;
        public void Enable() => throw new InvalidOperationException("Startup writes are prohibited in ICMP quality checks.");
        public void Disable() => throw new InvalidOperationException("Startup writes are prohibited in ICMP quality checks.");
    }

    private sealed class NoTunnel : IVpnInterfaceSource
    {
        public VpnInterfaceInventory Read() => new([], []);
    }

    private sealed class NoRoutes : IVpnRouteSource
    {
        public VpnRouteInventory Read(AddressFamily family) => new(family, [], Availability: IpFamilyAvailability.Unavailable);
    }
}
