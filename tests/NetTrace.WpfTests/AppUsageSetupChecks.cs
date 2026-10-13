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

internal static class AppUsageSetupChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string OwnedClientSid = "S-1-5-21-101-202-303-1001";

    public static void Run(string root, Action<bool, string> check)
    {
        CheckPlatform(root, check);
        CheckController(check);
        CheckInstallFailures(check);
        CheckHiddenCompletion(check);
        CheckWindow(root, check);
    }

    private static void CheckPlatform(string root, Action<bool, string> check)
    {
        var appDirectory = System.IO.Path.Combine(root, "owned application with spaces");
        var invocation = AppTrackingInstallLaunchSpec.Create(appDirectory, OwnedClientSid);
        var launch = invocation.CreateStartInfo();
        check(launch.FileName == System.IO.Path.Combine(appDirectory, "AppTracking", "Installer", "NetTrace.AppUsageInstaller.exe") &&
            invocation.ServiceDirectory == System.IO.Path.Combine(appDirectory, "AppTracking", "Service"),
            "Setup launches the existing bundled installer and collector from separate fixed tool directories");
        check(launch.UseShellExecute && launch.Verb == "runas" && launch.WindowStyle == ProcessWindowStyle.Hidden &&
            launch.FileName != Environment.ProcessPath,
            "Explicit setup elevates only the installer through runas without relaunching NetTrace");
        check(launch.ArgumentList.SequenceEqual(new[] { "install", "--source", invocation.ServiceDirectory, "--client-sid", OwnedClientSid }),
            "The elevated invocation preserves paths with spaces and uses only the existing install command");
        check(!invocation.HasRequiredFiles(path => !path.EndsWith("NetTrace.AppUsageService.exe", StringComparison.Ordinal)) &&
            invocation.HasRequiredFiles(_ => true),
            "Setup refuses an incomplete tool bundle before requesting elevation");
        var invalidPathRejected = false;
        var invalidSidRejected = false;
        try { _ = AppTrackingInstallLaunchSpec.Create("relative-directory", OwnedClientSid); }
        catch (ArgumentException) { invalidPathRejected = true; }
        try { _ = AppTrackingInstallLaunchSpec.Create(appDirectory, "S-1-5-18"); }
        catch (ArgumentException) { invalidSidRejected = true; }
        check(invalidPathRejected && invalidSidRejected, "Setup rejects a relative tool root or non-user identity before launching anything");

        var reader = new OwnedStateReader();
        var launcher = new ControlledLauncher();
        var setup = new AppTrackingSetupService(launcher, reader, appDirectory, OwnedClientSid, _ => true);
        foreach (var state in new[] { AppTrackingServiceState.Missing, AppTrackingServiceState.Running,
            AppTrackingServiceState.Stopped, AppTrackingServiceState.Unavailable })
        {
            reader.State = state;
            var status = WaitTask(setup.GetStateAsync(CancellationToken.None));
            check(status == state && launcher.Calls == 0,
                $"Reading {state} installation state never automatically launches the installer");
        }
        reader.Throw = true;
        check(WaitTask(setup.GetStateAsync(CancellationToken.None)) == AppTrackingServiceState.Unavailable && launcher.Calls == 0,
            "A failed service status inspection stays unavailable without offering automatic elevation");
        reader.Throw = false;
        using var identity = WindowsIdentity.GetCurrent();
        var elevationBefore = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        var first = setup.InstallAsync(CancellationToken.None);
        Wait(() => launcher.Calls == 1);
        var second = setup.InstallAsync(CancellationToken.None);
        check(launcher.Calls == 1 && !first.IsCompleted && !second.IsCompleted &&
            WaitTask(setup.GetStateAsync(CancellationToken.None)) == AppTrackingServiceState.Installing,
            "Concurrent setup waiters share one explicit installer launch and report installation in progress");
        var dispatched = false;
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => dispatched = true));
        Wait(() => dispatched);
        check(dispatched && !first.IsCompleted, "The dispatcher remains responsive while waiting for the elevated installer");
        launcher.Complete(0, 0);
        check(WaitTask(first) == AppTrackingInstallOutcome.Succeeded && WaitTask(second) == AppTrackingInstallOutcome.Succeeded,
            "A successful existing installer exit completes all setup waiters successfully");
        using var after = WindowsIdentity.GetCurrent();
        check(after.User?.Value == identity.User?.Value &&
            new WindowsPrincipal(after).IsInRole(WindowsBuiltInRole.Administrator) == elevationBefore,
            "The desktop process keeps its original identity and elevation token while the installer is separate");

        using var waiterCancellation = new CancellationTokenSource();
        var cancelledWaiter = setup.InstallAsync(waiterCancellation.Token);
        Wait(() => launcher.Calls == 2);
        waiterCancellation.Cancel();
        Wait(() => cancelledWaiter.IsCompleted);
        check(cancelledWaiter.IsCanceled && !launcher.IsCompleted(1) &&
            WaitTask(setup.GetStateAsync(CancellationToken.None)) == AppTrackingServiceState.Installing,
            "Cancelling a window's wait leaves its already launched installer alive to finish safely");
        var retainedWaiter = setup.InstallAsync(CancellationToken.None);
        check(launcher.Calls == 2, "A new view joins the already running installation instead of launching a duplicate UAC prompt");
        launcher.Complete(1, 0);
        check(WaitTask(retainedWaiter) == AppTrackingInstallOutcome.Succeeded, "A retained install waiter receives the existing installer's final result");

        foreach (var failure in new[] { "cancelled", "nonzero", "exception" })
        {
            var request = launcher.Calls;
            var attempt = setup.InstallAsync(CancellationToken.None);
            Wait(() => launcher.Calls == request + 1);
            if (failure == "cancelled") launcher.Fail(request, new Win32Exception(1223));
            else if (failure == "nonzero") launcher.Complete(request, 8);
            else launcher.Fail(request, new InvalidOperationException("private internal details"));
            check(WaitTask(attempt) == (failure == "cancelled" ? AppTrackingInstallOutcome.Cancelled : AppTrackingInstallOutcome.Failed),
                $"Installer {failure} maps to a safe typed setup outcome");
        }
        var missingLauncher = new ControlledLauncher();
        var missingKit = new AppTrackingSetupService(missingLauncher, reader, appDirectory, OwnedClientSid, _ => false);
        check(WaitTask(missingKit.InstallAsync(CancellationToken.None)) == AppTrackingInstallOutcome.FilesMissing && missingLauncher.Calls == 0,
            "Missing bundled installer files return a setup failure without a UAC prompt");
        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        var cancellationRejected = false;
        try { _ = missingKit.InstallAsync(alreadyCancelled.Token); }
        catch (OperationCanceledException) { cancellationRejected = true; }
        check(cancellationRejected && missingLauncher.Calls == 0, "An already cancelled setup invocation never starts an installer");
    }

    private static void CheckController(Action<bool, string> check)
    {
        var setup = new ControlledSetupService();
        var client = new ImmediateClient();
        var clock = new OwnedClock();
        var applied = new List<AppUsageTodayPresentation>();
        var uiThread = Environment.CurrentManagedThreadId;
        var applyThreads = new List<int>();
        var controller = new AppUsageTodayController(Dispatcher.CurrentDispatcher, client,
            state => { applied.Add(state); applyThreads.Add(Environment.CurrentManagedThreadId); }, clock, setupService: setup);
        try
        {
            controller.Tick();
            controller.SelectRange(AppUsageRange.SevenDays);
            WaitTask(controller.InstallAsync());
            check(setup.StatusCalls == 0 && setup.InstallCalls == 0 && client.Calls == 0 && applied.Count == 0,
                "Startup and inactive range or install attempts neither inspect nor install nor query App Tracking");
            controller.SetActive(true);
            Wait(() => !controller.IsRefreshing && controller.Current.State == AppUsageTodayState.Missing);
            check(controller.Current.Message == "App tracking is not installed." &&
                controller.Current.Detail == "Track network usage by application in the background." && controller.CanInstall &&
                controller.Current.Applications.Count == 0 && client.Calls == 0 && setup.InstallCalls == 0,
                "Missing production service supplies exact setup copy and an explicit install action without querying IPC");
            var statuses = setup.StatusCalls;
            controller.Tick();
            check(setup.StatusCalls == statuses && setup.InstallCalls == 0,
                "The setup page preserves its four-second schedule and never launches installation on a timer");
            clock.Advance(TimeSpan.FromSeconds(4));
            controller.Tick();
            Wait(() => setup.StatusCalls == statuses + 1 && !controller.IsRefreshing);
            check(controller.Current.State == AppUsageTodayState.Missing && client.Calls == 0 && setup.InstallCalls == 0,
                "Periodic missing-service inspection leaves setup available without installation or IPC traffic");
            setup.State = AppTrackingServiceState.Stopped;
            Refresh(controller, clock, () => controller.Current.State == AppUsageTodayState.Stopped);
            check(controller.Current.Message == "App tracking is stopped." && !controller.CanInstall && client.Calls == 0,
                "An installed stopped collector produces concise stopped state rather than offering a duplicate installation");
            WaitTask(controller.InstallAsync());
            check(setup.InstallCalls == 0, "The stopped state ignores an attempted install action");
            setup.State = AppTrackingServiceState.Running;
            Refresh(controller, clock, () => controller.Current.State == AppUsageTodayState.Available);
            check(client.LastRange == AppUsageRange.SevenDays && controller.Current.Applications.Count == 1 && !controller.CanInstall,
                "A running production collector retains the existing selected-range summary and application behavior");
            setup.State = AppTrackingServiceState.Unavailable;
            Refresh(controller, clock, () => controller.Current.State == AppUsageTodayState.Unavailable);
            check(!controller.CanInstall && client.Calls == 1 && controller.Current.Applications.Count == 0,
                "An unreadable service state hides setup and old application data instead of assuming the service is missing");
            setup.State = AppTrackingServiceState.Missing;
            Refresh(controller, clock, () => controller.Current.State == AppUsageTodayState.Missing);
            controller.SelectRange(AppUsageRange.ThirtyDays);
            Wait(() => !controller.IsRefreshing && controller.Current.State == AppUsageTodayState.Missing);
            var install = controller.InstallAsync();
            Wait(() => setup.InstallCalls == 1);
            check(controller.IsInstalling && !controller.CanInstall && controller.Current.State == AppUsageTodayState.Installing &&
                !install.IsCompleted && controller.SelectedRange == AppUsageRange.ThirtyDays,
                "Explicit installation enters a disabled in-progress state without losing the selected 30D range");
            var duplicate = controller.InstallAsync();
            var callsAtInstall = client.Calls;
            var statusesAtInstall = setup.StatusCalls;
            clock.Advance(TimeSpan.FromMinutes(1));
            for (var index = 0; index < 20; index++) controller.Tick();
            controller.SelectRange(AppUsageRange.SevenDays);
            controller.SelectRange(AppUsageRange.ThirtyDays);
            check(setup.InstallCalls == 1 && client.Calls == callsAtInstall && setup.StatusCalls == statusesAtInstall &&
                controller.SelectedRange == AppUsageRange.ThirtyDays,
                "Repeated install clicks, timer ticks and range choices share installation without polling or duplicate elevation");
            setup.State = AppTrackingServiceState.Running;
            setup.Complete(0, AppTrackingInstallOutcome.Succeeded);
            Wait(() => install.IsCompleted && duplicate.IsCompleted && !controller.IsInstalling &&
                controller.Current.State == AppUsageTodayState.Available);
            install.GetAwaiter().GetResult();
            duplicate.GetAwaiter().GetResult();
            check(client.Calls == callsAtInstall + 1 && client.LastRange == AppUsageRange.ThirtyDays &&
                controller.Current.Applications.Single().ApplicationIdentity == "owned-ThirtyDays",
                "Successful installation immediately reconnects once and loads the latest retained 30D range");
            check(applyThreads.All(thread => thread == uiThread), "Setup inspection and installation publish UI state only on the captured dispatcher");
            clock.Advance(TimeSpan.FromSeconds(4));
            controller.Tick();
            Wait(() => client.Calls == callsAtInstall + 2 && !controller.IsRefreshing);
            check(client.LastRange == AppUsageRange.ThirtyDays && setup.InstallCalls == 1,
                "The existing periodic refresh resumes for the retained range without re-running the installer");
        }
        finally { Dispose(controller); }
    }

    private static void CheckInstallFailures(Action<bool, string> check)
    {
        foreach (var outcome in new[] { AppTrackingInstallOutcome.Cancelled, AppTrackingInstallOutcome.Failed, AppTrackingInstallOutcome.FilesMissing })
        {
            var setup = new ControlledSetupService();
            var client = new ImmediateClient();
            var clock = new OwnedClock();
            var controller = new AppUsageTodayController(Dispatcher.CurrentDispatcher, client, _ => { }, clock, setupService: setup);
            try
            {
                controller.SelectRange(AppUsageRange.ThirtyDays);
                controller.SetActive(true);
                Wait(() => !controller.IsRefreshing && controller.Current.State == AppUsageTodayState.Missing);
                var install = controller.InstallAsync();
                Wait(() => setup.InstallCalls == 1);
                setup.Complete(0, outcome);
                Wait(() => install.IsCompleted && !controller.IsInstalling);
                install.GetAwaiter().GetResult();
                check(controller.Current.State == AppUsageTodayState.Missing && controller.CanInstall &&
                    controller.Current.Message == "App tracking is not installed." && !string.IsNullOrWhiteSpace(controller.Current.SetupError) &&
                    controller.SelectedRange == AppUsageRange.ThirtyDays && client.Calls == 0,
                    $"{outcome} returns to retryable setup with a short error and retains 30D without crashing or querying IPC");
                var error = controller.Current.SetupError;
                Refresh(controller, clock, () => controller.Current.State == AppUsageTodayState.Missing);
                check(controller.Current.SetupError == error && setup.InstallCalls == 1,
                    $"{outcome} feedback survives the next missing-service refresh without automatically retrying UAC");
                var retry = controller.InstallAsync();
                Wait(() => setup.InstallCalls == 2);
                setup.State = AppTrackingServiceState.Running;
                setup.Complete(1, AppTrackingInstallOutcome.Succeeded);
                Wait(() => retry.IsCompleted && controller.Current.State == AppUsageTodayState.Available);
                retry.GetAwaiter().GetResult();
                check(controller.Current.SetupError == "" && client.LastRange == AppUsageRange.ThirtyDays,
                    $"A deliberate retry after {outcome} clears feedback and loads the preserved selected range");
            }
            finally { Dispose(controller); }
        }
    }

    private static void CheckHiddenCompletion(Action<bool, string> check)
    {
        var setup = new ControlledSetupService();
        var client = new ImmediateClient();
        var clock = new OwnedClock();
        var applied = new List<AppUsageTodayPresentation>();
        var controller = new AppUsageTodayController(Dispatcher.CurrentDispatcher, client, applied.Add, clock, setupService: setup);
        try
        {
            controller.SetActive(true);
            Wait(() => !controller.IsRefreshing && controller.Current.State == AppUsageTodayState.Missing);
            controller.SelectRange(AppUsageRange.ThirtyDays);
            Wait(() => !controller.IsRefreshing);
            var install = controller.InstallAsync();
            Wait(() => setup.InstallCalls == 1);
            controller.SetActive(false);
            var appliedAtHide = applied.Count;
            var statusesAtHide = setup.StatusCalls;
            clock.Advance(TimeSpan.FromHours(1));
            for (var index = 0; index < 20; index++) controller.Tick();
            setup.State = AppTrackingServiceState.Running;
            setup.Complete(0, AppTrackingInstallOutcome.Succeeded);
            Wait(() => install.IsCompleted);
            install.GetAwaiter().GetResult();
            check(client.Calls == 0 && setup.StatusCalls == statusesAtHide && applied.Count == appliedAtHide,
                "Installation finishing on a hidden page performs zero IPC queries, status polls or presentation updates");
            controller.SelectRange(AppUsageRange.SevenDays);
            controller.Tick();
            WaitTask(controller.InstallAsync());
            check(setup.InstallCalls == 1 && client.Calls == 0 && applied.Count == appliedAtHide,
                "Hidden range changes and explicit install attempts retain selection without starting work or elevation");
            controller.SetActive(true);
            Wait(() => !controller.IsRefreshing && controller.Current.State == AppUsageTodayState.Available);
            check(client.Calls == 1 && client.LastRange == AppUsageRange.SevenDays,
                "Returning after a hidden installation immediately loads the latest retained 7D selection");
        }
        finally { Dispose(controller); }

        var closingSetup = new ControlledSetupService();
        var closingClient = new ImmediateClient();
        var closingApplies = 0;
        var closingController = new AppUsageTodayController(Dispatcher.CurrentDispatcher, closingClient, _ => closingApplies++, setupService: closingSetup);
        closingController.SetActive(true);
        Wait(() => !closingController.IsRefreshing && closingController.Current.State == AppUsageTodayState.Missing);
        var closingInstall = closingController.InstallAsync();
        Wait(() => closingSetup.InstallCalls == 1);
        var applyAtClose = closingApplies;
        var disposal = closingController.DisposeAsync().AsTask();
        closingSetup.State = AppTrackingServiceState.Running;
        closingSetup.Complete(0, AppTrackingInstallOutcome.Succeeded);
        Wait(() => disposal.IsCompleted && closingInstall.IsCompleted);
        disposal.GetAwaiter().GetResult();
        closingInstall.GetAwaiter().GetResult();
        check(closingClient.Disposed && closingClient.Calls == 0 && closingApplies == applyAtClose,
            "Closing during installation releases the UI client and ignores a late successful result");
    }

    private static void CheckWindow(string root, Action<bool, string> check)
    {
        using var monitoring = HistoryOverviewChecks.CreateMonitoring(new UsageStore(System.IO.Path.Combine(root, "setup-window")));
        var setup = new ControlledSetupService();
        var client = new ImmediateClient();
        var window = new MainWindow(monitoring, new OwnedStartup(), appUsageClient: client, appTrackingSetupService: setup)
            { ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Width = 760, Height = 600 };
        try
        {
            window.Show();
            HistoryOverviewChecks.StopViewTimer(window);
            Layout(window);
            check(setup.StatusCalls == 0 && setup.InstallCalls == 0 && client.Calls == 0,
                "The actual unelevated NetTrace window does not inspect, install or elevate App Tracking on startup");
            SelectTab(window, "AppUsageTab");
            Wait(() => Text(window, "AppUsageStateText") == "App tracking is not installed.");
            Layout(window);
            var button = Control<Button>(window, "AppUsageInstallButton");
            check(button.IsVisible && button.IsEnabled && button.Content?.ToString() == "Install App Tracking" &&
                Text(window, "AppUsageDetailText") == "Track network usage by application in the background.",
                "The actual missing-service page shows the requested setup copy and install button");
            check(Control<FrameworkElement>(window, "AppUsageSummaryPanel").Visibility == Visibility.Collapsed &&
                Control<FrameworkElement>(window, "AppUsageApplicationsPanel").Visibility == Visibility.Collapsed,
                "Missing-service setup hides the old summary and application list");
            var scroll = Control<ScrollViewer>(window, "AppUsageScrollViewer");
            var position = button.TranslatePoint(new Point(), scroll);
            check(scroll.ScrollableWidth < 0.01 && button.ActualWidth > 0 && position.X >= -0.5 &&
                position.X + button.ActualWidth <= scroll.ViewportWidth + 0.5,
                "The setup action fits at 760x600 without horizontal overflow");
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(root, "app-usage-setup-previews", "760x600-missing.png"));
            Control<ToggleButton>(window, "AppUsage30DButton").SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Wait(() => !Controller(window).IsRefreshing && Controller(window).Current.State == AppUsageTodayState.Missing);
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Wait(() => setup.InstallCalls == 1);
            check(!button.IsEnabled && Control<ToggleButton>(window, "AppUsage30DButton").IsChecked == true &&
                Controller(window).IsInstalling && client.Calls == 0,
                "Clicking the real install action disables repeated installation and keeps the selected 30D range");
            setup.Complete(0, AppTrackingInstallOutcome.Cancelled);
            Wait(() => !Controller(window).IsInstalling && button.IsEnabled);
            check(Text(window, "AppUsageStateText") == "App tracking is not installed." &&
                Control<TextBlock>(window, "AppUsageSetupErrorText").IsVisible &&
                !string.IsNullOrWhiteSpace(Text(window, "AppUsageSetupErrorText")),
                "Cancelling UAC on the real page restores setup and visible concise retry feedback");
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(root, "app-usage-setup-previews", "760x600-cancelled.png"));
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Wait(() => setup.InstallCalls == 2);
            setup.State = AppTrackingServiceState.Running;
            setup.Complete(1, AppTrackingInstallOutcome.Succeeded);
            Wait(() => Controller(window).Current.State == AppUsageTodayState.Available);
            check(!button.IsVisible && !Control<TextBlock>(window, "AppUsageSetupErrorText").IsVisible &&
                client.Calls == 1 && client.LastRange == AppUsageRange.ThirtyDays &&
                Control<ItemsControl>(window, "AppUsageApplications").Items.Count == 1,
                "The real page automatically reconnects selected 30D after installation and restores its normal application list");
            SelectTab(window, "DashboardTab");
            var hiddenStatusCalls = setup.StatusCalls;
            for (var index = 0; index < 20; index++) Controller(window).Tick();
            check(setup.StatusCalls == hiddenStatusCalls && client.Calls == 1 && setup.InstallCalls == 2,
                "The actual hidden App Usage page performs zero status, refresh or installer work");
            setup.State = AppTrackingServiceState.Stopped;
            SelectTab(window, "AppUsageTab");
            Wait(() => Controller(window).Current.State == AppUsageTodayState.Stopped);
            check(Text(window, "AppUsageStateText") == "App tracking is stopped." && !button.IsVisible &&
                Control<ToggleButton>(window, "AppUsage30DButton").IsChecked == true,
                "The real stopped-service state is concise and retains 30D without an install action");
            window.Hide();
            window.Show();
            HistoryOverviewChecks.StopViewTimer(window);
            Wait(() => !Controller(window).IsRefreshing);
            check(setup.InstallCalls == 2, "Hiding and restoring the window never automatically reruns App Tracking installation");
        }
        finally
        {
            window.Close();
            setup.CompletePending();
            Wait(() => client.Disposed);
        }
    }

    private static void Refresh(AppUsageTodayController controller, OwnedClock clock, Func<bool> condition)
    {
        clock.Advance(TimeSpan.FromSeconds(4));
        controller.Tick();
        Wait(() => !controller.IsRefreshing && condition());
    }
    private static void Dispose(AppUsageTodayController controller) => WaitTask(controller.DisposeAsync().AsTask());
    private static T WaitTask<T>(Task<T> task) { Wait(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
    private static void WaitTask(Task task) { Wait(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Wait(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Owned App Tracking setup condition was not ready.");
            HistoryOverviewChecks.Pump();
        }
        HistoryOverviewChecks.Pump();
    }
    private static T Control<T>(MainWindow window, string name) where T : class => window.FindName(name) as T ??
        throw new InvalidOperationException("Missing App Tracking setup control " + name);
    private static string Text(MainWindow window, string name) => Control<TextBlock>(window, name).Text;
    private static AppUsageTodayController Controller(MainWindow window) => (AppUsageTodayController)typeof(MainWindow)
        .GetField("_appUsageToday", PrivateInstance)!.GetValue(window)!;
    private static void SelectTab(MainWindow window, string name)
    {
        Control<TabControl>(window, "MainNavigation").SelectedItem = Control<TabItem>(window, name);
        Layout(window);
    }
    private static void Layout(MainWindow window) { window.UpdateLayout(); HistoryOverviewChecks.Pump(); window.UpdateLayout(); }

    internal sealed class RunningSetupService : IAppTrackingSetupService
    {
        public Task<AppTrackingServiceState> GetStateAsync(CancellationToken cancellationToken) => Task.FromResult(AppTrackingServiceState.Running);
        public Task<AppTrackingInstallOutcome> InstallAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Existing App Usage tests must never launch an installer.");
    }

    private sealed class ControlledSetupService : IAppTrackingSetupService
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource<AppTrackingInstallOutcome>> _installations = [];
        private int _state = (int)AppTrackingServiceState.Missing;
        private int _statusCalls;
        public AppTrackingServiceState State { get => (AppTrackingServiceState)Volatile.Read(ref _state); set => Volatile.Write(ref _state, (int)value); }
        public int StatusCalls => Volatile.Read(ref _statusCalls);
        public int InstallCalls { get { lock (_gate) return _installations.Count; } }
        public Task<AppTrackingServiceState> GetStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _statusCalls);
            return Task.FromResult(State);
        }
        public Task<AppTrackingInstallOutcome> InstallAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource<AppTrackingInstallOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _installations.Add(completion);
            return completion.Task;
        }
        public void Complete(int request, AppTrackingInstallOutcome result) { lock (_gate) _installations[request].TrySetResult(result); }
        public void CompletePending() { lock (_gate) foreach (var install in _installations) install.TrySetResult(AppTrackingInstallOutcome.Cancelled); }
    }

    private sealed class ImmediateClient : IAppUsageTodayClient
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
            var result = AppUsageTodayChecks.Result([new("owned-" + range, "Owned setup application", "owned.exe", null, 9000, 1000, 10000, false, false)]) with { Range = range };
            return Task.FromResult(AppUsageTodayChecks.Connected(result));
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class ControlledLauncher : IAppTrackingInstallerLauncher
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
        public bool IsCompleted(int request) { lock (_gate) return _launches[request].Task.IsCompleted; }
        public void Complete(int request, int exitCode) { lock (_gate) _launches[request].TrySetResult(exitCode); }
        public void Fail(int request, Exception exception) { lock (_gate) _launches[request].TrySetException(exception); }
    }
    private sealed class OwnedStateReader : IAppTrackingServiceStateReader
    {
        public AppTrackingServiceState State { get; set; } = AppTrackingServiceState.Missing;
        public bool Throw { get; set; }
        public AppTrackingServiceState GetState() => Throw ? throw new InvalidOperationException("Owned state reader failure") : State;
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
        public void Enable() => throw new InvalidOperationException("App Tracking setup must not change startup registration.");
        public void Disable() => throw new InvalidOperationException("App Tracking setup must not change startup registration.");
    }
}
