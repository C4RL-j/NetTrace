using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using AppUsageIpc;
using NetTrace;
using NetTrace.Presentation;
using NetTrace.Services;
using NetTrace.Utils;

// Explicit interactive validation only. The real Settings button is the sole
// mutating action. This harness never clears history or uninstalls the service.
internal static class AppTrackingUpdateLiveValidation
{
    public static int Run(string[] arguments)
    {
        if (arguments.Length != 2 || arguments[0] != "--output" || !Path.IsPathFullyQualified(arguments[1]))
        {
            Console.Error.WriteLine("Collector update live validation requires --output ABSOLUTE_JSON_PATH.");
            return 1;
        }
        var output = Path.GetFullPath(arguments[1]);
        var directory = Path.GetDirectoryName(output)!;
        using var beforeIdentity = WindowsIdentity.GetCurrent();
        var report = new LiveReport
        {
            ClientSid = beforeIdentity.User?.Value,
            ClientElevatedBefore = IsElevated(beforeIdentity),
            ClientProcessId = Environment.ProcessId
        };
        if (report.ClientElevatedBefore)
        {
            report.Error = "Collector update page validation requires the normal user token.";
            return Finish(output, report);
        }

        MainWindow? window = null;
        MonitoringService? monitoring = null;
        var service = new ObservedService(new AppTrackingSetupService());
        var client = new ObservedClient(new AppUsageTodayClient());
        try
        {
            report.VersionBefore = service.GetUpdateStatusAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (report.VersionBefore.Availability != AppTrackingUpdateAvailability.UpdateAvailable
                || new WindowsAppTrackingServiceStateReader().GetState() != AppTrackingServiceState.Running)
                throw new InvalidOperationException("This explicit workflow requires an older running production collector.");
            var baselineSnapshot = ReadCollectorObservation();
            report.BaselineIpcState = baselineSnapshot.State;
            report.BaselineIpcDetail = baselineSnapshot.Detail;
            report.CollectorInstanceBefore = baselineSnapshot.Instance;
            report.BaselineSnapshotAvailable = baselineSnapshot.State == IpcState.Connected && baselineSnapshot.Instance is not null;
            if (!report.BaselineSnapshotAvailable && baselineSnapshot.State is not
                (IpcState.CollectorUnavailable or IpcState.Disconnected or IpcState.TimedOut or IpcState.IncompatibleProtocol))
                throw new InvalidOperationException("The old production collector returned an unsafe baseline identity/protocol result: " + baselineSnapshot.State);
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            monitoring = HistoryOverviewChecks.CreateMonitoring(new UsageStore(Path.Combine(directory, "OwnedPhysicalShell")));
            window = new MainWindow(monitoring, appUsageClient: client, appTrackingSetupService: service)
            {
                Width = 760, Height = 600, ShowActivated = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            window.Show();
            var navigation = Control<TabControl>(window, "MainNavigation");
            var appUsageTab = Control<TabItem>(window, "AppUsageTab");
            var settingsTab = Control<TabItem>(window, "SettingsTab");
            navigation.SelectedItem = appUsageTab;
            appUsageTab.Focus();
            Control<RadioButton>(window, "AppUsage30DButton").IsChecked = true;
            var usageController = Controller<AppUsageTodayController>(window, "_appUsageToday");
            Wait(() => !usageController.IsRefreshing && usageController.SelectedRange == AppUsageRange.ThirtyDays
                && usageController.Current.State is AppUsageTodayState.Available or AppUsageTodayState.Empty or AppUsageTodayState.Unavailable,
                TimeSpan.FromSeconds(20));
            report.SelectedRangeBefore = usageController.SelectedRange;
            report.UsageBefore = client.Latest?.Value;
            report.BaselinePageUnavailable = usageController.Current.State == AppUsageTodayState.Unavailable;
            if (report.UsageBefore is { Range: AppUsageRange.ThirtyDays } beforeUsage)
            {
                report.BeforeRangeDownloadBytes = beforeUsage.TotalDownloadBytes;
                report.BeforeRangeUploadBytes = beforeUsage.TotalUploadBytes;
                report.BeforeRangeEvidence = "Authenticated IPC";
            }
            else
            {
                var persisted = ReadProtectedRangeEvidence(directory, report.VersionBefore.InstalledVersion);
                report.BeforeRangeDownloadBytes = persisted.DownloadBytes;
                report.BeforeRangeUploadBytes = persisted.UploadBytes;
                report.BeforeRangeEvidence = "Read-only protected before-update SQLite snapshot";
                report.BeforeRangePersistedRows = persisted.Rows;
            }
            navigation.SelectedItem = settingsTab;
            settingsTab.Focus();
            var settingsController = Controller<AppTrackingSettingsController>(window, "_appTrackingSettings");
            Wait(() => settingsController.CanUpdate, TimeSpan.FromSeconds(15));
            var update = Control<Button>(window, "SettingsAppTrackingUpdateButton");
            report.UpdateDisplayed = Text(window, "SettingsAppTrackingStatusText") == "Update available"
                && update.IsVisible && update.IsEnabled && update.Content?.ToString() == "Update App Tracking";
            report.NoAutomaticUpdate = service.UpdateCalls == 0 && service.OtherMutationCalls == 0;
            report.QueriesBeforeClick = client.QueryCount;
            if (!report.UpdateDisplayed || !report.NoAutomaticUpdate
                || usageController.SelectedRange != AppUsageRange.ThirtyDays)
                throw new InvalidOperationException("The update state did not preserve the explicit action and selected range.");
            window.UpdateLayout();
            HistoryOverviewChecks.SavePreview(window, Path.Combine(directory, "760x600-update-available.png"));

            // The caller expressly invoked the live workflow. Dispatch the real
            // button Click, then keep App Usage visible while its child updates.
            var completionBefore = client.Latest?.Sequence ?? 0;
            var updateWatch = Stopwatch.StartNew();
            report.ExplicitUpdateClicked = true;
            Write(output, report);
            update.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, update));
            navigation.SelectedItem = appUsageTab;
            appUsageTab.Focus();
            Wait(() => service.Outcome is not null && !settingsController.IsUpdating, TimeSpan.FromSeconds(150));
            report.UpdateOutcome = service.Outcome;
            report.UpdateCalls = service.UpdateCalls;
            report.OtherMutationCalls = service.OtherMutationCalls;
            using var afterIdentity = WindowsIdentity.GetCurrent();
            report.ClientElevatedAfter = IsElevated(afterIdentity);
            report.ClientIdentityRetained = afterIdentity.User?.Value == report.ClientSid;
            report.SelectedRangeAfter = usageController.SelectedRange;
            if (report.ClientElevatedAfter || !report.ClientIdentityRetained || report.UpdateCalls != 1
                || report.OtherMutationCalls != 0 || report.SelectedRangeAfter != AppUsageRange.ThirtyDays)
                throw new InvalidOperationException("Update changed the normal desktop identity, launched another mutation, or lost the selected range.");
            if (report.UpdateOutcome != AppTrackingUpdateOutcome.Succeeded)
                throw new InvalidOperationException("The explicit update did not succeed: " + report.UpdateOutcome);

            report.UpdateElapsedSeconds = updateWatch.Elapsed.TotalSeconds;
            var reconnectWatch = Stopwatch.StartNew();
            Wait(() => client.Latest is { Sequence: var sequence, CompletedAtUtc: var completed, Value: { Range: AppUsageRange.ThirtyDays } value }
                && sequence > completionBefore && completed >= service.OutcomeAtUtc && SummaryMatches(window, value)
                && usageController.Current.State is AppUsageTodayState.Available or AppUsageTodayState.Empty,
                TimeSpan.FromSeconds(25));
            report.ReconnectElapsedSeconds = reconnectWatch.Elapsed.TotalSeconds;
            report.UsageAfter = client.Latest!.Value;
            report.QueriesAfterClick = client.QueryCount;
            var updatedSnapshot = ReadCollectorObservation();
            if (updatedSnapshot.State != IpcState.Connected || updatedSnapshot.Instance is null)
                throw new InvalidOperationException("The updated production collector did not provide an authenticated snapshot: " + updatedSnapshot.State);
            report.CollectorInstanceAfter = updatedSnapshot.Instance;
            // When old IPC was unavailable there is no measured old GUID to
            // compare. The parent checks the protected before/after SCM PID.
            report.FreshCollectorInstance = report.CollectorInstanceBefore is null
                ? null : report.CollectorInstanceAfter != report.CollectorInstanceBefore;
            report.ReconnectedWithoutNavigation = appUsageTab.IsSelected && SummaryMatches(window, report.UsageAfter!)
                && usageController.SelectedRange == AppUsageRange.ThirtyDays;
            report.RangeCountersPreserved = report.UsageAfter!.TotalDownloadBytes >= report.BeforeRangeDownloadBytes
                && report.UsageAfter.TotalUploadBytes >= report.BeforeRangeUploadBytes;
            report.ServiceStateAfter = new WindowsAppTrackingServiceStateReader().GetState();
            report.VersionAfter = service.GetUpdateStatusAsync(CancellationToken.None).GetAwaiter().GetResult();
            window.UpdateLayout();
            HistoryOverviewChecks.SavePreview(window, Path.Combine(directory, "760x600-updated-30d.png"));

            navigation.SelectedItem = settingsTab;
            settingsTab.Focus();
            Wait(() => Text(window, "SettingsAppTrackingStatusText") == "Running" && !update.IsVisible,
                TimeSpan.FromSeconds(15));
            report.SettingsRunningAfter = true;
            report.ClearHistoryButtonAvailable = Control<Button>(window, "SettingsAppUsageHistoryClearButton").IsEnabled;
            var scroll = FindAncestor<ScrollViewer>(Control<FrameworkElement>(window, "SettingsContent"));
            window.UpdateLayout();
            report.NoHorizontalOverflow = scroll is null || scroll.ScrollableWidth <= 0.5;
            HistoryOverviewChecks.SavePreview(window, Path.Combine(directory, "760x600-updated-settings.png"));
            report.Success = report.ServiceStateAfter == AppTrackingServiceState.Running
                && report.VersionAfter.Availability == AppTrackingUpdateAvailability.Current
                && report.FreshCollectorInstance is not false && report.CollectorInstanceAfter is not null
                && report.RangeCountersPreserved && report.ReconnectedWithoutNavigation
                && report.SettingsRunningAfter && report.ClearHistoryButtonAvailable && report.NoHorizontalOverflow;
            return Finish(output, report);
        }
        catch (Exception exception)
        {
            report.Error = exception.Message;
            report.UpdateCalls = service.UpdateCalls;
            report.UpdateOutcome = service.Outcome;
            report.OtherMutationCalls = service.OtherMutationCalls;
            report.QueriesAfterClick = client.QueryCount;
            report.Diagnostics = Diagnose(window, client);
            report.ServiceStateAfter = new WindowsAppTrackingServiceStateReader().GetState();
            return Finish(output, report);
        }
        finally
        {
            window?.Close();
            monitoring?.Dispose();
            Application.Current?.Shutdown();
        }
    }

    // This is a read-only follow-up observation of an already updated service.
    // It sends real authenticated range queries and one local observer signal;
    // it never invokes update, install, uninstall, clear, start or stop.
    public static int RunObserveCurrent(string[] arguments)
    {
        if (arguments.Length != 2 || arguments[0] != "--output" || !Path.IsPathFullyQualified(arguments[1]))
        {
            Console.Error.WriteLine("Current collector observation requires --output ABSOLUTE_JSON_PATH.");
            return 1;
        }
        var output = Path.GetFullPath(arguments[1]);
        var directory = Path.GetDirectoryName(output)!;
        using var beforeIdentity = WindowsIdentity.GetCurrent();
        var report = new CurrentObservationReport
        {
            Mode = "Read-only current collector and local management observer signal; no service action",
            ClientSid = beforeIdentity.User?.Value,
            ClientProcessId = Environment.ProcessId,
            ClientElevatedBefore = IsElevated(beforeIdentity)
        };
        MainWindow? window = null;
        MonitoringService? monitoring = null;
        var service = new ObservedService(new AppTrackingSetupService());
        var client = new ObservedClient(new AppUsageTodayClient());
        try
        {
            if (report.ClientElevatedBefore)
                throw new InvalidOperationException("Current collector observation requires the normal user token.");
            report.Version = service.GetUpdateStatusAsync(CancellationToken.None).GetAwaiter().GetResult();
            report.ServiceState = service.GetStateAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (report.Version.Availability != AppTrackingUpdateAvailability.Current || report.ServiceState != AppTrackingServiceState.Running)
                throw new InvalidOperationException("Current collector observation requires a matching Running installed collector.");
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            monitoring = HistoryOverviewChecks.CreateMonitoring(new UsageStore(Path.Combine(directory, "OwnedPhysicalObservation")));
            window = new MainWindow(monitoring, appUsageClient: client, appTrackingSetupService: service)
            {
                Width = 760, Height = 600, ShowActivated = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            window.Show();
            var navigation = Control<TabControl>(window, "MainNavigation");
            var appUsageTab = Control<TabItem>(window, "AppUsageTab");
            var settingsTab = Control<TabItem>(window, "SettingsTab");
            navigation.SelectedItem = appUsageTab;
            appUsageTab.Focus();
            Control<RadioButton>(window, "AppUsage30DButton").IsChecked = true;
            var usage = Controller<AppUsageTodayController>(window, "_appUsageToday");
            Wait(() => !usage.IsRefreshing && client.Latest is { Value: { Range: AppUsageRange.ThirtyDays } value }
                && usage.Current.State is AppUsageTodayState.Available or AppUsageTodayState.Empty
                && SummaryMatches(window, value), TimeSpan.FromSeconds(20));
            report.SelectedRangeBeforeSignal = usage.SelectedRange;
            report.RangeBeforeSignal = RangeEvidence(client.Latest!.Value);
            window.UpdateLayout();
            HistoryOverviewChecks.SavePreview(window, Path.Combine(directory, "760x600-current-30d.png"));

            navigation.SelectedItem = settingsTab;
            settingsTab.Focus();
            var settings = Controller<AppTrackingSettingsController>(window, "_appTrackingSettings");
            var update = Control<Button>(window, "SettingsAppTrackingUpdateButton");
            Wait(() => !settings.IsRefreshing && Text(window, "SettingsAppTrackingStatusText") == "Running"
                && !update.IsVisible, TimeSpan.FromSeconds(15));
            report.SettingsStatus = Text(window, "SettingsAppTrackingStatusText");
            report.UpdateButtonVisible = update.IsVisible;
            report.ClearHistoryButtonAvailable = Control<Button>(window, "SettingsAppUsageHistoryClearButton").IsEnabled;
            window.UpdateLayout();
            HistoryOverviewChecks.SavePreview(window, Path.Combine(directory, "760x600-current-settings.png"));

            navigation.SelectedItem = appUsageTab;
            appUsageTab.Focus();
            Wait(() => !usage.IsRefreshing && usage.Current.State is AppUsageTodayState.Available or AppUsageTodayState.Empty,
                TimeSpan.FromSeconds(20));
            var sequenceBeforeSignal = client.Latest!.Sequence;
            report.QueriesBeforeSignal = client.QueryCount;
            report.LocalObserverSignalSent = true;
            var observationWatch = Stopwatch.StartNew();
            service.RaiseReadOnlyObserverSignal();
            Wait(() => !usage.IsRefreshing && client.Latest is { Sequence: var sequence, Value: { Range: AppUsageRange.ThirtyDays } value }
                && sequence > sequenceBeforeSignal && SummaryMatches(window, value)
                && usage.Current.State is AppUsageTodayState.Available or AppUsageTodayState.Empty,
                TimeSpan.FromSeconds(20));
            report.ObserverRefreshElapsedSeconds = observationWatch.Elapsed.TotalSeconds;
            report.QueriesAfterSignal = client.QueryCount;
            report.SelectedRangeAfterSignal = usage.SelectedRange;
            report.RangeAfterSignal = RangeEvidence(client.Latest!.Value);
            report.ObserverRefreshWithoutNavigation = appUsageTab.IsSelected && usage.SelectedRange == AppUsageRange.ThirtyDays
                && report.QueriesAfterSignal > report.QueriesBeforeSignal && SummaryMatches(window, client.Latest.Value);
            report.MutationCalls = service.UpdateCalls + service.OtherMutationCalls;
            using var afterIdentity = WindowsIdentity.GetCurrent();
            report.ClientElevatedAfter = IsElevated(afterIdentity);
            report.ClientIdentityRetained = afterIdentity.User?.Value == report.ClientSid;
            report.Diagnostics = Diagnose(window, client);
            report.ServiceState = service.GetStateAsync(CancellationToken.None).GetAwaiter().GetResult();
            window.UpdateLayout();
            HistoryOverviewChecks.SavePreview(window, Path.Combine(directory, "760x600-current-observer-refreshed-30d.png"));
            report.Success = report.ObserverRefreshWithoutNavigation && report.MutationCalls == 0
                && report.ServiceState == AppTrackingServiceState.Running && report.ClientIdentityRetained && !report.ClientElevatedAfter
                && report.SettingsStatus == "Running" && !report.UpdateButtonVisible && report.ClearHistoryButtonAvailable;
        }
        catch (Exception exception)
        {
            report.Error = exception.Message;
            report.Diagnostics = Diagnose(window, client);
            report.MutationCalls = service.UpdateCalls + service.OtherMutationCalls;
            report.QueriesAfterSignal = client.QueryCount;
        }
        finally
        {
            report.Completed = true;
            WriteCurrentObservation(output, report);
            window?.Close();
            monitoring?.Dispose();
            Application.Current?.Shutdown();
        }
        Console.WriteLine($"Current collector read-only observation: {(report.Success ? "passed" : "failed")}; report={output}");
        if (report.Error is not null) Console.Error.WriteLine(report.Error);
        return report.Success ? 0 : 1;
    }

    private static RangeObservation RangeEvidence(AppUsageQueryResult value) => new(value.Range, value.Availability,
        value.TrackedApplicationCount, value.TotalDownloadBytes, value.TotalUploadBytes, value.LogicalTotalBytes);

    private static ObservationDiagnostics Diagnose(MainWindow? window, ObservedClient client)
    {
        var result = new ObservationDiagnostics
        {
            QueryCount = client.QueryCount,
            LatestSuccessfulSequence = client.Latest?.Sequence,
            LatestSuccessfulCompletionUtc = client.Latest?.CompletedAtUtc,
            LastCompletion = client.LastCompletion
        };
        if (window is null) return result;
        try
        {
            var usage = Controller<AppUsageTodayController>(window, "_appUsageToday");
            result.PageState = usage.Current.State;
            result.SelectedRange = usage.SelectedRange;
            result.Refreshing = usage.IsRefreshing;
            result.AppUsageTabSelected = Control<TabItem>(window, "AppUsageTab").IsSelected;
            result.AppUsageContentVisible = Control<FrameworkElement>(window, "AppUsageContent").IsVisible;
            result.SummaryVisible = Control<FrameworkElement>(window, "AppUsageSummaryPanel").IsVisible;
            result.WindowVisible = window.IsVisible;
            result.WindowState = window.WindowState;
            result.TrackedAppsText = Text(window, "AppUsageTrackedAppsText");
            result.DownloadText = Text(window, "AppUsageDownloadText");
            result.UploadText = Text(window, "AppUsageUploadText");
            result.LogicalTotalText = Text(window, "AppUsageLogicalTotalText");
        }
        catch { }
        return result;
    }

    private static void WriteCurrentObservation(string output, CurrentObservationReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        File.WriteAllText(output + ".writing", JsonSerializer.Serialize(report, options));
        File.Move(output + ".writing", output, overwrite: true);
    }

    private static CollectorObservation ReadCollectorObservation()
    {
        static async Task<CollectorObservation> ReadAsync()
        {
            await using var client = new IpcClient(ServicePipeSecurity.DefaultPipeName,
                serviceName: ServicePipeSecurity.ProductionServiceName);
            var connection = await client.ConnectAsync().ConfigureAwait(false);
            if (connection.State != IpcState.Connected)
                return new(connection.State, null, connection.Detail);
            var snapshot = await client.GetSnapshotAsync().ConfigureAwait(false);
            if (snapshot.State != IpcState.Connected || snapshot.Value is null)
                return new(snapshot.State == IpcState.Connected ? IpcState.MalformedResponse : snapshot.State,
                    null, snapshot.Detail);
            return new(IpcState.Connected, snapshot.Value.CollectorInstanceId, null);
        }
        return ReadAsync().GetAwaiter().GetResult();
    }

    private static PersistedRangeEvidence ReadProtectedRangeEvidence(string directory, string? expectedInstalledVersion)
    {
        // The explicitly elevated reader created this normal-readable evidence.
        // The normal desktop never opens the protected production SQLite file.
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "before-protected.json")));
        var evidence = json.RootElement;
        if (!evidence.GetProperty("Success").GetBoolean() || !evidence.GetProperty("ReadOnly").GetBoolean()
            || evidence.GetProperty("Phase").GetString() != "Before"
            || evidence.GetProperty("Installed").GetProperty("Version").GetString() != expectedInstalledVersion)
            throw new InvalidOperationException("The protected before-update history evidence is absent or belongs to another installed package.");
        var end = DateOnly.FromDateTime(DateTime.Now);
        var start = end.AddDays(-29);
        long download = 0, upload = 0;
        var count = 0;
        foreach (var row in evidence.GetProperty("Database").GetProperty("Rows").EnumerateArray())
        {
            var date = DateOnly.ParseExact(row[0].GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (date < start || date > end) continue;
            download = checked(download + long.Parse(row[5].GetString()!, CultureInfo.InvariantCulture));
            upload = checked(upload + long.Parse(row[6].GetString()!, CultureInfo.InvariantCulture));
            count++;
        }
        return new(download, upload, count);
    }

    private static bool SummaryMatches(MainWindow window, AppUsageQueryResult usage) =>
        Control<FrameworkElement>(window, "AppUsageSummaryPanel").IsVisible
        && Text(window, "AppUsageTrackedAppsText") == usage.TrackedApplicationCount.ToString(CultureInfo.CurrentCulture)
        && Text(window, "AppUsageDownloadText") == ByteFormatter.Format(usage.TotalDownloadBytes)
        && Text(window, "AppUsageUploadText") == ByteFormatter.Format(usage.TotalUploadBytes)
        && Text(window, "AppUsageLogicalTotalText") == ByteFormatter.Format(usage.LogicalTotalBytes);

    private static T Controller<T>(MainWindow window, string field) where T : class =>
        typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window) as T
        ?? throw new InvalidOperationException("The real controller is absent: " + field);
    private static T Control<T>(MainWindow window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException("Missing update control: " + name);
    private static T? FindAncestor<T>(DependencyObject value) where T : DependencyObject
    {
        for (var parent = System.Windows.Media.VisualTreeHelper.GetParent(value); parent is not null;
             parent = System.Windows.Media.VisualTreeHelper.GetParent(parent))
            if (parent is T result) return result;
        return null;
    }
    private static string Text(MainWindow window, string name) => Control<TextBlock>(window, name).Text;
    private static bool IsElevated(WindowsIdentity identity) => new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    private static void Wait(Func<bool> predicate, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > timeout) throw new TimeoutException("The bounded update observation did not reach its expected state.");
            HistoryOverviewChecks.Pump();
        }
        HistoryOverviewChecks.Pump();
    }
    private static void Write(string output, LiveReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        File.WriteAllText(output + ".writing", JsonSerializer.Serialize(report, options));
        File.Move(output + ".writing", output, overwrite: true);
    }
    private static int Finish(string output, LiveReport report)
    {
        report.Completed = true;
        Write(output, report);
        Console.WriteLine($"Collector update live validation: {(report.Success ? "passed" : "failed")}; update={report.UpdateOutcome}; report={output}");
        if (report.Error is not null) Console.Error.WriteLine(report.Error);
        return report.Success ? 0 : 1;
    }

    private sealed class ObservedService(IAppTrackingUpdateService inner) : IAppTrackingUpdateService
    {
        private int calls;
        private int otherCalls;
        private int outcome = -1;
        private long outcomeTicks;
        public int UpdateCalls => Volatile.Read(ref calls);
        public int OtherMutationCalls => Volatile.Read(ref otherCalls);
        public DateTimeOffset OutcomeAtUtc => new(Volatile.Read(ref outcomeTicks), TimeSpan.Zero);
        public AppTrackingUpdateOutcome? Outcome => Volatile.Read(ref outcome) is var value && value >= 0
            ? (AppTrackingUpdateOutcome)value : null;
        private event EventHandler? observationOnlySignal;
        public event EventHandler? StateChanged
        {
            add { inner.StateChanged += value; observationOnlySignal += value; }
            remove { inner.StateChanged -= value; observationOnlySignal -= value; }
        }
        public void RaiseReadOnlyObserverSignal() => observationOnlySignal?.Invoke(this, EventArgs.Empty);
        public event EventHandler? HistoryCleared { add => inner.HistoryCleared += value; remove => inner.HistoryCleared -= value; }
        public Task<AppTrackingServiceState> GetStateAsync(CancellationToken token) => inner.GetStateAsync(token);
        public Task<AppTrackingUpdateStatus> GetUpdateStatusAsync(CancellationToken token) => inner.GetUpdateStatusAsync(token);
        public Task<AppUsageStorageState> GetStorageStateAsync(CancellationToken token) => inner.GetStorageStateAsync(token);
        public Task<AppTrackingInstallOutcome> InstallAsync(CancellationToken token) { Interlocked.Increment(ref otherCalls); return inner.InstallAsync(token); }
        public Task<AppTrackingUninstallOutcome> UninstallAsync(CancellationToken token) { Interlocked.Increment(ref otherCalls); return inner.UninstallAsync(token); }
        public Task<AppUsageHistoryClearOutcome> ClearHistoryAsync(CancellationToken token) { Interlocked.Increment(ref otherCalls); return inner.ClearHistoryAsync(token); }
        public async Task<AppTrackingUpdateOutcome> UpdateAsync(CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            var result = await inner.UpdateAsync(token).ConfigureAwait(false);
            Volatile.Write(ref outcomeTicks, DateTimeOffset.UtcNow.Ticks);
            Volatile.Write(ref outcome, (int)result);
            return result;
        }
    }
    private sealed class ObservedClient(IAppUsageTodayClient inner) : IAppUsageTodayClient
    {
        private int count;
        private int sequence;
        private CompletedQuery? latest;
        private QueryCompletion? lastCompletion;
        public int QueryCount => Volatile.Read(ref count);
        public CompletedQuery? Latest => Volatile.Read(ref latest);
        public QueryCompletion? LastCompletion => Volatile.Read(ref lastCompletion);
        public async Task<IpcResult<AppUsageQueryResult>> GetAppUsageRangeAsync(AppUsageRange range, CancellationToken token)
        {
            Interlocked.Increment(ref count);
            var result = await inner.GetAppUsageRangeAsync(range, token).ConfigureAwait(false);
            Volatile.Write(ref lastCompletion, new(range, result.State, result.Value?.Availability, DateTimeOffset.UtcNow));
            if (result.State == IpcState.Connected && result.Value?.Availability == AppUsageAvailability.Available)
                Volatile.Write(ref latest, new(Interlocked.Increment(ref sequence), DateTimeOffset.UtcNow, result.Value));
            return result;
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private sealed record CompletedQuery(int Sequence, DateTimeOffset CompletedAtUtc, AppUsageQueryResult Value);
    private sealed record QueryCompletion(AppUsageRange RequestedRange, IpcState State, AppUsageAvailability? Availability,
        DateTimeOffset CompletedAtUtc);
    private sealed record RangeObservation(AppUsageRange Range, AppUsageAvailability Availability, int TrackedApplications,
        long DownloadBytes, long UploadBytes, long LogicalTotalBytes);
    private sealed record CollectorObservation(IpcState State, Guid? Instance, string? Detail);
    private sealed record PersistedRangeEvidence(long DownloadBytes, long UploadBytes, int Rows);
    private sealed class LiveReport
    {
        public bool Completed { get; set; }
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string? ClientSid { get; set; }
        public int ClientProcessId { get; set; }
        public bool ClientElevatedBefore { get; set; }
        public bool ClientElevatedAfter { get; set; }
        public bool ClientIdentityRetained { get; set; }
        public AppTrackingUpdateStatus? VersionBefore { get; set; }
        public AppTrackingUpdateStatus? VersionAfter { get; set; }
        public AppTrackingServiceState ServiceStateAfter { get; set; }
        public bool UpdateDisplayed { get; set; }
        public bool NoAutomaticUpdate { get; set; }
        public bool ExplicitUpdateClicked { get; set; }
        public int UpdateCalls { get; set; }
        public int OtherMutationCalls { get; set; }
        public AppTrackingUpdateOutcome? UpdateOutcome { get; set; }
        public AppUsageRange SelectedRangeBefore { get; set; }
        public AppUsageRange SelectedRangeAfter { get; set; }
        public int QueriesBeforeClick { get; set; }
        public int QueriesAfterClick { get; set; }
        public double UpdateElapsedSeconds { get; set; }
        public double ReconnectElapsedSeconds { get; set; }
        public IpcState BaselineIpcState { get; set; }
        public string? BaselineIpcDetail { get; set; }
        public bool BaselineSnapshotAvailable { get; set; }
        public bool BaselinePageUnavailable { get; set; }
        public string? BeforeRangeEvidence { get; set; }
        public long BeforeRangeDownloadBytes { get; set; }
        public long BeforeRangeUploadBytes { get; set; }
        public int BeforeRangePersistedRows { get; set; }
        public Guid? CollectorInstanceBefore { get; set; }
        public Guid? CollectorInstanceAfter { get; set; }
        public bool? FreshCollectorInstance { get; set; }
        public AppUsageQueryResult? UsageBefore { get; set; }
        public AppUsageQueryResult? UsageAfter { get; set; }
        public bool RangeCountersPreserved { get; set; }
        public bool ReconnectedWithoutNavigation { get; set; }
        public bool SettingsRunningAfter { get; set; }
        public bool ClearHistoryButtonAvailable { get; set; }
        public bool NoHorizontalOverflow { get; set; }
        public ObservationDiagnostics? Diagnostics { get; set; }
    }
    private sealed class ObservationDiagnostics
    {
        public int QueryCount { get; set; }
        public int? LatestSuccessfulSequence { get; set; }
        public DateTimeOffset? LatestSuccessfulCompletionUtc { get; set; }
        public QueryCompletion? LastCompletion { get; set; }
        public AppUsageTodayState? PageState { get; set; }
        public AppUsageRange? SelectedRange { get; set; }
        public bool? Refreshing { get; set; }
        public bool? AppUsageTabSelected { get; set; }
        public bool? AppUsageContentVisible { get; set; }
        public bool? SummaryVisible { get; set; }
        public bool? WindowVisible { get; set; }
        public WindowState? WindowState { get; set; }
        public string? TrackedAppsText { get; set; }
        public string? DownloadText { get; set; }
        public string? UploadText { get; set; }
        public string? LogicalTotalText { get; set; }
    }
    private sealed class CurrentObservationReport
    {
        public bool Completed { get; set; }
        public bool Success { get; set; }
        public string? Mode { get; set; }
        public string? Error { get; set; }
        public string? ClientSid { get; set; }
        public int ClientProcessId { get; set; }
        public bool ClientElevatedBefore { get; set; }
        public bool ClientElevatedAfter { get; set; }
        public bool ClientIdentityRetained { get; set; }
        public AppTrackingUpdateStatus? Version { get; set; }
        public AppTrackingServiceState ServiceState { get; set; }
        public string? SettingsStatus { get; set; }
        public bool UpdateButtonVisible { get; set; }
        public bool ClearHistoryButtonAvailable { get; set; }
        public bool LocalObserverSignalSent { get; set; }
        public bool ObserverRefreshWithoutNavigation { get; set; }
        public double ObserverRefreshElapsedSeconds { get; set; }
        public int QueriesBeforeSignal { get; set; }
        public int QueriesAfterSignal { get; set; }
        public AppUsageRange SelectedRangeBeforeSignal { get; set; }
        public AppUsageRange SelectedRangeAfterSignal { get; set; }
        public RangeObservation? RangeBeforeSignal { get; set; }
        public RangeObservation? RangeAfterSignal { get; set; }
        public int MutationCalls { get; set; }
        public ObservationDiagnostics? Diagnostics { get; set; }
    }
}
