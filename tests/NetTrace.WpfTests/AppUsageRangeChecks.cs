using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using AppUsageIpc;
using NetTrace;
using NetTrace.Presentation;
using NetTrace.Services;
using NetTrace.Utils;

internal static class AppUsageRangeChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly AppUsageRange[] Ranges = [AppUsageRange.Today, AppUsageRange.SevenDays, AppUsageRange.ThirtyDays];

    public static void Run(string root, Action<bool, string> check)
    {
        CheckPresentation(check);
        CheckController(check);
        CheckWindow(root, check);
    }

    private static void CheckPresentation(Action<bool, string> check)
    {
        foreach (var range in Ranges)
        {
            var result = Result(range, "current-" + range);
            var presentation = AppUsageTodayPresentation.FromResult(Connected(result), range);
            check(presentation.State == AppUsageTodayState.Available && presentation.TrackedAppsText == "1" &&
                presentation.DownloadText == ByteFormatter.Format(12_700) && presentation.UploadText == ByteFormatter.Format(842) &&
                presentation.LogicalTotalText == ByteFormatter.Format(13_542),
                $"{range} presentation accepts the selected IPC range and preserves cumulative summaries");
            check(presentation.Applications.Single().ApplicationIdentity == "current-" + range &&
                presentation.Applications.Single().TotalText == ByteFormatter.Format(13_542),
                $"{range} presentation keeps the selected application list and existing byte formatter");
            var empty = AppUsageTodayPresentation.FromResult(Connected(Empty(range)), range);
            check(empty.State == AppUsageTodayState.Empty && empty.Message == EmptyText(range) && empty.TrackedAppsText == "0" &&
                empty.DownloadText == ByteFormatter.Format(0) && empty.UploadText == ByteFormatter.Format(0) &&
                empty.LogicalTotalText == ByteFormatter.Format(0) && empty.Applications.Count == 0,
                $"{range} empty state uses its exact range-specific text and truthful zero summaries");
            var unavailable = AppUsageTodayPresentation.FromResult(new(IpcState.CollectorUnavailable, null, "private path"), range);
            check(unavailable.State == AppUsageTodayState.Unavailable && unavailable.Message == "App tracking unavailable." &&
                unavailable.Applications.Count == 0 && unavailable.Detail == "",
                $"{range} unavailable state stays calm and clears old data");
            check(AppUsageTodayPresentation.FromResult(Connected(result with { Availability = AppUsageAvailability.Degraded }), range).State ==
                AppUsageTodayState.Unavailable,
                $"{range} degraded storage retains the existing unavailable state");
            foreach (var otherRange in Ranges.Where(other => other != range))
                check(AppUsageTodayPresentation.FromResult(Connected(Result(otherRange, "wrong-range")), range).State == AppUsageTodayState.Unavailable,
                    $"A {otherRange} response cannot populate the selected {range} presentation");
        }
    }

    private static void CheckController(Action<bool, string> check)
    {
        var client = new ControlledRangeClient();
        var clock = new OwnedClock();
        var selection = new AppUsageRangeSelection();
        var presentations = new List<AppUsageTodayPresentation>();
        var controller = new AppUsageTodayController(Dispatcher.CurrentDispatcher, client, presentations.Add, clock, selection);
        try
        {
            check(controller.SelectedRange == AppUsageRange.Today && selection.SelectedRange == AppUsageRange.Today,
                "A new app-session range selection defaults to Today");
            controller.Tick();
            check(client.Calls == 0, "Default Today selection sends no request while App Usage is inactive");
            controller.SelectRange(AppUsageRange.SevenDays);
            controller.Tick();
            check(controller.SelectedRange == AppUsageRange.SevenDays && selection.SelectedRange == AppUsageRange.SevenDays &&
                client.Calls == 0 && presentations.Count == 0,
                "Selecting a hidden range retains selection without querying or rendering");
            controller.SelectRange(AppUsageRange.Today);
            controller.SetActive(true);
            Wait(() => client.Calls == 1);
            check(client.Range(0) == AppUsageRange.Today && controller.Current.State == AppUsageTodayState.Loading,
                "Entering App Usage immediately requests its default Today range");
            Finish(client, controller, 0, Result(AppUsageRange.Today, "initial-today"));

            controller.SelectRange(AppUsageRange.SevenDays);
            Wait(() => client.Calls == 2);
            check(client.Range(1) == AppUsageRange.SevenDays && controller.Current.State == AppUsageTodayState.Loading &&
                controller.Current.Applications.Count == 0,
                "Choosing 7D immediately clears previous rows into loading and requests SevenDays");
            Finish(client, controller, 1, Result(AppUsageRange.SevenDays, "selected-seven"));
            check(controller.Current.Applications.Single().ApplicationIdentity == "selected-seven",
                "The current 7D response supplies the selected range");
            controller.SelectRange(AppUsageRange.ThirtyDays);
            Wait(() => client.Calls == 3);
            check(client.Range(2) == AppUsageRange.ThirtyDays,
                "Choosing 30D immediately requests ThirtyDays without waiting for a timer tick");
            Finish(client, controller, 2, Result(AppUsageRange.ThirtyDays, "selected-thirty"));
            clock.Advance(TimeSpan.FromMilliseconds(3999));
            controller.Tick();
            check(client.Calls == 3, "Range switching retains the existing four-second refresh deadline");
            clock.Advance(TimeSpan.FromMilliseconds(1));
            controller.Tick();
            Wait(() => client.Calls == 4);
            check(client.Range(3) == AppUsageRange.ThirtyDays,
                "The periodic refresh queries the currently selected 30D range");
            Finish(client, controller, 3, Result(AppUsageRange.ThirtyDays, "periodic-thirty"));
            controller.SelectRange(AppUsageRange.ThirtyDays);
            check(client.Calls == 4, "Re-selecting the current range avoids a duplicate request");

            controller.SetActive(false);
            var appliedWhileHidden = presentations.Count;
            clock.Advance(TimeSpan.FromMinutes(1));
            for (var index = 0; index < 20; index++) controller.Tick();
            check(client.Calls == 4 && presentations.Count == appliedWhileHidden,
                "Hidden App Usage sends zero periodic range queries and applies zero presentations");
            controller.SelectRange(AppUsageRange.SevenDays);
            check(client.Calls == 4 && presentations.Count == appliedWhileHidden,
                "A hidden range change also performs zero query or rendering work");
            controller.SetActive(true);
            Wait(() => client.Calls == 5);
            check(controller.SelectedRange == AppUsageRange.SevenDays && client.Range(4) == AppUsageRange.SevenDays,
                "Navigation back immediately refreshes the retained 7D selection");
            Finish(client, controller, 4, Result(AppUsageRange.SevenDays, "returned-seven"));
            clock.Advance(TimeSpan.FromSeconds(4));
            controller.Tick();
            Wait(() => client.Calls == 6);
            check(client.Range(5) == AppUsageRange.SevenDays, "The next periodic refresh follows the retained 7D selection");

            controller.SelectRange(AppUsageRange.ThirtyDays);
            check(client.Token(5).IsCancellationRequested && controller.Current.State == AppUsageTodayState.Loading,
                "Changing range cancels an old unfinished request and immediately publishes loading");
            controller.SelectRange(AppUsageRange.Today);
            controller.SelectRange(AppUsageRange.ThirtyDays);
            for (var index = 0; index < 10; index++) controller.Tick();
            check(client.Calls == 6 && client.MaxActive == 1 && controller.SelectedRange == AppUsageRange.ThirtyDays,
                "Rapid range choices coalesce to the latest selection while a cancellation-resistant request finishes");
            client.Complete(5, Connected(Result(AppUsageRange.SevenDays, "stale-seven")));
            Wait(() => client.Calls == 7);
            check(client.Range(6) == AppUsageRange.ThirtyDays && controller.Current.State == AppUsageTodayState.Loading &&
                presentations.All(state => state.Applications.All(row => row.ApplicationIdentity != "stale-seven")),
                "An older 7D response never overwrites the new 30D selection before its fresh request");
            Finish(client, controller, 6, Result(AppUsageRange.ThirtyDays, "fresh-thirty"));
            check(controller.Current.Applications.Single().ApplicationIdentity == "fresh-thirty",
                "Only the newest selected-range result is presented after rapid switching");

            controller.SelectRange(AppUsageRange.Today);
            Wait(() => client.Calls == 8);
            controller.SelectRange(AppUsageRange.SevenDays);
            controller.SelectRange(AppUsageRange.Today);
            check(client.Token(7).IsCancellationRequested && client.Calls == 8,
                "Today to 7D to Today also cancels and coalesces the original Today request");
            client.Complete(7, Connected(Result(AppUsageRange.Today, "stale-original-today")));
            Wait(() => client.Calls == 9);
            check(client.Range(8) == AppUsageRange.Today && controller.Current.State == AppUsageTodayState.Loading &&
                presentations.All(state => state.Applications.All(row => row.ApplicationIdentity != "stale-original-today")),
                "Generation guards reject an old Today response even when the latest selection is Today again");
            Finish(client, controller, 8, Result(AppUsageRange.Today, "fresh-returned-today"));
            check(controller.Current.Applications.Single().ApplicationIdentity == "fresh-returned-today" && client.MaxActive == 1,
                "The second Today generation supplies fresh data without overlapping requests");

            clock.Advance(TimeSpan.FromSeconds(4));
            controller.Tick();
            Wait(() => client.Calls == 10);
            controller.SetActive(false);
            controller.SelectRange(AppUsageRange.ThirtyDays);
            var appliedAtHide = presentations.Count;
            clock.Advance(TimeSpan.FromHours(1));
            for (var index = 0; index < 20; index++) controller.Tick();
            client.Complete(9, Connected(Result(AppUsageRange.Today, "hidden-stale-today")));
            Wait(() => !controller.IsRefreshing);
            check(client.Calls == 10 && client.Token(9).IsCancellationRequested && presentations.Count == appliedAtHide,
                "Hidden-page cancellation and range changes discard late results with zero further requests");
            controller.SetActive(true);
            Wait(() => client.Calls == 11);
            check(client.Range(10) == AppUsageRange.ThirtyDays,
                "Returning after a hidden range change requests its retained 30D range");
            client.Complete(10, new(IpcState.CollectorUnavailable, null, "private diagnostics"));
            Wait(() => !controller.IsRefreshing);
            check(controller.Current.State == AppUsageTodayState.Unavailable && controller.Current.Message == "App tracking unavailable." &&
                controller.SelectedRange == AppUsageRange.ThirtyDays,
                "Unavailable results retain the selected range for future refreshes");
            clock.Advance(TimeSpan.FromSeconds(4));
            controller.Tick();
            Wait(() => client.Calls == 12);
            Finish(client, controller, 11, Empty(AppUsageRange.ThirtyDays));
            check(controller.Current.State == AppUsageTodayState.Empty && controller.Current.Message == EmptyText(AppUsageRange.ThirtyDays),
                "The selected 30D periodic refresh recovers into its exact empty state");
            controller.SelectRange(AppUsageRange.SevenDays);
            Wait(() => client.Calls == 13);
            Finish(client, controller, 12, Empty(AppUsageRange.SevenDays));
            check(controller.Current.Message == EmptyText(AppUsageRange.SevenDays), "Selecting empty 7D replaces the prior 30D empty message");
            controller.SelectRange(AppUsageRange.Today);
            Wait(() => client.Calls == 14);
            Finish(client, controller, 13, Empty(AppUsageRange.Today));
            check(controller.Current.Message == EmptyText(AppUsageRange.Today), "Selecting empty Today restores the original Today empty message");
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
        var selection = new AppUsageRangeSelection();
        using var monitoring = HistoryOverviewChecks.CreateMonitoring(new UsageStore(System.IO.Path.Combine(root, "app-usage-range")));
        var client = new ControlledRangeClient();
        var window = CreateWindow(monitoring, client, selection);
        try
        {
            window.Show();
            HistoryOverviewChecks.StopViewTimer(window);
            Layout(window);
            check(client.Calls == 0 && Button(window, AppUsageRange.Today).IsChecked == true &&
                Button(window, AppUsageRange.SevenDays).IsChecked == false && Button(window, AppUsageRange.ThirtyDays).IsChecked == false,
                "The actual App Usage controls default to Today without querying on Dashboard startup");
            check(Button(window, AppUsageRange.Today).Content?.ToString() == "Today" &&
                Button(window, AppUsageRange.SevenDays).Content?.ToString() == "7D" &&
                Button(window, AppUsageRange.ThirtyDays).Content?.ToString() == "30D",
                "The real page exposes the requested Today, 7D and 30D controls");
            SelectTab(window, "AppUsageTab");
            Wait(() => client.Calls == 1);
            check(client.Range(0) == AppUsageRange.Today, "Opening the real page requests its default Today range through the injected IPC client");
            Finish(client, Controller(window), 0, Result(AppUsageRange.Today, "window-today"));
            Choose(window, AppUsageRange.SevenDays);
            Wait(() => client.Calls == 2);
            check(client.Range(1) == AppUsageRange.SevenDays && Button(window, AppUsageRange.Today).IsChecked == false &&
                Button(window, AppUsageRange.SevenDays).IsChecked == true && Text(window, "AppUsageStateText") == "Loading app usage...",
                "The actual 7D control selects one range and immediately starts its loading request");
            Finish(client, Controller(window), 1, Result(AppUsageRange.SevenDays, "window-seven"));
            check(Rows(window).Single().ApplicationIdentity == "window-seven" && Text(window, "AppUsageLogicalTotalText") == ByteFormatter.Format(13_542),
                "The actual 7D application list and summary display the returned selected range");
            Choose(window, AppUsageRange.ThirtyDays);
            Wait(() => client.Calls == 3);
            check(client.Range(2) == AppUsageRange.ThirtyDays && Button(window, AppUsageRange.SevenDays).IsChecked == false &&
                Button(window, AppUsageRange.ThirtyDays).IsChecked == true,
                "The actual 30D control immediately changes the selected IPC request");
            Finish(client, Controller(window), 2, Result(AppUsageRange.ThirtyDays, "window-thirty"));
            SelectTab(window, "DashboardTab");
            var hiddenCalls = client.Calls;
            for (var index = 0; index < 20; index++) Controller(window).Tick();
            check(client.Calls == hiddenCalls && selection.SelectedRange == AppUsageRange.ThirtyDays,
                "Navigation away retains 30D and sends zero hidden-page range requests");
            SelectTab(window, "AppUsageTab");
            Wait(() => client.Calls == 4);
            check(client.Range(3) == AppUsageRange.ThirtyDays && Button(window, AppUsageRange.ThirtyDays).IsChecked == true,
                "Navigation back keeps the selected 30D control and immediately refreshes 30D");
            Finish(client, Controller(window), 3, Result(AppUsageRange.ThirtyDays, "window-returned-thirty"));

            Choose(window, AppUsageRange.SevenDays);
            Wait(() => client.Calls == 5);
            Choose(window, AppUsageRange.ThirtyDays);
            Choose(window, AppUsageRange.Today);
            check(client.Token(4).IsCancellationRequested && client.Calls == 5 && Rows(window).Length == 0,
                "Rapid actual range-control switching cancels the old request and clears old rows into loading");
            client.Complete(4, Connected(Result(AppUsageRange.SevenDays, "window-stale-seven")));
            Wait(() => client.Calls == 6);
            check(client.Range(5) == AppUsageRange.Today && Rows(window).Length == 0 &&
                Text(window, "AppUsageStateText") == "Loading app usage...",
                "A stale actual 7D completion cannot populate the newly selected Today page");
            Finish(client, Controller(window), 5, Result(AppUsageRange.Today, "window-fresh-today"));
            check(Rows(window).Single().ApplicationIdentity == "window-fresh-today" && client.MaxActive == 1,
                "Only the latest actual range selection supplies rows without overlapping IPC requests");
            foreach (var range in new[] { AppUsageRange.SevenDays, AppUsageRange.ThirtyDays, AppUsageRange.Today })
            {
                var request = client.Calls;
                Choose(window, range);
                Wait(() => client.Calls == request + 1);
                Finish(client, Controller(window), request, Empty(range));
                check(Text(window, "AppUsageStateText") == EmptyText(range) && Rows(window).Length == 0 &&
                    Text(window, "AppUsageTrackedAppsText") == "0" &&
                    Control<FrameworkElement>(window, "AppUsageSummaryPanel").Visibility == Visibility.Visible,
                    $"The real {range} empty state keeps zero summaries and its exact requested message");
            }
            Choose(window, AppUsageRange.ThirtyDays);
            Wait(() => client.Calls == 10);
            client.Complete(9, new(IpcState.CollectorUnavailable, null, "private diagnostics"));
            Wait(() => !Controller(window).IsRefreshing);
            check(Text(window, "AppUsageStateText") == "App tracking unavailable." &&
                Control<FrameworkElement>(window, "AppUsageSummaryPanel").Visibility == Visibility.Collapsed &&
                Button(window, AppUsageRange.ThirtyDays).IsChecked == true && Rows(window).Length == 0,
                "The actual unavailable state keeps 30D selected and hides old summary and list data");
            SelectTab(window, "DashboardTab");
            SelectTab(window, "AppUsageTab");
            Wait(() => client.Calls == 11);
            Finish(client, Controller(window), 10, Result(AppUsageRange.ThirtyDays, "window-compact-thirty"));
            Layout(window);
            var scroll = Control<ScrollViewer>(window, "AppUsageScrollViewer");
            var bounds = Ranges.Select(range => Button(window, range)).Select(button =>
                (Button: button, Position: button.TranslatePoint(new Point(), scroll))).ToArray();
            check(window.Width == 760 && window.Height == 600 && scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled &&
                scroll.ScrollableWidth < 0.01 && bounds.All(item => item.Button.ActualWidth > 0 && item.Button.ActualHeight > 0 &&
                    item.Position.X >= -0.5 && item.Position.X + item.Button.ActualWidth <= scroll.ViewportWidth + 0.5),
                "All three range controls remain readable and inside the page at 760x600 without horizontal overflow");
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(root, "app-usage-range-previews", "760x600-30d.png"));
            window.Hide();
            Layout(window);
            var callsAtHide = client.Calls;
            Choose(window, AppUsageRange.SevenDays);
            for (var index = 0; index < 20; index++) Controller(window).Tick();
            check(client.Calls == callsAtHide && selection.SelectedRange == AppUsageRange.SevenDays,
                "A hidden actual window retains a new range selection with zero requests");
            window.Show();
            HistoryOverviewChecks.StopViewTimer(window);
            Wait(() => client.Calls == 12);
            check(client.Range(11) == AppUsageRange.SevenDays && Button(window, AppUsageRange.SevenDays).IsChecked == true,
                "Restoring the actual window immediately refreshes its retained 7D selection");
            Finish(client, Controller(window), 11, Result(AppUsageRange.SevenDays, "window-restored-seven"));
            window.Close();
            Wait(() => client.Disposed);
            check(selection.SelectedRange == AppUsageRange.SevenDays, "Closing a view preserves the app-session range selection");
        }
        finally
        {
            window.Close();
            client.CompletePending();
            Wait(() => client.Disposed);
        }

        var reopenedClient = new ControlledRangeClient();
        var reopened = CreateWindow(monitoring, reopenedClient, selection);
        try
        {
            reopened.Show();
            HistoryOverviewChecks.StopViewTimer(reopened);
            Layout(reopened);
            check(Button(reopened, AppUsageRange.SevenDays).IsChecked == true && reopenedClient.Calls == 0,
                "Recreating a window in the same app session restores 7D without querying Dashboard");
            SelectTab(reopened, "AppUsageTab");
            Wait(() => reopenedClient.Calls == 1);
            check(reopenedClient.Range(0) == AppUsageRange.SevenDays,
                "The recreated app-session view immediately requests its retained 7D range when opened");
            Finish(reopenedClient, Controller(reopened), 0, Result(AppUsageRange.SevenDays, "recreated-seven"));
            check(Rows(reopened).Single().ApplicationIdentity == "recreated-seven" && !monitoring.GetSnapshot().IsRunning,
                "Recreated selected-range data displays without starting physical monitoring");
        }
        finally
        {
            reopened.Close();
            reopenedClient.CompletePending();
            Wait(() => reopenedClient.Disposed);
        }
    }

    private static MainWindow CreateWindow(MonitoringService monitoring, ControlledRangeClient client, AppUsageRangeSelection selection) =>
        new(monitoring, new OwnedStartup(), appUsageClient: client, appUsageRangeSelection: selection,
            appTrackingSetupService: new AppUsageSetupChecks.RunningSetupService())
            { ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Width = 760, Height = 600 };
    private static AppUsageQueryResult Result(AppUsageRange range, string identity) => AppUsageTodayChecks.Result(
        [new(identity, "Owned " + identity, identity + ".exe", null, 12_700, 842, 13_542, false, false)]) with { Range = range };
    private static AppUsageQueryResult Empty(AppUsageRange range) => AppUsageTodayChecks.Result([]) with { Range = range };
    private static IpcResult<AppUsageQueryResult> Connected(AppUsageQueryResult result) => AppUsageTodayChecks.Connected(result);
    private static string EmptyText(AppUsageRange range) => range switch
    {
        AppUsageRange.Today => "No app network usage recorded today.",
        AppUsageRange.SevenDays => "No app network usage recorded in the last 7 days.",
        AppUsageRange.ThirtyDays => "No app network usage recorded in the last 30 days.",
        _ => throw new ArgumentOutOfRangeException(nameof(range))
    };
    private static void Finish(ControlledRangeClient client, AppUsageTodayController controller, int request, AppUsageQueryResult result)
    {
        client.Complete(request, Connected(result));
        Wait(() => !controller.IsRefreshing);
    }
    private static T Control<T>(MainWindow window, string name) where T : class => window.FindName(name) as T ??
        throw new InvalidOperationException("Missing App Usage range control " + name);
    private static ToggleButton Button(MainWindow window, AppUsageRange range) => Control<ToggleButton>(window, range switch
    {
        AppUsageRange.Today => "AppUsageTodayButton",
        AppUsageRange.SevenDays => "AppUsage7DButton",
        AppUsageRange.ThirtyDays => "AppUsage30DButton",
        _ => throw new ArgumentOutOfRangeException(nameof(range))
    });
    private static string Text(MainWindow window, string name) => Control<TextBlock>(window, name).Text;
    private static AppUsageApplicationRow[] Rows(MainWindow window) => Control<ItemsControl>(window, "AppUsageApplications").Items.Cast<AppUsageApplicationRow>().ToArray();
    private static AppUsageTodayController Controller(MainWindow window) => (AppUsageTodayController)typeof(MainWindow)
        .GetField("_appUsageToday", PrivateInstance)!.GetValue(window)!;
    private static void Choose(MainWindow window, AppUsageRange range)
    {
        Button(window, range).SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        Layout(window);
    }
    private static void SelectTab(MainWindow window, string name)
    {
        Control<TabControl>(window, "MainNavigation").SelectedItem = Control<TabItem>(window, name);
        Layout(window);
    }
    private static void Layout(MainWindow window) { window.UpdateLayout(); HistoryOverviewChecks.Pump(); window.UpdateLayout(); }
    private static void Wait(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Owned App Usage range condition was not ready.");
            HistoryOverviewChecks.Pump();
        }
        HistoryOverviewChecks.Pump();
    }

    private sealed class ControlledRangeClient : IAppUsageTodayClient
    {
        private readonly object _gate = new();
        private readonly List<(AppUsageRange Range, TaskCompletionSource<IpcResult<AppUsageQueryResult>> Completion, CancellationToken Token)> _requests = [];
        private int _active;
        private int _maxActive;
        private bool _disposed;
        public int Calls { get { lock (_gate) return _requests.Count; } }
        public int MaxActive { get { lock (_gate) return _maxActive; } }
        public bool Disposed { get { lock (_gate) return _disposed; } }
        public Task<IpcResult<AppUsageQueryResult>> GetAppUsageRangeAsync(AppUsageRange range, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<IpcResult<AppUsageQueryResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _requests.Add((range, completion, cancellationToken));
                _active++;
                _maxActive = Math.Max(_maxActive, _active);
            }
            // Keep cancelled operations pending until the fixture completes them:
            // range generation must reject stale data independently of cancellation.
            return Observe(completion.Task);
        }
        private async Task<IpcResult<AppUsageQueryResult>> Observe(Task<IpcResult<AppUsageQueryResult>> task)
        {
            try { return await task.ConfigureAwait(false); }
            finally { lock (_gate) _active--; }
        }
        public AppUsageRange Range(int request) { lock (_gate) return _requests[request].Range; }
        public CancellationToken Token(int request) { lock (_gate) return _requests[request].Token; }
        public void Complete(int request, IpcResult<AppUsageQueryResult> result)
        { lock (_gate) _requests[request].Completion.TrySetResult(result); }
        public void CompletePending()
        { lock (_gate) foreach (var request in _requests) request.Completion.TrySetResult(new(IpcState.Cancelled, null)); }
        public ValueTask DisposeAsync() { lock (_gate) _disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class OwnedClock : TimeProvider
    {
        private DateTimeOffset _utc = new(2026, 10, 13, 4, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _utc;
        public void Advance(TimeSpan interval) => _utc += interval;
    }
    private sealed class OwnedStartup : IWindowsStartupRegistration
    {
        public bool IsEnabled() => false;
        public void Enable() => throw new InvalidOperationException("App Usage range switching must not change startup registration.");
        public void Disable() => throw new InvalidOperationException("App Usage range switching must not change startup registration.");
    }
}
