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

internal static class AppTrackingSettingsChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string OwnedClientSid = "S-1-5-21-101-202-303-1001";

    public static void Run(string root, Action<bool, string> check)
    {
        CheckPlatform(root, check);
        CheckController(check);
        CheckFailures(check);
        CheckAppUsageUpdates(check);
        CheckStaleResponses(check);
        CheckLifecycle(check);
        CheckWindow(root, check);
    }

    private static void CheckPlatform(string root, Action<bool, string> check)
    {
        var appDirectory = System.IO.Path.Combine(root, "owned management application with spaces");
        var invocation = AppTrackingUninstallLaunchSpec.Create(appDirectory);
        var start = invocation.CreateStartInfo();
        check(start.FileName == System.IO.Path.Combine(appDirectory, "AppTracking", "Installer", "NetTrace.AppUsageInstaller.exe") &&
            start.WorkingDirectory == System.IO.Path.GetDirectoryName(start.FileName),
            "Management invokes the existing bundled production installer from its fixed application directory");
        check(start.UseShellExecute && start.Verb == "runas" && start.WindowStyle == ProcessWindowStyle.Hidden &&
            start.FileName != Environment.ProcessPath,
            "Explicit uninstall elevates only the existing installer and never relaunches NetTrace");
        check(start.ArgumentList.SequenceEqual(new[] { "uninstall" }),
            "Uninstall uses only the existing history-preserving uninstall command without database deletion or configuration arguments");
        var inspected = new List<string>();
        check(invocation.HasRequiredFiles(path => { inspected.Add(path); return true; }) && inspected.Count == 4 &&
            inspected.All(path => System.IO.Path.GetDirectoryName(path) == start.WorkingDirectory) &&
            inspected.Contains(System.IO.Path.Combine(start.WorkingDirectory!, "NetTrace.AppUsageInstaller.runtimeconfig.json")),
            "Uninstall validates all four installer entry files without depending on collector files or opening the usage database");
        check(!invocation.HasRequiredFiles(path => !path.EndsWith(".deps.json", StringComparison.Ordinal)),
            "Incomplete uninstall tooling is rejected before any UAC prompt");
        var relativeRejected = false;
        try { _ = AppTrackingUninstallLaunchSpec.Create("relative directory"); }
        catch (ArgumentException) { relativeRejected = true; }
        check(relativeRejected, "Uninstall rejects a relative installer directory before launching anything");

        var reader = new OwnedStateReader();
        var launcher = new ControlledUninstaller();
        var installer = new ControlledInstaller();
        var service = new AppTrackingSetupService(installer, reader, appDirectory, OwnedClientSid, _ => true,
            uninstallerLauncher: launcher);
        foreach (var state in new[] { AppTrackingServiceState.Running, AppTrackingServiceState.Stopped,
            AppTrackingServiceState.Missing, AppTrackingServiceState.Unavailable })
        {
            reader.State = state;
            check(WaitTask(service.GetStateAsync(CancellationToken.None)) == state && launcher.Calls == 0 && installer.Calls == 0,
                $"Reading {state} management state never launches an installer or uninstaller");
        }
        reader.State = AppTrackingServiceState.Running;
        var observedStates = new List<AppTrackingServiceState>();
        service.StateChanged += (_, _) => throw new InvalidOperationException("owned failing observer");
        service.StateChanged += (_, _) => observedStates.Add(service.GetStateAsync(CancellationToken.None).GetAwaiter().GetResult());
        using var identityBefore = WindowsIdentity.GetCurrent();
        var elevatedBefore = new WindowsPrincipal(identityBefore).IsInRole(WindowsBuiltInRole.Administrator);
        var first = service.UninstallAsync(CancellationToken.None);
        Wait(() => launcher.Calls == 1);
        var second = service.UninstallAsync(CancellationToken.None);
        check(launcher.Calls == 1 && !first.IsCompleted && !second.IsCompleted &&
            WaitTask(service.GetStateAsync(CancellationToken.None)) == AppTrackingServiceState.Uninstalling,
            "Concurrent uninstall waiters share one explicit launch and expose uninstall-in-progress state");
        check(WaitTask(service.InstallAsync(CancellationToken.None)) == AppTrackingInstallOutcome.Failed && installer.Calls == 0,
            "An install request cannot launch a competing elevated child while uninstall is running");
        var dispatched = false;
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => dispatched = true));
        Wait(() => dispatched);
        check(dispatched && !first.IsCompleted, "Uninstall waits leave the desktop dispatcher responsive");
        reader.State = AppTrackingServiceState.Missing;
        launcher.Complete(0, 0);
        check(WaitTask(first) == AppTrackingUninstallOutcome.Succeeded && WaitTask(second) == AppTrackingUninstallOutcome.Succeeded,
            "A successful existing uninstaller exit completes shared waiters successfully");
        check(observedStates.SequenceEqual(new[] { AppTrackingServiceState.Missing }),
            "Management notifications observe fresh service state after operation completion and survive a failing observer");
        using var identityAfter = WindowsIdentity.GetCurrent();
        check(identityBefore.User?.Value == identityAfter.User?.Value &&
            new WindowsPrincipal(identityAfter).IsInRole(WindowsBuiltInRole.Administrator) == elevatedBefore,
            "Uninstall leaves the desktop process identity and elevation token unchanged");

        using var waiterCancellation = new CancellationTokenSource();
        var cancelledWaiter = service.UninstallAsync(waiterCancellation.Token);
        Wait(() => launcher.Calls == 2);
        waiterCancellation.Cancel();
        Wait(() => cancelledWaiter.IsCompleted);
        check(cancelledWaiter.IsCanceled && !launcher.IsCompleted(1) &&
            WaitTask(service.GetStateAsync(CancellationToken.None)) == AppTrackingServiceState.Uninstalling,
            "Cancelling a view wait leaves the already launched elevated uninstall running safely");
        var retainedWaiter = service.UninstallAsync(CancellationToken.None);
        check(launcher.Calls == 2, "A recreated view joins an existing uninstall without a second UAC prompt");
        launcher.Complete(1, 0);
        check(WaitTask(retainedWaiter) == AppTrackingUninstallOutcome.Succeeded,
            "The recreated view receives the original uninstaller result");

        foreach (var failure in new[] { "cancelled", "nonzero", "exception" })
        {
            var request = launcher.Calls;
            var attempt = service.UninstallAsync(CancellationToken.None);
            Wait(() => launcher.Calls == request + 1);
            if (failure == "cancelled") launcher.Fail(request, new Win32Exception(1223));
            else if (failure == "nonzero") launcher.Complete(request, 8);
            else launcher.Fail(request, new InvalidOperationException("private exception details"));
            check(WaitTask(attempt) == (failure == "cancelled" ? AppTrackingUninstallOutcome.Cancelled : AppTrackingUninstallOutcome.Failed),
                $"Uninstaller {failure} produces a safe typed management outcome");
        }
        var missingLauncher = new ControlledUninstaller();
        var missingKit = new AppTrackingSetupService(installer, reader, appDirectory, OwnedClientSid, _ => false,
            uninstallerLauncher: missingLauncher);
        check(WaitTask(missingKit.UninstallAsync(CancellationToken.None)) == AppTrackingUninstallOutcome.FilesMissing && missingLauncher.Calls == 0,
            "Missing uninstall tooling returns a retryable outcome without requesting elevation");
        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        var cancellationRejected = false;
        try { _ = missingKit.UninstallAsync(alreadyCancelled.Token); }
        catch (OperationCanceledException) { cancellationRejected = true; }
        check(cancellationRejected && missingLauncher.Calls == 0,
            "An already cancelled uninstall invocation never starts an elevated child");

        var installAttempt = service.InstallAsync(CancellationToken.None);
        Wait(() => installer.Calls == 1);
        var uninstallCalls = launcher.Calls;
        check(WaitTask(service.UninstallAsync(CancellationToken.None)) == AppTrackingUninstallOutcome.Failed && launcher.Calls == uninstallCalls,
            "An uninstall request cannot launch a competing elevated child while installation is running");
        installer.Complete(0);
        WaitTask(installAttempt);
    }

    private static void CheckController(Action<bool, string> check)
    {
        var service = new ControlledManagementService();
        var clock = new OwnedClock();
        var confirms = 0;
        var allowConfirmation = false;
        var uninstalled = 0;
        var applied = new List<AppTrackingSettingsPresentation>();
        var applyThreads = new List<int>();
        var uiThread = Environment.CurrentManagedThreadId;
        var controller = new AppTrackingSettingsController(Dispatcher.CurrentDispatcher, service,
            state => { applied.Add(state); applyThreads.Add(Environment.CurrentManagedThreadId); },
            () => { confirms++; return allowConfirmation; }, () => uninstalled++, clock);
        try
        {
            controller.Tick();
            WaitTask(controller.UninstallAsync());
            check(service.StatusCalls == 0 && service.UninstallCalls == 0 && confirms == 0 && applied.Count == 0,
                "Inactive Settings neither inspects App Tracking nor confirms or launches uninstall");
            controller.SetActive(true);
            Wait(() => !controller.IsRefreshing && controller.Current.StatusText == "Running");
            check(controller.Current.IsInstalled && controller.CanUninstall && controller.Current.CanUninstall && service.UninstallCalls == 0,
                "Running App Tracking is shown installed with an explicit uninstall action and no automatic launch");
            var statusCalls = service.StatusCalls;
            controller.Tick();
            check(service.StatusCalls == statusCalls && service.UninstallCalls == 0,
                "Settings retains its refresh schedule and never uninstalls on a timer");
            service.State = AppTrackingServiceState.Stopped;
            Refresh(controller, clock, () => controller.Current.StatusText == "Stopped");
            check(controller.Current.IsInstalled && controller.CanUninstall,
                "A stopped installed collector remains eligible for explicit uninstall");
            service.State = AppTrackingServiceState.Missing;
            Refresh(controller, clock, () => controller.Current.StatusText == "Not installed");
            check(!controller.Current.IsInstalled && !controller.CanUninstall && !controller.Current.CanUninstall,
                "A missing collector reports Not installed and has no uninstall action");
            WaitTask(controller.UninstallAsync());
            check(confirms == 0 && service.UninstallCalls == 0, "Attempting to uninstall missing App Tracking never prompts or launches");
            service.State = AppTrackingServiceState.Unavailable;
            Refresh(controller, clock, () => controller.Current.StatusText == "Unavailable");
            check(!controller.CanUninstall && service.UninstallCalls == 0,
                "Unreadable SCM state never guesses that App Tracking can be removed");
            service.State = AppTrackingServiceState.Running;
            service.Notify();
            Wait(() => !controller.IsRefreshing && controller.Current.StatusText == "Running");
            WaitTask(controller.UninstallAsync());
            check(confirms == 1 && service.UninstallCalls == 0 && controller.CanUninstall && controller.Current.Error == "",
                "Cancelling the required confirmation leaves service state unchanged without UAC or an error");
            allowConfirmation = true;
            var uninstall = controller.UninstallAsync();
            Wait(() => service.UninstallCalls == 1);
            check(confirms == 2 && controller.IsUninstalling && !controller.CanUninstall &&
                controller.Current.StatusText == "Uninstalling..." && !uninstall.IsCompleted,
                "A confirmed explicit uninstall enters disabled progress state before launching the existing tool");
            var duplicate = controller.UninstallAsync();
            var statusAtUninstall = service.StatusCalls;
            clock.Advance(TimeSpan.FromMinutes(1));
            for (var index = 0; index < 20; index++) controller.Tick();
            check(confirms == 2 && service.UninstallCalls == 1 && service.StatusCalls == statusAtUninstall,
                "Repeated uninstall clicks and timer ticks do not prompt again or poll over the active uninstall");
            service.State = AppTrackingServiceState.Missing;
            service.Complete(0, AppTrackingUninstallOutcome.Succeeded);
            Wait(() => uninstall.IsCompleted && duplicate.IsCompleted && !controller.IsUninstalling &&
                controller.Current.StatusText == "Not installed");
            WaitTask(uninstall);
            WaitTask(duplicate);
            check(!controller.Current.IsInstalled && !controller.CanUninstall && controller.Current.Error == "" && uninstalled == 1,
                "Successful uninstall immediately reports Not installed and notifies App Usage exactly once");
            check(applyThreads.All(thread => thread == uiThread),
                "Management refresh and uninstall publish Settings state only on its captured dispatcher");
            controller.SetActive(false);
            var hiddenStatuses = service.StatusCalls;
            var hiddenApplies = applied.Count;
            service.State = AppTrackingServiceState.Running;
            service.Notify();
            clock.Advance(TimeSpan.FromMinutes(1));
            for (var index = 0; index < 20; index++) controller.Tick();
            HistoryOverviewChecks.Pump();
            check(service.StatusCalls == hiddenStatuses && applied.Count == hiddenApplies && service.UninstallCalls == 1,
                "Hidden Settings performs zero status requests, rendering, confirmation or automatic uninstall");
            controller.SetActive(true);
            Wait(() => !controller.IsRefreshing && controller.Current.StatusText == "Running");
            check(controller.CanUninstall && service.UninstallCalls == 1,
                "Returning to Settings inspects current service state without launching another uninstall");
        }
        finally { Dispose(controller); }
    }

    private static void CheckFailures(Action<bool, string> check)
    {
        foreach (var outcome in new[] { AppTrackingUninstallOutcome.Cancelled, AppTrackingUninstallOutcome.Failed,
            AppTrackingUninstallOutcome.FilesMissing })
        {
            var service = new ControlledManagementService();
            var clock = new OwnedClock();
            var uninstalled = 0;
            var controller = new AppTrackingSettingsController(Dispatcher.CurrentDispatcher, service, _ => { }, () => true,
                () => uninstalled++, clock);
            try
            {
                controller.SetActive(true);
                Wait(() => !controller.IsRefreshing && controller.Current.StatusText == "Running");
                var attempt = controller.UninstallAsync();
                Wait(() => service.UninstallCalls == 1);
                service.Complete(0, outcome);
                Wait(() => attempt.IsCompleted && !controller.IsRefreshing && !controller.IsUninstalling);
                WaitTask(attempt);
                check(controller.Current.StatusText == "Running" && controller.CanUninstall &&
                    !string.IsNullOrWhiteSpace(controller.Current.Error) && controller.Current.Error.Length <= 160 && uninstalled == 0,
                    $"{outcome} restores installed service state with short retry feedback and no success notification");
                var error = controller.Current.Error;
                Refresh(controller, clock, () => controller.Current.StatusText == "Running");
                check(controller.Current.Error == error && service.UninstallCalls == 1,
                    $"{outcome} feedback survives ordinary refresh without an automatic elevated retry");
                var retry = controller.UninstallAsync();
                Wait(() => service.UninstallCalls == 2);
                service.State = AppTrackingServiceState.Missing;
                service.Complete(1, AppTrackingUninstallOutcome.Succeeded);
                Wait(() => retry.IsCompleted && controller.Current.StatusText == "Not installed");
                WaitTask(retry);
                check(controller.Current.Error == "" && !controller.CanUninstall && uninstalled == 1,
                    $"A confirmed retry after {outcome} clears feedback and completes removal without crashing");
            }
            finally { Dispose(controller); }
        }
    }

    private static void CheckAppUsageUpdates(Action<bool, string> check)
    {
        var service = new ControlledManagementService();
        var client = new OwnedUsageClient();
        var presentations = new List<AppUsageTodayPresentation>();
        var usage = new AppUsageTodayController(Dispatcher.CurrentDispatcher, client, presentations.Add, setupService: service);
        try
        {
            usage.SelectRange(AppUsageRange.ThirtyDays);
            usage.SetActive(true);
            Wait(() => !usage.IsRefreshing && usage.Current.State == AppUsageTodayState.Available);
            service.State = AppTrackingServiceState.Missing;
            service.Notify();
            Wait(() => !usage.IsRefreshing && usage.Current.State == AppUsageTodayState.Missing);
            check(usage.Current.Message == "App tracking is not installed." && usage.CanInstall && client.Calls == 1 &&
                usage.SelectedRange == AppUsageRange.ThirtyDays,
                "App Usage immediately changes to existing missing-service setup on a management notification and retains 30D");
            service.State = AppTrackingServiceState.Running;
            service.Notify();
            Wait(() => !usage.IsRefreshing && usage.Current.State == AppUsageTodayState.Available);
            usage.SetActive(false);
            var hiddenCalls = client.Calls;
            var hiddenStatuses = service.StatusCalls;
            var hiddenApplies = presentations.Count;
            service.State = AppTrackingServiceState.Missing;
            service.Notify();
            for (var index = 0; index < 20; index++) usage.Tick();
            HistoryOverviewChecks.Pump();
            check(client.Calls == hiddenCalls && service.StatusCalls == hiddenStatuses && presentations.Count == hiddenApplies,
                "Uninstall notification sends zero refresh requests or presentations from a hidden App Usage page");
            usage.SetActive(true);
            Wait(() => !usage.IsRefreshing && usage.Current.State == AppUsageTodayState.Missing);
            check(usage.CanInstall && usage.SelectedRange == AppUsageRange.ThirtyDays && client.Calls == hiddenCalls,
                "Returning to App Usage after uninstall shows setup without restarting or querying missing IPC and retains 30D");
        }
        finally { WaitTask(usage.DisposeAsync().AsTask()); }
    }

    private static void CheckLifecycle(Action<bool, string> check)
    {
        var service = new ControlledManagementService();
        var applies = 0;
        var uninstalled = 0;
        var controller = new AppTrackingSettingsController(Dispatcher.CurrentDispatcher, service, _ => applies++, () => true,
            () => uninstalled++);
        controller.SetActive(true);
        Wait(() => !controller.IsRefreshing && controller.Current.StatusText == "Running");
        var uninstall = controller.UninstallAsync();
        Wait(() => service.UninstallCalls == 1);
        controller.SetActive(false);
        var hiddenApplies = applies;
        var hiddenStatuses = service.StatusCalls;
        service.State = AppTrackingServiceState.Missing;
        service.Complete(0, AppTrackingUninstallOutcome.Succeeded);
        Wait(() => uninstall.IsCompleted);
        WaitTask(uninstall);
        check(applies == hiddenApplies + 1 && service.StatusCalls == hiddenStatuses && uninstalled == 1 &&
            controller.Current.StatusText == "Not installed",
            "An uninstall completed after navigation immediately records Not installed without polling hidden Settings");
        controller.SetActive(true);
        Wait(() => !controller.IsRefreshing && controller.Current.StatusText == "Not installed");
        check(!controller.CanUninstall, "Settings re-entry reads missing state after an uninstall completed while hidden");
        Dispose(controller);

        var closingService = new ControlledManagementService();
        var closingApplies = 0;
        var closingNotifications = 0;
        var closing = new AppTrackingSettingsController(Dispatcher.CurrentDispatcher, closingService, _ => closingApplies++, () => true,
            () => closingNotifications++);
        closing.SetActive(true);
        Wait(() => !closing.IsRefreshing && closing.Current.StatusText == "Running");
        var closingUninstall = closing.UninstallAsync();
        Wait(() => closingService.UninstallCalls == 1);
        var atClose = closingApplies;
        var disposal = closing.DisposeAsync().AsTask();
        closingService.State = AppTrackingServiceState.Missing;
        closingService.Complete(0, AppTrackingUninstallOutcome.Succeeded);
        Wait(() => closingUninstall.IsCompleted && disposal.IsCompleted);
        WaitTask(closingUninstall);
        WaitTask(disposal);
        closingService.Notify();
        HistoryOverviewChecks.Pump();
        check(closingApplies == atClose && closingNotifications == 0,
            "Closing during uninstall ignores late UI callbacks and detaches management notifications without interrupting the child");
    }

    private static void CheckStaleResponses(Action<bool, string> check)
    {
        var service = new ControlledManagementService { HoldStatusReads = true };
        var clock = new OwnedClock();
        var published = new List<AppTrackingSettingsPresentation>();
        var controller = new AppTrackingSettingsController(Dispatcher.CurrentDispatcher, service, published.Add, () => true,
            timeProvider: clock);
        try
        {
            controller.SetActive(true);
            Wait(() => service.HeldStatusCalls == 1);
            controller.SetActive(false);
            service.State = AppTrackingServiceState.Stopped;
            service.HoldStatusReads = false;
            controller.SetActive(true);
            service.CompleteStatus(0, AppTrackingServiceState.Running);
            Wait(() => !controller.IsRefreshing && controller.Current.StatusText == "Stopped");
            check(published.All(page => page.StatusText != "Running") && controller.CanUninstall,
                "A status reply from before Settings navigation cannot overwrite the newer stopped-service inspection");
            service.HoldStatusReads = true;
            clock.Advance(TimeSpan.FromSeconds(4));
            controller.Tick();
            Wait(() => service.HeldStatusCalls == 2);
            var uninstall = controller.UninstallAsync();
            Wait(() => service.UninstallCalls == 1);
            service.State = AppTrackingServiceState.Missing;
            service.Complete(0, AppTrackingUninstallOutcome.Succeeded);
            Wait(() => uninstall.IsCompleted && controller.Current.StatusText == "Not installed");
            WaitTask(uninstall);
            service.HoldStatusReads = false;
            service.CompleteStatus(1, AppTrackingServiceState.Running);
            Wait(() => !controller.IsRefreshing);
            check(controller.Current.StatusText == "Not installed" && !controller.CanUninstall,
                "A late pre-uninstall Running response cannot overwrite immediately verified Not installed state");
        }
        finally { service.CompletePendingStatuses(); Dispose(controller); }

        var usageService = new ControlledManagementService();
        var client = new ControlledUsageClient();
        var usage = new AppUsageTodayController(Dispatcher.CurrentDispatcher, client, _ => { }, setupService: usageService);
        try
        {
            usage.SelectRange(AppUsageRange.ThirtyDays);
            usage.SetActive(true);
            Wait(() => client.Calls == 1);
            usageService.State = AppTrackingServiceState.Missing;
            usage.ServiceUninstalled();
            check(usage.Current.State == AppUsageTodayState.Missing && usage.SelectedRange == AppUsageRange.ThirtyDays && client.Calls == 1,
                "Verified uninstall immediately clears an active loading page into setup without another IPC request");
            client.Complete(AppUsageRange.ThirtyDays);
            Wait(() => !usage.IsRefreshing);
            check(usage.Current.State == AppUsageTodayState.Missing && usage.Current.Applications.Count == 0,
                "Late application data from before uninstall cannot restore old summaries or rows over setup");
        }
        finally { client.Complete(AppUsageRange.ThirtyDays); WaitTask(usage.DisposeAsync().AsTask()); }

        var readOnly = new AppTrackingSettingsController(Dispatcher.CurrentDispatcher,
            new AppUsageSetupChecks.RunningSetupService(), _ => { },
            () => throw new InvalidOperationException("Read-only inspection must not prompt"));
        try
        {
            readOnly.SetActive(true);
            Wait(() => !readOnly.IsRefreshing && readOnly.Current.StatusText == "Running");
            WaitTask(readOnly.UninstallAsync());
            check(readOnly.Current.IsInstalled && !readOnly.CanUninstall && !readOnly.Current.CanUninstall,
                "A read-only injected status provider displays installed state without exposing unsupported uninstall");
        }
        finally { Dispose(readOnly); }
    }

    private static void CheckWindow(string root, Action<bool, string> check)
    {
        using var monitoring = HistoryOverviewChecks.CreateMonitoring(new UsageStore(System.IO.Path.Combine(root, "management-window")));
        var service = new ControlledManagementService();
        var client = new OwnedUsageClient();
        var confirms = 0;
        var allowConfirmation = false;
        using var tokenBefore = WindowsIdentity.GetCurrent();
        var elevatedBefore = new WindowsPrincipal(tokenBefore).IsInRole(WindowsBuiltInRole.Administrator);
        var window = new MainWindow(monitoring, new OwnedStartup(), appUsageClient: client, appTrackingSetupService: service,
            confirmAppTrackingUninstall: () => { confirms++; return allowConfirmation; })
            { ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Width = 760, Height = 600 };
        try
        {
            window.Show();
            HistoryOverviewChecks.StopViewTimer(window);
            Layout(window);
            check(service.StatusCalls == 0 && service.InstallCalls == 0 && service.UninstallCalls == 0 && confirms == 0 && client.Calls == 0,
                "The real NetTrace window launches no installation, uninstallation, confirmation or elevation at startup");
            SelectTab(window, "AppUsageTab");
            Wait(() => UsageController(window).Current.State == AppUsageTodayState.Available);
            Control<ToggleButton>(window, "AppUsage30DButton").SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Wait(() => !UsageController(window).IsRefreshing && client.LastRange == AppUsageRange.ThirtyDays);
            SelectTab(window, "SettingsTab");
            Wait(() => Text(window, "SettingsAppTrackingStatusText") == "Running");
            var button = Control<Button>(window, "SettingsAppTrackingUninstallButton");
            check(button.IsVisible && button.IsEnabled && button.Content?.ToString() == "Uninstall App Tracking",
                "Actual Settings displays Running and its explicit Uninstall App Tracking action");
            var hiddenUsageCalls = client.Calls;
            service.State = AppTrackingServiceState.Stopped;
            service.Notify();
            Wait(() => Text(window, "SettingsAppTrackingStatusText") == "Stopped");
            check(button.IsVisible && button.IsEnabled && client.Calls == hiddenUsageCalls,
                "Actual stopped-service Settings retains uninstall while hidden App Usage sends zero IPC requests");
            service.State = AppTrackingServiceState.Missing;
            service.Notify();
            Wait(() => Text(window, "SettingsAppTrackingStatusText") == "Not installed");
            check(!button.IsVisible && !button.IsEnabled,
                "Actual missing-service Settings hides and disables the uninstall action");
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            HistoryOverviewChecks.Pump();
            check(confirms == 0 && service.UninstallCalls == 0,
                "A routed click on unavailable uninstall cannot bypass missing-service availability");
            service.State = AppTrackingServiceState.Running;
            service.Notify();
            Wait(() => Text(window, "SettingsAppTrackingStatusText") == "Running");
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            HistoryOverviewChecks.Pump();
            check(confirms == 1 && service.UninstallCalls == 0 && button.IsEnabled,
                "Cancelling confirmation from the real Settings button does not launch UAC or change the service");
            allowConfirmation = true;
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Wait(() => service.UninstallCalls == 1);
            check(!button.IsEnabled && SettingsController(window).IsUninstalling && confirms == 2,
                "A confirmed real-button uninstall disables duplicate actions during its elevated wait");
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            HistoryOverviewChecks.Pump();
            check(confirms == 2 && service.UninstallCalls == 1,
                "Duplicate routed clicks cannot cause another confirmation or UAC launch");
            service.Complete(0, AppTrackingUninstallOutcome.Cancelled);
            Wait(() => !SettingsController(window).IsUninstalling && button.IsEnabled);
            check(Text(window, "SettingsAppTrackingStatusText") == "Running" &&
                Control<TextBlock>(window, "SettingsAppTrackingErrorText").IsVisible &&
                !string.IsNullOrWhiteSpace(Text(window, "SettingsAppTrackingErrorText")),
                "Cancelled UAC restores the real Settings action with visible short retry feedback");
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Wait(() => service.UninstallCalls == 2);
            service.Complete(1, AppTrackingUninstallOutcome.Failed);
            Wait(() => !SettingsController(window).IsUninstalling && button.IsEnabled);
            check(Control<TextBlock>(window, "SettingsAppTrackingErrorText").IsVisible && client.Calls == hiddenUsageCalls,
                "Failed uninstall permits retry without querying hidden App Usage or crashing the real window");
            Layout(window);
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(root, "app-tracking-settings-previews", "760x600-retry.png"));
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Wait(() => service.UninstallCalls == 3);
            service.State = AppTrackingServiceState.Missing;
            service.Complete(2, AppTrackingUninstallOutcome.Succeeded);
            Wait(() => Text(window, "SettingsAppTrackingStatusText") == "Not installed" && !SettingsController(window).IsUninstalling);
            check(!button.IsVisible && !Control<TextBlock>(window, "SettingsAppTrackingErrorText").IsVisible && client.Calls == hiddenUsageCalls,
                "Successful real-button uninstall immediately updates Settings to Not installed and keeps hidden App Usage silent");
            check(UsageController(window).Current.State == AppUsageTodayState.Missing &&
                UsageController(window).SelectedRange == AppUsageRange.ThirtyDays,
                "Successful Settings uninstall immediately clears cached hidden App Usage into setup before navigation");
            Layout(window);
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(root, "app-tracking-settings-previews", "760x600-not-installed.png"));
            SelectTab(window, "AppUsageTab");
            Wait(() => UsageController(window).Current.State == AppUsageTodayState.Missing);
            check(Text(window, "AppUsageStateText") == "App tracking is not installed." &&
                Control<Button>(window, "AppUsageInstallButton").IsVisible &&
                Control<ToggleButton>(window, "AppUsage30DButton").IsChecked == true && client.Calls == hiddenUsageCalls,
                "The same running NetTrace window shows existing setup after uninstall while preserving selected 30D");
            using var tokenAfter = WindowsIdentity.GetCurrent();
            check(tokenAfter.User?.Value == tokenBefore.User?.Value &&
                new WindowsPrincipal(tokenAfter).IsInRole(WindowsBuiltInRole.Administrator) == elevatedBefore,
                "The real window retains its original unelevated process token through all uninstall outcomes");
        }
        finally
        {
            window.Close();
            service.CompletePending();
            Wait(() => client.Disposed);
        }
    }

    private static void Refresh(AppTrackingSettingsController controller, OwnedClock clock, Func<bool> condition)
    {
        clock.Advance(TimeSpan.FromSeconds(4));
        controller.Tick();
        Wait(() => !controller.IsRefreshing && condition());
    }
    private static void Dispose(AppTrackingSettingsController controller) => WaitTask(controller.DisposeAsync().AsTask());
    private static T WaitTask<T>(Task<T> task) { Wait(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
    private static void WaitTask(Task task) { Wait(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Wait(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Owned App Tracking management condition was not ready.");
            HistoryOverviewChecks.Pump();
        }
        HistoryOverviewChecks.Pump();
    }
    private static T Control<T>(MainWindow window, string name) where T : class => window.FindName(name) as T ??
        throw new InvalidOperationException("Missing App Tracking Settings control " + name);
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

    private sealed class ControlledManagementService : IAppTrackingManagementService
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource<AppTrackingUninstallOutcome>> _uninstalls = [];
        private readonly List<TaskCompletionSource<AppTrackingServiceState>> _heldStatuses = [];
        private int _state = (int)AppTrackingServiceState.Running;
        private int _statusCalls;
        private int _holdStatusReads;
        public event EventHandler? StateChanged;
        public AppTrackingServiceState State { get => (AppTrackingServiceState)Volatile.Read(ref _state); set => Volatile.Write(ref _state, (int)value); }
        public int StatusCalls => Volatile.Read(ref _statusCalls);
        public int UninstallCalls { get { lock (_gate) return _uninstalls.Count; } }
        public int HeldStatusCalls { get { lock (_gate) return _heldStatuses.Count; } }
        public bool HoldStatusReads { get => Volatile.Read(ref _holdStatusReads) != 0; set => Volatile.Write(ref _holdStatusReads, value ? 1 : 0); }
        public int InstallCalls { get; private set; }
        public Task<AppTrackingServiceState> GetStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _statusCalls);
            if (HoldStatusReads)
            {
                var completion = new TaskCompletionSource<AppTrackingServiceState>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate) _heldStatuses.Add(completion);
                // Intentionally ignore cancellation to model a late native status read.
                return completion.Task;
            }
            return Task.FromResult(State);
        }
        public Task<AppTrackingInstallOutcome> InstallAsync(CancellationToken cancellationToken)
        {
            InstallCalls++;
            throw new InvalidOperationException("Settings must not install App Tracking.");
        }
        public Task<AppTrackingUninstallOutcome> UninstallAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource<AppTrackingUninstallOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _uninstalls.Add(completion);
            return completion.Task;
        }
        public void Complete(int request, AppTrackingUninstallOutcome outcome)
        {
            lock (_gate) _uninstalls[request].TrySetResult(outcome);
        }
        public void Notify() => StateChanged?.Invoke(this, EventArgs.Empty);
        public void CompleteStatus(int request, AppTrackingServiceState state)
        {
            lock (_gate) _heldStatuses[request].TrySetResult(state);
        }
        public void CompletePendingStatuses()
        {
            lock (_gate) foreach (var completion in _heldStatuses) completion.TrySetResult(AppTrackingServiceState.Unavailable);
        }
        public void CompletePending()
        {
            lock (_gate) foreach (var completion in _uninstalls) completion.TrySetResult(AppTrackingUninstallOutcome.Cancelled);
        }
    }
    private sealed class ControlledUsageClient : IAppUsageTodayClient
    {
        private readonly TaskCompletionSource<IpcResult<AppUsageQueryResult>> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task<IpcResult<AppUsageQueryResult>> GetAppUsageRangeAsync(AppUsageRange range, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return _completion.Task;
        }
        public void Complete(AppUsageRange range) => _completion.TrySetResult(AppUsageTodayChecks.Connected(
            AppUsageTodayChecks.Result([new("owned-stale", "Old application", "old.exe", null, 9_000, 1_000, 10_000, false, false)])
                with { Range = range }));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class OwnedUsageClient : IAppUsageTodayClient
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
            var result = AppUsageTodayChecks.Result([new("owned-management-" + range, "Owned management app", "owned.exe", null,
                9000, 1000, 10000, false, false)]) with { Range = range };
            return Task.FromResult(AppUsageTodayChecks.Connected(result));
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class ControlledUninstaller : IAppTrackingUninstallerLauncher
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource<int>> _launches = [];
        public int Calls { get { lock (_gate) return _launches.Count; } }
        public Task<int> LaunchAsync(AppTrackingUninstallLaunchSpec invocation)
        {
            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _launches.Add(completion);
            return completion.Task;
        }
        public bool IsCompleted(int request) { lock (_gate) return _launches[request].Task.IsCompleted; }
        public void Complete(int request, int code) { lock (_gate) _launches[request].TrySetResult(code); }
        public void Fail(int request, Exception exception) { lock (_gate) _launches[request].TrySetException(exception); }
    }
    private sealed class ControlledInstaller : IAppTrackingInstallerLauncher
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource<int>> _launches = [];
        public int Calls { get { lock (_gate) return _launches.Count; } }
        public Task<int> LaunchAsync(AppTrackingInstallLaunchSpec invocation)
        {
            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _launches.Add(completion);
            return completion.Task;
        }
        public void Complete(int code) { lock (_gate) _launches[^1].TrySetResult(code); }
    }
    private sealed class OwnedStateReader : IAppTrackingServiceStateReader
    {
        public AppTrackingServiceState State { get; set; } = AppTrackingServiceState.Running;
        public AppTrackingServiceState GetState() => State;
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
        public void Enable() => throw new InvalidOperationException("Tracking management must not change startup registration.");
        public void Disable() => throw new InvalidOperationException("Tracking management must not change startup registration.");
    }
}
