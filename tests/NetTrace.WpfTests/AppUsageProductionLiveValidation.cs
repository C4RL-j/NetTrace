using System.Diagnostics;
using System.Globalization;
using System.IO;
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

// Explicit normal-token observation of the production service through the real
// window. Only physical monitoring uses an owned fixture; app queries use IPC.
internal static class AppUsageProductionLiveValidation
{
    public static int Run(string[] arguments)
    {
        if (arguments.Length != 2 || arguments[0] != "--output" || !Path.IsPathFullyQualified(arguments[1]))
        {
            Console.Error.WriteLine("Production live validation requires --output ABSOLUTE_REPORT_PATH.");
            return 1;
        }
        var output = Path.GetFullPath(arguments[1]);
        var directory = Path.GetDirectoryName(output)!;
        var report = new LiveReport
        {
            ClientElevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
            ServiceName = ServicePipeSecurity.ProductionServiceName
        };
        if (report.ClientElevated)
        {
            Console.Error.WriteLine("Production page validation requires the normal user token.");
            return 1;
        }
        MainWindow? window = null;
        MonitoringService? monitoring = null;
        ObservedClient? client = null;
        try
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            monitoring = HistoryOverviewChecks.CreateMonitoring(new UsageStore(Path.Combine(directory, "OwnedPhysicalShell")));
            client = new ObservedClient(new AppUsageTodayClient());
            window = new MainWindow(monitoring, appUsageClient: client)
            {
                Width = 760, Height = 600, ShowActivated = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            window.Show();
            var tab = Control<TabItem>(window, "AppUsageTab");
            Control<TabControl>(window, "MainNavigation").SelectedItem = tab;
            tab.Focus();
            window.UpdateLayout();
            HistoryOverviewChecks.Pump();
            if (!Control<FrameworkElement>(window, "AppUsageContent").IsVisible)
                throw new InvalidOperationException("The production App Usage page was not visible.");
            report.PageVisible = true;
            foreach (var range in Ranges)
            {
                var result = LoadRange(window, client, range);
                report.BeforeRestart.Add(new(range, result, ReadSummary(window)));
                Capture(window, directory, "before-" + range + ".png");
            }
            WriteMarker(directory, "client-ready-for-restart.json");
            Wait(() => File.Exists(Path.Combine(directory, "service-stopped.json")), TimeSpan.FromSeconds(45), "The service stop marker did not arrive.");
            Wait(() => Text(window, "AppUsageStateText") is "App tracking unavailable." or "App tracking is stopped.", TimeSpan.FromSeconds(16),
                "The real page did not show unavailable while the production service was stopped.");
            report.UnavailableDuringRestart = true;
            Capture(window, directory, "service-stopped.png");
            var priorCompletion = client.Latest?.Sequence ?? 0;
            WriteMarker(directory, "client-observed-unavailable.json");
            Wait(() => File.Exists(Path.Combine(directory, "service-restarted.json")), TimeSpan.FromSeconds(45), "The service restart marker did not arrive.");
            Wait(() => client.Latest is { Sequence: var sequence, Value: { Availability: AppUsageAvailability.Available } result }
                    && sequence > priorCompletion && result.Range == AppUsageRange.ThirtyDays && SummaryMatches(window, result),
                TimeSpan.FromSeconds(20), "The selected 30D page did not recover after service restart.");
            report.RecoveredWithoutNavigation = Control<RadioButton>(window, "AppUsage30DButton").IsChecked == true;
            if (!report.RecoveredWithoutNavigation) throw new InvalidOperationException("Restart recovery changed the selected range.");
            foreach (var range in Ranges)
            {
                var result = LoadRange(window, client, range);
                var before = report.BeforeRestart.Single(item => item.Range == range).Value;
                if (result.TotalDownloadBytes < before.TotalDownloadBytes || result.TotalUploadBytes < before.TotalUploadBytes)
                    throw new InvalidOperationException("A persisted range decreased after the clean service restart.");
                report.AfterRestart.Add(new(range, result, ReadSummary(window)));
                Capture(window, directory, "after-" + range + ".png");
            }
            report.QueryStarts = client.Starts;
            report.MaximumConcurrentQueries = client.MaximumConcurrent;
            if (report.MaximumConcurrentQueries != 1) throw new InvalidOperationException("Production page requests overlapped.");
            var scroll = Control<ScrollViewer>(window, "AppUsageScrollViewer");
            report.CompactNoHorizontalOverflow = scroll.ScrollableWidth <= 0.5;
            if (!report.CompactNoHorizontalOverflow) throw new InvalidOperationException("The compact production page had horizontal overflow.");
            report.Success = true;
            return Finish(output, report);
        }
        catch (Exception exception)
        {
            report.QueryStarts = client?.Starts ?? [];
            report.Error = exception.Message;
            Console.Error.WriteLine("Production App Usage page validation failed: " + exception.Message);
            return Finish(output, report);
        }
        finally
        {
            window?.Close();
            monitoring?.Dispose();
            Application.Current?.Shutdown();
        }
    }

    private static readonly AppUsageRange[] Ranges = [AppUsageRange.Today, AppUsageRange.SevenDays, AppUsageRange.ThirtyDays];

