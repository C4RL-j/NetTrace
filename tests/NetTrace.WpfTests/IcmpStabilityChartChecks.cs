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
using NetTrace.Services;

internal static class IcmpStabilityChartChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(string root, string adapterId, Action<bool, string> check)
    {
        var store = new UsageStore(System.IO.Path.Combine(root, "icmp-stability-chart"));
        var history = new ConnectionHistoryStore(store);
        var clock = new ControlledClock();
        var pings = new ControlledPingSource();
        var counters = new OwnedCounters(adapterId, clock);
        using var icmp = new IcmpMonitor(pings, interval: TimeSpan.FromMilliseconds(40),
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
        WaitWithoutUi(() => pings.Calls == 1 && history.GetActiveSession() is not null);
        var physicalId = history.GetActiveSession()!.Id;
        var worker = icmp.Completion;
        monitoring.SampleNow();
        var baseline = Baseline(monitoring);
        var captures = counters.Captures;

        CompleteCycle(monitoring, pings, clock, Success(20), Success(30), Success(20), Success(10));
        CompleteCycle(monitoring, pings, clock, Failure(), Failure(), Failure(), Failure());
        CompleteCycle(monitoring, pings, clock, Failure(), Success(42), Failure(), Failure());
        check(context.Dashboard is null && monitoring.RollingIcmpSamples.Count == 3 &&
            monitoring.GetSnapshot().IsRunning,
            "Completed chart history accumulates in background mode before any window exists");

        context.ShowDashboard();
        var window = context.Dashboard!;
        var chart = Control<IcmpStabilityChart>(window, "IcmpStabilityChart");
        var navigation = Control<TabControl>(window, "MainNavigation");
        var health = Control<TabItem>(window, "HealthTab");
        var viewTimer = ViewTimer(window);
        check(chart.Data.Count == 0 && Control<TabItem>(window, "DashboardTab").IsSelected,
            "A dashboard window does not prepare chart data while the Health page is inactive");
        viewTimer.Stop();
        navigation.SelectedItem = health;
        window.UpdateLayout();
        Pump();
        check(chart.Data.Count == 3 && chart.Data.Samples.SequenceEqual(monitoring.RollingIcmpSamples) && !viewTimer.IsEnabled,
            "Opening Health immediately consumes existing history without waiting for a view timer or another probe");
        check(chart.Data.LatencySegments.Count == 2 && chart.Data.LatencySegments.All(segment => segment.Count == 1) &&
            chart.Data.LatencySegments[0][0].Position == 0 && chart.Data.LatencySegments[0][0].Milliseconds == 20 &&
            chart.Data.LatencySegments[1][0].Position == 1 && chart.Data.LatencySegments[1][0].Milliseconds == 42 &&
            chart.Data.JitterSegments.Count == 1 && chart.Data.JitterSegments[0].Single().Milliseconds == 10,
            "The native chart preserves singleton readings and real gaps for all-failed and insufficient-success cycles");
        check(chart.ActualWidth > 100 && chart.ActualHeight > 100 && chart.IsVisible,
            "The Connection Stability chart receives visible bounded layout space in the existing Health page");

        var initialData = chart.Data;
        for (var repeat = 0; repeat < 4; repeat++) window.RefreshDashboard();
        check(ReferenceEquals(initialData, chart.Data) && ReferenceEquals(viewTimer, ViewTimer(window)),
            "Repeated refreshes of the same completed cycle reuse chart presentation data and the existing view timer");

        viewTimer.Start();
        CompleteCycle(monitoring, pings, clock, Success(50), Success(60), Success(50), Success(40));
        Wait(() => chart.Data.Count == 4);
        check(!ReferenceEquals(initialData, chart.Data) && chart.Data.Samples.SequenceEqual(monitoring.RollingIcmpSamples) &&
            chart.Data.LatencySegments[^1][^1].Milliseconds == 50,
            "A new completed cycle updates the visible chart through the existing window refresh mechanism");
        viewTimer.Stop();
        var visibleData = chart.Data;

        navigation.SelectedItem = Control<TabItem>(window, "ConnectionsTab");
        Pump();
        CompleteCycle(monitoring, pings, clock, Success(80), Success(80), Success(80), Success(80));
        window.RefreshDashboard();
        Pump();
        check(ReferenceEquals(visibleData, chart.Data) && monitoring.RollingIcmpSamples.Count == 5 &&
            ReferenceEquals(worker, icmp.Completion) && pings.MaximumConcurrent == 1,
            "Leaving Health stops chart updates while the same background ICMP worker continues producing samples");
        navigation.SelectedItem = health;
        window.UpdateLayout();
        Pump();
        check(chart.Data.Count == 5 && chart.Data.Samples.SequenceEqual(monitoring.RollingIcmpSamples),
            "Returning to Health immediately replaces its presentation with current history without duplicate points");
        var returnedData = chart.Data;
        for (var repeat = 0; repeat < 3; repeat++)
        {
            navigation.SelectedItem = Control<TabItem>(window, "DashboardTab");
            Pump();
            navigation.SelectedItem = health;
            Pump();
        }
        check(ReferenceEquals(returnedData, chart.Data) && chart.Data.Count == 5,
            "Repeated page returns with unchanged history do not rebuild or append duplicate chart data");

        window.Hide();
        Pump();
        check(!viewTimer.IsEnabled && !chart.IsVisible && monitoring.GetSnapshot().IsRunning,
            "Hiding the dashboard pauses its existing view timer without stopping monitoring");
        CompleteCycle(monitoring, pings, clock, Success(90), Success(100), Success(90), Success(80));
        window.RefreshDashboard();
        Pump();
        check(ReferenceEquals(returnedData, chart.Data) && monitoring.RollingIcmpSamples.Count == 6,
            "A hidden Health page does not transform new samples even when a dashboard refresh is explicitly requested");
        window.Show();
        window.UpdateLayout();
        Pump();
        check(viewTimer.IsEnabled && chart.Data.Count == 6 && chart.Data.Samples.SequenceEqual(monitoring.RollingIcmpSamples),
            "Showing the dashboard restores its original view timer and immediately displays current chart history");

        var beforeMinimize = chart.Data;
        window.WindowState = WindowState.Minimized;
        Pump();
        check(!viewTimer.IsEnabled && monitoring.GetSnapshot().IsRunning,
            "Minimizing the dashboard pauses presentation refreshes while background monitoring stays active");
        CompleteCycle(monitoring, pings, clock, Success(120), Success(130), Success(120), Success(110));
        window.RefreshDashboard();
        Pump();
        check(ReferenceEquals(beforeMinimize, chart.Data) && monitoring.RollingIcmpSamples.Count == 7,
            "A minimized Health page does not update chart data for completed background measurements");
        window.WindowState = WindowState.Normal;
        window.UpdateLayout();
        Pump();
        check(viewTimer.IsEnabled && chart.Data.Count == 7 && chart.Data.Samples.SequenceEqual(monitoring.RollingIcmpSamples),
            "Restoring the dashboard immediately displays samples collected while minimized");
        check(history.GetActiveSession()!.Id == physicalId && ReferenceEquals(baseline, Baseline(monitoring)) &&
            counters.Captures == captures && monitoring.GetSnapshot().SessionDownloaded == 0 &&
            ReferenceEquals(worker, icmp.Completion) && pings.MaximumConcurrent == 1,
            "Chart navigation, hide and minimize preserve the bandwidth baseline, physical session and single ICMP worker");

        var closedData = chart.Data;
        window.Close();
        CompleteCycle(monitoring, pings, clock, Success(150), Success(140), Success(150), Success(160));
        window.RefreshDashboard();
        Pump();
        check(context.Dashboard is null && !viewTimer.IsEnabled && ReferenceEquals(closedData, chart.Data) &&
            monitoring.RollingIcmpSamples.Count == 8 && history.GetActiveSession()!.Id == physicalId,
            "Closing Health to the tray freezes the old chart while completed measurements continue in memory");
        context.ShowDashboard();
        var recreated = context.Dashboard!;
        Control<TabControl>(recreated, "MainNavigation").SelectedItem = Control<TabItem>(recreated, "HealthTab");
        recreated.UpdateLayout();
        Pump();
        var recreatedChart = Control<IcmpStabilityChart>(recreated, "IcmpStabilityChart");
        check(created == 2 && !ReferenceEquals(chart, recreatedChart) && recreatedChart.Data.Count == 8 &&
            recreatedChart.Data.Samples.SequenceEqual(monitoring.RollingIcmpSamples) &&
            ReferenceEquals(worker, icmp.Completion) && pings.MaximumConcurrent == 1,
            "Tray Open recreates a Health chart from the current shared history without another monitoring worker");

        RenderPreview(root, chart.Data.Samples, check);
        context.Exit();
        icmp.Completion.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        WaitWithoutUi(() => pings.Active == 0);
        check(exits == 1 && !monitoring.GetSnapshot().IsRunning && history.GetActiveSession() is null &&
            history.GetRecentSessions().Single().EndReason == "ApplicationExit" && !ViewTimer(recreated).IsEnabled,
            "Tray Exit cancels the existing ICMP worker and closes physical monitoring without a chart lifecycle dependency");
    }

    private static void CompleteCycle(MonitoringService monitoring, ControlledPingSource source,
        ControlledClock clock, params IcmpPingReply[] replies)
    {
        if (replies.Length != IcmpMonitor.AttemptsPerCycle) throw new ArgumentException("Supply one complete ICMP burst.");
        var previous = monitoring.LatestIcmpMeasurement;
        clock.Advance(TimeSpan.FromSeconds(15));
        var timestamp = clock.GetUtcNow();
        source.Queue(replies);
        WaitWithoutUi(() => monitoring.LatestIcmpMeasurement is { } latest &&
            !ReferenceEquals(previous, latest) && latest.TimestampUtc == timestamp && source.Active == 1);
    }

    private static void RenderPreview(string root, IReadOnlyList<IcmpProbeResult> completedSamples,
        Action<bool, string> check)
    {
        // A standalone render exercises the real native control without changing
        // monitoring history or introducing another production view collection.
        var chart = new IcmpStabilityChart { Width = 680, Height = 220 };
        var start = completedSamples[0].TimestampUtc;
        var samples = Enumerable.Range(0, 60).Select(index => new IcmpProbeResult(
            start.AddSeconds(index * 15), "1.1.1.1", index is 15 or 42 ? 0 : 4,
            index is 15 or 42 ? 4 : 0, index is 15 or 42 ? null : 35d + index % 7 * 4,
            index is 15 or 42 ? 100 : 0, index is 15 or 42 ? null : 3d + index % 5)).ToArray();
        chart.UpdateSamples(samples);
        var host = new Window
        {
            Content = chart, SizeToContent = SizeToContent.WidthAndHeight,
            ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
        };
        try
        {
            host.Show();
            host.UpdateLayout();
            Pump();
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                context.DrawRectangle(new SolidColorBrush(Color.FromRgb(15, 23, 42)), null,
                    new Rect(0, 0, chart.Width, chart.Height));
                context.DrawRectangle(new VisualBrush(chart), null, new Rect(0, 0, chart.Width, chart.Height));
            }
            var bitmap = new RenderTargetBitmap((int)chart.Width, (int)chart.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            var pixels = new byte[(int)chart.Width * (int)chart.Height * 4];
            bitmap.CopyPixels(pixels, (int)chart.Width * 4, 0);
            var colors = Enumerable.Range(0, pixels.Length / 4)
                .Select(index => (Blue: pixels[index * 4], Green: pixels[index * 4 + 1], Red: pixels[index * 4 + 2]))
                .ToArray();
            check(chart.Data.Count == 60 && chart.Data.LatencySegments.Count == 3 && chart.Data.JitterSegments.Count == 3 &&
                colors.Any(color => color.Red < 100 && color.Green > 140 && color.Blue > 180) &&
                colors.Any(color => color.Red > 180 && color.Green > 140 && color.Blue < 120),
                "The native WPF renderer draws both real series and preserves gaps in a full 60-cycle chart");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var directory = System.IO.Path.Combine(root, "stability-chart-previews");
            System.IO.Directory.CreateDirectory(directory);
            using var output = System.IO.File.Create(System.IO.Path.Combine(directory, "60-cycles-with-gaps.png"));
            encoder.Save(output);
        }
        finally { host.Close(); }
    }

    private static IcmpPingReply Success(long value) => new(IPStatus.Success, value);
    private static IcmpPingReply Failure() => new(IPStatus.TimedOut, 9999);
    private static T Control<T>(Window window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"Missing stability chart control {name}.");
    private static DispatcherTimer ViewTimer(MainWindow window) =>
        (DispatcherTimer)typeof(MainWindow).GetField("_timer", PrivateInstance)!.GetValue(window)!;
    private static object? Baseline(MonitoringService monitoring) =>
        typeof(MonitoringService).GetField("_previousSnapshot", PrivateInstance)!.GetValue(monitoring);
    private static void StopSampler(MonitoringService monitoring) =>
        ((System.Threading.Timer)typeof(MonitoringService).GetField("_timer", PrivateInstance)!.GetValue(monitoring)!)
            .Change(Timeout.Infinite, Timeout.Infinite);

    private static void Wait(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("The chart did not update using the existing view mechanism.");
            Pump();
        }
        Pump();
    }

    private static void WaitWithoutUi(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("The owned ICMP chart worker did not reach the expected state.");
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
        private int _maximumConcurrent;
        public int Calls => Volatile.Read(ref _calls);
        public int Active => Volatile.Read(ref _active);
        public int MaximumConcurrent => Volatile.Read(ref _maximumConcurrent);

        public void Queue(params IcmpPingReply[] replies)
        {
            foreach (var reply in replies)
                if (!_replies.Writer.TryWrite(reply)) throw new InvalidOperationException("The owned ping reply queue is closed.");
        }

        public async Task<IcmpPingReply> SendAsync(IPAddress target, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var active = Interlocked.Increment(ref _active);
            while (true)
            {
                var previous = Volatile.Read(ref _maximumConcurrent);
                if (active <= previous || Interlocked.CompareExchange(ref _maximumConcurrent, active, previous) == previous) break;
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
            new NetworkIdentity("WiFi", "ICMP_STABILITY_CHART", "ICMP_STABILITY_CHART", adapterId), null);
        public void Dispose() { }
    }

    private sealed class OwnedCounters(string adapterId, TimeProvider clock) : INetworkMonitor
    {
        public int Captures { get; private set; }
        public IReadOnlyList<AdapterInfo> GetActiveAdapters() =>
            [new(adapterId, "Owned chart adapter", "Injected physical adapter", "Wireless80211", 86_000_000, "192.0.2.10", true)];
        public NetworkSnapshot? Capture(string requestedAdapter)
        {
            Captures++;
            return requestedAdapter == adapterId ? new(1000, 500, clock.GetUtcNow()) : null;
        }
    }

    private sealed class ReadOnlyStartup : IWindowsStartupRegistration
    {
        public bool IsEnabled() => false;
        public void Enable() => throw new InvalidOperationException("Startup writes are prohibited in this chart fixture.");
        public void Disable() => throw new InvalidOperationException("Startup writes are prohibited in this chart fixture.");
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
