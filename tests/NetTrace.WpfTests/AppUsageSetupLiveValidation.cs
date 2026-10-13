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

// Explicit live workflow only. This uses the real setup service, installer and
// production IPC; physical monitoring uses an owned isolated fixture.
internal static class AppUsageSetupLiveValidation
{
    public static int Run(string[] arguments)
    {
        if (arguments.Length != 2 || arguments[0] != "--output" || !Path.IsPathFullyQualified(arguments[1]))
        {
            Console.Error.WriteLine("Setup UI live validation requires --output ABSOLUTE_JSON_PATH.");
            return 1;
        }
        var output = Path.GetFullPath(arguments[1]);
        using var originalIdentity = WindowsIdentity.GetCurrent();
        var report = new LiveReport
        {
            ClientSid = originalIdentity.User?.Value,
            ClientElevatedBefore = IsElevated(originalIdentity),
            ClientProcessId = Environment.ProcessId
        };
        if (report.ClientElevatedBefore)
        {
            report.Error = "Setup UI live validation requires the normal user token.";
            return Finish(output, report);
        }

        MainWindow? window = null;
        MonitoringService? monitoring = null;
        var setup = new ObservedSetupService(new AppTrackingSetupService());
        var client = new ObservedClient(new AppUsageTodayClient());
        try
        {
            report.BaselineState = new WindowsAppTrackingServiceStateReader().GetState();
            if (report.BaselineState != AppTrackingServiceState.Missing)
                throw new InvalidOperationException("Live setup validation refuses an existing production service.");
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var directory = Path.GetDirectoryName(output)!;
            monitoring = HistoryOverviewChecks.CreateMonitoring(new UsageStore(Path.Combine(directory, "OwnedPhysicalShell")));
            window = new MainWindow(monitoring, appUsageClient: client, appTrackingSetupService: setup)
            {
                Width = 760, Height = 600, ShowActivated = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            window.Show();
            var navigation = Control<TabControl>(window, "MainNavigation");
            var tab = Control<TabItem>(window, "AppUsageTab");
            navigation.SelectedItem = tab;
            tab.Focus();
            Control<RadioButton>(window, "AppUsage30DButton").IsChecked = true;
            window.UpdateLayout();
            var controller = (AppUsageTodayController?)typeof(MainWindow)
                .GetField("_appUsageToday", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
                ?? throw new InvalidOperationException("The real App Usage controller is absent.");
            Wait(() => controller.Current.State == AppUsageTodayState.Missing, TimeSpan.FromSeconds(12));
            var install = Control<Button>(window, "AppUsageInstallButton");
            report.SetupDisplayed = Text(window, "AppUsageStateText") == "App tracking is not installed."
                && Text(window, "AppUsageDetailText") == "Track network usage by application in the background."
                && install.IsVisible && install.IsEnabled && install.Content?.ToString() == "Install App Tracking";
            report.NoAutomaticInstall = setup.InstallCalls == 0;
            report.QueriesBeforeClick = client.QueryCount;
            report.SelectedRangeBefore = controller.SelectedRange;
            if (!report.SetupDisplayed || !report.NoAutomaticInstall || client.QueryCount != 0
                || controller.SelectedRange != AppUsageRange.ThirtyDays)
                throw new InvalidOperationException("The missing-service setup state did not preserve the explicit action and selected range.");
            install.Focus();
            HistoryOverviewChecks.SavePreview(window, Path.Combine(directory, "760x600-setup.png"));

            // The caller explicitly invoked this interactive workflow. Raising
            // the button's real routed Click is its sole install/elevation action.
            report.ExplicitInstallClicked = true;
            Write(output, report);
            install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, install));
            Wait(() => setup.Outcome is not null && !controller.IsInstalling, TimeSpan.FromSeconds(120));
            report.InstallOutcome = setup.Outcome;
            report.InstallCalls = setup.InstallCalls;
            using var identityAfter = WindowsIdentity.GetCurrent();
            report.ClientElevatedAfter = IsElevated(identityAfter);
            report.ClientIdentityRetained = identityAfter.User?.Value == report.ClientSid;
            report.SelectedRangeAfter = controller.SelectedRange;
            if (report.ClientElevatedAfter || !report.ClientIdentityRetained || report.InstallCalls != 1
                || controller.SelectedRange != AppUsageRange.ThirtyDays)
                throw new InvalidOperationException("Install changed the desktop token, launched more than once, or lost the selected range.");

            if (report.InstallOutcome == AppTrackingInstallOutcome.Cancelled)
            {
                report.CancellationReturnedToSetup = controller.Current.State == AppUsageTodayState.Missing
                    && Text(window, "AppUsageSetupErrorText") == "Installation was cancelled."
                    && install.IsVisible && install.IsEnabled;
                report.Success = report.CancellationReturnedToSetup;
                report.ServiceStateAfter = new WindowsAppTrackingServiceStateReader().GetState();
                HistoryOverviewChecks.SavePreview(window, Path.Combine(directory, "760x600-uac-cancelled.png"));
                return Finish(output, report);
            }
            if (report.InstallOutcome != AppTrackingInstallOutcome.Succeeded)
                throw new InvalidOperationException("The explicit installer did not succeed: " + report.InstallOutcome);

            Wait(() => client.LatestAvailable is { Range: AppUsageRange.ThirtyDays } result
                && controller.Current.State is AppUsageTodayState.Available or AppUsageTodayState.Empty
                && SummaryMatches(window, result), TimeSpan.FromSeconds(15));
            report.ServiceStateAfter = new WindowsAppTrackingServiceStateReader().GetState();
            report.ConnectedRange = client.LatestAvailable;
            report.SummaryBound = SummaryMatches(window, report.ConnectedRange!);
            AssertRows(window, report.ConnectedRange!);
            report.RowsBound = true;
            report.ReconnectedWithoutNavigation = tab.IsSelected && controller.SelectedRange == AppUsageRange.ThirtyDays
                && report.SummaryBound && !install.IsVisible;
            report.QueriesAfterClick = client.QueryCount;
            report.RequestedRanges = client.RequestedRanges;
            window.UpdateLayout();
            var scroll = Control<ScrollViewer>(window, "AppUsageScrollViewer");
            report.NoHorizontalOverflow = scroll.ScrollableWidth <= 0.5 && scroll.ExtentWidth <= scroll.ViewportWidth + 0.5;
            report.Registration = ReadRegistration();
            HistoryOverviewChecks.SavePreview(window, Path.Combine(directory, "760x600-installed-30d.png"));
            report.Success = report.ServiceStateAfter == AppTrackingServiceState.Running
                && report.ReconnectedWithoutNavigation && report.NoHorizontalOverflow;
            return Finish(output, report);
        }
        catch (Exception exception)
        {
            report.Error = exception.Message;
            report.InstallCalls = setup.InstallCalls;
            report.InstallOutcome = setup.Outcome;
            report.ServiceStateAfter = new WindowsAppTrackingServiceStateReader().GetState();
            if (report.ExplicitInstallClicked && report.ServiceStateAfter != AppTrackingServiceState.Missing)
            {
                try { report.Registration = ReadRegistration(); }
                catch (Exception statusError) { report.StatusError = statusError.Message; }
            }
            return Finish(output, report);
        }
        finally
        {
            window?.Close();
            monitoring?.Dispose();
            Application.Current?.Shutdown();
        }
    }

