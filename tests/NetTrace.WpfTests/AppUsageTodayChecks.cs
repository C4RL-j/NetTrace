using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AppUsageIpc;
using NetTrace;
using NetTrace.Presentation;
using NetTrace.Services;
using NetTrace.Utils;

internal static class AppUsageTodayChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly DateOnly Today = new(2026, 10, 13);

    public static void Run(string root, Action<bool, string> check)
    {
        CheckPresentation(check);
        CheckController(check);
        CheckWindow(root, check);
    }

    private static void CheckPresentation(Action<bool, string> check)
    {
        var result = Fixture();
        var presentation = AppUsageTodayPresentation.FromResult(Connected(result));
        check(presentation.State == AppUsageTodayState.Available,
            "Today typed IPC results produce available presentation");
        check(presentation.TrackedAppsText == "4", "Today presentation preserves tracked application count");
        check(presentation.DownloadText == ByteFormatter.Format(1900), "Today download uses the existing cumulative byte formatter");
        check(presentation.UploadText == ByteFormatter.Format(1050), "Today upload uses the existing cumulative byte formatter");
        check(presentation.LogicalTotalText == ByteFormatter.Format(2950), "Today logical total preserves Brave, NetMod, System and Unknown bytes");
        check(presentation.Applications.Select(row => row.ApplicationIdentity).SequenceEqual(result.Applications.Select(row => row.ApplicationIdentity)),
            "Today presentation preserves service total and identity ordering instead of sorting names");
        check(presentation.Applications.Single(row => row.ApplicationIdentity == "system").DisplayName == "System",
            "System usage remains a normal visible application row");
        check(presentation.Applications.Single(row => row.ApplicationIdentity == "unknown").DisplayName == "Unknown / Unattributed",
            "Unknown and Unattributed usage remains a normal visible application row");
        var netmod = presentation.Applications.Single(row => row.ApplicationIdentity == "netmod");
        check(netmod.DownloadText == ByteFormatter.Format(500) && netmod.UploadText == ByteFormatter.Format(500) &&
            netmod.TotalText == ByteFormatter.Format(1000), "NetMod is presented without subtraction or physical traffic reconciliation");
        check(presentation.Applications[0].HasExecutableName && presentation.Applications[0].ExecutableName == "brave.exe" &&
            !presentation.Applications[^1].HasExecutableName,
            "Application executable metadata is optional and never replaced by invented names");
        foreach (var bytes in new long[] { 842, 12_700, 19_085_312, 1_535_484_723 })
        {
            var sample = AppUsageTodayPresentation.FromResult(Connected(Result(
                [new("format", "Format application", "format.exe", null, bytes, 0, bytes, false, false)])));
            check(sample.DownloadText == ByteFormatter.Format(bytes) && sample.LogicalTotalText == ByteFormatter.Format(bytes) &&
                sample.Applications[0].TotalText == ByteFormatter.Format(bytes) && !sample.DownloadText.Contains("/s", StringComparison.Ordinal),
                $"Today {bytes}-byte summary and row reuse cumulative size formatting without speed units");
        }
        var empty = AppUsageTodayPresentation.FromResult(Connected(Result([])));
        check(empty.State == AppUsageTodayState.Empty && empty.Message == "No app network usage recorded today." &&
            empty.Applications.Count == 0 && empty.TrackedAppsText == "0" && empty.LogicalTotalText == ByteFormatter.Format(0),
            "An empty successful Today response has real zero summaries and no fabricated application rows");
        foreach (var state in new[] { IpcState.CollectorUnavailable, IpcState.AccessDenied, IpcState.PeerRejected,
            IpcState.IncompatibleProtocol, IpcState.MalformedResponse, IpcState.Disconnected, IpcState.TimedOut, IpcState.OperationUnsupported })
        {
            var unavailable = AppUsageTodayPresentation.FromResult(new(state, null, "Sensitive path C:\\private\\secret.db at Internal.StackTrace()"));
            check(unavailable.State == AppUsageTodayState.Unavailable && unavailable.Message == "App tracking unavailable." &&
                unavailable.Applications.Count == 0 && !unavailable.Detail.Contains("private", StringComparison.OrdinalIgnoreCase) &&
                !unavailable.Detail.Contains("StackTrace", StringComparison.Ordinal),
                $"Today {state} produces a calm unavailable page without raw IPC diagnostics");
        }
        foreach (var availability in new[] { AppUsageAvailability.Unavailable, AppUsageAvailability.Degraded })
        {
            var unavailable = AppUsageTodayPresentation.FromResult(Connected(result with
                { Availability = availability, Diagnostic = "Sensitive storage details" }));
            check(unavailable.State == AppUsageTodayState.Unavailable && unavailable.Applications.Count == 0 &&
                !unavailable.Detail.Contains("Sensitive", StringComparison.Ordinal),
                $"Today {availability} storage remains a safe unavailable presentation");
        }
        check(AppUsageTodayPresentation.FromResult(Connected(result with { Range = AppUsageRange.SevenDays })).State == AppUsageTodayState.Unavailable,
            "The Today page rejects a successful response for an unrequested range");
    }

    private static void CheckController(Action<bool, string> check)
    {
        var clock = new OwnedClock();
        var client = new ControlledClient();
        var presentations = new List<AppUsageTodayPresentation>();
        var uiThread = Environment.CurrentManagedThreadId;
        var applyThreads = new List<int>();
        var controller = new AppUsageTodayController(Dispatcher.CurrentDispatcher, client,
            state => { presentations.Add(state); applyThreads.Add(Environment.CurrentManagedThreadId); }, clock);
        try
        {
            controller.Tick();
            check(client.Calls == 0, "Inactive App Usage controller makes no Today queries");
            check(AppUsageTodayController.RefreshInterval == TimeSpan.FromSeconds(4), "Today refresh cadence is four seconds through the existing UI timer");
            controller.SetActive(true);
            check(controller.Current.State == AppUsageTodayState.Loading, "Entering App Usage publishes loading before waiting for IPC");
            Wait(() => client.Calls == 1);
            var dispatched = false;
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => dispatched = true));
            Wait(() => dispatched);
            check(controller.IsRefreshing && client.Active == 1 && dispatched, "An unfinished Today IPC query leaves the WPF dispatcher responsive");
            clock.Advance(TimeSpan.FromSeconds(20));
            for (var index = 0; index < 12; index++) controller.Tick();
            check(client.Calls == 1 && client.MaxActive == 1, "Repeated due refresh ticks coalesce while a Today request is active");
            client.Complete(0, Connected(Fixture()));
            Wait(() => !controller.IsRefreshing && controller.Current.State == AppUsageTodayState.Available);
            check(applyThreads.All(thread => thread == uiThread), "Async Today completion applies controls only on the captured WPF dispatcher");
            controller.Tick();
            Wait(() => client.Calls == 2);
            client.Complete(1, Connected(Fixture()));
            Wait(() => !controller.IsRefreshing);
            clock.Advance(TimeSpan.FromMilliseconds(3900));
            controller.Tick();
            check(client.Calls == 2, "A visible Today page avoids polling before the four-second interval");
            clock.Advance(TimeSpan.FromMilliseconds(100));
            controller.Tick();
            Wait(() => client.Calls == 3);
            check(client.MaxActive == 1, "The next scheduled Today refresh starts once the interval elapses");
            controller.SetActive(false);
            check(client.Token(2).IsCancellationRequested, "Navigating away cancels the unnecessary Today request");
            var appliedAtHide = presentations.Count;
            clock.Advance(TimeSpan.FromMinutes(1));
            for (var index = 0; index < 10; index++) controller.Tick();
            check(client.Calls == 3, "A hidden App Usage page stops all refresh queries");
            client.Complete(2, Connected(Result([new("stale", "Stale result", null, null, 9, 1, 10, false, false)])));
            Wait(() => !controller.IsRefreshing);
            check(presentations.Count == appliedAtHide && controller.Current.Applications.All(row => row.ApplicationIdentity != "stale"),
                "A result that arrives after navigation away never renders stale application data");
            controller.SetActive(true);
            Wait(() => client.Calls == 4);
            check(controller.Current.State == AppUsageTodayState.Loading, "Returning to App Usage immediately loads a fresh Today result");
            controller.SetActive(false);
            controller.SetActive(true);
            for (var index = 0; index < 8; index++) controller.Tick();
            check(client.Calls == 4 && client.MaxActive == 1 && client.Token(3).IsCancellationRequested,
                "Rapid return coalesces behind an old query that has not actually completed");
            client.Complete(3, Connected(Result([new("old", "Old generation", null, null, 100, 0, 100, false, false)])));
            Wait(() => client.Calls == 5);
            check(controller.Current.State == AppUsageTodayState.Loading && client.MaxActive == 1,
                "Once the old generation ends, rapid return starts one fresh query without showing old rows");
            client.Complete(4, Connected(Fixture()));
            Wait(() => !controller.IsRefreshing);
            check(controller.Current.Applications.Select(row => row.ApplicationIdentity).SequenceEqual(Fixture().Applications.Select(row => row.ApplicationIdentity)),
                "Only the current navigation generation supplies the returning Today page");
            clock.Advance(TimeSpan.FromSeconds(4));
            controller.Tick();
            Wait(() => client.Calls == 6);
            client.Fail(5, new InvalidOperationException("Sensitive worker exception C:\\private\\secret.db"));
            Wait(() => !controller.IsRefreshing);
            check(controller.Current.State == AppUsageTodayState.Unavailable && !controller.Current.Detail.Contains("Sensitive", StringComparison.Ordinal),
                "An unexpected query failure becomes safe unavailable state instead of escaping the async refresh");
            clock.Advance(TimeSpan.FromSeconds(4));
            controller.Tick();
            Wait(() => client.Calls == 7);
            client.Complete(6, Connected(Fixture()));
            Wait(() => !controller.IsRefreshing);
            check(controller.Current.State == AppUsageTodayState.Available, "A subsequent scheduled query recovers the same page after unavailability");
            clock.Advance(TimeSpan.FromSeconds(4));
            controller.Tick();
            Wait(() => client.Calls == 8);
            var appliedAtDispose = presentations.Count;
            var disposal = controller.DisposeAsync().AsTask();
            check(client.Token(7).IsCancellationRequested, "Closing the controller cancels its in-flight query");
            controller.SetActive(true);
            controller.Tick();
            client.Complete(7, Connected(Fixture()));
            Wait(() => disposal.IsCompleted && client.Disposed);
            disposal.GetAwaiter().GetResult();
            check(client.Calls == 8 && presentations.Count == appliedAtDispose,
                "Disposed Today controller releases its client and ignores late completions and refresh attempts");
        }
        finally
        {
            var disposal = controller.DisposeAsync().AsTask();
            client.CompletePending();
            Wait(() => disposal.IsCompleted && client.Disposed);
            disposal.GetAwaiter().GetResult();
        }
    }

    private static void CheckWindow(string root, Action<bool, string> check)
    {
        var previews = System.IO.Path.Combine(root, "app-usage-today-previews");
        using var monitoring = HistoryOverviewChecks.CreateMonitoring(new UsageStore(System.IO.Path.Combine(root, "app-usage-today")));
        var client = new ControlledClient();
        var window = new MainWindow(monitoring, new OwnedStartup(), appUsageClient: client)
            { ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Width = 760, Height = 600 };
        try
        {
            window.Show();
            HistoryOverviewChecks.StopViewTimer(window);
            Layout(window);
            check(client.Calls == 0, "Normal NetTrace startup does not query App Usage before its page is selected");
            var navigation = Control<TabControl>(window, "MainNavigation");
            var tab = Control<TabItem>(window, "AppUsageTab");
            check(navigation.Items.Count == 6 && tab.Header?.ToString() == "App Usage" && navigation.Items.IndexOf(tab) == 4 &&
                ReferenceEquals(tab.Style, Control<TabItem>(window, "HistoryTab").Style),
                "App Usage navigation exists between History and Settings with the existing tab style");
            Select(window, "AppUsageTab");
            Wait(() => client.Calls == 1);
            check(tab.IsSelected && Control<FrameworkElement>(window, "AppUsageContent").IsVisible &&
                !Control<FrameworkElement>(window, "DashboardContent").IsVisible, "App Usage opens as the exclusive active page");
            check(Text(window, "AppUsageStateText") == "Loading app usage..." &&
                Control<FrameworkElement>(window, "AppUsageSummaryPanel").Visibility == Visibility.Collapsed,
                "The actual Today page shows lightweight loading while the async query is pending");
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, "760x600-loading.png"));
            client.Complete(0, Connected(Fixture()));
            Wait(() => Control<ItemsControl>(window, "AppUsageApplications").Items.Count == 4);
            Layout(window);
            check(Text(window, "AppUsageTrackedAppsText") == "4", "Today tracked-app count binds to the compact summary");
            check(Text(window, "AppUsageDownloadText") == ByteFormatter.Format(1900), "Today download binds to the compact summary");
            check(Text(window, "AppUsageUploadText") == ByteFormatter.Format(1050), "Today upload binds to the compact summary");
            check(Text(window, "AppUsageLogicalTotalText") == ByteFormatter.Format(2950), "Today logical total binds to the compact summary");
            var list = Control<ItemsControl>(window, "AppUsageApplications");
            var rows = list.Items.Cast<AppUsageApplicationRow>().ToArray();
            check(rows.Select(row => row.ApplicationIdentity).SequenceEqual(Fixture().Applications.Select(row => row.ApplicationIdentity)) &&
                Control<FrameworkElement>(window, "AppUsageApplicationsPanel").IsVisible,
                "The native application list renders all service rows in returned order");
            var rowText = Descendants(list).OfType<TextBlock>().ToArray();
            check(rowText.Any(text => text.Text == "Brave Browser") && rowText.Any(text => text.Text == "brave.exe") &&
                rows.All(row => rowText.Any(text => text.Text == row.DownloadText) && rowText.Any(text => text.Text == row.UploadText) &&
                    rowText.Any(text => text.Text == row.TotalText)),
                "Application rows visibly include display name, executable and all three cumulative amounts");
            check(rowText.Any(text => text.Text == "System") && rowText.Any(text => text.Text == "Unknown / Unattributed") &&
                rowText.Any(text => text.Text == "NetMod"), "System, Unknown and NetMod all render normally on the actual Today page");
            var pageText = Descendants(Control<FrameworkElement>(window, "AppUsageContent")).OfType<TextBlock>().Select(text => text.Text).ToArray();
            check(pageText.Contains("APP USAGE") && pageText.Contains("TODAY") && pageText.Contains("TOP APPLICATIONS") &&
                !Descendants(Control<FrameworkElement>(window, "AppUsageContent")).OfType<Button>().Any(),
                "App Usage uses the requested header, static Today label and application section without range switches or action buttons");
            check(!new[] { "AppUsageDownloadText", "AppUsageUploadText", "AppUsageLogicalTotalText" }.Select(name => Control<TextBlock>(window, name).Foreground)
                    .Any(brush => ReferenceEquals(brush, window.FindResource("StatusPositiveBrush")) || ReferenceEquals(brush, window.FindResource("StatusNegativeBrush"))),
                "Usage measurements use normal technical styling without good or bad status colors");
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, "760x600-applications.png"));
            CheckMinimumLayout(window, check, "compact applications");

            Select(window, "DashboardTab");
            var hiddenCalls = client.Calls;
            for (var index = 0; index < 8; index++) window.RefreshDashboard();
            Pump();
            check(client.Calls == hiddenCalls, "Hidden actual App Usage page makes no refresh queries during dashboard updates");
            Select(window, "AppUsageTab");
            Wait(() => client.Calls == 2);
            check(Text(window, "AppUsageStateText") == "Loading app usage...", "Returning to the actual App Usage page starts fresh loading immediately");
            client.Complete(1, Connected(LongFixture()));
            Wait(() => list.Items.Count == 24);
            Layout(window);
            CheckMinimumLayout(window, check, "long application metadata and GB amounts");
            var longText = Descendants(list).OfType<TextBlock>().Where(text => text.Text.StartsWith("Long owned application", StringComparison.Ordinal) ||
                text.Text.StartsWith("long-owned-executable", StringComparison.Ordinal)).ToArray();
            check(longText.Length == 48 && longText.All(text => text.TextTrimming == TextTrimming.CharacterEllipsis &&
                text.ActualWidth > 0 && double.IsFinite(text.ActualWidth) && text.ActualWidth <= list.ActualWidth + 0.5),
                "Long display and executable names trim to measured row widths at 760x600");
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, "760x600-long-metadata-top.png"));
            var scroll = Control<ScrollViewer>(window, "AppUsageScrollViewer");
            scroll.ScrollToEnd();
            Layout(window);
            var lastContainer = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1);
            var lastTotal = Descendants(lastContainer).OfType<TextBlock>().Single(text => text.Text == ((AppUsageApplicationRow)list.Items[list.Items.Count - 1]).TotalText);
            var totalPosition = lastTotal.TranslatePoint(new Point(), scroll);
            check(scroll.ScrollableHeight > 0 && scroll.VerticalOffset > 0 && totalPosition.Y >= -0.5 &&
                totalPosition.Y + lastTotal.ActualHeight <= scroll.ActualHeight + 0.5,
                "Vertical scrolling reaches the final application's readable total at the minimum window size");
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, "760x600-long-metadata-bottom.png"));

            Select(window, "DashboardTab");
            Select(window, "AppUsageTab");
            Wait(() => client.Calls == 3);
            client.Complete(2, Connected(Result([])));
            Wait(() => Text(window, "AppUsageStateText") == "No app network usage recorded today.");
            check(list.Items.Count == 0 && Control<FrameworkElement>(window, "AppUsageApplicationsPanel").Visibility == Visibility.Collapsed &&
                Text(window, "AppUsageTrackedAppsText") == "0", "Actual Today empty state clears old rows and displays a truthful zero summary");
            scroll.ScrollToTop();
            Layout(window);
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, "760x600-empty.png"));
            Select(window, "DashboardTab");
            Select(window, "AppUsageTab");
            Wait(() => client.Calls == 4);
            client.Complete(3, new(IpcState.CollectorUnavailable, null, "C:\\private\\secret.db at Internal.StackTrace()"));
            Wait(() => Text(window, "AppUsageStateText") == "App tracking unavailable.");
            check(Control<FrameworkElement>(window, "AppUsageSummaryPanel").Visibility == Visibility.Collapsed && list.Items.Count == 0 &&
                !Text(window, "AppUsageDetailText").Contains("private", StringComparison.Ordinal),
                "Actual unavailable page hides stale summaries and rows without exposing exceptions");
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(previews, "760x600-unavailable.png"));

            Select(window, "DashboardTab");
            Select(window, "AppUsageTab");
            Wait(() => client.Calls == 5);
            window.Hide();
            Pump();
            check(client.Token(4).IsCancellationRequested, "Hiding the entire window cancels App Usage work");
            client.Complete(4, Connected(Fixture()));
            Wait(() => !Controller(window).IsRefreshing);
            check(list.Items.Count == 0, "A hidden-window completion cannot render old application rows");
            window.Show();
            HistoryOverviewChecks.StopViewTimer(window);
            Wait(() => client.Calls == 6);
            check(Text(window, "AppUsageStateText") == "Loading app usage...", "Restoring a visible Today page immediately refreshes through the same client");
            client.Complete(5, Connected(Fixture()));
            Wait(() => list.Items.Count == 4);
            window.WindowState = WindowState.Minimized;
            Pump();
            var minimizedCalls = client.Calls;
            for (var index = 0; index < 5; index++) window.RefreshDashboard();
            check(client.Calls == minimizedCalls, "A minimized App Usage page stops query refreshes");
            window.WindowState = WindowState.Normal;
            HistoryOverviewChecks.StopViewTimer(window);
            Wait(() => client.Calls == 7);
            check(Text(window, "AppUsageStateText") == "Loading app usage...", "Restoring from minimize requests fresh Today without changing window dimensions");
            Select(window, "DashboardTab");
            check(client.Token(6).IsCancellationRequested, "Actual navigation away cancels the in-flight Today query");
            client.Complete(6, Connected(LongFixture()));
            Wait(() => !Controller(window).IsRefreshing);
            check(list.Items.Count == 0, "Actual navigation away safely discards a late response");
            Select(window, "AppUsageTab");
            Wait(() => client.Calls == 8);
            window.Close();
            check(client.Token(7).IsCancellationRequested, "Closing the WPF view cancels its Today query");
            client.Complete(7, Connected(Fixture()));
            Wait(() => client.Disposed);
            check(client.MaxActive == 1 && typeof(MainWindow).GetFields(PrivateInstance).Count(field => field.FieldType == typeof(DispatcherTimer)) == 1 &&
                monitoring.GetSnapshot().IsRunning == false,
                "Today view lifecycle retains one existing UI timer and never starts physical monitoring or overlapping IPC work");
        }
        finally
        {
            window.Close();
            client.CompletePending();
            Wait(() => client.Disposed);
        }
        Console.WriteLine("Synthetic App Usage Today previews: " + previews);
    }

    private static void CheckMinimumLayout(MainWindow window, Action<bool, string> check, string context)
    {
        Layout(window);
        var scroll = Control<ScrollViewer>(window, "AppUsageScrollViewer");
        var summary = Control<FrameworkElement>(window, "AppUsageSummaryPanel");
        check(window.MinWidth == 760 && window.MinHeight == 600 && window.Width == 760 && window.Height == 600 &&
            scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && scroll.ScrollableWidth < 0.01 &&
            scroll.ExtentWidth <= scroll.ViewportWidth + 0.5 && summary.ActualWidth <= scroll.ViewportWidth + 0.5 &&
            new[] { "AppUsageDownloadText", "AppUsageUploadText", "AppUsageLogicalTotalText" }.All(name =>
                Control<TextBlock>(window, name).ActualWidth > 0 && Control<TextBlock>(window, name).ActualHeight > 0),
            $"Today {context} fits the supported 760x600 window without horizontal overflow and preserves summary amounts");
    }

    internal static AppUsageQueryResult Fixture() => Result(
    [
        new("brave", "Brave Browser", "brave.exe", "C:\\Owned\\Brave\\brave.exe", 1000, 200, 1200, false, false),
        new("netmod", "NetMod", "NetMod.exe", "C:\\Owned\\NetMod.exe", 500, 500, 1000, false, false),
        new("system", "System", null, null, 400, 100, 500, true, false),
        new("unknown", "Unknown / Unattributed", null, null, 0, 250, 250, false, true)
    ]);

    private static AppUsageQueryResult LongFixture() => Result(Enumerable.Range(0, 24).Select(index =>
    {
        var download = 1_535_484_723L - index * 1_000_000;
        return new AppUsageApplication($"long-{index:D2}", "Long owned application " + index + " " + new string('W', 160),
            "long-owned-executable-" + index + "-" + new string('x', 120) + ".exe", null,
            download, 128_000_000, download + 128_000_000, false, false);
    }).ToArray());

    internal static AppUsageQueryResult Result(IReadOnlyList<AppUsageApplication> applications) => new(AppUsageRange.Today, Today, Today,
        applications.Count, applications.Sum(row => row.DownloadBytes), applications.Sum(row => row.UploadBytes),
        applications.Sum(row => row.TotalBytes), applications);
    internal static IpcResult<AppUsageQueryResult> Connected(AppUsageQueryResult result) => new(IpcState.Connected, result);
    private static T Control<T>(MainWindow window, string name) where T : class => window.FindName(name) as T ??
        throw new InvalidOperationException("Missing App Usage control " + name);
    private static string Text(MainWindow window, string name) => Control<TextBlock>(window, name).Text;
    private static AppUsageTodayController Controller(MainWindow window) => (AppUsageTodayController)typeof(MainWindow)
        .GetField("_appUsageToday", PrivateInstance)!.GetValue(window)!;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root) => HistoryOverviewChecks.Descendants(root);
    private static void Select(MainWindow window, string tab)
    {
        Control<TabControl>(window, "MainNavigation").SelectedItem = Control<TabItem>(window, tab);
        Layout(window);
    }
    private static void Layout(MainWindow window) { window.UpdateLayout(); Pump(); window.UpdateLayout(); }
    private static void Pump() => HistoryOverviewChecks.Pump();
    private static void Wait(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Owned App Usage condition was not ready.");
            Pump();
        }
        Pump();
    }

    internal sealed class UnavailableClient : IAppUsageTodayClient
    {
        public Task<IpcResult<AppUsageQueryResult>> GetTodayAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new IpcResult<AppUsageQueryResult>(IpcState.CollectorUnavailable, null));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ControlledClient : IAppUsageTodayClient
    {
        private readonly object _gate = new();
        private readonly List<(TaskCompletionSource<IpcResult<AppUsageQueryResult>> Completion, CancellationToken Token)> _requests = [];
        private int _active;
        private int _maxActive;
        private bool _disposed;
        public int Calls { get { lock (_gate) return _requests.Count; } }
        public int Active { get { lock (_gate) return _active; } }
        public int MaxActive { get { lock (_gate) return _maxActive; } }
        public bool Disposed { get { lock (_gate) return _disposed; } }
        public Task<IpcResult<AppUsageQueryResult>> GetTodayAsync(CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<IpcResult<AppUsageQueryResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _requests.Add((completion, cancellationToken));
                _active++;
                _maxActive = Math.Max(_maxActive, _active);
            }
            // Deliberately retain cancellation-resistant requests to verify actual
            // completion, generation discard and no-overlap behavior independently.
            return Observe(completion.Task);
        }
        private async Task<IpcResult<AppUsageQueryResult>> Observe(Task<IpcResult<AppUsageQueryResult>> task)
        {
            try { return await task.ConfigureAwait(false); }
            finally { lock (_gate) _active--; }
        }
        public CancellationToken Token(int index) { lock (_gate) return _requests[index].Token; }
        public void Complete(int index, IpcResult<AppUsageQueryResult> result)
        { lock (_gate) _requests[index].Completion.TrySetResult(result); }
        public void Fail(int index, Exception exception)
        { lock (_gate) _requests[index].Completion.TrySetException(exception); }
        public void CompletePending()
        {
            lock (_gate)
                foreach (var request in _requests) request.Completion.TrySetResult(new(IpcState.Cancelled, null));
        }
        public ValueTask DisposeAsync() { lock (_gate) _disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class OwnedClock : TimeProvider
    {
        private DateTimeOffset _utc = new(2026, 10, 13, 4, 0, 0, TimeSpan.Zero);
        private long _timestamp;
        public override DateTimeOffset GetUtcNow() => _utc;
        public override long GetTimestamp() => _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan interval) { _utc += interval; _timestamp += interval.Ticks; }
    }

    private sealed class OwnedStartup : IWindowsStartupRegistration
    {
        public bool IsEnabled() => false;
        public void Enable() => throw new InvalidOperationException("App Usage must not alter startup registration.");
        public void Disable() => throw new InvalidOperationException("App Usage must not alter startup registration.");
    }
}
