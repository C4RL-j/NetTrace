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

internal static class IcmpPacketLossChartChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(string root, string adapterId, Action<bool, string> check)
    {
        var store = new UsageStore(System.IO.Path.Combine(root, "icmp-packet-loss-chart"));
        var history = new ConnectionHistoryStore(store);
        var clock = new ControlledClock();
        var source = new ControlledPingSource();
        var counters = new OwnedCounters(adapterId, clock);
        using var icmp = new IcmpMonitor(source, interval: TimeSpan.FromMilliseconds(40),
            timeout: TimeSpan.FromMinutes(5), timeProvider: clock);
        using var monitoring = new MonitoringService(store, new OwnedIdentity(adapterId),
            vpnVerifier: new VpnRouteVerifier(new VpnDetectionService(new NoTunnel()), new NoRoutes()),
            networkMonitor: counters, timeProvider: clock, icmpMonitor: icmp);
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
            "Packet-loss background launch has no dashboard or fake completed samples and uses the existing ping worker");

        context.ShowDashboard();
        var window = context.Dashboard!;
        var line = Control<IcmpStabilityChart>(window, "IcmpStabilityChart");
        var loss = Control<IcmpPacketLossChart>(window, "IcmpPacketLossChart");
        var navigation = Control<TabControl>(window, "MainNavigation");
        var health = Control<TabItem>(window, "HealthTab");
        var timer = ViewTimer(window);
        timer.Stop();
        navigation.SelectedItem = health;
        window.UpdateLayout();
        Pump();
        check(Shared(line, loss) && loss.Data.Count == 0 && !timer.IsEnabled,
            "Entering Health with empty history immediately shares one empty snapshot between both charts");
        var content = Control<StackPanel>(window, "HealthContent");
        var parent = LogicalTreeHelper.GetParent(loss);
        check(parent is StackPanel && ReferenceEquals(parent, LogicalTreeHelper.GetParent(line)) &&
            content.Children.OfType<Border>().Count() == 2 && loss.ActualHeight == 70 &&
            loss.TranslatePoint(new Point(), (UIElement)parent).Y > line.TranslatePoint(new Point(), (UIElement)parent).Y,
            "Packet Loss is a compact strip directly below the existing line chart inside the same second Health card");

        timer.Start();
        CompleteCycle(monitoring, source, clock, Failure(), Failure(), Failure(), Failure());
        Wait(() => loss.Data.Count == 1);
        check(Shared(line, loss) && loss.Data.PacketLossPoints.Single() is { Position: 1, Percent: 100 } &&
            line.Data.LatencySegments.Count == 0 && line.Data.JitterSegments.Count == 0 &&
            Control<TextBlock>(window, "IcmpLatencyText").Text == "Unavailable" &&
            Control<TextBlock>(window, "IcmpJitterText").Text == "Unavailable" &&
            Control<TextBlock>(window, "IcmpPacketLossText").Text == "100%",
            "One completed all-failed cycle retains a newest-position 100% loss bar while latency and jitter remain gaps");
        check(FindText(content).Any(text => text.Contains("ICMP replies can be blocked", StringComparison.Ordinal)) &&
            !FindText(content).Any(text => text is "Offline" or "Internet Down" or "No Internet" or "Connection Failed"),
            "Displaying 100% ICMP loss preserves the disclaimer without declaring internet availability");
        var initial = loss.Data;
        for (var repeat = 0; repeat < 4; repeat++) window.RefreshDashboard();
        check(Shared(line, loss) && ReferenceEquals(initial, loss.Data) && ReferenceEquals(timer, ViewTimer(window)),
            "Repeated identical Health refreshes reuse the shared presentation snapshot and existing view timer");

        CompleteCycle(monitoring, source, clock, Success(10), Success(20), Success(30), Failure());
        Wait(() => loss.Data.Count == 2);
        check(Shared(line, loss) && !ReferenceEquals(initial, loss.Data) &&
            loss.Data.Samples.SequenceEqual(monitoring.RollingIcmpSamples) &&
            loss.Data.PacketLossPoints.Select(point => point.Percent).SequenceEqual(new double?[] { 100, 25 }),
            "A completed partial-loss cycle updates both visible charts together through the existing view timer");
        timer.Stop();
        var visible = loss.Data;
        navigation.SelectedItem = Control<TabItem>(window, "ConnectionsTab");
        Pump();
        CompleteCycle(monitoring, source, clock, Success(20), Failure(), Success(20), Failure());
        window.RefreshDashboard();
        Pump();
        check(Shared(line, loss) && ReferenceEquals(visible, loss.Data) && monitoring.RollingIcmpSamples.Count == 3 &&
            ReferenceEquals(worker, icmp.Completion) && source.MaximumConcurrent == 1,
            "An inactive Health page leaves both charts untouched while the same background worker completes new loss history");
        navigation.SelectedItem = health;
        window.UpdateLayout();
        Pump();
        check(Shared(line, loss) && loss.Data.Count == 3 && loss.Data.PacketLossPoints[^1].Percent == 50 &&
            loss.Data.Samples.SequenceEqual(monitoring.RollingIcmpSamples),
            "Returning to Health immediately displays synchronized current packet-loss history without waiting for a new cycle");
        var returned = loss.Data;
        for (var repeat = 0; repeat < 3; repeat++)
        {
            navigation.SelectedItem = Control<TabItem>(window, "DashboardTab");
            Pump();
            navigation.SelectedItem = health;
            Pump();
        }
        check(Shared(line, loss) && ReferenceEquals(returned, loss.Data) && loss.Data.PacketLossPoints.Count == 3,
            "Repeated page returns with unchanged history neither rebuild the shared snapshot nor duplicate loss bars");

        window.Hide();
        Pump();
        CompleteCycle(monitoring, source, clock, Failure(), Success(40), Failure(), Failure());
        window.RefreshDashboard();
        Pump();
        check(!timer.IsEnabled && !loss.IsVisible && Shared(line, loss) && ReferenceEquals(returned, loss.Data) &&
            monitoring.RollingIcmpSamples.Count == 4,
            "Hiding the window stops presentation refreshes while preserving newly completed background loss samples");
        window.Show();
        window.UpdateLayout();
        Pump();
        check(timer.IsEnabled && Shared(line, loss) && loss.Data.Count == 4 && loss.Data.PacketLossPoints[^1].Percent == 75,
            "Showing the window immediately restores both charts from one current history snapshot");
        var beforeMinimize = loss.Data;
        window.WindowState = WindowState.Minimized;
        Pump();
        CompleteCycle(monitoring, source, clock, Success(0), Success(0), Success(0), Success(0));
        window.RefreshDashboard();
        Pump();
        check(!timer.IsEnabled && Shared(line, loss) && ReferenceEquals(beforeMinimize, loss.Data) &&
            monitoring.RollingIcmpSamples.Count == 5,
            "A minimized Health view does not transform completed loss measurements collected in the background");
        window.WindowState = WindowState.Normal;
        window.UpdateLayout();
        Pump();
        check(timer.IsEnabled && Shared(line, loss) && loss.Data.Count == 5 && loss.Data.PacketLossPoints[^1].Percent == 0,
            "Restoring a minimized window immediately includes the newest zero-loss completed sample");

        CheckSmallLayout(window, line, loss, root, check);
        var closed = loss.Data;
        window.Close();
        CompleteCycle(monitoring, source, clock, Failure(), Failure(), Failure(), Failure());
        window.RefreshDashboard();
        Pump();
        check(context.Dashboard is null && !timer.IsEnabled && ReferenceEquals(closed, loss.Data) &&
            monitoring.RollingIcmpSamples.Count == 6 && history.GetActiveSession()!.Id == physicalId,
            "Closing the dashboard freezes its packet-loss strip while tray monitoring keeps recording completed history");
        context.ShowDashboard();
        var recreated = context.Dashboard!;
        Control<TabControl>(recreated, "MainNavigation").SelectedItem = Control<TabItem>(recreated, "HealthTab");
        recreated.UpdateLayout();
        Pump();
        var recreatedLine = Control<IcmpStabilityChart>(recreated, "IcmpStabilityChart");
        var recreatedLoss = Control<IcmpPacketLossChart>(recreated, "IcmpPacketLossChart");
        check(created == 2 && !ReferenceEquals(loss, recreatedLoss) && Shared(recreatedLine, recreatedLoss) &&
            recreatedLoss.Data.Count == 6 && recreatedLoss.Data.Samples.SequenceEqual(monitoring.RollingIcmpSamples),
            "Tray Open recreates both charts from the latest shared monitoring history without duplicating bars");

        recreated.Hide();
        Pump();
        var oldest = monitoring.RollingIcmpSamples[0];
        for (var i = 0; i < 55; i++)
            CompleteCycle(monitoring, source, clock, Success(10), Success(20), Success(30), i % 2 == 0 ? Failure() : Success(40));
        recreated.Show();
        recreated.UpdateLayout();
        Pump();
        check(Shared(recreatedLine, recreatedLoss) && recreatedLoss.Data.Count == 60 &&
            recreatedLoss.Data.PacketLossPoints.Count == 60 && !recreatedLoss.Data.Samples.Contains(oldest) &&
            recreatedLoss.Data.Samples.SequenceEqual(monitoring.RollingIcmpSamples) &&
            recreatedLoss.Data.PacketLossPoints[0].Position == 0 && recreatedLoss.Data.PacketLossPoints[^1].Position == 1,
            "A restored window after 61 cycles displays the same newest 60 ordered samples in both charts");
        check(ReferenceEquals(worker, icmp.Completion) && source.MaximumConcurrent == 1 &&
            ReferenceEquals(baseline, Baseline(monitoring)) && counters.Captures == captures &&
            history.GetActiveSession()!.Id == physicalId && monitoring.GetSnapshot().SessionDownloaded == 0 &&
            ReferenceEquals(timer, ViewTimer(window)),
            "Packet-loss navigation and rollover preserve the original ping worker, timer, bandwidth baseline and physical session");

        RenderFixtures(root, clock.GetUtcNow(), check);
        context.Exit();
        icmp.Completion.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        WaitWithoutUi(() => source.Active == 0);
        check(exits == 1 && !monitoring.GetSnapshot().IsRunning && history.GetActiveSession() is null &&
            history.GetRecentSessions().Single().EndReason == "ApplicationExit" && !ViewTimer(recreated).IsEnabled,
            "Tray Exit still cancels the existing worker and cleanly closes monitoring without a packet-loss lifecycle service");
    }

    private static void RenderFixtures(string root, DateTimeOffset start, Action<bool, string> check)
    {
        IcmpProbeResult Sample(int index, double loss)
        {
            var failed = (int)(loss / 25);
            return new(start.AddSeconds(index * 15), "1.1.1.1", 4 - failed, failed,
                failed == 4 ? null : 42, loss, failed >= 3 ? null : 6);
        }
        var fixtures = new (string Name, IcmpProbeResult[] Samples)[]
        {
            ("empty", []),
            ("zero-loss", [Sample(0, 0), Sample(1, 0), Sample(2, 0)]),
            ("partial-loss", [Sample(0, 25), Sample(1, 50), Sample(2, 75)]),
            ("single-full-loss", [Sample(0, 100)]),
            ("all-failed", Enumerable.Range(0, 6).Select(index => Sample(index, 100)).ToArray()),
            ("mixed-60-cycles", Enumerable.Range(0, 60).Select(index => Sample(index, index % 5 * 25)).ToArray()),
        };
        foreach (var fixture in fixtures)
        {
            var chart = new IcmpPacketLossChart { Width = 680, Height = 70 };
            var data = IcmpStabilityChartData.Create(fixture.Samples);
            chart.UpdateData(data);
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
                var bitmap = Render(chart, chart.Width, chart.Height);
                Save(bitmap, System.IO.Path.Combine(root, "packet-loss-chart-previews", fixture.Name + ".png"));
                check(ReferenceEquals(data, chart.Data) && chart.Data.PacketLossPoints.Count == fixture.Samples.Length &&
                    HasPaint(bitmap) && BarsMatchPercentages(bitmap, chart.Data),
                    $"The native packet-loss renderer paints accurate bar heights for the {fixture.Name} shared-snapshot fixture");
            }
            finally { host.Close(); }
        }
    }

    private static void CheckSmallLayout(MainWindow window, IcmpStabilityChart line, IcmpPacketLossChart loss,
        string root, Action<bool, string> check)
    {
        window.Width = 760;
        window.Height = 600;
        window.UpdateLayout();
        Pump();
        var scroll = VisualAncestors(loss).OfType<ScrollViewer>().First();
        check(loss.ActualWidth >= 400 && Math.Abs(loss.ActualWidth - line.ActualWidth) < 0.01 &&
            scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled &&
            scroll.ScrollableWidth < 0.01 && loss.ActualHeight == 70,
            "At the existing minimum window size both history charts share readable width without horizontal scrolling");
        scroll.ScrollToBottom();
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
        Save(bitmap, System.IO.Path.Combine(root, "packet-loss-chart-previews", "760x600-health.png"));
        window.Width = 920;
        window.Height = 740;
        scroll.ScrollToTop();
        window.UpdateLayout();
        Pump();
    }

    private static RenderTargetBitmap Render(Visual visual, double width, double height)
    {
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(15, 23, 42)), null, new Rect(0, 0, width, height));
            var offset = VisualTreeHelper.GetOffset(visual);
            context.DrawRectangle(new VisualBrush(visual)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(offset.X, offset.Y, width, height),
                Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
            }, null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        return bitmap;
    }

    private static bool HasPaint(RenderTargetBitmap bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return Enumerable.Range(0, pixels.Length / 4).Any(index => pixels[index * 4 + 1] > 80);
    }

    private static bool BarsMatchPercentages(RenderTargetBitmap bitmap, IcmpStabilityChartData data)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        bool LossAt(int x, int y)
        {
            var pixel = (y * bitmap.PixelWidth + x) * 4;
            return pixels[pixel] > 220 && pixels[pixel + 1] is > 115 and < 160 &&
                pixels[pixel + 2] is > 145 and < 190;
        }
        // These reflect the renderer's intentional shared left/right chart padding.
        var left = 52d;
        var right = bitmap.PixelWidth - 14d;
        var height = bitmap.PixelHeight - 14d;
        if (data.Count == 0 || data.PacketLossPoints.All(point => point.Percent == 0))
            return !Enumerable.Range(52, bitmap.PixelWidth - 66)
                .Any(x => Enumerable.Range(7, (int)height).Any(y => LossAt(x, y)));
        var spacing = data.Count == 1 ? right - left : (right - left) / (data.Count - 1);
        var barWidth = Math.Min(8, spacing * 0.7);
        return data.PacketLossPoints.All(point =>
        {
            var barLeft = Math.Clamp(left + (right - left) * point.Position - barWidth / 2, left, right - barWidth);
            var x = (int)Math.Floor(barLeft + barWidth / 2);
            var paintedHeight = Enumerable.Range(7, (int)height).Count(y => LossAt(x, y));
            return Math.Abs(paintedHeight - height * ((point.Percent ?? 0) / 100)) <= 1;
        });
    }

    private static void Save(RenderTargetBitmap bitmap, string path)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = System.IO.File.Create(path);
        encoder.Save(output);
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

    private static bool Shared(IcmpStabilityChart line, IcmpPacketLossChart loss) => ReferenceEquals(line.Data, loss.Data);
    private static IcmpPingReply Success(long latency) => new(IPStatus.Success, latency);
    private static IcmpPingReply Failure() => new(IPStatus.TimedOut, 9999);
    private static T Control<T>(Window window, string name) where T : class => window.FindName(name) as T
        ?? throw new InvalidOperationException($"Missing packet-loss check control {name}.");
    private static DispatcherTimer ViewTimer(MainWindow window) =>
        (DispatcherTimer)typeof(MainWindow).GetField("_timer", PrivateInstance)!.GetValue(window)!;
    private static object? Baseline(MonitoringService monitoring) =>
        typeof(MonitoringService).GetField("_previousSnapshot", PrivateInstance)!.GetValue(monitoring);
    private static void StopSampler(MonitoringService monitoring) =>
        ((System.Threading.Timer)typeof(MonitoringService).GetField("_timer", PrivateInstance)!.GetValue(monitoring)!)
            .Change(Timeout.Infinite, Timeout.Infinite);

    private static void CompleteCycle(MonitoringService monitoring, ControlledPingSource source,
        ControlledClock clock, params IcmpPingReply[] replies)
    {
        if (replies.Length != 4) throw new ArgumentException("Supply one complete ICMP burst.");
        var previous = monitoring.LatestIcmpMeasurement;
        clock.Advance(TimeSpan.FromSeconds(15));
        var timestamp = clock.GetUtcNow();
        source.Queue(replies);
        WaitWithoutUi(() => monitoring.LatestIcmpMeasurement is { } latest &&
            !ReferenceEquals(previous, latest) && latest.TimestampUtc == timestamp && source.Active == 1);
    }

    private static void Wait(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("The packet-loss presentation did not reach its expected state.");
            Pump();
        }
        Pump();
    }

    private static void WaitWithoutUi(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("The owned packet-loss probe did not reach its expected state.");
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
                if (!_replies.Writer.TryWrite(reply)) throw new InvalidOperationException("The owned ping queue is closed.");
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
            new NetworkIdentity("WiFi", "ICMP_PACKET_LOSS_CHART", "ICMP_PACKET_LOSS_CHART", adapterId), null);
        public void Dispose() { }
    }

    private sealed class OwnedCounters(string adapterId, TimeProvider clock) : INetworkMonitor
    {
        public int Captures { get; private set; }
        public IReadOnlyList<AdapterInfo> GetActiveAdapters() =>
            [new(adapterId, "Owned loss chart adapter", "Injected physical adapter", "Wireless80211", 86_000_000, "192.0.2.10", true)];
        public NetworkSnapshot? Capture(string requestedAdapter)
        {
            Captures++;
            return requestedAdapter == adapterId ? new(1000, 500, clock.GetUtcNow()) : null;
        }
    }

    private sealed class ReadOnlyStartup : IWindowsStartupRegistration
    {
        public bool IsEnabled() => false;
        public void Enable() => throw new InvalidOperationException("Startup writes are prohibited in packet-loss checks.");
        public void Disable() => throw new InvalidOperationException("Startup writes are prohibited in packet-loss checks.");
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
