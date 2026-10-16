using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using NetTrace;
using NetTrace.Models;
using NetTrace.Services;

internal static class DownloadSpeedTestChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly string[] StableElements =
    ["DashboardUsageSelectorButton", "DashboardUsageCard", "DashboardTrafficChart", "DownloadRateText",
        "UploadRateText", "DashboardProfileText", "DashboardSessionText"];

    public static void Run(string root, Action<bool, string> check)
    {
        var previews = System.IO.Path.Combine(root, "download-speed-test-previews");
        CheckStates(root, previews, check);
        CheckRealWarning(root, check);
        CheckInteraction(root, previews, check);
        CheckRealCancellation(root, previews, check);
        Console.WriteLine("Real WPF speed-test card state previews: " + previews);
    }

    private static void CheckStates(string root, string previews, Action<bool, string> check)
    {
        using var monitoring = HistoryOverviewChecks.CreateMonitoring(new UsageStore(System.IO.Path.Combine(root, "speed-test-layout")));
        var service = new ControlledDownload();
        var window = CreateWindow(monitoring, service, _ => false);
        try
        {
            window.Show();
            HistoryOverviewChecks.StopViewTimer(window);
            var cases = new[]
            {
                new DownloadSpeedTestSnapshot(DownloadSpeedTestState.Idle, 0, 25_000_000, 0, TimeSpan.Zero),
                new DownloadSpeedTestSnapshot(DownloadSpeedTestState.Running, 12_750_000, 25_000_000, 97.68, TimeSpan.FromSeconds(1.044)),
                new DownloadSpeedTestSnapshot(DownloadSpeedTestState.Cancelled, 12_750_000, 25_000_000, 97.68, TimeSpan.FromSeconds(1.044)),
                new DownloadSpeedTestSnapshot(DownloadSpeedTestState.Completed, 25_000_000, 25_000_000, 99.54, TimeSpan.FromSeconds(2.009)),
                new DownloadSpeedTestSnapshot(DownloadSpeedTestState.Error, 0, 25_000_000, 0, TimeSpan.FromSeconds(2),
                    "The speed-test server is unavailable. An intentionally long explanation must stay inside the fixed card."),
                new DownloadSpeedTestSnapshot(DownloadSpeedTestState.Cancelled, 32_000, 25_000_000, 0.25, TimeSpan.FromSeconds(1))
            };
            foreach (var (width, height) in new[] { (760d, 600d), (920d, 740d) })
            {
                window.Width = width;
                window.Height = height;
                Apply(window, cases[0]);
                Layout(window);
                var page = Page(window);
                page.ScrollToTop();
                Layout(window);
                var initial = StableElements.ToDictionary(name => name, name => Bounds(Control<FrameworkElement>(window, name),
                    Control<FrameworkElement>(window, "DashboardContent")));
                var card = Control<Border>(window, "SpeedTestCard");
                var initialCard = Bounds(card, Control<FrameworkElement>(window, "DashboardContent"));
                var graph = Control<FrameworkElement>(window, "DashboardTrafficChart");
                check(card.Height == 52 && Math.Abs(card.ActualHeight - 52) < 0.01 &&
                    graph.Height == 152 && graph.Margin.Top == 54 &&
                    Math.Abs(initialCard.Top - initial["DashboardUsageSelectorButton"].Top) < 0.5 &&
                    initialCard.Left > initial["DashboardUsageSelectorButton"].Right,
                    $"At {width} DIP the fixed card uses the existing 52-DIP reserved gap to the right of TODAY with unchanged graph dimensions");
                var extent = page.ExtentHeight;
                foreach (var sample in cases)
                {
                    Apply(window, sample);
                    Layout(window);
                    var label = sample.State + (sample.State == DownloadSpeedTestState.Cancelled && !sample.HasMeasurement ? "-too-short" : "");
                    check(SameRect(Bounds(card, Control<FrameworkElement>(window, "DashboardContent")), initialCard) &&
                        StableElements.All(name => SameRect(Bounds(Control<FrameworkElement>(window, name),
                            Control<FrameworkElement>(window, "DashboardContent")), initial[name])) &&
                        Math.Abs(page.ExtentHeight - extent) < 0.01,
                        $"At {width} DIP {label} preserves the exact card, graph, TODAY, live cards, connection positions and scroll extent");
                    var statusText = Control<TextBlock>(window, "SpeedTestStatusText").Text;
                    var expectedButton = sample.State == DownloadSpeedTestState.Running ? "Cancel" :
                        sample.State == DownloadSpeedTestState.Idle ? "Start Test" : "Test Again";
                    check(Control<Button>(window, "SpeedTestActionButton").Content?.ToString() == expectedButton &&
                        (sample.State != DownloadSpeedTestState.Cancelled ||
                            statusText.Contains("partial", StringComparison.OrdinalIgnoreCase) == sample.HasMeasurement) &&
                        (sample.State != DownloadSpeedTestState.Completed || statusText.Contains("Completed", StringComparison.OrdinalIgnoreCase)) &&
                        (sample.State != DownloadSpeedTestState.Error || statusText.Contains("Error", StringComparison.OrdinalIgnoreCase)) &&
                        Control<ProgressBar>(window, "SpeedTestProgress").Visibility ==
                            (sample.State == DownloadSpeedTestState.Running ? Visibility.Visible : Visibility.Hidden),
                        $"At {width} DIP {label} shows its proper action and status, reserves progress space, and distinguishes partial cancellation");
                    CheckContents(window, check, $"{width} DIP {label}");
                    HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, $"{width}x{height}-{label}.png"));
                }
                var status = Control<FrameworkElement>(window, "StatusText");
                var content = Control<FrameworkElement>(window, "DashboardContent");
                var rootElement = (FrameworkElement)window.Content;
                var statusBounds = Bounds(status, rootElement);
                check(!HistoryOverviewChecks.Descendants(content).Contains(status) && statusBounds.Left > rootElement.ActualWidth / 2 &&
                    statusBounds.Top < Bounds(Control<FrameworkElement>(window, "MainNavigation"), rootElement).Top &&
                    Control<FrameworkElement>(window, "StatusDot").ActualWidth <= 10,
                    $"At {width} DIP Monitoring is a subtle global indicator in the top-right app header");
            }
            check(service.Calls == 0 && monitoring.GetUsageTotals().Today == (0L, 0L),
                "Rendering all speed-test states starts no network work and neither adds nor subtracts normal usage");
        }
        finally { window.Close(); }
    }

    private static void CheckInteraction(string root, string previews, Action<bool, string> check)
    {
        using var monitoring = HistoryOverviewChecks.CreateMonitoring(new UsageStore(System.IO.Path.Combine(root, "speed-test-interaction")),
            identity: new HistoryOverviewChecks.OwnedIdentity(new NetworkIdentity("Ethernet", "Owned speed", "Owned speed", HistoryOverviewChecks.AdapterId)));
        var service = new ControlledDownload();
        var confirmations = new List<long>();
        var allow = false;
        var window = CreateWindow(monitoring, service, limit => { confirmations.Add(limit); return allow; });
        try
        {
            window.Show();
            HistoryOverviewChecks.StopViewTimer(window);
            monitoring.Start();
            Wait(() => monitoring.GetSnapshot().Session is not null);
            Layout(window);
            var selector = Control<ComboBox>(window, "SpeedTestLimitSelector");
            var limits = selector.Items.Cast<ComboBoxItem>().Select(item => Convert.ToInt32(item.Tag)).ToArray();
            check(limits.SequenceEqual(new[] { 25, 50, 100, 250 }) && selector.SelectedIndex == 0,
                "The real card offers exactly 25, 50, 100 and 250 MB and defaults to the smallest data limit");
            var warning = Control<TextBlock>(window, "SpeedTestWarningText");
            check(warning.Text.Contains("data", StringComparison.OrdinalIgnoreCase) &&
                (warning.Text.Contains("usage", StringComparison.OrdinalIgnoreCase) || warning.Text.Contains("totals", StringComparison.OrdinalIgnoreCase)),
                "The card visibly warns that real download data remains in ordinary NetTrace usage");
            var geometry = Geometry(window);
            var valid = monitoring.GetSnapshot();
            var otherAdapter = valid.SelectedAdapter! with { Id = "different-unmonitored-adapter" };
            var canStart = typeof(MainWindow).GetMethod("CanStartDownloadSpeedTest", PrivateInstance)!;
            var allowed = (bool)canStart.Invoke(window, new object[] { valid with { SelectedAdapter = otherAdapter }, 25_000_000L })!;
            check(!allowed && service.Calls == 0 && confirmations.Count == 0,
                "A selected adapter that differs from the physical route cannot start a download or show a misleading data warning");
            Apply(window, new(DownloadSpeedTestState.Idle, 0, 25_000_000, 0, TimeSpan.Zero));
            foreach (var (limit, index) in limits.Select((limit, index) => (limit, index)))
            {
                selector.SelectedIndex = index;
                Click(window);
                Layout(window);
                check(confirmations[^1] == limit * 1_000_000L && service.Calls == 0 &&
                    Geometry(window).SequenceEqual(geometry),
                    $"Declining the {limit} MB pre-start warning prevents all downloads and preserves layout");
            }
            selector.SelectedIndex = 0;
            allow = true;
            Click(window);
            Wait(() => service.Calls == 1);
            service.Report(new(DownloadSpeedTestState.Running, 12_750_000, 25_000_000, 97.68, TimeSpan.FromSeconds(1.044)));
            Wait(() => Control<Button>(window, "SpeedTestActionButton").Content?.ToString() == "Cancel" &&
                Control<TextBlock>(window, "SpeedTestRateText").Text.Contains("97.7", StringComparison.Ordinal) &&
                Control<TextBlock>(window, "SpeedTestDataText").Text.Contains("12.8", StringComparison.Ordinal));
            Layout(window);
            check(!selector.IsEnabled && Control<TextBlock>(window, "SpeedTestRateText").Text.Contains("97.7", StringComparison.Ordinal) &&
                Control<TextBlock>(window, "SpeedTestDataText").Text.Contains("12.8", StringComparison.Ordinal) &&
                Control<ProgressBar>(window, "SpeedTestProgress").Value > 0,
                "A running request renders live Mbps, real received data and progress with a stable disabled data limit");
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, "760x600-actual-start-running.png"));
            Click(window);
            Wait(() => service.LastToken.IsCancellationRequested &&
                Control<Button>(window, "SpeedTestActionButton").Content?.ToString() == "Test Again");
            Layout(window);
            check(Control<TextBlock>(window, "SpeedTestStatusText").Text.Contains("partial", StringComparison.OrdinalIgnoreCase) &&
                selector.IsEnabled && Geometry(window).SequenceEqual(geometry),
                "The actual Cancel action signals the download immediately and retains a partial result without moving the dashboard");
            Click(window);
            Wait(() => service.Calls == 2);
            service.Complete(new(DownloadSpeedTestState.Completed, 25_000_000, 25_000_000, 99.54, TimeSpan.FromSeconds(2.009)));
            Wait(() => Control<Button>(window, "SpeedTestActionButton").Content?.ToString() == "Test Again");
            check(Control<TextBlock>(window, "SpeedTestStatusText").Text.Contains("Completed", StringComparison.OrdinalIgnoreCase) &&
                Geometry(window).SequenceEqual(geometry), "Test Again completes another capped request in the same dashboard space");
            Click(window);
            Wait(() => service.Calls == 3);
            service.Complete(new(DownloadSpeedTestState.Error, 0, 25_000_000, 0, TimeSpan.FromSeconds(1), "Owned server unavailable."));
            Wait(() => Control<Button>(window, "SpeedTestActionButton").Content?.ToString() == "Test Again");
            check(Geometry(window).SequenceEqual(geometry) && selector.IsEnabled,
                "An error leaves the same fixed dashboard layout and allows another explicit test");
            Click(window);
            Wait(() => service.Calls == 4);
            monitoring.Suspend();
            Wait(() => service.LastToken.IsCancellationRequested &&
                Control<Button>(window, "SpeedTestActionButton").Content?.ToString() == "Test Again");
            check(Geometry(window).SequenceEqual(geometry),
                "Suspending normal monitoring cancels the download immediately without shifting the dashboard");
            monitoring.Resume();
            Wait(() => monitoring.GetSnapshot().Session is not null && !monitoring.GetSnapshot().IsSuspended);
            Click(window);
            Wait(() => service.Calls == 5);
            monitoring.SelectAdapter(null);
            Wait(() => service.LastToken.IsCancellationRequested &&
                Control<Button>(window, "SpeedTestActionButton").Content?.ToString() == "Test Again");
            check(Geometry(window).SequenceEqual(geometry),
                "Losing the selected monitored adapter stops the download rather than continuing uncounted traffic");
            monitoring.SelectAdapter(HistoryOverviewChecks.AdapterId);
            Click(window);
            Wait(() => service.Calls == 6);
            window.Close();
            Wait(() => service.LastToken.IsCancellationRequested && service.Disposed);
            check(service.MaxConcurrent == 1 && monitoring.GetUsageTotals().Today == (0L, 0L),
                "Closing the real window stops its download, never overlaps requests and does not fabricate accounting from payload progress");
        }
        finally { window.Close(); }
    }

    private static void CheckRealWarning(string root, Action<bool, string> check)
    {
        using var monitoring = HistoryOverviewChecks.CreateMonitoring(new UsageStore(System.IO.Path.Combine(root, "speed-test-warning")));
        var service = new ControlledDownload();
        var window = CreateWindow(monitoring, service, _ => false);
        try
        {
            window.Show();
            HistoryOverviewChecks.StopViewTimer(window);
            var owner = new WindowInteropHelper(window).Handle;
            var observe = Task.Run(async () =>
            {
                var elapsed = Stopwatch.StartNew();
                IntPtr dialog;
                do
                {
                    dialog = FindWindow("#32770", "Speed Test — real data usage");
                    if (dialog != IntPtr.Zero && GetWindow(dialog, 4) == owner) break;
                    if (elapsed.Elapsed > TimeSpan.FromSeconds(10))
                        throw new TimeoutException("The real owned speed-test warning dialog was not found.");
                    await Task.Delay(20).ConfigureAwait(false);
                } while (true);
                var no = GetDlgItem(dialog, 7);
                try
                {
                    var body = new StringBuilder();
                    EnumChildWindows(dialog, (child, _) =>
                    {
                        var className = new StringBuilder(64);
                        GetClassName(child, className, className.Capacity);
                        if (className.ToString() == "Static")
                        {
                            var text = new StringBuilder(2048);
                            GetWindowText(child, text, text.Capacity);
                            body.AppendLine(text.ToString());
                        }
                        return true;
                    }, IntPtr.Zero);
                    var defaultId = SendMessage(dialog, 0x0400, IntPtr.Zero, IntPtr.Zero).ToInt64() & 0xFFFF;
                    var defaultNo = no != IntPtr.Zero && (defaultId == 7 || (GetWindowLong(no, -16) & 0xF) == 1);
                    return (Text: body.ToString(), DefaultNo: defaultNo);
                }
                finally
                {
                    // Decline the real native modal. No service call or data transfer
                    // is involved, even if a warning assertion subsequently fails.
                    if (no != IntPtr.Zero) SendMessage(no, 0x00F5, IntPtr.Zero, IntPtr.Zero);
                    else PostMessage(dialog, 0x0111, new IntPtr(7), IntPtr.Zero);
                }
            });
            var accepted = (bool)typeof(MainWindow).GetMethod("ConfirmDownloadSpeedTest", PrivateInstance)!
                .Invoke(window, new object[] { 25_000_000L })!;
            var warning = observe.GetAwaiter().GetResult();
            check(!accepted && service.Calls == 0 && warning.Text.Contains("25 MB", StringComparison.Ordinal) &&
                warning.Text.Contains("real internet data", StringComparison.OrdinalIgnoreCase) &&
                warning.Text.Contains("normal usage totals", StringComparison.OrdinalIgnoreCase) &&
                warning.Text.Contains("cancel", StringComparison.OrdinalIgnoreCase),
                "The real pre-start native warning names the data cap, internet consumption, normal usage totals and immediate cancellation");
            check(warning.DefaultNo, "The real data-consumption warning defaults to No and declining it performs no download");
        }
        finally { window.Close(); }
    }

    private static void CheckRealCancellation(string root, string previews, Action<bool, string> check)
    {
        using var monitoring = HistoryOverviewChecks.CreateMonitoring(new UsageStore(System.IO.Path.Combine(root, "speed-test-real-cancel")),
            identity: new HistoryOverviewChecks.OwnedIdentity(new NetworkIdentity("Ethernet", "Owned speed", "Owned speed", HistoryOverviewChecks.AdapterId)));
        using var server = new StalledBodyServer();
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, MaxResponseDrainSize = 0 })
            { Timeout = Timeout.InfiniteTimeSpan };
        var actual = new DownloadSpeedTest(client, server.Endpoint);
        var warningCount = 0;
        var window = CreateWindow(monitoring, actual, limit => { warningCount++; return limit == 25_000_000; });
        try
        {
            window.Show();
            HistoryOverviewChecks.StopViewTimer(window);
            monitoring.Start();
            Wait(() => monitoring.GetSnapshot().Session is not null);
            var geometry = Geometry(window);
            Click(window);
            Wait(() => server.DataSent.Task.IsCompleted, TimeSpan.FromSeconds(10));
            Wait(() => Control<ProgressBar>(window, "SpeedTestProgress").Value > 0);
            var elapsed = Stopwatch.StartNew();
            Click(window);
            Wait(() => server.Disconnected.Task.IsCompleted &&
                Control<Button>(window, "SpeedTestActionButton").Content?.ToString() == "Test Again", TimeSpan.FromSeconds(2));
            check(elapsed.Elapsed < TimeSpan.FromSeconds(2) && warningCount == 1 &&
                Control<TextBlock>(window, "SpeedTestStatusText").Text.Contains("partial", StringComparison.OrdinalIgnoreCase) &&
                Geometry(window).SequenceEqual(geometry),
                "The actual WPF Cancel button interrupts a real stalled HTTP body immediately, closes its socket and retains a fixed partial-result card");
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, "760x600-real-http-cancelled.png"));
        }
        finally { window.Close(); }
    }

    // Explicit opt-in only. The ordinary WPF checks never make an internet download.
    // This runs the real WPF shell, real service, physical adapter sampler and normal
    // SQLite accounting with an isolated database, using just the smallest cap.
    public static int RunLiveValidation()
    {
        MainWindow? window = null;
        MonitoringService? monitoring = null;
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NetTrace-speed-test-live-" + Guid.NewGuid().ToString("N"));
        try
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var store = new UsageStore(root);
            monitoring = new MonitoringService(store, new NetworkIdentityService(), networkMonitor: new NetworkMonitor(),
                icmpMonitor: new IcmpMonitor(new NoPing()), reachabilityMonitor: TestReachability.Create());
            var real = new ObservedDownload();
            var warnings = 0;
            window = CreateWindow(monitoring, real, limit =>
            {
                if (limit != 25_000_000) throw new InvalidOperationException("Live validation exceeded the authorized 25 MB test cap.");
                warnings++;
                Console.WriteLine("Accepted pre-start warning: 25 MB real download payload plus normal protocol overhead; all traffic remains in normal usage.");
                return true;
            });
            window.Width = 920;
            window.Height = 740;
            window.Opacity = 1;
            window.ShowInTaskbar = true;
            window.Show();
            monitoring.Start();
            Wait(() => monitoring.GetSnapshot().Session is not null &&
                monitoring.GetSnapshot().SelectedAdapter is { Type: "Ethernet" or "Wireless80211" }, TimeSpan.FromSeconds(15));
            monitoring.SampleNow();
            window.RefreshDashboard();
            Layout(window);
            var baseline = monitoring.GetSnapshot();
            var usageBefore = monitoring.GetUsageTotals().Today;
            var geometry = Geometry(window);
            Control<ComboBox>(window, "SpeedTestLimitSelector").SelectedIndex = 0;
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(root, "920x740-real-idle.png"));
            Click(window);
            var deadline = Stopwatch.StartNew();
            var savedRunning = false;
            while (real.Result is null)
            {
                if (deadline.Elapsed > TimeSpan.FromMinutes(5.1)) throw new TimeoutException("The real bounded download did not finish.");
                Layout(window);
                if (!Geometry(window).SequenceEqual(geometry)) throw new InvalidOperationException("The dashboard shifted during the real transfer.");
                if (!savedRunning && real.Latest is { State: DownloadSpeedTestState.Running, BytesReceived: > 0 })
                {
                    HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(root, "920x740-real-running.png"));
                    savedRunning = true;
                }
            }
            Wait(() => Control<Button>(window, "SpeedTestActionButton").Content?.ToString() == "Test Again");
            var result = real.Result!;
            if (result.State != DownloadSpeedTestState.Completed || result.BytesReceived != 25_000_000 ||
                !result.HasMeasurement || result.MegabitsPerSecond <= 0)
                throw new InvalidOperationException("Real internet test did not complete: " + result.State + " " + result.ErrorMessage);
            // A final existing-adapter sample includes the complete transfer even if
            // it finished between the normal one-second sampler ticks.
            monitoring.SampleNow();
            window.RefreshDashboard();
            Layout(window);
            var after = monitoring.GetSnapshot();
            var usageAfter = monitoring.GetUsageTotals().Today;
            var usageDelta = usageAfter.Downloaded - usageBefore.Downloaded;
            var appSessionDelta = after.SessionDownloaded - baseline.SessionDownloaded;
            var connectionDelta = (after.Session?.DownloadBytes ?? 0) - (baseline.Session?.DownloadBytes ?? 0);
            if (after.Session?.Id != baseline.Session?.Id || usageDelta < result.BytesReceived ||
                appSessionDelta < result.BytesReceived || connectionDelta < result.BytesReceived)
                throw new InvalidOperationException($"Normal physical accounting did not include the real test: today {usageDelta}, app {appSessionDelta}, connection {connectionDelta}, payload {result.BytesReceived}.");
            monitoring.Flush();
            var stored = store.GetTotals(after.PendingDay, after.PendingDay);
            if (stored.Downloaded < usageAfter.Downloaded)
                throw new InvalidOperationException("The ordinary usage flush did not persist the counted test traffic.");
            if (!Geometry(window).SequenceEqual(geometry) || warnings != 1)
                throw new InvalidOperationException("The real test changed layout or bypassed the pre-start warning.");
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(root, "920x740-real-completed.png"));
            Console.WriteLine($"Real 25 MB WPF test completed: {result.MegabitsPerSecond:F2} Mbps; {result.BytesReceived} payload bytes; normal usage delta {usageDelta}; normal app-session delta {appSessionDelta}; connection delta {connectionDelta}; persisted download {stored.Downloaded}.");
            Console.WriteLine("All real transfer states kept identical card and surrounding bounds. Live WPF evidence: " + root);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Real speed-test validation failed: " + exception);
            return 1;
        }
        finally
        {
            window?.Close();
            monitoring?.Dispose();
            Application.Current?.Shutdown();
        }
    }

    private static void CheckContents(MainWindow window, Action<bool, string> check, string label)
    {
        var card = Control<Border>(window, "SpeedTestCard");
        var page = Page(window);
        var names = new[] { "SpeedTestLimitSelector", "SpeedTestActionButton", "SpeedTestRateText",
            "SpeedTestDataText", "SpeedTestStatusText", "SpeedTestWarningText", "SpeedTestProgress" };
        check(names.Select(name => Control<FrameworkElement>(window, name)).Where(element => element.Visibility == Visibility.Visible)
                .All(element => Bounds(element, card) is var bounds && bounds.Left >= -0.5 && bounds.Top >= -0.5 &&
                    bounds.Right <= card.ActualWidth + 0.5 && bounds.Bottom <= card.ActualHeight + 0.5) &&
            page.ScrollableWidth < 0.01 && page.ExtentWidth <= page.ViewportWidth + 0.5,
            $"{label} keeps all visible speed-test content inside the card with no horizontal dashboard overflow");
        var button = Control<Button>(window, "SpeedTestActionButton");
        check(button.ActualWidth >= 65 && button.ActualHeight >= 24,
            $"{label} keeps the compact primary or Cancel button visible and usable");
    }

    private static MainWindow CreateWindow(MonitoringService monitoring, IDownloadSpeedTest service, Func<long, bool> confirm) =>
        new(monitoring, new OwnedStartup(), new WifiRadioService(new NoRadio()),
            appUsageClient: new AppUsageTodayChecks.UnavailableClient(), downloadSpeedTest: service, confirmDownloadSpeedTest: confirm)
        { ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Width = 760, Height = 600 };

    private static void Apply(MainWindow window, DownloadSpeedTestSnapshot sample) =>
        typeof(MainWindow).GetMethod("ApplyDownloadSpeedTestSnapshot", PrivateInstance)!.Invoke(window, new object[] { sample });

    private static void Click(MainWindow window) => typeof(Button).GetMethod("OnClick", PrivateInstance)!
        .Invoke(Control<Button>(window, "SpeedTestActionButton"), null);

    private static Rect[] Geometry(MainWindow window)
    {
        Layout(window);
        var content = Control<FrameworkElement>(window, "DashboardContent");
        return StableElements.Append("SpeedTestCard").Select(name => Bounds(Control<FrameworkElement>(window, name), content)).ToArray();
    }

    private static Rect Bounds(FrameworkElement element, Visual parent) => new(element.TranslatePoint(new Point(), (UIElement)parent),
        new Size(element.ActualWidth, element.ActualHeight));

    private static bool SameRect(Rect left, Rect right) => Math.Abs(left.X - right.X) < 0.01 &&
        Math.Abs(left.Y - right.Y) < 0.01 && Math.Abs(left.Width - right.Width) < 0.01 && Math.Abs(left.Height - right.Height) < 0.01;

    private static T Control<T>(Window window, string name) where T : class => HistoryOverviewChecks.Control<T>(window, name);

    private static ScrollViewer Page(MainWindow window) => (ScrollViewer)Control<TabItem>(window, "DashboardTab").Content;

    private static void Layout(MainWindow window)
    {
        window.UpdateLayout();
        HistoryOverviewChecks.Pump();
        window.UpdateLayout();
    }

    private static void Wait(Func<bool> ready, TimeSpan? timeout = null)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready())
        {
            if (elapsed.Elapsed > (timeout ?? TimeSpan.FromSeconds(5))) throw new TimeoutException("The WPF speed-test state was not ready.");
            HistoryOverviewChecks.Pump();
        }
        HistoryOverviewChecks.Pump();
    }

    private sealed class OwnedStartup : IWindowsStartupRegistration
    {
        public bool IsEnabled() => false;
        public void Enable() => throw new InvalidOperationException("Speed-test checks cannot change startup settings.");
        public void Disable() => throw new InvalidOperationException("Speed-test checks cannot change startup settings.");
    }

    private sealed class NoRadio : IWifiRadioSource
    {
        public WifiRadioSnapshot Read(string adapterId) => new(adapterId, false);
    }

    private sealed class NoPing : IPingSource
    {
        public Task<IcmpPingReply> SendAsync(IPAddress target, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new IcmpPingReply(IPStatus.TimedOut));
    }

    private sealed class ControlledDownload : IDownloadSpeedTest
    {
        private TaskCompletionSource<DownloadSpeedTestSnapshot>? _completion;
        private IProgress<DownloadSpeedTestSnapshot>? _progress;
        private DownloadSpeedTestSnapshot? _latest;
        private CancellationTokenRegistration _cancellation;
        private int _active;
        public int Calls { get; private set; }
        public int MaxConcurrent { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public bool Disposed { get; private set; }

        public async Task<DownloadSpeedTestSnapshot> RunAsync(long byteLimit, IProgress<DownloadSpeedTestSnapshot>? progress,
            CancellationToken cancellationToken)
        {
            Calls++;
            MaxConcurrent = Math.Max(MaxConcurrent, ++_active);
            LastToken = cancellationToken;
            _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _progress = progress;
            _latest = new(DownloadSpeedTestState.Running, 0, byteLimit, 0, TimeSpan.Zero);
            Report(_latest);
            _cancellation = cancellationToken.Register(() => Complete(_latest! with { State = DownloadSpeedTestState.Cancelled }));
            try { return await _completion.Task; }
            finally { _active--; _cancellation.Dispose(); }
        }

        public void Report(DownloadSpeedTestSnapshot sample) { _latest = sample; _progress?.Report(sample); }
        public void Complete(DownloadSpeedTestSnapshot sample)
        {
            Report(sample);
            _completion?.TrySetResult(sample);
        }
        public void Dispose() { Disposed = true; }
    }

    private sealed class ObservedDownload : IDownloadSpeedTest
    {
        private readonly DownloadSpeedTest _real = new();
        private DownloadSpeedTestSnapshot? _latest;
        private DownloadSpeedTestSnapshot? _result;
        public DownloadSpeedTestSnapshot? Latest => Volatile.Read(ref _latest);
        public DownloadSpeedTestSnapshot? Result => Volatile.Read(ref _result);
        public async Task<DownloadSpeedTestSnapshot> RunAsync(long byteLimit, IProgress<DownloadSpeedTestSnapshot>? progress,
            CancellationToken cancellationToken)
        {
            var result = await _real.RunAsync(byteLimit, new ImmediateProgress(sample =>
            {
                Volatile.Write(ref _latest, sample);
                progress?.Report(sample);
            }), cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _result, result);
            return result;
        }
        public void Dispose() => _real.Dispose();
    }

    private sealed class ImmediateProgress(Action<DownloadSpeedTestSnapshot> report) : IProgress<DownloadSpeedTestSnapshot>
    {
        public void Report(DownloadSpeedTestSnapshot value) => report(value);
    }

    private sealed class StalledBodyServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _server;
        public TaskCompletionSource DataSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Uri Endpoint { get; }

        public StalledBodyServer()
        {
            _listener.Start();
            Endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/__down");
            _server = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                var stream = client.GetStream();
                var headers = new List<byte>();
                var one = new byte[1];
                while (headers.Count < 16_384 && await stream.ReadAsync(one, _lifetime.Token) > 0)
                {
                    headers.Add(one[0]);
                    if (headers.Count >= 4 && headers[^4] == 13 && headers[^3] == 10 && headers[^2] == 13 && headers[^1] == 10)
                        break;
                }
                if (!Encoding.ASCII.GetString(headers.ToArray()).Contains("bytes=25000000", StringComparison.Ordinal))
                    throw new InvalidOperationException("The WPF card did not request its selected 25 MB cap.");
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 25000000\r\nConnection: close\r\n\r\n"), _lifetime.Token);
                var chunk = new byte[64 * 1024];
                var remaining = 2_000_000;
                while (remaining > 0)
                {
                    var count = Math.Min(chunk.Length, remaining);
                    await stream.WriteAsync(chunk.AsMemory(0, count), _lifetime.Token);
                    remaining -= count;
                    await Task.Delay(20, _lifetime.Token);
                }
                await stream.FlushAsync(_lifetime.Token);
                DataSent.TrySetResult();
                try
                {
                    if (await stream.ReadAsync(one, _lifetime.Token) == 0) Disconnected.TrySetResult();
                }
                catch (System.IO.IOException) { Disconnected.TrySetResult(); }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (SocketException) when (_lifetime.IsCancellationRequested) { }
        }

        public void Dispose()
        {
            _lifetime.Cancel();
            _listener.Stop();
            try { _server.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { }
            _lifetime.Dispose();
        }
    }

    private delegate bool EnumWindowCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")]
    private static extern IntPtr GetDlgItem(IntPtr dialog, int identifier);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowCallback callback, IntPtr state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder text, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr first, IntPtr second);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr first, IntPtr second);
}
