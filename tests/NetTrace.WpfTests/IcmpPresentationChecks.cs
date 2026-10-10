using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NetTrace;
using NetTrace.Models;
using NetTrace.Services;

internal static class IcmpPresentationChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly string[] MetricNames =
        ["IcmpTargetText", "IcmpLatencyText", "IcmpJitterText", "IcmpPacketLossText", "IcmpRepliesText", "IcmpLastSampleText"];

    public static void Run(string root, string adapterId, Action<bool, string> check)
    {
        var store = new UsageStore(System.IO.Path.Combine(root, "icmp-presentation"));
        var history = new ConnectionHistoryStore(store);
        var clock = new ControlledClock();
        var source = new ControlledPingSource();
        using var icmp = new IcmpMonitor(source, IPAddress.Parse("9.9.9.9"),
            interval: TimeSpan.FromMilliseconds(40), timeout: TimeSpan.FromMinutes(5), timeProvider: clock);
        using var monitoring = new MonitoringService(store, new OwnedIdentity(adapterId),
            vpnVerifier: new VpnRouteVerifier(new VpnDetectionService(new NoTunnel()), new NoRoutes()),
            networkMonitor: new OwnedCounters(adapterId, clock), timeProvider: clock, icmpMonitor: icmp, reachabilityMonitor: TestReachability.Create());
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
        Wait(() => source.Calls == 1 && history.GetActiveSession() is not null);
        var physicalId = history.GetActiveSession()!.Id;
        var worker = icmp.Completion;
        check(context.Dashboard is null && monitoring.GetSnapshot().IsRunning && monitoring.LatestIcmpMeasurement is null,
            "ICMP presentation reads a background-owned worker without requiring a dashboard");

        context.ShowDashboard();
        var window = context.Dashboard!;
        var navigation = Control<TabControl>(window, "MainNavigation");
        var health = Control<TabItem>(window, "HealthTab");
        var viewTimer = ViewTimer(window);
        navigation.SelectedItem = health;
        window.UpdateLayout();
        Pump();
        check(Control<TextBlock>(window, "HealthWaitingText") is { IsVisible: true, Text: "Waiting for first measurement..." } &&
            MetricNames.All(name => Text(window, name) == "—"),
            "Health waits for the first completed measurement without inventing latency, loss or reply counts");
        check(viewTimer.IsEnabled && viewTimer.Interval == TimeSpan.FromSeconds(1),
            "Health uses the existing one-second window refresh timer");
        var healthContent = Control<StackPanel>(window, "HealthContent");
        check(!Descendants(healthContent).OfType<Button>().Any() &&
            Descendants(healthContent).OfType<TextBlock>().Any(text => text.Text.Contains("ICMP replies can be blocked", StringComparison.Ordinal)),
            "Health displays raw ICMP measurements and a blocking caveat without adding probe actions");

        source.Queue(Success(10), Success(20), Success(5), Success(15));
        Wait(() => Text(window, "IcmpLatencyText") == Latency(12.5) && Text(window, "IcmpRepliesText") == "4 / 4");
        check(Text(window, "IcmpTargetText") == "9.9.9.9" && Text(window, "IcmpPacketLossText") == "0%" &&
            Text(window, "IcmpLatencyText") == Latency(12.5),
            "The existing view timer displays the configured target, successful average latency and zero loss");
        check(Text(window, "IcmpJitterText") == Latency(35d / 3),
            "Health displays the mean absolute difference between successive successful reply latencies");
        check(Control<TextBlock>(window, "HealthWaitingText").Visibility == Visibility.Collapsed,
            "The waiting message disappears after a completed four-reply ICMP cycle");
        check(Text(window, "IcmpLastSampleText") == LocalTimestamp(clock.GetUtcNow()),
            "The latest ICMP timestamp is displayed in local time with seconds");

        clock.Advance(TimeSpan.FromSeconds(15));
        var partialTime = clock.GetUtcNow();
        source.Queue(Success(10), Success(20), Success(31), new IcmpPingReply(IPStatus.TimedOut, 9999));
        Wait(() => Text(window, "IcmpRepliesText") == "3 / 4" && Text(window, "IcmpLastSampleText") == LocalTimestamp(partialTime));
        check(Text(window, "IcmpLatencyText") == Latency(61d / 3) && Text(window, "IcmpPacketLossText") == "25%",
            "Health displays partial packet loss and rounds successful-reply average latency to two decimals");
        check(Text(window, "IcmpJitterText") == Latency(10.5),
            "Health excludes failed replies from jitter and updates the value after the next completed cycle");
        check(ReferenceEquals(viewTimer, ViewTimer(window)) && ReferenceEquals(worker, icmp.Completion),
            "New ICMP measurements update automatically without creating a timer or restarting probes");

        clock.Advance(TimeSpan.FromSeconds(15));
        var failureTime = clock.GetUtcNow();
        source.Queue(Failure(), Failure(), Failure(), Failure());
        Wait(() => Text(window, "IcmpRepliesText") == "0 / 4" && Text(window, "IcmpLastSampleText") == LocalTimestamp(failureTime));
        check(Text(window, "IcmpLatencyText") == "Unavailable" && Text(window, "IcmpJitterText") == "Unavailable" &&
            Text(window, "IcmpPacketLossText") == "100%" &&
            Control<TextBlock>(window, "HealthWaitingText").Visibility == Visibility.Collapsed,
            "A completed all-failed cycle displays 100 percent loss and unavailable latency and jitter");
        check(monitoring.GetSnapshot().Status == "Monitoring" && history.GetActiveSession()!.Id == physicalId &&
            !Descendants(healthContent).OfType<TextBlock>().Any(text => text.Text is "Healthy" or "Degraded" or "Internet Offline"),
            "ICMP failure does not infer internet availability or change physical monitoring state");

        clock.Advance(TimeSpan.FromSeconds(15));
        source.Queue(Success(0), Success(0), Success(0), Success(0));
        Wait(() => Text(window, "IcmpLatencyText") == Latency(0) && Text(window, "IcmpRepliesText") == "4 / 4");
        check(Text(window, "IcmpPacketLossText") == "0%" && Text(window, "IcmpJitterText") == Latency(0),
            "Successful zero-millisecond replies remain real zero latency and zero jitter measurements");
        WaitWithoutUi(() => source.Calls == 17 && source.Active == 1);
        var callsBeforeNavigation = source.Calls;
        var latestBeforeNavigation = monitoring.LatestIcmpMeasurement;
        for (var round = 0; round < 2; round++)
        {
            foreach (var name in new[] { "DashboardTab", "ConnectionsTab", "HistoryTab", "SettingsTab", "HealthTab" })
            {
                navigation.SelectedItem = Control<TabItem>(window, name);
                window.UpdateLayout();
                Pump();
            }
        }
        check(source.Calls == callsBeforeNavigation && source.MaximumConcurrent == 1 &&
            ReferenceEquals(worker, icmp.Completion) && ReferenceEquals(latestBeforeNavigation, monitoring.LatestIcmpMeasurement) &&
            Text(window, "IcmpJitterText") == Latency(0),
            "Repeated navigation reuses the running ICMP worker and never starts another blocked probe");

        navigation.SelectedItem = Control<TabItem>(window, "DashboardTab");
        Pump();
        viewTimer.Stop(); // Prove page visibility refresh works even before another timer tick.
        clock.Advance(TimeSpan.FromSeconds(15));
        var returnTime = clock.GetUtcNow();
        source.Queue(Success(50), Success(60), Success(50), Success(40));
        WaitWithoutUi(() => monitoring.LatestIcmpMeasurement?.TimestampUtc == returnTime && source.Calls == 21);
        check(Text(window, "IcmpLatencyText") == Latency(0) && Text(window, "IcmpJitterText") == Latency(0),
            "An inactive Health page retains its view until it is selected again");
        navigation.SelectedItem = health;
        window.UpdateLayout();
        Pump();
        check(Text(window, "IcmpLatencyText") == Latency(50) && Text(window, "IcmpJitterText") == Latency(10) &&
            Text(window, "IcmpLastSampleText") == LocalTimestamp(returnTime) &&
            !viewTimer.IsEnabled,
            "Returning to Health immediately displays the latest latency and jitter without waiting for a timer tick");
        viewTimer.Start();

        var completedBeforePartial = monitoring.RollingIcmpSamples;
        source.Queue(Success(100), Success(100));
        WaitWithoutUi(() => source.Calls == 23 && source.Active == 1);
        Pump();
        check(Text(window, "IcmpLatencyText") == Latency(50) && Text(window, "IcmpJitterText") == Latency(10) &&
            monitoring.LatestIcmpMeasurement?.TimestampUtc == returnTime,
            "An incomplete new burst cannot replace the last completed ICMP measurement");
        check(completedBeforePartial.Count == 5 && monitoring.RollingIcmpSamples.SequenceEqual(completedBeforePartial) &&
            ReferenceEquals(completedBeforePartial[^1], monitoring.LatestIcmpMeasurement),
            "An unfinished burst does not enter rolling history or replace its last completed sample");
        window.Close();
        check(context.Dashboard is null && !viewTimer.IsEnabled && monitoring.GetSnapshot().IsRunning,
            "Closing the Health dashboard stops its view timer while background monitoring continues");
        clock.Advance(TimeSpan.FromSeconds(15));
        var backgroundTime = clock.GetUtcNow();
        source.Queue(Failure(), Failure());
        WaitWithoutUi(() => monitoring.LatestIcmpMeasurement?.TimestampUtc == backgroundTime && source.Calls == 25);
        check(monitoring.LatestIcmpMeasurement is
            { SuccessfulReplies: 2, FailedReplies: 2, AverageLatencyMs: 100, PacketLossPercent: 50, JitterMs: 0 } &&
            history.GetActiveSession()!.Id == physicalId && ReferenceEquals(worker, icmp.Completion),
            "The same ICMP worker completes fresh measurements while the dashboard is closed");
        var backgroundSamples = monitoring.RollingIcmpSamples;
        check(backgroundSamples.Count == 6 && ReferenceEquals(backgroundSamples[^1], monitoring.LatestIcmpMeasurement) &&
            backgroundSamples.Select(sample => sample.TimestampUtc)
                .SequenceEqual(backgroundSamples.Select(sample => sample.TimestampUtc).Order()),
            "Completed ICMP samples keep accumulating in chronological order while NetTrace runs tray-only");
        context.ShowDashboard();
        var recreated = context.Dashboard!;
        Control<TabControl>(recreated, "MainNavigation").SelectedItem = Control<TabItem>(recreated, "HealthTab");
        recreated.UpdateLayout();
        Pump();
        check(created == 2 && !ReferenceEquals(window, recreated) && Text(recreated, "IcmpTargetText") == "9.9.9.9" &&
            Text(recreated, "IcmpLatencyText") == Latency(100) && Text(recreated, "IcmpJitterText") == Latency(0) &&
            Text(recreated, "IcmpPacketLossText") == "50%" &&
            Text(recreated, "IcmpRepliesText") == "2 / 4" && Text(recreated, "IcmpLastSampleText") == LocalTimestamp(backgroundTime),
            "Tray Open recreates Health with the freshest background latency, jitter and local sample time");

        clock.Advance(TimeSpan.FromSeconds(15));
        var insufficientTime = clock.GetUtcNow();
        source.Queue(Failure(), Success(42), Failure(), Failure());
        Wait(() => Text(recreated, "IcmpRepliesText") == "1 / 4" &&
            Text(recreated, "IcmpLastSampleText") == LocalTimestamp(insufficientTime));
        check(Text(recreated, "IcmpLatencyText") == Latency(42) && Text(recreated, "IcmpPacketLossText") == "75%" &&
            Text(recreated, "IcmpJitterText") == "Unavailable",
            "One successful reply preserves its valid latency but cannot produce a jitter value");
        check(completedBeforePartial.Count == 5 && completedBeforePartial[^1].TimestampUtc == returnTime &&
            backgroundSamples.Count == 6 && backgroundSamples[^1].TimestampUtc == backgroundTime &&
            monitoring.RollingIcmpSamples.Count == 7 &&
            ReferenceEquals(monitoring.RollingIcmpSamples[^1], monitoring.LatestIcmpMeasurement),
            "Previously obtained rolling-history snapshots stay stable as the background worker appends completed samples");
        WaitWithoutUi(() => source.Calls == 29 && source.Active == 1);

        var stopTime = Stopwatch.StartNew();
        context.Exit();
        icmp.Completion.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        WaitWithoutUi(() => source.Active == 0);
        check(stopTime.Elapsed < TimeSpan.FromSeconds(1) && exits == 1 && !monitoring.GetSnapshot().IsRunning &&
            history.GetActiveSession() is null && history.GetRecentSessions().Single().EndReason == "ApplicationExit",
            "Tray Exit cancels the pending ICMP request promptly and preserves clean physical-session shutdown");
    }

    private static string Latency(double value) => $"{value:0.##} ms";
    private static string LocalTimestamp(DateTimeOffset value) => value.ToLocalTime().ToString("G");
    private static IcmpPingReply Success(long value) => new(IPStatus.Success, value);
    private static IcmpPingReply Failure() => new(IPStatus.TimedOut, 9999);
    private static string Text(Window window, string name) => Control<TextBlock>(window, name).Text;
    private static T Control<T>(Window window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"Missing ICMP presentation control {name}.");
    private static DispatcherTimer ViewTimer(MainWindow window) =>
        (DispatcherTimer)typeof(MainWindow).GetField("_timer", PrivateInstance)!.GetValue(window)!;
    private static void StopSampler(MonitoringService monitoring) =>
        ((System.Threading.Timer)typeof(MonitoringService).GetField("_timer", PrivateInstance)!.GetValue(monitoring)!)
            .Change(Timeout.Infinite, Timeout.Infinite);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child))
                yield return descendant;
    }

    private static void Wait(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("ICMP presentation did not update automatically.");
            Pump();
        }
        Pump();
    }

    private static void WaitWithoutUi(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Owned ICMP worker did not reach the expected state.");
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
                if (!_replies.Writer.TryWrite(reply)) throw new InvalidOperationException("Owned reply queue is closed.");
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
        private long _utcTicks = new DateTimeOffset(2026, 10, 10, 4, 5, 6, TimeSpan.Zero).Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _utcTicks, elapsed.Ticks);
    }

    private sealed class OwnedIdentity(string adapterId) : INetworkIdentityService
    {
        public event EventHandler? NetworkChanged { add { } remove { } }
        public NetworkIdentityResult GetCurrentNetwork() => new(NetworkIdentityAvailability.Available,
            new NetworkIdentity("WiFi", "ICMP_PRESENTATION", "ICMP_PRESENTATION", adapterId), null);
        public void Dispose() { }
    }

    private sealed class OwnedCounters(string adapterId, TimeProvider clock) : INetworkMonitor
    {
        public IReadOnlyList<AdapterInfo> GetActiveAdapters() =>
            [new(adapterId, "Owned ICMP adapter", "Injected physical adapter", "Wireless80211", 86_000_000, "192.0.2.10", true)];
        public NetworkSnapshot? Capture(string requestedAdapter) =>
            requestedAdapter == adapterId ? new(1000, 500, clock.GetUtcNow()) : null;
    }

    private sealed class ReadOnlyStartup : IWindowsStartupRegistration
    {
        public bool IsEnabled() => false;
        public void Enable() => throw new InvalidOperationException("Startup writes are prohibited in this presentation fixture.");
        public void Disable() => throw new InvalidOperationException("Startup writes are prohibited in this presentation fixture.");
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
