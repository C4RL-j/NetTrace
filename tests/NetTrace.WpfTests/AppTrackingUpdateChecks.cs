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

internal static class AppTrackingUpdateChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string ClientSid = "S-1-5-21-101-202-303-1001";
    private const string OwnershipDescription = "NetTrace App Usage production collector; installation format 005.1.";
    private static readonly string OldVersion = new('a', 64);
    private static readonly string NewVersion = new('b', 64);

    public static void Run(string root, Action<bool, string> check)
    {
        CheckVersions(root, check);
        CheckPlatform(root, check);
        CheckController(check);
        CheckAlreadyCurrentRace(check);
        CheckFailures(check);
        CheckReconnect(check);
        CheckStaleResponse(check);
        CheckClosedCompletion(check);
        CheckWindow(root, check);
    }

    private static void CheckVersions(string root, Action<bool, string> check)
    {
        var inventory = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NetTrace.AppUsageService.dll"] = new('1', 64),
            ["NetTrace.AppUsageService.exe"] = new('2', 64),
            ["NetTrace.AppUsageService.pdb"] = new('3', 64),
            ["runtimes\\win-x64\\native\\e_sqlite3.dll"] = new('4', 64)
        };
        var version = CollectorPackageVersion.Compute(inventory);
        var reordered = inventory.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        check(version.Length == 64 && version.All(Uri.IsHexDigit) && CollectorPackageVersion.Compute(reordered) == version,
            "Collector package versions are stable SHA-256 identities independent of inventory enumeration order");
        reordered["NetTrace.AppUsageService.dll"] = new('5', 64);
        check(CollectorPackageVersion.Compute(reordered) != version,
            "Changed collector content produces a new package version even when assembly version numbers are unchanged");
        reordered = new(inventory, StringComparer.Ordinal);
        reordered.Remove("NetTrace.AppUsageService.pdb");
        check(CollectorPackageVersion.Compute(reordered) != version,
            "Version comparison includes the full installer binary inventory, including its existing PDB entries");
        reordered = new(inventory, StringComparer.Ordinal);
        var native = reordered["runtimes\\win-x64\\native\\e_sqlite3.dll"];
        reordered.Remove("runtimes\\win-x64\\native\\e_sqlite3.dll");
        reordered["runtimes\\win-arm64\\native\\e_sqlite3.dll"] = native;
        check(CollectorPackageVersion.Compute(reordered) != version,
            "Package version identity includes native dependency relative paths as well as file content");

        var programFiles = System.IO.Path.Combine(root, "owned program files");
        var commonData = System.IO.Path.Combine(root, "owned program data");
        var image = System.IO.Path.Combine(programFiles, "NetTrace", "AppUsageCollector", "Versions", OldVersion,
            "NetTrace.AppUsageService.exe");
        var log = System.IO.Path.Combine(commonData, "NetTrace", "AppUsageService", "service.log");
        var command = $"\"{image}\" --client-sid {ClientSid} --service-name NetTraceAppUsageCollector --log \"{log}\"";
        check(WindowsAppTrackingUpdateVersionReader.TryReadInstalledVersion(command, "LocalSystem", OwnershipDescription,
                programFiles, commonData, out var installed) && installed == OldVersion,
            "A read-only owned SCM registration exposes its protected installed package version without opening ProgramData storage");
        foreach (var entry in new[]
        {
            (command, "LocalService", OwnershipDescription),
            (command, "LocalSystem", "Unowned service"),
            (command.Replace(image, System.IO.Path.Combine(root, "external", OldVersion, "NetTrace.AppUsageService.exe"), StringComparison.Ordinal), "LocalSystem", OwnershipDescription),
            (command.Replace(OldVersion, "not-a-version", StringComparison.Ordinal), "LocalSystem", OwnershipDescription),
            (command.Replace("--service-name NetTraceAppUsageCollector", "--service-name OtherService", StringComparison.Ordinal), "LocalSystem", OwnershipDescription),
            (command + " --unexpected", "LocalSystem", OwnershipDescription),
            (command.Replace(ClientSid, "S-1-5-18", StringComparison.Ordinal), "LocalSystem", OwnershipDescription),
            (command.Replace(log, System.IO.Path.Combine(root, "elsewhere", "service.log"), StringComparison.Ordinal), "LocalSystem", OwnershipDescription)
        })
        {
            check(!WindowsAppTrackingUpdateVersionReader.TryReadInstalledVersion(entry.Item1, entry.Item2, entry.Item3,
                    programFiles, commonData, out _),
                "Update version detection rejects an unowned or malformed SCM identity, image layout or command line");
        }
    }

    private static void CheckPlatform(string root, Action<bool, string> check)
    {
        var appDirectory = System.IO.Path.Combine(root, "owned update application with spaces");
        var versionReader = new OwnedVersionReader();
        var stateReader = new OwnedStateReader();
        var installer = new ControlledInstaller();
        var remover = new GuardUninstaller();
        var clearer = new GuardClearer();
        var service = new AppTrackingSetupService(installer, stateReader, appDirectory, ClientSid, _ => true,
            uninstallerLauncher: remover, historyLauncher: clearer, storageReader: new OwnedStorageReader(),
            versionReader: versionReader);
        check(installer.Calls == 0 && versionReader.Calls == 0,
            "Constructing tracking management neither reads installed versions nor automatically launches an update");
        foreach (var availability in Enum.GetValues<AppTrackingUpdateAvailability>())
        {
            versionReader.Status = new(availability, availability == AppTrackingUpdateAvailability.NotInstalled ? null : OldVersion, NewVersion);
            check(WaitTask(service.GetUpdateStatusAsync(CancellationToken.None)).Availability == availability && installer.Calls == 0,
                $"Read-only {availability} update inspection never requests elevation or changes the running collector");
        }
        versionReader.Status = new(AppTrackingUpdateAvailability.Current, NewVersion, NewVersion);
        check(WaitTask(service.UpdateAsync(CancellationToken.None)) == AppTrackingUpdateOutcome.Succeeded && installer.Calls == 0,
            "An explicit update with matching installed and bundled versions completes without launching the installer");
        versionReader.Status = new(AppTrackingUpdateAvailability.NotInstalled, null, NewVersion);
        check(WaitTask(service.UpdateAsync(CancellationToken.None)) == AppTrackingUpdateOutcome.NotInstalled && installer.Calls == 0,
            "Updating a service removed since inspection does not silently turn into installation");
        versionReader.Status = new(AppTrackingUpdateAvailability.Unavailable);
        check(WaitTask(service.UpdateAsync(CancellationToken.None)) == AppTrackingUpdateOutcome.Failed && installer.Calls == 0,
            "An unreadable or unowned registration cannot launch an elevated repair by guessing its version");
        versionReader.Status = new(AppTrackingUpdateAvailability.UpdateAvailable, OldVersion, NewVersion);
        var states = new List<AppTrackingServiceState>();
        service.StateChanged += (_, _) => throw new InvalidOperationException("owned failing update observer");
        service.StateChanged += (_, _) => states.Add(service.GetStateAsync(CancellationToken.None).GetAwaiter().GetResult());
        using var before = WindowsIdentity.GetCurrent();
        var elevatedBefore = new WindowsPrincipal(before).IsInRole(WindowsBuiltInRole.Administrator);
        var first = service.UpdateAsync(CancellationToken.None);
        Wait(() => installer.Calls == 1);
        var second = service.UpdateAsync(CancellationToken.None);
        check(installer.Calls == 1 && !first.IsCompleted && !second.IsCompleted &&
            WaitTask(service.GetStateAsync(CancellationToken.None)) == AppTrackingServiceState.Updating,
            "Concurrent explicit update waiters share one installer and expose update-in-progress state");
        var invocation = installer.Invocation(0);
        var start = invocation.CreateStartInfo();
        check(start.FileName == System.IO.Path.Combine(appDirectory, "AppTracking", "Installer", "NetTrace.AppUsageInstaller.exe") &&
            start.ArgumentList.SequenceEqual(new[] { "install", "--source", System.IO.Path.Combine(appDirectory, "AppTracking", "Service"), "--client-sid", ClientSid }),
            "Update reuses the existing production install/update command with its fixed bundled source and intended normal-client SID");
        check(start.UseShellExecute && start.Verb == "runas" && start.WindowStyle == ProcessWindowStyle.Hidden &&
            start.FileName != Environment.ProcessPath,
            "Only the explicit existing installer child requests UAC; NetTrace is never relaunched elevated");
        check(versionReader.RefreshedCalls > 0,
            "Explicit update refreshes bundled version inspection before installing rather than relying on a stale display cache");
        check(WaitTask(service.InstallAsync(CancellationToken.None)) == AppTrackingInstallOutcome.Failed &&
            WaitTask(service.UninstallAsync(CancellationToken.None)) == AppTrackingUninstallOutcome.Failed &&
            WaitTask(service.ClearHistoryAsync(CancellationToken.None)) == AppUsageHistoryClearOutcome.Failed &&
            remover.Calls == 0 && clearer.Calls == 0 && installer.Calls == 1,
            "Update excludes concurrent install, uninstall and clear operations so no second privileged child or history deletion starts");
        var responsive = false;
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => responsive = true));
        Wait(() => responsive);
        check(responsive && !first.IsCompleted, "Waiting for the elevated update leaves the NetTrace dispatcher responsive");
        versionReader.Status = new(AppTrackingUpdateAvailability.Current, NewVersion, NewVersion);
        installer.Complete(0, 0);
        check(WaitTask(first) == AppTrackingUpdateOutcome.Succeeded && WaitTask(second) == AppTrackingUpdateOutcome.Succeeded &&
            states.Contains(AppTrackingServiceState.Running),
            "A successful existing installer result completes shared update waiters and notifies fresh Running service state");
        check(remover.Calls == 0 && clearer.Calls == 0 && WaitTask(service.GetStorageStateAsync(CancellationToken.None)) == AppUsageStorageState.Exists,
            "Update never invokes uninstall or history-clear tooling and leaves existing App Usage storage available");
        using var after = WindowsIdentity.GetCurrent();
        check(after.User?.Value == before.User?.Value &&
            new WindowsPrincipal(after).IsInRole(WindowsBuiltInRole.Administrator) == elevatedBefore,
            "The platform update leaves the desktop identity and elevation token unchanged");

        versionReader.Status = new(AppTrackingUpdateAvailability.UpdateAvailable, OldVersion, NewVersion);
        using var cancelledWaiter = new CancellationTokenSource();
        var cancelling = service.UpdateAsync(cancelledWaiter.Token);
        Wait(() => installer.Calls == 2);
        cancelledWaiter.Cancel();
        Wait(() => cancelling.IsCompleted);
        var retained = service.UpdateAsync(CancellationToken.None);
        check(cancelling.IsCanceled && installer.Calls == 2 && !installer.IsCompleted(1) &&
            WaitTask(service.GetStateAsync(CancellationToken.None)) == AppTrackingServiceState.Updating,
            "A cancelled window wait leaves the elevated update running and a recreated view joins it without another UAC prompt");
        installer.Complete(1, 0);
        WaitTask(retained);
        foreach (var failure in new[] { "cancelled", "nonzero", "exception" })
        {
            var index = installer.Calls;
            var update = service.UpdateAsync(CancellationToken.None);
            Wait(() => installer.Calls == index + 1);
            if (failure == "cancelled") installer.Fail(index, new Win32Exception(1223));
            else if (failure == "nonzero") installer.Complete(index, 9);
            else installer.Fail(index, new InvalidOperationException("private updater exception"));
            check(WaitTask(update) == (failure == "cancelled" ? AppTrackingUpdateOutcome.Cancelled : AppTrackingUpdateOutcome.Failed),
                $"Update {failure} becomes a safe typed outcome and releases operation ownership for retry");
        }
        var missing = new AppTrackingSetupService(installer, stateReader, appDirectory, ClientSid, _ => false,
            versionReader: versionReader);
        var launches = installer.Calls;
        check(WaitTask(missing.UpdateAsync(CancellationToken.None)) == AppTrackingUpdateOutcome.FilesMissing && installer.Calls == launches,
            "An incomplete bundled update kit returns FilesMissing without a UAC prompt");
        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        var rejected = false;
        try { _ = service.UpdateAsync(alreadyCancelled.Token); }
        catch (OperationCanceledException) { rejected = true; }
        check(rejected && installer.Calls == launches, "An already cancelled update invocation never launches the installer");
    }

    private static void CheckController(Action<bool, string> check)
    {
        var service = new ControlledUpdateService();
        var clock = new OwnedClock();
        var applied = new List<AppTrackingSettingsPresentation>();
        var controller = new AppTrackingSettingsController(Dispatcher.CurrentDispatcher, service, applied.Add,
            () => throw new InvalidOperationException("Update must not request uninstall confirmation"), timeProvider: clock,
            confirmClearHistory: () => throw new InvalidOperationException("Update must not request history deletion confirmation"));
        try
        {
            controller.Tick();
            WaitTask(controller.UpdateAsync());
            check(service.StatusCalls == 0 && service.VersionCalls == 0 && service.UpdateCalls == 0 && applied.Count == 0,
                "Inactive Settings neither inspects versions nor automatically updates tracking");
            controller.SetActive(true);
            Wait(() => !controller.IsRefreshing && controller.Current.StatusText == "Update available");
            check(controller.Current.IsInstalled && controller.Current.UpdateAvailable && controller.Current.CanUpdate && controller.CanUpdate &&
                controller.Current.InstalledVersion == OldVersion && controller.Current.BundledVersion == NewVersion && service.UpdateCalls == 0,
                "An installed older Running collector reports Update available with explicit action and both package identities");
            service.State = AppTrackingServiceState.Stopped;
            Refresh(controller, clock);
            check(controller.Current.StatusText == "Update available" && controller.CanUpdate,
                "An installed older Stopped collector also exposes update without adding Start or Stop controls");
            service.Status = new(AppTrackingUpdateAvailability.Current, NewVersion, NewVersion);
            Refresh(controller, clock);
            check(controller.Current.StatusText == "Stopped" && !controller.CanUpdate && !controller.Current.UpdateAvailable,
                "Matching versions retain the existing Stopped status and do not offer a redundant update");
            service.State = AppTrackingServiceState.Running;
            Refresh(controller, clock);
            check(controller.Current.StatusText == "Running" && !controller.CanUpdate,
                "Matching versions retain existing Running behavior without an update action");
            WaitTask(controller.UpdateAsync());
            check(service.UpdateCalls == 0, "Calling update while the collector is current cannot request elevation");
            service.State = AppTrackingServiceState.Missing;
            service.Status = new(AppTrackingUpdateAvailability.NotInstalled, null, NewVersion);
            Refresh(controller, clock);
            check(controller.Current.StatusText == "Not installed" && !controller.CanUpdate && !controller.CanUninstall,
                "Absent App Tracking remains Not installed with no update or uninstall action");
            service.State = AppTrackingServiceState.Running;
            service.Status = new(AppTrackingUpdateAvailability.Unavailable);
            Refresh(controller, clock);
            check(!controller.CanUpdate && !controller.Current.UpdateAvailable && service.UpdateCalls == 0,
                "Unavailable version inspection never invents an update action or launches repair automatically");
            service.Status = new(AppTrackingUpdateAvailability.UpdateAvailable, OldVersion, NewVersion);
            service.Notify();
            Wait(() => !controller.IsRefreshing && controller.CanUpdate);
            var update = controller.UpdateAsync();
            Wait(() => service.UpdateCalls == 1);
            check(controller.IsUpdating && !controller.CanUpdate && !controller.CanUninstall && !controller.CanClearHistory &&
                !controller.Current.CanUpdate && !controller.Current.CanUninstall && !controller.Current.CanClearHistory,
                "An explicit update disables duplicate and conflicting Settings management actions");
            var statusCalls = service.StatusCalls;
            var versionCalls = service.VersionCalls;
            WaitTask(controller.UpdateAsync());
            clock.Advance(TimeSpan.FromMinutes(1));
            for (var index = 0; index < 20; index++) controller.Tick();
            check(service.UpdateCalls == 1 && service.StatusCalls == statusCalls && service.VersionCalls == versionCalls,
                "Repeated update clicks and periodic refresh never launch a second installer or poll over the active update");
            service.Complete(0, AppTrackingUpdateOutcome.Succeeded);
            Wait(() => update.IsCompleted && !controller.IsRefreshing && !controller.IsUpdating && controller.Current.StatusText == "Running");
            WaitTask(update);
            check(!controller.CanUpdate && !controller.Current.UpdateAvailable && controller.Current.Error == "" &&
                controller.CanUninstall && controller.CanClearHistory && service.ClearCalls == 0 && service.UninstallCalls == 0,
                "A successful update immediately restores Running management and clear-history availability without deleting history");
            controller.SetActive(false);
            var hiddenStatusCalls = service.StatusCalls;
            var hiddenVersionCalls = service.VersionCalls;
            service.Notify();
            clock.Advance(TimeSpan.FromMinutes(1));
            for (var index = 0; index < 20; index++) controller.Tick();
            HistoryOverviewChecks.Pump();
            check(service.StatusCalls == hiddenStatusCalls && service.VersionCalls == hiddenVersionCalls && service.UpdateCalls == 1,
                "Hidden Settings sends zero service or version requests and never automatically starts an update");
        }
        finally { WaitTask(controller.DisposeAsync().AsTask()); }

        var readOnly = new AppTrackingSettingsController(Dispatcher.CurrentDispatcher, new AppUsageSetupChecks.RunningSetupService(), _ => { }, () => true);
        try
        {
            readOnly.SetActive(true);
            Wait(() => !readOnly.IsRefreshing);
            WaitTask(readOnly.UpdateAsync());
            check(readOnly.Current.StatusText == "Running" && !readOnly.CanUpdate,
                "Existing read-only setup providers preserve Running behavior without unsupported update controls");
        }
        finally { WaitTask(readOnly.DisposeAsync().AsTask()); }
    }

    private static void CheckAlreadyCurrentRace(Action<bool, string> check)
    {
        var service = new ControlledUpdateService { State = AppTrackingServiceState.Stopped };
        var applied = new List<AppTrackingSettingsPresentation>();
        var controller = new AppTrackingSettingsController(Dispatcher.CurrentDispatcher, service, applied.Add, () => true);
        try
        {
            controller.SetActive(true);
            Wait(() => !controller.IsRefreshing && controller.CanUpdate);
            var update = controller.UpdateAsync();
            Wait(() => service.UpdateCalls == 1);
            var beforeCompletion = applied.Count;
            // Another explicit installer can bring the registration current
            // between the displayed mismatch and this action's version recheck.
            // The platform's Current/no-launch path must not invent a start.
            service.Complete(0, AppTrackingUpdateOutcome.Succeeded, AppTrackingServiceState.Stopped);
            Wait(() => update.IsCompleted && !controller.IsRefreshing && !controller.IsUpdating);
            WaitTask(update);
            check(controller.Current.StatusText == "Stopped" && !controller.CanUpdate &&
                applied.Skip(beforeCompletion).All(page => page.StatusText != "Running") &&
                service.State == AppTrackingServiceState.Stopped && service.ClearCalls == 0 && service.UninstallCalls == 0,
                "An already-current update recheck preserves observed Stopped status throughout completion without inventing a restart or clearing history");
        }
        finally { WaitTask(controller.DisposeAsync().AsTask()); }
    }

    private static void CheckFailures(Action<bool, string> check)
    {
        foreach (var outcome in new[] { AppTrackingUpdateOutcome.Cancelled, AppTrackingUpdateOutcome.Failed,
            AppTrackingUpdateOutcome.FilesMissing, AppTrackingUpdateOutcome.NotInstalled })
        {
            var service = new ControlledUpdateService();
            var clock = new OwnedClock();
            var controller = new AppTrackingSettingsController(Dispatcher.CurrentDispatcher, service, _ => { }, () => true,
                timeProvider: clock, confirmClearHistory: () => true);
            try
            {
                controller.SetActive(true);
                Wait(() => !controller.IsRefreshing && controller.CanUpdate);
                var update = controller.UpdateAsync();
                Wait(() => service.UpdateCalls == 1);
                service.Complete(0, outcome);
                Wait(() => update.IsCompleted && !controller.IsRefreshing && !controller.IsUpdating);
                WaitTask(update);
                var removed = outcome == AppTrackingUpdateOutcome.NotInstalled;
                check(controller.CanUpdate == !removed && controller.Current.StatusText == (removed ? "Not installed" : "Update available") &&
                    !string.IsNullOrWhiteSpace(controller.Current.Error) && controller.Current.Error.Length <= 160 &&
                    service.InstallCalls == 0 && service.UninstallCalls == 0 && service.ClearCalls == 0,
                    $"Update {outcome} shows short safe feedback and restores explicit retry without another management action");
                var error = controller.Current.Error;
                Refresh(controller, clock);
                check(controller.Current.Error == error && service.UpdateCalls == 1,
                    $"Update {outcome} feedback survives ordinary refresh without automatic UAC retry");
                if (removed)
                {
                    service.State = AppTrackingServiceState.Running;
                    service.Status = new(AppTrackingUpdateAvailability.UpdateAvailable, OldVersion, NewVersion);
                    service.Notify();
                    Wait(() => !controller.IsRefreshing && controller.CanUpdate);
                }
                var retry = controller.UpdateAsync();
                Wait(() => service.UpdateCalls == 2);
                service.Complete(1, AppTrackingUpdateOutcome.Succeeded);
                Wait(() => retry.IsCompleted && !controller.IsRefreshing && controller.Current.StatusText == "Running");
                WaitTask(retry);
                check(controller.Current.Error == "" && !controller.CanUpdate && service.ClearCalls == 0,
                    $"An explicit retry after {outcome} reconnects successfully without clearing history");
            }
            finally { WaitTask(controller.DisposeAsync().AsTask()); }
        }
    }

    private static void CheckReconnect(Action<bool, string> check)
    {
        foreach (var range in Enum.GetValues<AppUsageRange>())
        {
            var service = new ControlledUpdateService();
            var client = new OwnedUsageClient();
            var applied = new List<AppUsageTodayPresentation>();
            var usage = new AppUsageTodayController(Dispatcher.CurrentDispatcher, client, applied.Add, setupService: service);
            try
            {
                usage.SelectRange(range);
                usage.SetActive(true);
                Wait(() => !usage.IsRefreshing && usage.Current.State == AppUsageTodayState.Available);
                var oldCalls = client.Calls;
                var update = service.UpdateAsync(CancellationToken.None);
                Wait(() => !usage.IsRefreshing && usage.Current.State == AppUsageTodayState.Unavailable);
                check(client.Calls == oldCalls && usage.SelectedRange == range && !usage.CanInstall,
                    $"Updating pauses {range} IPC access without prompting installation or losing the selected range");
                service.Complete(0, AppTrackingUpdateOutcome.Succeeded);
                WaitTask(update);
                Wait(() => !usage.IsRefreshing && usage.Current.State == AppUsageTodayState.Available && client.Calls == oldCalls + 1);
                check(client.LastRange == range && usage.SelectedRange == range && usage.Current.Applications.Count == 1,
                    $"A successful update reconnects active {range} automatically and presents existing usage without navigation");
                usage.SetActive(false);
                var hiddenCalls = client.Calls;
                var hiddenStates = service.StatusCalls;
                var hiddenApplies = applied.Count;
                var hiddenUpdate = service.UpdateAsync(CancellationToken.None);
                service.Complete(1, AppTrackingUpdateOutcome.Succeeded);
                WaitTask(hiddenUpdate);
                for (var index = 0; index < 20; index++) usage.Tick();
                HistoryOverviewChecks.Pump();
                check(client.Calls == hiddenCalls && service.StatusCalls == hiddenStates && applied.Count == hiddenApplies && usage.SelectedRange == range,
                    $"Update completion sends zero IPC, status or render requests from hidden {range} while retaining selection");
                usage.SetActive(true);
                Wait(() => !usage.IsRefreshing && client.Calls == hiddenCalls + 1);
                check(client.LastRange == range && usage.Current.State == AppUsageTodayState.Available,
                    $"Returning to {range} after update immediately queries the refreshed collector without restarting NetTrace");
            }
            finally { WaitTask(usage.DisposeAsync().AsTask()); }
        }
    }

    private static void CheckStaleResponse(Action<bool, string> check)
    {
        var service = new ControlledUpdateService();
        var client = new HeldFirstUsageClient();
        var usage = new AppUsageTodayController(Dispatcher.CurrentDispatcher, client, _ => { }, setupService: service);
        try
        {
            usage.SetActive(true);
            Wait(() => client.Calls == 1);
            var update = service.UpdateAsync(CancellationToken.None);
            usage.SelectRange(AppUsageRange.ThirtyDays);
            service.Complete(0, AppTrackingUpdateOutcome.Succeeded);
            WaitTask(update);
            client.CompleteFirst();
            Wait(() => client.Calls == 2 && !usage.IsRefreshing);
            check(usage.SelectedRange == AppUsageRange.ThirtyDays && client.LastRange == AppUsageRange.ThirtyDays &&
                usage.Current.Applications.Single().DisplayName == "Current application",
                "An old pre-update response cannot overwrite the latest range or its reconnected collector response");
        }
        finally { client.CompleteFirst(); WaitTask(usage.DisposeAsync().AsTask()); }
    }

    private static void CheckClosedCompletion(Action<bool, string> check)
    {
        var service = new ControlledUpdateService();
        var renders = 0;
        var controller = new AppTrackingSettingsController(Dispatcher.CurrentDispatcher, service, _ => renders++, () => true);
        controller.SetActive(true);
        Wait(() => !controller.IsRefreshing && controller.CanUpdate);
        var update = controller.UpdateAsync();
        Wait(() => service.UpdateCalls == 1);
        WaitTask(controller.DisposeAsync().AsTask());
        var before = renders;
        service.Complete(0, AppTrackingUpdateOutcome.Succeeded);
        WaitTask(update);
        check(renders == before && service.State == AppTrackingServiceState.Running && service.UpdateCalls == 1,
            "Closing Settings does not kill an initiated update or render its late result into a disposed view");
    }

    private static void CheckWindow(string root, Action<bool, string> check)
    {
        var store = new UsageStore(System.IO.Path.Combine(root, "tracking-update-window"));
        using var monitoring = HistoryOverviewChecks.CreateMonitoring(store);
        var service = new ControlledUpdateService();
        var client = new OwnedUsageClient();
        using var before = WindowsIdentity.GetCurrent();
        var elevatedBefore = new WindowsPrincipal(before).IsInRole(WindowsBuiltInRole.Administrator);
        var window = new MainWindow(monitoring, new OwnedStartup(), appUsageClient: client, appTrackingSetupService: service,
            confirmAppTrackingUninstall: () => throw new InvalidOperationException("Update must not ask to uninstall"),
            confirmAppUsageHistoryClear: () => throw new InvalidOperationException("Update must not ask to delete history"))
            { ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Width = 760, Height = 600 };
        try
        {
            window.Show();
            HistoryOverviewChecks.StopViewTimer(window);
            Layout(window);
            var physicalBefore = monitoring.GetSnapshot();
            var dashboardBefore = Text(window, "DownloadSessionText") + Text(window, "UploadSessionText");
            var historyBefore = Text(window, "HistoryTodaySessionsText") + Text(window, "CurrentSessionUsageText");
            var historyRowsBefore = monitoring.History.GetRecentSessions().ToArray();
            check(service.UpdateCalls == 0 && service.InstallCalls == 0 && service.ClearCalls == 0 && service.UninstallCalls == 0,
                "The real NetTrace window does not request update, elevation or history deletion at startup");
            SelectTab(window, "AppUsageTab");
            Wait(() => !UsageController(window).IsRefreshing && UsageController(window).Current.State == AppUsageTodayState.Available);
            Control<ToggleButton>(window, "AppUsage30DButton").SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Wait(() => !UsageController(window).IsRefreshing && client.LastRange == AppUsageRange.ThirtyDays);
            SelectTab(window, "SettingsTab");
            Wait(() => !SettingsController(window).IsRefreshing && SettingsController(window).CanUpdate);
            var button = Control<Button>(window, "SettingsAppTrackingUpdateButton");
            Layout(window);
            check(button.IsVisible && button.IsEnabled && button.Content?.ToString() == "Update App Tracking" &&
                Text(window, "SettingsAppTrackingStatusText") == "Update available",
                "The real Settings page exposes Update App Tracking only for an installed mismatched collector");
            HistoryOverviewChecks.SavePreview(window, System.IO.Path.Combine(root, "app-tracking-update-previews", "760x600-update-available.png"));
            var hiddenCalls = client.Calls;
            foreach (var outcome in new[] { AppTrackingUpdateOutcome.Cancelled, AppTrackingUpdateOutcome.Failed, AppTrackingUpdateOutcome.Succeeded })
            {
                var index = service.UpdateCalls;
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Wait(() => service.UpdateCalls == index + 1);
                check(!button.IsEnabled && !Control<Button>(window, "SettingsAppTrackingUninstallButton").IsEnabled &&
                    !Control<Button>(window, "SettingsAppUsageHistoryClearButton").IsEnabled,
                    $"The real explicit {outcome} update enters guarded progress without invoking another management confirmation");
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                HistoryOverviewChecks.Pump();
                check(service.UpdateCalls == index + 1, "Duplicate real Update button clicks cannot launch a second elevated child");
                service.Complete(index, outcome);
                Wait(() => !SettingsController(window).IsUpdating && !SettingsController(window).IsRefreshing);
                if (outcome == AppTrackingUpdateOutcome.Succeeded)
                {
                    check(!button.IsVisible && Text(window, "SettingsAppTrackingStatusText") == "Running" &&
                        !Control<TextBlock>(window, "SettingsAppTrackingErrorText").IsVisible,
                        "Successful real-button update immediately restores Running and removes the obsolete update action and retry error");
                }
                else
                {
                    check(button.IsVisible && button.IsEnabled && Control<TextBlock>(window, "SettingsAppTrackingErrorText").IsVisible &&
                        !string.IsNullOrWhiteSpace(Text(window, "SettingsAppTrackingErrorText")),
                        $"Real-button {outcome} restores Update available and short inline retry feedback without crashing");
                }
                check(client.Calls == hiddenCalls && UsageController(window).SelectedRange == AppUsageRange.ThirtyDays,
                    $"The real {outcome} update keeps hidden App Usage silent and preserves selected 30D");
            }
            check(service.ClearCalls == 0 && service.UninstallCalls == 0 && service.InstallCalls == 0 &&
                Control<Button>(window, "SettingsAppUsageHistoryClearButton").IsEnabled,
                "Updated Settings exposes the existing clear-history capability without exercising it or deleting recorded usage");
            check(Text(window, "DownloadSessionText") + Text(window, "UploadSessionText") == dashboardBefore &&
                Text(window, "HistoryTodaySessionsText") + Text(window, "CurrentSessionUsageText") == historyBefore &&
                monitoring.GetSnapshot().Session?.Id == physicalBefore.Session?.Id &&
                monitoring.History.GetRecentSessions().SequenceEqual(historyRowsBefore),
                "Updating App Tracking preserves Dashboard values, persisted connection History and the physical accounting session");
            SelectTab(window, "AppUsageTab");
            Wait(() => !UsageController(window).IsRefreshing && client.Calls == hiddenCalls + 1);
            check(Control<ToggleButton>(window, "AppUsage30DButton").IsChecked == true &&
                UsageController(window).Current.State == AppUsageTodayState.Available && UsageController(window).Current.Applications.Count == 1,
                "Returning to the same real 30D page loads existing history from the updated collector without restarting NetTrace");
            using var after = WindowsIdentity.GetCurrent();
            check(after.User?.Value == before.User?.Value &&
                new WindowsPrincipal(after).IsInRole(WindowsBuiltInRole.Administrator) == elevatedBefore,
                "The real NetTrace window retains its original process identity and elevation token after update, UAC cancellation and retry");
            SelectTab(window, "SettingsTab");
            service.State = AppTrackingServiceState.Stopped;
            service.Notify();
            Wait(() => !SettingsController(window).IsRefreshing && Text(window, "SettingsAppTrackingStatusText") == "Stopped");
            check(!button.IsVisible && Control<Button>(window, "SettingsAppTrackingUninstallButton").IsEnabled,
                "Current stopped tracking retains uninstall and hides Update in the real Settings page");
            service.State = AppTrackingServiceState.Missing;
            service.Status = new(AppTrackingUpdateAvailability.NotInstalled, null, NewVersion);
            service.Notify();
            Wait(() => !SettingsController(window).IsRefreshing && Text(window, "SettingsAppTrackingStatusText") == "Not installed");
            check(!button.IsVisible && !Control<Button>(window, "SettingsAppTrackingUninstallButton").IsVisible,
                "Missing tracking hides both installed-service Update and Uninstall actions in the real window");
        }
        finally
        {
            window.Close();
            service.CompletePending();
            Wait(() => client.Disposed);
        }
    }

    private static void Refresh(AppTrackingSettingsController controller, OwnedClock clock)
    {
        clock.Advance(TimeSpan.FromSeconds(4));
        controller.Tick();
        Wait(() => !controller.IsRefreshing);
    }
    private static T WaitTask<T>(Task<T> task) { Wait(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
    private static void WaitTask(Task task) { Wait(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Wait(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Owned App Tracking update condition was not ready.");
            HistoryOverviewChecks.Pump();
        }
        HistoryOverviewChecks.Pump();
    }
    private static T Control<T>(MainWindow window, string name) where T : class => window.FindName(name) as T ??
        throw new InvalidOperationException("Missing App Tracking update control " + name);
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

    private sealed class OwnedVersionReader : IAppTrackingUpdateVersionReader
    {
        private int _calls, _refreshedCalls;
        public AppTrackingUpdateStatus Status { get; set; } = new(AppTrackingUpdateAvailability.UpdateAvailable, OldVersion, NewVersion);
        public int Calls => Volatile.Read(ref _calls);
        public int RefreshedCalls => Volatile.Read(ref _refreshedCalls);
        public AppTrackingUpdateStatus GetStatus(string serviceDirectory, bool refreshBundledVersion = false)
        {
            Interlocked.Increment(ref _calls);
            if (refreshBundledVersion) Interlocked.Increment(ref _refreshedCalls);
            return Status;
        }
    }
    private sealed class OwnedStateReader : IAppTrackingServiceStateReader
    {
        public AppTrackingServiceState GetState() => AppTrackingServiceState.Running;
    }
    private sealed class OwnedStorageReader : IAppUsageStorageStateReader
    {
        public AppUsageStorageState GetState() => AppUsageStorageState.Exists;
    }
    private sealed class ControlledInstaller : IAppTrackingInstallerLauncher
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource<int>> _completions = [];
        private readonly List<AppTrackingInstallLaunchSpec> _invocations = [];
        public int Calls { get { lock (_gate) return _completions.Count; } }
        public Task<int> LaunchAsync(AppTrackingInstallLaunchSpec invocation)
        {
            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) { _invocations.Add(invocation); _completions.Add(completion); }
            return completion.Task;
        }
        public AppTrackingInstallLaunchSpec Invocation(int index) { lock (_gate) return _invocations[index]; }
        public bool IsCompleted(int index) { lock (_gate) return _completions[index].Task.IsCompleted; }
        public void Complete(int index, int code) { lock (_gate) _completions[index].TrySetResult(code); }
        public void Fail(int index, Exception error) { lock (_gate) _completions[index].TrySetException(error); }
    }
    private sealed class GuardUninstaller : IAppTrackingUninstallerLauncher
    {
        public int Calls { get; private set; }
        public Task<int> LaunchAsync(AppTrackingUninstallLaunchSpec invocation) { Calls++; return Task.FromResult(7); }
    }
    private sealed class GuardClearer : IAppUsageHistoryClearLauncher
    {
        public int Calls { get; private set; }
        public Task<int> LaunchAsync(AppUsageHistoryClearLaunchSpec invocation) { Calls++; return Task.FromResult(7); }
    }
    private sealed class ControlledUpdateService : IAppTrackingUpdateService
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource<AppTrackingUpdateOutcome>> _updates = [];
        private int _state = (int)AppTrackingServiceState.Running;
        private int _statusCalls, _versionCalls;
        public event EventHandler? StateChanged;
        public event EventHandler? HistoryCleared { add { } remove { } }
        public AppTrackingServiceState State { get => (AppTrackingServiceState)Volatile.Read(ref _state); set => Volatile.Write(ref _state, (int)value); }
        public AppTrackingUpdateStatus Status { get; set; } = new(AppTrackingUpdateAvailability.UpdateAvailable, OldVersion, NewVersion);
        public int StatusCalls => Volatile.Read(ref _statusCalls);
        public int VersionCalls => Volatile.Read(ref _versionCalls);
        public int UpdateCalls { get { lock (_gate) return _updates.Count; } }
        public int InstallCalls { get; private set; }
        public int UninstallCalls { get; private set; }
        public int ClearCalls { get; private set; }
        public Task<AppTrackingServiceState> GetStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _statusCalls);
            return Task.FromResult(State);
        }
        public Task<AppTrackingUpdateStatus> GetUpdateStatusAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _versionCalls);
            return Task.FromResult(Status);
        }
        public Task<AppUsageStorageState> GetStorageStateAsync(CancellationToken cancellationToken) => Task.FromResult(AppUsageStorageState.Exists);
        public Task<AppTrackingUpdateOutcome> UpdateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource<AppTrackingUpdateOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _updates.Add(completion);
            State = AppTrackingServiceState.Updating;
            Notify();
            return completion.Task;
        }
        public Task<AppTrackingInstallOutcome> InstallAsync(CancellationToken cancellationToken) { InstallCalls++; return Task.FromResult(AppTrackingInstallOutcome.Failed); }
        public Task<AppTrackingUninstallOutcome> UninstallAsync(CancellationToken cancellationToken) { UninstallCalls++; return Task.FromResult(AppTrackingUninstallOutcome.Failed); }
        public Task<AppUsageHistoryClearOutcome> ClearHistoryAsync(CancellationToken cancellationToken) { ClearCalls++; return Task.FromResult(AppUsageHistoryClearOutcome.Failed); }
        public void Complete(int index, AppTrackingUpdateOutcome outcome, AppTrackingServiceState? finalState = null)
        {
            State = finalState ?? (outcome == AppTrackingUpdateOutcome.NotInstalled ? AppTrackingServiceState.Missing : AppTrackingServiceState.Running);
            if (outcome == AppTrackingUpdateOutcome.Succeeded) Status = new(AppTrackingUpdateAvailability.Current, NewVersion, NewVersion);
            if (outcome == AppTrackingUpdateOutcome.NotInstalled) Status = new(AppTrackingUpdateAvailability.NotInstalled, null, NewVersion);
            Notify();
            lock (_gate) _updates[index].TrySetResult(outcome);
        }
        public void Notify() => StateChanged?.Invoke(this, EventArgs.Empty);
        public void CompletePending()
        {
            lock (_gate) foreach (var update in _updates) update.TrySetResult(AppTrackingUpdateOutcome.Cancelled);
        }
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
            return Task.FromResult(AppUsageTodayChecks.Connected(AppUsageTodayChecks.Result(
                [new("owned-retained-update", "Recorded application", "recorded.exe", null, 9000, 1000, 10000, false, false)]) with { Range = range }));
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
            return Task.FromResult(AppUsageTodayChecks.Connected(AppUsageTodayChecks.Result(
                [new("owned-current-update", "Current application", "current.exe", null, 9000, 1000, 10000, false, false)]) with { Range = range }));
        }
        public void CompleteFirst() => _first.TrySetResult(AppUsageTodayChecks.Connected(AppUsageTodayChecks.Result(
            [new("owned-stale-update", "Obsolete application", "old.exe", null, 90_000, 10_000, 100_000, false, false)])));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
        public void Enable() => throw new InvalidOperationException("Update must not change NetTrace startup registration.");
        public void Disable() => throw new InvalidOperationException("Update must not change NetTrace startup registration.");
    }
}