    private static JsonElement ReadRegistration()
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "AppTracking", "Installer", "NetTrace.AppUsageInstaller.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("status");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The read-only installer status tool did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000)) throw new TimeoutException("The read-only installer status query timed out.");
        if (process.ExitCode != 0) throw new InvalidOperationException("The read-only installer status query failed: " + standardError.GetAwaiter().GetResult());
        using var json = JsonDocument.Parse(standardOutput.GetAwaiter().GetResult());
        return json.RootElement.Clone();
    }

    private static bool SummaryMatches(MainWindow window, AppUsageQueryResult usage) =>
        Control<FrameworkElement>(window, "AppUsageSummaryPanel").IsVisible
        && Text(window, "AppUsageTrackedAppsText") == usage.TrackedApplicationCount.ToString(CultureInfo.CurrentCulture)
        && Text(window, "AppUsageDownloadText") == ByteFormatter.Format(usage.TotalDownloadBytes)
        && Text(window, "AppUsageUploadText") == ByteFormatter.Format(usage.TotalUploadBytes)
        && Text(window, "AppUsageLogicalTotalText") == ByteFormatter.Format(usage.LogicalTotalBytes);

    private static void AssertRows(MainWindow window, AppUsageQueryResult usage)
    {
        var rows = Control<ItemsControl>(window, "AppUsageApplications").Items.Cast<AppUsageApplicationRow>().ToArray();
        if (rows.Length != usage.Applications.Count) throw new InvalidOperationException("The connected application row count differs from IPC.");
        for (var index = 0; index < rows.Length; index++)
        {
            var expected = usage.Applications[index];
            var row = rows[index];
            if (row.ApplicationIdentity != expected.ApplicationIdentity || row.DisplayName != expected.DisplayName
                || row.DownloadText != ByteFormatter.Format(expected.DownloadBytes)
                || row.UploadText != ByteFormatter.Format(expected.UploadBytes)
                || row.TotalText != ByteFormatter.Format(expected.TotalBytes))
                throw new InvalidOperationException("A connected application row differs from IPC.");
        }
    }

    private static bool IsElevated(WindowsIdentity identity) => new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    private static T Control<T>(MainWindow window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException("Missing setup control: " + name);
    private static string Text(MainWindow window, string name) => Control<TextBlock>(window, name).Text;
    private static void Wait(Func<bool> predicate, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > timeout) throw new TimeoutException("The bounded setup observation did not reach its expected state.");
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
        Console.WriteLine($"Setup UI live validation: {(report.Success ? "passed" : "failed")}; installer={report.InstallOutcome}; report={output}");
        if (report.Error is not null) Console.Error.WriteLine(report.Error);
        return report.Success ? 0 : 1;
    }

    private sealed class ObservedSetupService(IAppTrackingSetupService inner) : IAppTrackingSetupService
    {
        private int calls;
        private int outcome = -1;
        public int InstallCalls => Volatile.Read(ref calls);
        public AppTrackingInstallOutcome? Outcome => Volatile.Read(ref outcome) is var value && value >= 0
            ? (AppTrackingInstallOutcome)value : null;
        public Task<AppTrackingServiceState> GetStateAsync(CancellationToken cancellationToken) => inner.GetStateAsync(cancellationToken);
        public async Task<AppTrackingInstallOutcome> InstallAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            var result = await inner.InstallAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref outcome, (int)result);
            return result;
        }
    }
    private sealed class ObservedClient(IAppUsageTodayClient inner) : IAppUsageTodayClient
    {
        private readonly List<AppUsageRange> ranges = [];
        private AppUsageQueryResult? latest;
        public int QueryCount { get { lock (ranges) return ranges.Count; } }
        public AppUsageRange[] RequestedRanges { get { lock (ranges) return ranges.ToArray(); } }
        public AppUsageQueryResult? LatestAvailable => Volatile.Read(ref latest);
        public async Task<IpcResult<AppUsageQueryResult>> GetAppUsageRangeAsync(AppUsageRange range, CancellationToken cancellationToken)
        {
            lock (ranges) ranges.Add(range);
            var result = await inner.GetAppUsageRangeAsync(range, cancellationToken).ConfigureAwait(false);
            if (result.State == IpcState.Connected && result.Value?.Availability == AppUsageAvailability.Available)
                Volatile.Write(ref latest, result.Value);
            return result;
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private sealed class LiveReport
    {
        public bool Completed { get; set; }
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string? StatusError { get; set; }
        public string? ClientSid { get; set; }
        public int ClientProcessId { get; set; }
        public bool ClientElevatedBefore { get; set; }
        public bool ClientElevatedAfter { get; set; }
        public bool ClientIdentityRetained { get; set; }
        public AppTrackingServiceState BaselineState { get; set; }
        public AppTrackingServiceState ServiceStateAfter { get; set; }
        public bool SetupDisplayed { get; set; }
        public bool NoAutomaticInstall { get; set; }
        public bool ExplicitInstallClicked { get; set; }
        public int InstallCalls { get; set; }
        public AppTrackingInstallOutcome? InstallOutcome { get; set; }
        public bool CancellationReturnedToSetup { get; set; }
        public AppUsageRange SelectedRangeBefore { get; set; }
        public AppUsageRange SelectedRangeAfter { get; set; }
        public int QueriesBeforeClick { get; set; }
        public int QueriesAfterClick { get; set; }
        public AppUsageRange[]? RequestedRanges { get; set; }
        public AppUsageQueryResult? ConnectedRange { get; set; }
        public bool SummaryBound { get; set; }
        public bool RowsBound { get; set; }
        public bool ReconnectedWithoutNavigation { get; set; }
        public bool NoHorizontalOverflow { get; set; }
        public JsonElement? Registration { get; set; }
    }
}
