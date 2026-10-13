using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using AppUsageIpc;
using NetTrace;
using NetTrace.Presentation;
using NetTrace.Services;

internal static class AppUsageHistoryClearChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string OwnedClientSid = "S-1-5-21-101-202-303-1001";

    public static void Run(string root, Action<bool, string> check)
    {
        CheckPlatform(root, check);
        CheckController(check);
        CheckFailures(check);
        CheckRangeRefresh(check);
        CheckStaleAndClosedCompletion(check);
        CheckWindow(root, check);
    }

    private static void CheckPlatform(string root, Action<bool, string> check)
    {
        var applicationDirectory = System.IO.Path.Combine(root, "owned history application with spaces");
        var invocation = AppUsageHistoryClearLaunchSpec.Create(applicationDirectory);
        var start = invocation.CreateStartInfo();
        check(start.FileName == System.IO.Path.Combine(applicationDirectory, "AppTracking", "Installer", "NetTrace.AppUsageInstaller.exe") &&
            start.WorkingDirectory == System.IO.Path.GetDirectoryName(start.FileName),
            "History clear uses the existing bundled installer from its fixed application directory");
        check(start.UseShellExecute && start.Verb == "runas" && start.WindowStyle == ProcessWindowStyle.Hidden &&
            start.FileName != Environment.ProcessPath,
            "Only an explicitly launched history tool requests elevation; NetTrace is never relaunched elevated");
        check(start.ArgumentList.SequenceEqual(new[] { "clear-history" }),
            "History clear supplies only the fixed clear-history command without caller paths, database flags or uninstall arguments");
        var inspected = new List<string>();
        check(invocation.HasRequiredFiles(path => { inspected.Add(path); return true; }) && inspected.Count == 4 &&
            inspected.All(path => System.IO.Path.GetDirectoryName(path) == start.WorkingDirectory),
            "Clear validates the four existing installer entry files without depending on collector files or opening storage");
        check(!invocation.HasRequiredFiles(path => !path.EndsWith(".deps.json", StringComparison.Ordinal)),
            "Incomplete clear tooling is rejected before requesting elevation");
        var relativeRejected = false;
        try { _ = AppUsageHistoryClearLaunchSpec.Create("relative application"); }
        catch (ArgumentException) { relativeRejected = true; }
        check(relativeRejected, "A relative history tool directory is rejected before any process launch");

        var reader = new OwnedStateReader();
        var storage = new OwnedStorageReader();
        var clearLauncher = new ControlledClearLauncher();
        var installer = new ControlledInstaller();
        var uninstaller = new ControlledUninstaller();
        var service = new AppTrackingSetupService(installer, reader, applicationDirectory, OwnedClientSid, _ => true,
            uninstallerLauncher: uninstaller, historyLauncher: clearLauncher, storageReader: storage);
        foreach (var state in Enum.GetValues<AppUsageStorageState>())
        {
            storage.State = state;
            check(WaitTask(service.GetStorageStateAsync(CancellationToken.None)) == state && clearLauncher.Calls == 0 &&
                installer.Calls == 0 && uninstaller.Calls == 0,
                $"Readonly {state} storage inspection launches no privileged operation or automatic clear");
        }
        storage.Throw = true;
        check(WaitTask(service.GetStorageStateAsync(CancellationToken.None)) == AppUsageStorageState.Unavailable,
            "A failed readonly storage probe produces Unavailable rather than guessing the database is missing");
        storage.Throw = false;
        var historyEvents = 0;
        var genericEvents = 0;
        AppTrackingServiceState? stateAtCompletion = null;
        service.StateChanged += (_, _) => genericEvents++;
        service.HistoryCleared += (_, _) => throw new InvalidOperationException("owned failing observer");
        service.HistoryCleared += (_, _) =>
        {
            historyEvents++;
            stateAtCompletion = service.GetStateAsync(CancellationToken.None).GetAwaiter().GetResult();
        };
        using var before = WindowsIdentity.GetCurrent();
        var elevatedBefore = new WindowsPrincipal(before).IsInRole(WindowsBuiltInRole.Administrator);
        var first = service.ClearHistoryAsync(CancellationToken.None);
        Wait(() => clearLauncher.Calls == 1);
        var second = service.ClearHistoryAsync(CancellationToken.None);
        check(clearLauncher.Calls == 1 && !first.IsCompleted && !second.IsCompleted,
            "Concurrent clear waiters share one explicit launch and one UAC prompt");
        check(WaitTask(service.GetStateAsync(CancellationToken.None)) == AppTrackingServiceState.Running,
            "An in-progress history clear preserves the actual Running service state");
        check(WaitTask(service.InstallAsync(CancellationToken.None)) == AppTrackingInstallOutcome.Failed && installer.Calls == 0 &&
            WaitTask(service.UninstallAsync(CancellationToken.None)) == AppTrackingUninstallOutcome.Failed && uninstaller.Calls == 0,
            "Clear excludes competing installation or uninstallation without launching another privileged child");
        var responsive = false;
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => responsive = true));
        Wait(() => responsive);
        check(!first.IsCompleted && responsive, "A pending elevated clear leaves the desktop dispatcher responsive");
        clearLauncher.Complete(0, 0);
        check(WaitTask(first) == AppUsageHistoryClearOutcome.Succeeded && WaitTask(second) == AppUsageHistoryClearOutcome.Succeeded,
            "Verified exit zero completes all clear waiters successfully");
        check(historyEvents == 1 && genericEvents == 0 && stateAtCompletion == AppTrackingServiceState.Running,
            "Success clears the operation gate and emits one history notification without duplicate service-change refreshes");
        using var after = WindowsIdentity.GetCurrent();
        check(before.User?.Value == after.User?.Value &&
            new WindowsPrincipal(after).IsInRole(WindowsBuiltInRole.Administrator) == elevatedBefore,
            "History clear retains the desktop identity and its original unelevated token");

        using var cancellation = new CancellationTokenSource();
        var cancelledWaiter = service.ClearHistoryAsync(cancellation.Token);
        Wait(() => clearLauncher.Calls == 2);
        cancellation.Cancel();
        Wait(() => cancelledWaiter.IsCompleted);
        check(cancelledWaiter.IsCanceled && !clearLauncher.IsCompleted(1),
            "Cancelling a view wait does not interrupt an already launched elevated history transaction");
        var retainedWaiter = service.ClearHistoryAsync(CancellationToken.None);
        check(clearLauncher.Calls == 2, "A recreated view joins the existing clear without another elevation prompt");
        clearLauncher.Complete(1, 0);
        check(WaitTask(retainedWaiter) == AppUsageHistoryClearOutcome.Succeeded && historyEvents == 2,
            "The retained waiter receives the completed clear result with one success notification");
        foreach (var failure in new[] { "cancelled", "nonzero", "missing storage", "collector update", "exception" })
        {
            var index = clearLauncher.Calls;
            var attempt = service.ClearHistoryAsync(CancellationToken.None);
            Wait(() => clearLauncher.Calls == index + 1);
            if (failure == "cancelled") clearLauncher.Fail(index, new Win32Exception(1223));
            else if (failure == "exception") clearLauncher.Fail(index, new InvalidOperationException("private diagnostic path"));
            else clearLauncher.Complete(index, failure switch { "missing storage" => 3, "collector update" => 4, _ => 9 });
            var expected = failure switch
            {
                "cancelled" => AppUsageHistoryClearOutcome.Cancelled,
                "missing storage" => AppUsageHistoryClearOutcome.StorageMissing,
                "collector update" => AppUsageHistoryClearOutcome.CollectorUpdateRequired,
                _ => AppUsageHistoryClearOutcome.Failed
            };
            check(WaitTask(attempt) == expected && historyEvents == 2,
                $"A {failure} clear returns a safe typed result without falsely reporting that history was cleared");
        }
        var missingLauncher = new ControlledClearLauncher();
        var missingKit = new AppTrackingSetupService(installer, reader, applicationDirectory, OwnedClientSid, _ => false,
            historyLauncher: missingLauncher, storageReader: storage);
        check(WaitTask(missingKit.ClearHistoryAsync(CancellationToken.None)) == AppUsageHistoryClearOutcome.FilesMissing && missingLauncher.Calls == 0,
            "Missing clear tooling returns a retryable result before UAC");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var rejected = false;
        try { _ = service.ClearHistoryAsync(cancelled.Token); }
        catch (OperationCanceledException) { rejected = true; }
        check(rejected && clearLauncher.Calls == 7, "An already cancelled clear request never starts a privileged child");

        var install = service.InstallAsync(CancellationToken.None);
        Wait(() => installer.Calls == 1);
        check(WaitTask(service.ClearHistoryAsync(CancellationToken.None)) == AppUsageHistoryClearOutcome.Failed && clearLauncher.Calls == 7,
            "Clear cannot compete with an existing installation transaction");
        installer.Complete(0);
        WaitTask(install);
        var uninstall = service.UninstallAsync(CancellationToken.None);
        Wait(() => uninstaller.Calls == 1);
        check(WaitTask(service.ClearHistoryAsync(CancellationToken.None)) == AppUsageHistoryClearOutcome.Failed && clearLauncher.Calls == 7,
            "Clear cannot compete with an existing uninstallation transaction");
        uninstaller.Complete(0);
        WaitTask(uninstall);
    }

    private static void CheckController(Action<bool, string> check)
    {
        var service = new ControlledHistoryService();
        var clock = new OwnedClock();
        var confirmations = 0;
        var allow = false;
        var cleared = 0;
        var presentations = new List<AppTrackingSettingsPresentation>();
        var threads = new List<int>();
        var uiThread = Environment.CurrentManagedThreadId;
        var controller = new AppTrackingSettingsController(Dispatcher.CurrentDispatcher, service,
            page => { presentations.Add(page); threads.Add(Environment.CurrentManagedThreadId); },
            () => throw new InvalidOperationException("Clear must not confirm uninstall"), timeProvider: clock,
            confirmClearHistory: () => { confirmations++; return allow; }, onHistoryCleared: () => cleared++);
        try
        {
            controller.Tick();
            WaitTask(controller.ClearHistoryAsync());
            check(service.StatusCalls == 0 && service.StorageCalls == 0 && service.ClearCalls == 0 && confirmations == 0,
                "Hidden Settings does not inspect storage, confirm, or automatically clear history");
            controller.SetActive(true);
            Wait(() => !controller.IsRefreshing && controller.Current.StatusText == "Running");
            check(controller.CanClearHistory && controller.Current.CanClearHistory && controller.Current.StorageAvailable && service.ClearCalls == 0,
                "Existing App Usage storage exposes an explicit clear action without automatic elevation");
            foreach (var state in new[] { AppTrackingServiceState.Stopped, AppTrackingServiceState.Missing })
            {
                service.State = state;
                service.Notify();
                Wait(() => !controller.IsRefreshing && controller.Current.StatusText == (state == AppTrackingServiceState.Missing ? "Not installed" : "Stopped"));
                check(controller.CanClearHistory && controller.Current.StorageAvailable,
                    $"Retained App Usage storage can be explicitly cleared while the service is {state}");
            }
            service.State = AppTrackingServiceState.Running;
            foreach (var storage in new[] { AppUsageStorageState.Missing, AppUsageStorageState.Unavailable, AppUsageStorageState.Protected })
            {
                service.Storage = storage;
                service.Notify();
                Wait(() => !controller.IsRefreshing && controller.Current.StatusText == "Running" &&
                    controller.Current.StorageAvailable == (storage == AppUsageStorageState.Protected));
                var permitted = storage == AppUsageStorageState.Protected;
                check(controller.CanClearHistory == permitted && controller.Current.CanClearHistory == permitted && service.ClearCalls == 0,
                    $"Storage {storage} is handled without confusing protected access with a missing database");
            }
            WaitTask(controller.ClearHistoryAsync());
            check(confirmations == 1 && service.ClearCalls == 0 && controller.CanClearHistory && controller.Current.Error == "",
                "Cancelling the explicit destructive confirmation keeps history and tracking unchanged without UAC");
            allow = true;
            var clear = controller.ClearHistoryAsync();
            Wait(() => service.ClearCalls == 1);
            check(controller.IsClearingHistory && !controller.CanClearHistory && !controller.CanUninstall &&
                controller.Current.StatusText == "Running" && !controller.Current.CanClearHistory && !clear.IsCompleted,
                "Confirmed clear disables competing actions without claiming the running service stopped");
            var duplicate = controller.ClearHistoryAsync();
            var uninstall = controller.UninstallAsync();
            var statusCalls = service.StatusCalls;
            clock.Advance(TimeSpan.FromMinutes(1));
            for (var index = 0; index < 20; index++) controller.Tick();
            check(confirmations == 2 && service.ClearCalls == 1 && service.UninstallCalls == 0 && service.StatusCalls == statusCalls,
                "Duplicate clear/uninstall actions and refresh ticks cannot launch competing work over an active clear");
            service.Complete(0, AppUsageHistoryClearOutcome.Succeeded);
            Wait(() => clear.IsCompleted && !controller.IsRefreshing && !controller.IsClearingHistory);
            WaitTask(clear); WaitTask(duplicate); WaitTask(uninstall);
            check(controller.Current.StatusText == "Running" && controller.CanClearHistory && controller.Current.Error == "" &&
                cleared == 1 && service.InstallCalls == 0 && service.UninstallCalls == 0 && !service.HasData,
                "Successful clear preserves tracking, retains its schema-backed storage, and notifies the view exactly once");
            check(threads.All(thread => thread == uiThread), "Storage probes and elevated clears publish Settings state only on the UI dispatcher");
            controller.SetActive(false);
            var applies = presentations.Count;
            statusCalls = service.StatusCalls;
            var storageCalls = service.StorageCalls;
            service.Notify();
            clock.Advance(TimeSpan.FromMinutes(1));
            for (var index = 0; index < 20; index++) controller.Tick();
            HistoryOverviewChecks.Pump();
            check(service.StatusCalls == statusCalls && service.StorageCalls == storageCalls && presentations.Count == applies && service.ClearCalls == 1,
                "Hidden Settings remains silent after clearing and never retries automatically");
        }
        finally { Dispose(controller); }

        var readOnly = new AppTrackingSettingsController(Dispatcher.CurrentDispatcher,
            new AppUsageSetupChecks.RunningSetupService(), _ => { }, () => true, confirmClearHistory: () => true);
        try
        {
            readOnly.SetActive(true);
            Wait(() => !readOnly.IsRefreshing && readOnly.Current.StatusText == "Running");
            WaitTask(readOnly.ClearHistoryAsync());
            check(!readOnly.CanClearHistory && !readOnly.Current.CanClearHistory && !readOnly.Current.StorageAvailable,
                "A read-only injected service provider does not expose unsupported history deletion");
        }
        finally { Dispose(readOnly); }
    }

    private static void CheckFailures(Action<bool, string> check)
    {
        foreach (var outcome in new[] { AppUsageHistoryClearOutcome.Cancelled, AppUsageHistoryClearOutcome.Failed,
            AppUsageHistoryClearOutcome.FilesMissing, AppUsageHistoryClearOutcome.StorageMissing, AppUsageHistoryClearOutcome.CollectorUpdateRequired })
        {
            var service = new ControlledHistoryService();
            var clock = new OwnedClock();
            var cleared = 0;
            var controller = new AppTrackingSettingsController(Dispatcher.CurrentDispatcher, service, _ => { }, () => true,
                timeProvider: clock, confirmClearHistory: () => true, onHistoryCleared: () => cleared++);
            try
            {
                controller.SetActive(true);
                Wait(() => !controller.IsRefreshing && controller.CanClearHistory);
                var attempt = controller.ClearHistoryAsync();
                Wait(() => service.ClearCalls == 1);
                if (outcome == AppUsageHistoryClearOutcome.StorageMissing) service.Storage = AppUsageStorageState.Missing;
                service.Complete(0, outcome);
                Wait(() => attempt.IsCompleted && !controller.IsRefreshing && !controller.IsClearingHistory);
                WaitTask(attempt);
                check(controller.Current.StatusText == "Running" && !string.IsNullOrWhiteSpace(controller.Current.Error) &&
                    controller.Current.Error.Length <= 160 && cleared == 0 && service.HasData && service.UninstallCalls == 0,
                    $"Clear {outcome} reports short feedback without crashing, stopping tracking or falsely clearing the view");
                var error = controller.Current.Error;
                clock.Advance(TimeSpan.FromSeconds(4));
                controller.Tick();
                Wait(() => !controller.IsRefreshing);
                check(controller.Current.Error == error && service.ClearCalls == 1 &&
                    controller.CanClearHistory == (outcome != AppUsageHistoryClearOutcome.StorageMissing),
                    $"Clear {outcome} preserves retry feedback across status refresh without another UAC prompt");
                service.Storage = AppUsageStorageState.Exists;
                service.Notify();
                Wait(() => !controller.IsRefreshing && controller.CanClearHistory);
                var retry = controller.ClearHistoryAsync();
                Wait(() => service.ClearCalls == 2);
                service.Complete(1, AppUsageHistoryClearOutcome.Succeeded);
                Wait(() => retry.IsCompleted && !controller.IsRefreshing && !controller.IsClearingHistory);
                WaitTask(retry);
                check(controller.Current.Error == "" && controller.CanClearHistory && cleared == 1 && service.State == AppTrackingServiceState.Running,
                    $"A confirmed retry after {outcome} succeeds and keeps tracking running");
            }
            finally { Dispose(controller); }
        }
        var throwing = new ControlledHistoryService();
        var confirmation = new AppTrackingSettingsController(Dispatcher.CurrentDispatcher, throwing, _ => { }, () => true,
            confirmClearHistory: () => throw new InvalidOperationException("private confirmation detail"));
        try
        {
            confirmation.SetActive(true);
            Wait(() => !confirmation.IsRefreshing && confirmation.CanClearHistory);
            WaitTask(confirmation.ClearHistoryAsync());
            check(throwing.ClearCalls == 0 && confirmation.CanClearHistory &&
                !string.IsNullOrWhiteSpace(confirmation.Current.Error) && !confirmation.Current.Error.Contains("private", StringComparison.Ordinal),
                "An unavailable confirmation dialog produces safe feedback without invoking the privileged tool");
        }
        finally { Dispose(confirmation); }
    }

    private static void CheckRangeRefresh(Action<bool, string> check)
    {
        foreach (var range in Enum.GetValues<AppUsageRange>())
        {
            var service = new ControlledHistoryService();
            var client = new OwnedUsageClient(service);
            var clock = new OwnedClock();
            var usage = new AppUsageTodayController(Dispatcher.CurrentDispatcher, client, _ => { }, clock,
                setupService: service);
            try
            {
                usage.SelectRange(range);
                usage.SetActive(true);
                Wait(() => !usage.IsRefreshing && usage.Current.State == AppUsageTodayState.Available);
                var oldCalls = client.Calls;
                service.HasData = false;
                usage.RefreshAfterHistoryClear();
                check(usage.SelectedRange == range && usage.Current.Applications.Count == 0,
                    $"Clearing immediately removes cached {range} rows while preserving that selected range");
                Wait(() => !usage.IsRefreshing && usage.Current.State == AppUsageTodayState.Empty);
                check(client.Calls == oldCalls + 1 && client.LastRange == range && usage.Current.Applications.Count == 0 &&
                    usage.Current.DownloadText == "0 B" && usage.Current.UploadText == "0 B" &&
                    service.State == AppTrackingServiceState.Running && service.InstallCalls == 0 && service.UninstallCalls == 0,
                    $"The active {range} page immediately refreshes empty results without reinstalling or stopping tracking");
                service.HasData = true;
                clock.Advance(TimeSpan.FromSeconds(4));
                usage.Tick();
                Wait(() => !usage.IsRefreshing && usage.Current.State == AppUsageTodayState.Available);
                check(client.LastRange == range && usage.Current.Applications.Count == 1 && usage.SelectedRange == range,
                    $"New post-clear application usage appears on the normal {range} periodic refresh");
                usage.SetActive(false);
                var hiddenCalls = client.Calls;
                service.HasData = false;
                service.NotifyHistoryCleared();
                HistoryOverviewChecks.Pump();
                check(client.Calls == hiddenCalls && usage.Current.State == AppUsageTodayState.Empty && usage.SelectedRange == range,
                    $"The hidden {range} page clears cached history with zero IPC requests");
                usage.SetActive(true);
                Wait(() => !usage.IsRefreshing && usage.Current.State == AppUsageTodayState.Empty);
                check(client.Calls == hiddenCalls + 1 && client.LastRange == range,
                    $"Returning to {range} after clear loads current data without restarting NetTrace");
            }
            finally { WaitTask(usage.DisposeAsync().AsTask()); }
        }
    }

    private static void CheckStaleAndClosedCompletion(Action<bool, string> check)
    {
        var service = new ControlledHistoryService();
        var client = new HeldFirstUsageClient();
        var usage = new AppUsageTodayController(Dispatcher.CurrentDispatcher, client, _ => { },
            setupService: service);
        try
        {
            usage.SelectRange(AppUsageRange.ThirtyDays);
            usage.SetActive(true);
            Wait(() => client.Calls == 1);
            service.NotifyHistoryCleared();
            HistoryOverviewChecks.Pump();
            check(usage.Current.State == AppUsageTodayState.Empty && usage.SelectedRange == AppUsageRange.ThirtyDays,
                "Clear invalidates an in-flight pre-clear range and immediately hides its obsolete data");
            client.CompleteFirst();
            Wait(() => client.Calls == 2 && !usage.IsRefreshing);
            check(usage.Current.State == AppUsageTodayState.Empty && usage.Current.Applications.Count == 0 && client.LastRange == AppUsageRange.ThirtyDays,
                "Late pre-clear data cannot overwrite the immediate clear or the fresh selected-range response");
        }
        finally { client.CompleteFirst(); WaitTask(usage.DisposeAsync().AsTask()); }

        var closedService = new ControlledHistoryService();
        var renders = 0;
        var callbacks = 0;
        var settings = new AppTrackingSettingsController(Dispatcher.CurrentDispatcher, closedService, _ => renders++, () => true,
            confirmClearHistory: () => true, onHistoryCleared: () => callbacks++);
        settings.SetActive(true);
        Wait(() => !settings.IsRefreshing && settings.CanClearHistory);
        var clear = settings.ClearHistoryAsync();
        Wait(() => closedService.ClearCalls == 1);
        WaitTask(settings.DisposeAsync().AsTask());
        var before = renders;
        closedService.Complete(0, AppUsageHistoryClearOutcome.Succeeded);
        WaitTask(clear);
        check(renders == before && callbacks == 0 && closedService.State == AppTrackingServiceState.Running,
            "A closed Settings view does not render late clear completion or alter the lifetime of the running collector");
    }

    private static void CheckWindow(string root, Action<bool, string> check)
    {
        using var monitoring = HistoryOverviewChecks.CreateMonitoring(new UsageStore(System.IO.Path.Combine(root, "history-clear-window")));
        var service = new ControlledHistoryService();
        var client = new OwnedUsageClient(service);
        var confirmations = 0;
        var allow = false;
        using var before = WindowsIdentity.GetCurrent();
        var elevatedBefore = new WindowsPrincipal(before).IsInRole(WindowsBuiltInRole.Administrator);
        var window = new MainWindow(monitoring, new OwnedStartup(), appUsageClient: client, appTrackingSetupService: service,
            confirmAppTrackingUninstall: () => throw new InvalidOperationException("Clear must not invoke uninstall confirmation"),
            confirmAppUsageHistoryClear: () => { confirmations++; return allow; })
            { ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Width = 760, Height = 600 };
        try
        {
            window.Show();
            HistoryOverviewChecks.StopViewTimer(window);
            Layout(window);
            var physicalBefore = monitoring.GetSnapshot();
            var dashboardBefore = Text(window, "DownloadSessionText") + Text(window, "UploadSessionText");
            var historyBefore = Text(window, "HistoryTodaySessionsText") + Text(window, "CurrentSessionUsageText");
            check(service.ClearCalls == 0 && confirmations == 0 && service.InstallCalls == 0 && service.UninstallCalls == 0,
                "The real NetTrace window never confirms, clears or requests elevation at startup");
            SelectTab(window, "AppUsageTab");
            Wait(() => !UsageController(window).IsRefreshing && UsageController(window).Current.State == AppUsageTodayState.Available);
            Control<ToggleButton>(window, "AppUsage30DButton").SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Wait(() => !UsageController(window).IsRefreshing && client.LastRange == AppUsageRange.ThirtyDays);
            SelectTab(window, "SettingsTab");
            Wait(() => !SettingsController(window).IsRefreshing && SettingsController(window).CanClearHistory);
            var button = Control<Button>(window, "SettingsAppUsageHistoryClearButton");
            Layout(window);
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(root, "app-usage-history-clear-previews", "760x600-running.png"));
            check(button.IsVisible && button.IsEnabled && button.Content?.ToString() == "Clear App Usage History" &&
                Text(window, "SettingsAppTrackingStatusText") == "Running",
                "Actual Settings exposes Clear App Usage History beside current Running tracking status");
            var hiddenCalls = client.Calls;
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            HistoryOverviewChecks.Pump();
            check(confirmations == 1 && service.ClearCalls == 0 && service.HasData && button.IsEnabled,
                "Cancelling confirmation from the actual Clear button preserves history without UAC");
            allow = true;
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Wait(() => service.ClearCalls == 1);
            check(!button.IsEnabled && !Control<Button>(window, "SettingsAppTrackingUninstallButton").IsEnabled &&
                Text(window, "SettingsAppTrackingStatusText") == "Running",
                "Actual Clear disables duplicate or uninstall actions without changing the collector status");
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            HistoryOverviewChecks.Pump();
            check(confirmations == 2 && service.ClearCalls == 1, "Duplicate real-button clicks cannot bypass the active clear guard");
            service.Complete(0, AppUsageHistoryClearOutcome.Cancelled);
            Wait(() => !SettingsController(window).IsClearingHistory && !SettingsController(window).IsRefreshing);
            check(button.IsEnabled && Control<TextBlock>(window, "SettingsAppTrackingErrorText").IsVisible && service.HasData && client.Calls == hiddenCalls,
                "Cancelled UAC shows short retry feedback in the real window without querying hidden App Usage");
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Wait(() => service.ClearCalls == 2);
            service.Complete(1, AppUsageHistoryClearOutcome.Failed);
            Wait(() => !SettingsController(window).IsClearingHistory && !SettingsController(window).IsRefreshing);
            check(button.IsEnabled && !string.IsNullOrWhiteSpace(Text(window, "SettingsAppTrackingErrorText")) && service.HasData,
                "Failed real-button clear retains history and permits explicit retry without a crash");
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Wait(() => service.ClearCalls == 3);
            service.Complete(2, AppUsageHistoryClearOutcome.Succeeded);
            Wait(() => !SettingsController(window).IsClearingHistory && !SettingsController(window).IsRefreshing &&
                UsageController(window).Current.State == AppUsageTodayState.Empty);
            check(button.IsEnabled && !Control<TextBlock>(window, "SettingsAppTrackingErrorText").IsVisible &&
                Text(window, "SettingsAppTrackingStatusText") == "Running" && service.InstallCalls == 0 && service.UninstallCalls == 0,
                "Successful real-button clear retains Running tracking and schema-backed storage while removing retry feedback");
            check(client.Calls == hiddenCalls && UsageController(window).SelectedRange == AppUsageRange.ThirtyDays &&
                UsageController(window).Current.Applications.Count == 0,
                "The same window immediately clears hidden App Usage cache, preserves 30D, and sends zero hidden IPC requests");
            check(Text(window, "DownloadSessionText") + Text(window, "UploadSessionText") == dashboardBefore &&
                Text(window, "HistoryTodaySessionsText") + Text(window, "CurrentSessionUsageText") == historyBefore &&
                monitoring.GetSnapshot().Session?.Id == physicalBefore.Session?.Id,
                "Clearing App Usage leaves Dashboard display, connection History and physical session accounting unchanged");
            SelectTab(window, "AppUsageTab");
            Wait(() => !UsageController(window).IsRefreshing && UsageController(window).Current.State == AppUsageTodayState.Empty);
            check(Control<ToggleButton>(window, "AppUsage30DButton").IsChecked == true &&
                Text(window, "AppUsageStateText") == "No app network usage recorded in the last 30 days.",
                "Navigating back immediately displays the existing empty 30D state without restarting NetTrace");
            foreach (var pair in new[] { ("AppUsageTodayButton", AppUsageRange.Today), ("AppUsage7DButton", AppUsageRange.SevenDays) })
            {
                Control<ToggleButton>(window, pair.Item1).SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                Wait(() => !UsageController(window).IsRefreshing && client.LastRange == pair.Item2);
                check(UsageController(window).Current.State == AppUsageTodayState.Empty && UsageController(window).Current.Applications.Count == 0,
                    $"The real {pair.Item2} view also displays empty results after clear");
            }
            service.HasData = true;
            Control<ToggleButton>(window, "AppUsage30DButton").SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Wait(() => !UsageController(window).IsRefreshing && UsageController(window).Current.State == AppUsageTodayState.Available);
            check(UsageController(window).Current.Applications.Count == 1 && client.LastRange == AppUsageRange.ThirtyDays,
                "The real page presents new usage after clear through normal range queries while tracking stays running");
            using var after = WindowsIdentity.GetCurrent();
            check(after.User?.Value == before.User?.Value &&
                new WindowsPrincipal(after).IsInRole(WindowsBuiltInRole.Administrator) == elevatedBefore,
                "The real NetTrace window retains its original unelevated process token through clear and retry");
            SelectTab(window, "SettingsTab");
            service.State = AppTrackingServiceState.Missing;
            service.Storage = AppUsageStorageState.Exists;
            service.Notify();
            Wait(() => !SettingsController(window).IsRefreshing && Text(window, "SettingsAppTrackingStatusText") == "Not installed");
            check(button.IsEnabled && !Control<Button>(window, "SettingsAppTrackingUninstallButton").IsVisible,
                "Actual Settings allows clearing retained storage after uninstall without exposing uninstall for an absent service");
            service.Storage = AppUsageStorageState.Missing;
            service.Notify();
            Wait(() => !SettingsController(window).IsRefreshing && !SettingsController(window).Current.StorageAvailable);
            check(button.IsVisible && !button.IsEnabled, "Actual Clear remains discoverable but disabled when no App Usage storage exists");
            Layout(window);
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(root, "app-usage-history-clear-previews", "760x600-not-installed.png"));
        }
        finally
        {
            window.Close();
            service.CompletePending();
            Wait(() => client.Disposed);
        }
    }

    private static void Dispose(AppTrackingSettingsController controller) => WaitTask(controller.DisposeAsync().AsTask());
    private static T WaitTask<T>(Task<T> task) { Wait(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
    private static void WaitTask(Task task) { Wait(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Wait(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(6)) throw new TimeoutException("Owned App Usage history clear condition was not ready.");
            HistoryOverviewChecks.Pump();
        }
        HistoryOverviewChecks.Pump();
    }
    private static T Control<T>(MainWindow window, string name) where T : class => window.FindName(name) as T ??
        throw new InvalidOperationException("Missing history clear control " + name);
    private static string Text(MainWindow window, string name) => Control<TextBlock>(window, name).Text;
    private static AppTrackingSettingsController SettingsController(MainWindow window) => (AppTrackingSettingsController)typeof(MainWindow)
        .GetField("_appTrackingSettings", PrivateInstance)!.GetValue(window)!;
    private static AppUsageTodayController UsageController(MainWindow window) => (AppUsageTodayController)typeof(MainWindow)
        .GetField("_appUsageToday", PrivateInstance)!.GetValue(window)!;
    private static void SelectTab(MainWindow window, string name)
    {
        Control<TabControl>(window, "MainNavigation").SelectedItem = Control<TabItem>(window, name);
        Layout(window);
    }
    private static void Layout(MainWindow window) { window.UpdateLayout(); HistoryOverviewChecks.Pump(); window.UpdateLayout(); }

    private sealed class ControlledHistoryService : IAppUsageHistoryManagementService
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource<AppUsageHistoryClearOutcome>> _clears = [];
        private int _statusCalls, _storageCalls, _state = (int)AppTrackingServiceState.Running;
        private int _storage = (int)AppUsageStorageState.Exists;
        private int _hasData = 1;
        public event EventHandler? StateChanged;
        public event EventHandler? HistoryCleared;
        public AppTrackingServiceState State { get => (AppTrackingServiceState)Volatile.Read(ref _state); set => Volatile.Write(ref _state, (int)value); }
        public AppUsageStorageState Storage { get => (AppUsageStorageState)Volatile.Read(ref _storage); set => Volatile.Write(ref _storage, (int)value); }
        public bool HasData { get => Volatile.Read(ref _hasData) != 0; set => Volatile.Write(ref _hasData, value ? 1 : 0); }
        public int StatusCalls => Volatile.Read(ref _statusCalls);
        public int StorageCalls => Volatile.Read(ref _storageCalls);
        public int ClearCalls { get { lock (_gate) return _clears.Count; } }
        public int InstallCalls { get; private set; }
        public int UninstallCalls { get; private set; }
        public Task<AppTrackingServiceState> GetStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _statusCalls);
            return Task.FromResult(State);
        }
        public Task<AppUsageStorageState> GetStorageStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _storageCalls);
            return Task.FromResult(Storage);
        }
        public Task<AppUsageHistoryClearOutcome> ClearHistoryAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource<AppUsageHistoryClearOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _clears.Add(completion);
            return completion.Task;
        }
        public Task<AppTrackingInstallOutcome> InstallAsync(CancellationToken cancellationToken)
        {
            InstallCalls++;
            return Task.FromResult(AppTrackingInstallOutcome.Failed);
        }
        public Task<AppTrackingUninstallOutcome> UninstallAsync(CancellationToken cancellationToken)
        {
            UninstallCalls++;
            return Task.FromResult(AppTrackingUninstallOutcome.Failed);
        }
        public void Complete(int index, AppUsageHistoryClearOutcome outcome)
        {
            if (outcome == AppUsageHistoryClearOutcome.Succeeded) { HasData = false; NotifyHistoryCleared(); }
            lock (_gate) _clears[index].TrySetResult(outcome);
        }
        public void Notify() => StateChanged?.Invoke(this, EventArgs.Empty);
        public void NotifyHistoryCleared() => HistoryCleared?.Invoke(this, EventArgs.Empty);
        public void CompletePending()
        {
            lock (_gate) foreach (var clear in _clears) clear.TrySetResult(AppUsageHistoryClearOutcome.Cancelled);
        }
    }
    private sealed class OwnedUsageClient(ControlledHistoryService service) : IAppUsageTodayClient
    {
        private readonly object _gate = new();
        private readonly List<AppUsageRange> _ranges = [];
        public int Calls { get { lock (_gate) return _ranges.Count; } }
        public AppUsageRange LastRange { get { lock (_gate) return _ranges[^1]; } }
        public bool Disposed { get; private set; }
        public Task<IpcResult<AppUsageQueryResult>> GetAppUsageRangeAsync(AppUsageRange range, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate) _ranges.Add(range);
            var result = AppUsageTodayChecks.Result(service.HasData
                ? [new("owned-after-clear", "Owned application", "owned.exe", null, 9000, 1000, 10000, false, false)] : []) with { Range = range };
            return Task.FromResult(AppUsageTodayChecks.Connected(result));
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class HeldFirstUsageClient : IAppUsageTodayClient
    {
        private readonly TaskCompletionSource<IpcResult<AppUsageQueryResult>> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public AppUsageRange LastRange { get; private set; }
        public Task<IpcResult<AppUsageQueryResult>> GetAppUsageRangeAsync(AppUsageRange range, CancellationToken cancellationToken)
        {
            LastRange = range;
            if (Interlocked.Increment(ref _calls) == 1) return _first.Task;
            return Task.FromResult(AppUsageTodayChecks.Connected(AppUsageTodayChecks.Result([]) with { Range = range }));
        }
        public void CompleteFirst() => _first.TrySetResult(AppUsageTodayChecks.Connected(AppUsageTodayChecks.Result(
            [new("owned-stale-before-clear", "Old application", "old.exe", null, 90_000, 10_000, 100_000, false, false)])
            with { Range = AppUsageRange.ThirtyDays }));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class ControlledClearLauncher : IAppUsageHistoryClearLauncher
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource<int>> _launches = [];
        public int Calls { get { lock (_gate) return _launches.Count; } }
        public Task<int> LaunchAsync(AppUsageHistoryClearLaunchSpec invocation)
        {
            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _launches.Add(completion);
            return completion.Task;
        }
        public bool IsCompleted(int index) { lock (_gate) return _launches[index].Task.IsCompleted; }
        public void Complete(int index, int code) { lock (_gate) _launches[index].TrySetResult(code); }
        public void Fail(int index, Exception error) { lock (_gate) _launches[index].TrySetException(error); }
    }
    private sealed class ControlledInstaller : IAppTrackingInstallerLauncher
    {
        private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task<int> LaunchAsync(AppTrackingInstallLaunchSpec invocation) { Interlocked.Increment(ref _calls); return _completion.Task; }
        public void Complete(int code) => _completion.TrySetResult(code);
    }
    private sealed class ControlledUninstaller : IAppTrackingUninstallerLauncher
    {
        private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task<int> LaunchAsync(AppTrackingUninstallLaunchSpec invocation) { Interlocked.Increment(ref _calls); return _completion.Task; }
        public void Complete(int code) => _completion.TrySetResult(code);
    }
    private sealed class OwnedStateReader : IAppTrackingServiceStateReader
    {
        public AppTrackingServiceState GetState() => AppTrackingServiceState.Running;
    }
    private sealed class OwnedStorageReader : IAppUsageStorageStateReader
    {
        public AppUsageStorageState State { get; set; } = AppUsageStorageState.Exists;
        public bool Throw { get; set; }
        public AppUsageStorageState GetState() => Throw ? throw new InvalidOperationException("owned readonly failure") : State;
    }
    private sealed class OwnedClock : TimeProvider
    {
        private DateTimeOffset _utc = new(2026, 10, 13, 7, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _utc;
        public void Advance(TimeSpan interval) => _utc += interval;
    }
    private sealed class OwnedStartup : IWindowsStartupRegistration
    {
        public bool IsEnabled() => false;
        public void Enable() => throw new InvalidOperationException("History clear must not change startup registration.");
        public void Disable() => throw new InvalidOperationException("History clear must not change startup registration.");
    }
}
