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

internal static class InternetReachabilityChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(string root, string adapterId, Action<bool, string> check)
    {
        var store = new UsageStore(System.IO.Path.Combine(root, "internet-reachability"));
        var history = new ConnectionHistoryStore(store);
        var clock = new ControlledClock();
        var probes = new ControlledProbeSource();
        var pings = new ControlledPingSource();
        var counters = new OwnedCounters(adapterId, clock);
        using var reachability = new InternetReachabilityMonitor(probes, interval: TimeSpan.FromMilliseconds(40),
            timeout: TimeSpan.FromMinutes(5), timeProvider: clock);
        using var icmp = new IcmpMonitor(pings, interval: TimeSpan.FromMilliseconds(40),
            timeout: TimeSpan.FromMinutes(5), timeProvider: clock);
        using var monitoring = new MonitoringService(store, new OwnedIdentity(adapterId),
            vpnVerifier: new VpnRouteVerifier(new VpnDetectionService(new NoTunnel()), new NoRoutes()),
            networkMonitor: counters, timeProvider: clock, icmpMonitor: icmp, reachabilityMonitor: reachability);
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
        WaitWithoutUi(() => probes.Active == 4 && pings.Active == 1 && history.GetActiveSession() is not null);
        var physicalId = history.GetActiveSession()!.Id;
        var reachabilityWorker = reachability.Completion;
        var icmpWorker = icmp.Completion;
        monitoring.SampleNow();
        var baseline = Baseline(monitoring);
        var captures = counters.Captures;
        check(context.Dashboard is null && monitoring.LatestInternetReachability is null && probes.Calls == 4 &&
            monitoring.RollingIcmpSamples.Count == 0,
            "Background startup begins one bounded DNS/HTTPS check independently of ICMP without creating a dashboard");

        context.ShowDashboard();
        var window = context.Dashboard!;
        var navigation = Control<TabControl>(window, "MainNavigation");
        var health = Control<TabItem>(window, "HealthTab");
        var timer = ViewTimer(window);
        timer.Stop();
        navigation.SelectedItem = health;
        window.UpdateLayout();
        Pump();
        var reachabilityText = Control<TextBlock>(window, "InternetReachabilityText");
        var quality = Control<TextBlock>(window, "IcmpQualityText");
        var line = Control<IcmpStabilityChart>(window, "IcmpStabilityChart");
        var loss = Control<IcmpPacketLossChart>(window, "IcmpPacketLossChart");
        var cards = Control<StackPanel>(window, "HealthContent").Children.OfType<Border>().ToArray();
        check(reachabilityText.Text == "Checking" && quality.Text == "Measuring" && reachabilityText.IsVisible,
            "Health shows Checking before the first completed reachability check without blocking startup");
        check(cards.Length == 2 && VisualAncestors(reachabilityText).Contains(cards[0]) &&
            VisualAncestors(quality).Contains(cards[0]) && Grid.GetRow(reachabilityText) < Grid.GetRow(quality) &&
            FindText(cards[0]).Any(text => text == "Internet reachability"),
            "Internet reachability is one compact row above ICMP quality in the existing upper card and two-card layout");
        check(!FindText(cards[0]).Any(text => text is "Online" or "Offline") &&
            ReferenceEquals(reachabilityText.Foreground, window.FindResource("StatusNeutralBrush")),
            "The initial reachability state uses readable Checking text and the existing neutral theme instead of Online or Offline");
        SavePreview(window, root, "checking");

        for (var cycle = 0; cycle < 3; cycle++) CompleteIcmp(monitoring, pings, clock, 40);
        window.RefreshDashboard();
        check(quality.Text == "Excellent" && reachabilityText.Text == "Checking",
            "ICMP can become Excellent while an independent reachability check is still in flight");
        CompleteReachability(monitoring, probes, clock, true, true);
        window.RefreshDashboard();
        check(reachabilityText.Text == "Available" && quality.Text == "Excellent" &&
            monitoring.LatestInternetReachability is { Status: InternetReachabilityStatus.Available, DnsSucceeded: true, HttpsSucceeded: true },
            "Successful DNS and HTTPS display Available alongside independently evaluated Excellent ICMP quality");
        check(((SolidColorBrush)reachabilityText.Foreground).Color == Color.FromRgb(34, 197, 94),
            "Available combines explicit text with the existing restrained positive status color");
        SavePreview(window, root, "available");
        SavePreview(window, root, "available-icmp-excellent");
        var rawSamples = monitoring.RollingIcmpSamples;
        var latestIcmp = monitoring.LatestIcmpMeasurement;
        var lineData = line.Data;
        var qualitySummary = Summary(window);

        CompleteReachability(monitoring, probes, clock, true, false);
        window.RefreshDashboard();
        check(reachabilityText.Text == "Limited" && quality.Text == "Excellent" &&
            monitoring.LatestInternetReachability is { Status: InternetReachabilityStatus.Limited, DnsSucceeded: true, HttpsSucceeded: false },
            "DNS success with both HTTPS probes failing displays Limited without changing ICMP quality");
        check(((SolidColorBrush)reachabilityText.Foreground).Color == Color.FromRgb(251, 191, 36),
            "Limited uses explicit caution text and the existing amber chart color");
        SavePreview(window, root, "limited");
        CompleteReachability(monitoring, probes, clock, false, false);
        window.RefreshDashboard();
        check(reachabilityText.Text == "Unavailable" && quality.Text == "Excellent" &&
            monitoring.LatestInternetReachability is { Status: InternetReachabilityStatus.Unavailable, DnsSucceeded: false, HttpsSucceeded: false },
            "All DNS and HTTPS failures display Unavailable independently of good ICMP measurements");
        check(((SolidColorBrush)reachabilityText.Foreground).Color == Color.FromRgb(239, 68, 68),
            "Unavailable uses explicit text and the existing restrained error status color");
        SavePreview(window, root, "unavailable");
        check(monitoring.RollingIcmpSamples.SequenceEqual(rawSamples) && ReferenceEquals(latestIcmp, monitoring.LatestIcmpMeasurement) &&
            ReferenceEquals(lineData, line.Data) && ReferenceEquals(line.Data, loss.Data) && ReferenceEquals(qualitySummary, Summary(window)),
            "Reachability-only completions preserve raw ICMP history, latency, jitter, packet-loss chart data and cached quality calculations");

        CompleteReachability(monitoring, probes, clock, false, true, oneHttpsOnly: true);
        window.RefreshDashboard();
        check(reachabilityText.Text == "Available" &&
            monitoring.LatestInternetReachability is { DnsSucceeded: false, HttpsSucceeded: true },
            "One successful HTTPS endpoint establishes Available even when independent DNS and the other HTTPS endpoint fail");
        for (var cycle = 0; cycle < 12; cycle++) CompleteIcmp(monitoring, pings, clock, null);
        window.RefreshDashboard();
        check(reachabilityText.Text == "Available" && quality.Text == "Unavailable" &&
            Control<TextBlock>(window, "IcmpLatencyText").Text == "Unavailable" &&
            Control<TextBlock>(window, "IcmpJitterText").Text == "Unavailable" &&
            Control<TextBlock>(window, "IcmpPacketLossText").Text == "100%" &&
            loss.Data.PacketLossPoints[^1].Percent == 100,
            "Available internet reachability coexists with unavailable ICMP quality, missing latency/jitter and full-loss history");
        check(FindText(cards[0]).Any(text => text.Contains("ICMP replies can be blocked", StringComparison.Ordinal) &&
                text.Contains("does not determine internet availability", StringComparison.Ordinal) &&
                text.Contains("DNS", StringComparison.Ordinal) && text.Contains("HTTPS", StringComparison.Ordinal)),
            "The concise disclaimer explains blocked ICMP and separate DNS/HTTPS reachability without claiming every service works");
        SavePreview(window, root, "available-icmp-unavailable");

        timer.Start();
        CompleteReachability(monitoring, probes, clock, true, false);
        Wait(() => reachabilityText.Text == "Limited");
        check(quality.Text == "Unavailable" && ReferenceEquals(timer, ViewTimer(window)),
            "A completed reachability check updates visible Health through the existing view timer without waiting for another ICMP cycle");
        timer.Stop();
        var calls = probes.Calls;
        for (var repeat = 0; repeat < 4; repeat++) window.RefreshDashboard();
        check(probes.Calls == calls && ReferenceEquals(reachabilityWorker, reachability.Completion) &&
            typeof(MainWindow).GetFields(PrivateInstance).Count(field => field.FieldType == typeof(DispatcherTimer)) == 1,
            "Repeated presentation refreshes neither start probes nor create a second dashboard refresh timer");

        navigation.SelectedItem = Control<TabItem>(window, "ConnectionsTab");
        Pump();
        CompleteReachability(monitoring, probes, clock, true, true);
        window.RefreshDashboard();
        check(reachabilityText.Text == "Limited" && monitoring.LatestInternetReachability!.Status == InternetReachabilityStatus.Available,
            "Reachability monitoring continues while Health is inactive without presentation work on its hidden row");
        navigation.SelectedItem = health;
        window.UpdateLayout();
        Pump();
        check(reachabilityText.Text == "Available" && quality.Text == "Unavailable",
            "Entering Health immediately consumes the latest existing reachability result without restarting probes");
        calls = probes.Calls;
        for (var repeat = 0; repeat < 3; repeat++)
        {
            navigation.SelectedItem = Control<TabItem>(window, "DashboardTab");
            Pump();
            navigation.SelectedItem = health;
            Pump();
        }
        check(probes.Calls == calls && ReferenceEquals(reachabilityWorker, reachability.Completion) && probes.MaximumConcurrent == 4,
            "Repeated page changes retain one reachability worker with only its bounded four parallel probes");

        window.Hide();
        Pump();
        CompleteReachability(monitoring, probes, clock, true, false);
        window.RefreshDashboard();
        check(!timer.IsEnabled && reachabilityText.Text == "Available" &&
            monitoring.LatestInternetReachability!.Status == InternetReachabilityStatus.Limited,
            "Hidden dashboards skip reachability presentation while tray monitoring continues completing checks");
        window.Show();
        window.UpdateLayout();
        Pump();
        check(timer.IsEnabled && reachabilityText.Text == "Limited",
            "Showing a hidden dashboard immediately displays its latest completed Limited result");
        window.WindowState = WindowState.Minimized;
        Pump();
        CompleteReachability(monitoring, probes, clock, false, false);
        window.RefreshDashboard();
        check(!timer.IsEnabled && reachabilityText.Text == "Limited" &&
            monitoring.LatestInternetReachability!.Status == InternetReachabilityStatus.Unavailable,
            "Minimization stops presentation work without suspending DNS/HTTPS monitoring");
        window.WindowState = WindowState.Normal;
        window.UpdateLayout();
        Pump();
        check(timer.IsEnabled && reachabilityText.Text == "Unavailable",
            "Restoring the dashboard immediately displays the latest reachability result without another worker");
        CheckSmallLayout(window, reachabilityText, quality, line, loss, root, check);

        var lastRaw = monitoring.RollingIcmpSamples;
        window.Close();
        CompleteReachability(monitoring, probes, clock, false, true);
        window.RefreshDashboard();
        Pump();
        check(context.Dashboard is null && !timer.IsEnabled && reachabilityText.Text == "Unavailable" &&
            monitoring.LatestInternetReachability!.Status == InternetReachabilityStatus.Available,
            "Closing the dashboard leaves the application-owned reachability monitor active and freezes the closed view");
        context.ShowDashboard();
        var recreated = context.Dashboard!;
        Control<TabControl>(recreated, "MainNavigation").SelectedItem = Control<TabItem>(recreated, "HealthTab");
        recreated.UpdateLayout();
        Pump();
        check(created == 2 && Control<TextBlock>(recreated, "InternetReachabilityText").Text == "Available" &&
            Control<TextBlock>(recreated, "IcmpQualityText").Text == "Unavailable" &&
            monitoring.RollingIcmpSamples.SequenceEqual(lastRaw),
            "Tray Open recreates Health with the current reachability result and unchanged ICMP history");
        check(ReferenceEquals(reachabilityWorker, reachability.Completion) && ReferenceEquals(icmpWorker, icmp.Completion) &&
            probes.MaximumConcurrent == 4 && pings.MaximumConcurrent == 1 && ReferenceEquals(baseline, Baseline(monitoring)) &&
            captures == counters.Captures && history.GetActiveSession()!.Id == physicalId &&
            monitoring.GetSnapshot().SessionDownloaded == 0,
            "Reachability page and tray operations preserve both workers, physical session, sampler baseline and bandwidth accounting");

        var beforeSleep = monitoring.LatestInternetReachability;
        probes.QueueDns(true, false);
        WaitWithoutUi(() => probes.DnsActive == 0 && probes.HttpsActive == 2);
        check(ReferenceEquals(beforeSleep, monitoring.LatestInternetReachability),
            "Completing DNS alone never publishes a half-finished reachability result");
        monitoring.Suspend();
        WaitWithoutUi(() => probes.Active == 0 && pings.Active == 0);
        check(ReferenceEquals(beforeSleep, monitoring.LatestInternetReachability) && monitoring.GetSnapshot().IsSuspended,
            "Sleep cancels and discards incomplete HTTPS work while retaining only the last fully completed result");
        calls = probes.Calls;
        monitoring.Resume();
        StopSampler(monitoring);
        WaitWithoutUi(() => probes.Active == 4 && pings.Active == 1 && probes.Calls == calls + 4 &&
            history.GetActiveSession() is { } current && current.Id != physicalId);
        check(ReferenceEquals(reachabilityWorker, reachability.Completion) && ReferenceEquals(icmpWorker, icmp.Completion),
            "Resume promptly performs one fresh DNS/HTTPS check through the existing monitoring lifecycle and worker");
        CompleteReachability(monitoring, probes, clock, false, false);
        recreated.RefreshDashboard();
        check(Control<TextBlock>(recreated, "InternetReachabilityText").Text == "Unavailable" &&
            monitoring.LatestInternetReachability is { DnsSucceeded: false, HttpsSucceeded: false } &&
            monitoring.LatestInternetReachability!.CheckedAtUtc > beforeSleep!.CheckedAtUtc,
            "A fresh resumed failure replaces the prior success without mixing its canceled pre-sleep DNS result");
        context.Exit();
        reachability.Completion.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        icmp.Completion.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        WaitWithoutUi(() => probes.Active == 0 && pings.Active == 0);
        check(exits == 1 && !monitoring.GetSnapshot().IsRunning && history.GetActiveSession() is null &&
            history.GetRecentSessions().First().EndReason == "ApplicationExit" && !ViewTimer(recreated).IsEnabled &&
            probes.Canceled >= 6 && pings.Canceled >= 2,
            "Tray Exit promptly cancels all in-flight reachability and ICMP work and cleanly closes the resumed physical session");
    }

    private static void CheckSmallLayout(MainWindow window, TextBlock reachability, TextBlock quality,
        IcmpStabilityChart line, IcmpPacketLossChart loss, string root, Action<bool, string> check)
    {
        window.Width = 760;
        window.Height = 600;
        window.UpdateLayout();
        Pump();
        var scroll = VisualAncestors(reachability).OfType<ScrollViewer>().First();
        scroll.ScrollToTop();
        window.UpdateLayout();
        Pump();
        var position = reachability.TranslatePoint(new Point(), scroll);
        check(reachability.ActualWidth >= 100 && reachability.ActualHeight >= 14 && position.X >= 0 && position.Y >= 0 &&
            position.Y + reachability.ActualHeight <= scroll.ActualHeight &&
            reachability.TranslatePoint(new Point(), window).Y < quality.TranslatePoint(new Point(), window).Y &&
            scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && scroll.ScrollableWidth < 0.01,
            "At 760 by 600 the compact Internet reachability row remains readable above ICMP quality without horizontal overflow");
        SavePreview(window, root, "760x600-health");
        scroll.ScrollToBottom();
        window.UpdateLayout();
        Pump();
        var stripPosition = loss.TranslatePoint(new Point(), scroll);
        check(stripPosition.Y >= 0 && stripPosition.Y + loss.ActualHeight <= scroll.ActualHeight &&
            Math.Abs(line.ActualWidth - loss.ActualWidth) < 0.01 && line.Height == 110 && loss.Height == 70,
            "The existing vertical scroll and synchronized compact charts remain intact after adding the reachability row");
        window.Width = 920;
        window.Height = 740;
        scroll.ScrollToTop();
        window.UpdateLayout();
        Pump();
    }

    private static void CompleteReachability(MonitoringService monitoring, ControlledProbeSource source,
        ControlledClock clock, bool dns, bool https, bool oneHttpsOnly = false)
    {
        WaitWithoutUi(() => source.Active == 4);
        var previous = monitoring.LatestInternetReachability;
        clock.Advance(TimeSpan.FromSeconds(60));
        source.QueueDns(dns, dns);
        source.QueueHttps(oneHttpsOnly ? false : https, https);
        WaitWithoutUi(() => monitoring.LatestInternetReachability is { } latest &&
            !ReferenceEquals(previous, latest) && source.Active == 4);
    }

    private static void CompleteIcmp(MonitoringService monitoring, ControlledPingSource source, ControlledClock clock, long? latency)
    {
        var previous = monitoring.LatestIcmpMeasurement;
        clock.Advance(TimeSpan.FromSeconds(15));
        var reply = latency is { } value ? new IcmpPingReply(IPStatus.Success, value) : new IcmpPingReply(IPStatus.TimedOut, 9999);
        source.Queue(Enumerable.Repeat(reply, 4));
        WaitWithoutUi(() => monitoring.LatestIcmpMeasurement is { } latest && !ReferenceEquals(previous, latest) && source.Active == 1);
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
        var path = System.IO.Path.Combine(root, "internet-reachability-previews", name + ".png");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = System.IO.File.Create(path);
        encoder.Save(output);
    }

    private static IcmpQualitySummary Summary(MainWindow window) =>
        (IcmpQualitySummary)typeof(MainWindow).GetField("_qualitySummary", PrivateInstance)!.GetValue(window)!;
    private static T Control<T>(Window window, string name) where T : class => window.FindName(name) as T
        ?? throw new InvalidOperationException($"Missing reachability check control {name}.");
    private static DispatcherTimer ViewTimer(MainWindow window) =>
        (DispatcherTimer)typeof(MainWindow).GetField("_timer", PrivateInstance)!.GetValue(window)!;
    private static object? Baseline(MonitoringService monitoring) =>
        typeof(MonitoringService).GetField("_previousSnapshot", PrivateInstance)!.GetValue(monitoring);
    private static void StopSampler(MonitoringService monitoring) =>
        ((System.Threading.Timer)typeof(MonitoringService).GetField("_timer", PrivateInstance)!.GetValue(monitoring)!)
            .Change(Timeout.Infinite, Timeout.Infinite);
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
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Reachability UI did not reach its expected state.");
            Pump();
        }
        Pump();
    }
    private static void WaitWithoutUi(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("The owned reachability probe did not reach its expected state.");
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

    private sealed class ControlledProbeSource : IInternetReachabilityProbeSource
    {
        private readonly Channel<bool> _dns = Channel.CreateUnbounded<bool>();
        private readonly Channel<bool> _https = Channel.CreateUnbounded<bool>();
        private int _calls;
        private int _dnsActive;
        private int _httpsActive;
        private int _active;
        private int _maximum;
        private int _canceled;
        public int Calls => Volatile.Read(ref _calls);
        public int DnsActive => Volatile.Read(ref _dnsActive);
        public int HttpsActive => Volatile.Read(ref _httpsActive);
        public int Active => Volatile.Read(ref _active);
        public int MaximumConcurrent => Volatile.Read(ref _maximum);
        public int Canceled => Volatile.Read(ref _canceled);
        public void QueueDns(bool first, bool second) { _dns.Writer.TryWrite(first); _dns.Writer.TryWrite(second); }
        public void QueueHttps(bool first, bool second) { _https.Writer.TryWrite(first); _https.Writer.TryWrite(second); }
        public Task<bool> ResolveDnsAsync(string hostname, CancellationToken cancellationToken) => Read(_dns, true, cancellationToken);
        public Task<bool> ProbeHttpsAsync(Uri endpoint, CancellationToken cancellationToken) => Read(_https, false, cancellationToken);
        private async Task<bool> Read(Channel<bool> channel, bool dns, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            if (dns) Interlocked.Increment(ref _dnsActive); else Interlocked.Increment(ref _httpsActive);
            var active = Interlocked.Increment(ref _active);
            while (true)
            {
                var previous = Volatile.Read(ref _maximum);
                if (active <= previous || Interlocked.CompareExchange(ref _maximum, active, previous) == previous) break;
            }
            try { return await channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { Interlocked.Increment(ref _canceled); throw; }
            finally
            {
                if (dns) Interlocked.Decrement(ref _dnsActive); else Interlocked.Decrement(ref _httpsActive);
                Interlocked.Decrement(ref _active);
            }
        }
    }
    private sealed class ControlledPingSource : IPingSource
    {
        private readonly Channel<IcmpPingReply> _replies = Channel.CreateUnbounded<IcmpPingReply>();
        private int _active;
        private int _maximum;
        private int _canceled;
        public int Active => Volatile.Read(ref _active);
        public int MaximumConcurrent => Volatile.Read(ref _maximum);
        public int Canceled => Volatile.Read(ref _canceled);
        public void Queue(IEnumerable<IcmpPingReply> replies) { foreach (var reply in replies) _replies.Writer.TryWrite(reply); }
        public async Task<IcmpPingReply> SendAsync(IPAddress target, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            while (true)
            {
                var previous = Volatile.Read(ref _maximum);
                if (active <= previous || Interlocked.CompareExchange(ref _maximum, active, previous) == previous) break;
            }
            try { return await _replies.Reader.ReadAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { Interlocked.Increment(ref _canceled); throw; }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
    private sealed class ControlledClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 10, 11, 4, 5, 6, TimeSpan.Zero).Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }
    private sealed class OwnedIdentity(string adapterId) : INetworkIdentityService
    {
        public event EventHandler? NetworkChanged { add { } remove { } }
        public NetworkIdentityResult GetCurrentNetwork() => new(NetworkIdentityAvailability.Available,
            new NetworkIdentity("WiFi", "REACHABILITY", "REACHABILITY", adapterId), null);
        public void Dispose() { }
    }
    private sealed class OwnedCounters(string adapterId, TimeProvider clock) : INetworkMonitor
    {
        public int Captures { get; private set; }
        public IReadOnlyList<AdapterInfo> GetActiveAdapters() =>
            [new(adapterId, "Owned reachability adapter", "Injected physical adapter", "Wireless80211", 86_000_000, "192.0.2.10", true)];
        public NetworkSnapshot? Capture(string requestedAdapter)
        {
            Captures++;
            return requestedAdapter == adapterId ? new(1000, 500, clock.GetUtcNow()) : null;
        }
    }
    private sealed class ReadOnlyStartup : IWindowsStartupRegistration
    {
        public bool IsEnabled() => false;
        public void Enable() => throw new InvalidOperationException("Startup writes are prohibited in reachability checks.");
        public void Disable() => throw new InvalidOperationException("Startup writes are prohibited in reachability checks.");
    }
    private sealed class NoTunnel : IVpnInterfaceSource { public VpnInterfaceInventory Read() => new([], []); }
    private sealed class NoRoutes : IVpnRouteSource
    {
        public VpnRouteInventory Read(AddressFamily family) => new(family, [], Availability: IpFamilyAvailability.Unavailable);
    }
}