    private static AppUsageQueryResult LoadRange(MainWindow window, ObservedClient client, AppUsageRange range)
    {
        var button = Control<RadioButton>(window, range switch
        {
            AppUsageRange.Today => "AppUsageTodayButton",
            AppUsageRange.SevenDays => "AppUsage7DButton",
            _ => "AppUsage30DButton"
        });
        button.IsChecked = true;
        button.Focus();
        window.UpdateLayout();
        Wait(() => client.Latest?.Value is { Availability: AppUsageAvailability.Available } result && result.Range == range
                && button.IsChecked == true && SummaryMatches(window, result),
            TimeSpan.FromSeconds(20), "The production " + range + " range did not load through IPC.");
        var value = client.Latest!.Value!;
        AssertRows(window, value);
        return value;
    }

    private static bool SummaryMatches(MainWindow window, AppUsageQueryResult result) =>
        Control<FrameworkElement>(window, "AppUsageSummaryPanel").IsVisible &&
        Text(window, "AppUsageTrackedAppsText") == result.TrackedApplicationCount.ToString(CultureInfo.CurrentCulture) &&
        Text(window, "AppUsageDownloadText") == ByteFormatter.Format(result.TotalDownloadBytes) &&
        Text(window, "AppUsageUploadText") == ByteFormatter.Format(result.TotalUploadBytes) &&
        Text(window, "AppUsageLogicalTotalText") == ByteFormatter.Format(result.LogicalTotalBytes);

    private static void AssertRows(MainWindow window, AppUsageQueryResult result)
    {
        var items = Control<ItemsControl>(window, "AppUsageApplications");
        if (items.Items.Count != result.Applications.Count || (items.Items.Count > 0 && !items.IsVisible))
            throw new InvalidOperationException("The production application list did not bind the returned rows.");
        var rows = items.Items.Cast<AppUsageApplicationRow>().ToArray();
        for (var index = 0; index < rows.Length; index++)
        {
            var expected = result.Applications[index];
            if (rows[index].ApplicationIdentity != expected.ApplicationIdentity || rows[index].DisplayName != expected.DisplayName ||
                rows[index].DownloadText != ByteFormatter.Format(expected.DownloadBytes) ||
                rows[index].UploadText != ByteFormatter.Format(expected.UploadBytes) ||
                rows[index].TotalText != ByteFormatter.Format(expected.TotalBytes))
                throw new InvalidOperationException("A production row changed IPC ordering or byte values.");
        }
    }

    private static DisplayedSummary ReadSummary(MainWindow window) => new(
        Text(window, "AppUsageTrackedAppsText"), Text(window, "AppUsageDownloadText"),
        Text(window, "AppUsageUploadText"), Text(window, "AppUsageLogicalTotalText"));
    private static T Control<T>(MainWindow window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException("Missing production page control: " + name);
    private static string Text(MainWindow window, string name) => Control<TextBlock>(window, name).Text;
    private static void Capture(MainWindow window, string directory, string name) =>
        HistoryOverviewChecks.SavePreview(window, Path.Combine(directory, "Screenshots", name));
    private static void WriteMarker(string directory, string name) => File.WriteAllText(Path.Combine(directory, name), "{}");
    private static void Wait(Func<bool> predicate, TimeSpan timeout, string failure)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > timeout) throw new TimeoutException(failure);
            HistoryOverviewChecks.Pump();
        }
        HistoryOverviewChecks.Pump();
    }
    private static int Finish(string output, LiveReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        File.WriteAllText(output + ".writing", JsonSerializer.Serialize(report, options));
        File.Move(output + ".writing", output, overwrite: true);
        Console.WriteLine($"Normal-token production App Usage validation: {(report.Success ? "passed" : "failed")}; report={output}");
        return report.Success ? 0 : 1;
    }

    private sealed class ObservedClient(IAppUsageTodayClient inner) : IAppUsageTodayClient
    {
        private int sequence;
        private int active;
        private int maximum;
        private CompletedQuery? latest;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<QueryStart> starts = [];
        public CompletedQuery? Latest => Volatile.Read(ref latest);
        public int MaximumConcurrent => Volatile.Read(ref maximum);
        public QueryStart[] Starts { get { lock (starts) return starts.ToArray(); } }
        public async Task<IpcResult<AppUsageQueryResult>> GetAppUsageRangeAsync(AppUsageRange range, CancellationToken cancellationToken)
        {
            lock (starts) starts.Add(new(clock.Elapsed.TotalSeconds, range));
            var concurrency = Interlocked.Increment(ref active);
            if (concurrency > maximum) Interlocked.Exchange(ref maximum, concurrency);
            try
            {
                var result = await inner.GetAppUsageRangeAsync(range, cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref latest, new(Interlocked.Increment(ref sequence), result.State == IpcState.Connected ? result.Value : null));
                return result;
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private sealed record CompletedQuery(int Sequence, AppUsageQueryResult? Value);
    private sealed record QueryStart(double ElapsedSeconds, AppUsageRange Range);
    private sealed record DisplayedSummary(string TrackedApps, string Download, string Upload, string LogicalTotal);
    private sealed record RangeObservation(AppUsageRange Range, AppUsageQueryResult Value, DisplayedSummary DisplayedSummary);
    private sealed class LiveReport
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public bool ClientElevated { get; set; }
        public string ServiceName { get; set; } = "";
        public bool PageVisible { get; set; }
        public List<RangeObservation> BeforeRestart { get; } = [];
        public List<RangeObservation> AfterRestart { get; } = [];
        public bool UnavailableDuringRestart { get; set; }
        public bool RecoveredWithoutNavigation { get; set; }
        public bool CompactNoHorizontalOverflow { get; set; }
        public int MaximumConcurrentQueries { get; set; }
        public QueryStart[] QueryStarts { get; set; } = [];
    }
}
