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

// Explicit normal-token validation of the real page and authenticated service
// client. Physical monitoring uses an isolated fixture and generates no traffic.
internal static class AppUsageTodayLiveValidation
{
    public static int Run(string[] arguments)
    {
        string? output = null;
        string? screenshots = null;
        bool absent = false;
        for (var i = 0; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case "--output" when i + 1 < arguments.Length: output = Path.GetFullPath(arguments[++i]); break;
                case "--screenshots" when i + 1 < arguments.Length: screenshots = Path.GetFullPath(arguments[++i]); break;
                case "--absent": absent = true; break;
                default: Console.Error.WriteLine("Unrecognized Today live validation argument."); return 1;
            }
        }
        if (output is null) { Console.Error.WriteLine("An explicit output report is required."); return 1; }

        var elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        if (elevated) { Console.Error.WriteLine("Today page validation requires the normal user token."); return 1; }

        MainWindow? window = null;
        MonitoringService? monitoring = null;
        ObservedTodayClient? client = null;
        var report = new LiveReport { ClientElevated = elevated, AbsentProbe = absent };
        try
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var owned = Path.Combine(Path.GetDirectoryName(output)!, "OwnedPhysicalShell");
            monitoring = HistoryOverviewChecks.CreateMonitoring(new UsageStore(owned));
            client = new ObservedTodayClient(new AppUsageTodayClient(ServicePipeSecurity.DefaultServiceName));
            window = new MainWindow(monitoring, appUsageClient: client,
                appTrackingSetupService: new AppTrackingSetupService(
                    stateReader: new WindowsAppTrackingServiceStateReader(ServicePipeSecurity.DefaultServiceName)))
            {
                Width = 760, Height = 600, ShowActivated = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            window.Show();
            Select(window, "AppUsageTab");
            report.PageLoaded = Control<FrameworkElement>(window, "AppUsageContent").IsVisible;
            if (!report.PageLoaded) throw new InvalidOperationException("The real App Usage page did not become visible.");

            if (absent)
            {
                Wait(() => Text(window, "AppUsageStateText") == "App tracking is not installed.", TimeSpan.FromSeconds(12));
                report.UnavailableDisplayed = true;
                report.QueryCount = client.QueryCount;
                report.Success = true;
                return Finish(output, report);
            }

            Wait(() => client.LatestAvailable is { TrackedApplicationCount: > 0 } result && SummaryMatches(window, result),
                TimeSpan.FromSeconds(15));
            var first = client.LatestAvailable!;
            report.FirstToday = first;
            report.SummaryBound = SummaryMatches(window, first);
            report.FirstDisplayedSummary = ReadSummary(window);
            AssertRows(window, first);
            report.FirstRenderedApplicationCount = Control<ItemsControl>(window, "AppUsageApplications").Items.Count;
            Capture(window, screenshots, "760x600-today-top.png");

            // Only naturally occurring application traffic advances these values.
            // The page's production four-second cadence drives every query here.
            Wait(() => client.LatestAvailable is { } result && result.LogicalTotalBytes > first.LogicalTotalBytes &&
                SummaryMatches(window, result), TimeSpan.FromSeconds(30));
            report.LogicalTotalIncreased = true;
            report.AfterPassiveTraffic = client.LatestAvailable;

            Select(window, "DashboardTab");
            var hiddenBefore = client.QueryCount;
            report.HiddenQueriesBefore = hiddenBefore;
            var hiddenWatch = Stopwatch.StartNew();
            report.HiddenStartedSeconds = client.ElapsedSeconds;
            report.HiddenPageSelected = Control<TabItem>(window, "DashboardTab").IsSelected;
            report.HiddenContentVisible = Control<FrameworkElement>(window, "AppUsageContent").IsVisible;
            if (!report.HiddenPageSelected || report.HiddenContentVisible)
                throw new InvalidOperationException("Navigation did not hide the Today page.");
            while (hiddenWatch.Elapsed < TimeSpan.FromSeconds(8.5))
            {
                HistoryOverviewChecks.Pump();
                if (!Control<TabItem>(window, "DashboardTab").IsSelected || Control<FrameworkElement>(window, "AppUsageContent").IsVisible)
                    throw new InvalidOperationException("Page selection changed during the hidden observation.");
            }
            report.HiddenDurationSeconds = hiddenWatch.Elapsed.TotalSeconds;
            report.HiddenQueriesBefore = hiddenBefore;
            report.HiddenQueriesAfter = client.QueryCount;
            if (report.HiddenQueriesBefore != report.HiddenQueriesAfter)
                throw new InvalidOperationException("A Today query started while the page was hidden.");

            var lastCompleted = client.CompletedCount;
            Select(window, "AppUsageTab");
            Wait(() => client.CompletedCount > lastCompleted && client.LatestAvailable is { } result &&
                SummaryMatches(window, result), TimeSpan.FromSeconds(12));
            report.ReturnFresh = true;
            report.FinalToday = client.LatestAvailable;
            report.FinalDisplayedSummary = ReadSummary(window);
            AssertRows(window, report.FinalToday!);
            window.UpdateLayout();
            var scroll = Control<ScrollViewer>(window, "AppUsageScrollViewer");
            report.CompactNoHorizontalOverflow = scroll.ScrollableWidth <= 0.5 && scroll.ExtentWidth <= scroll.ViewportWidth + 0.5;
            if (!report.CompactNoHorizontalOverflow)
                throw new InvalidOperationException("The compact Today page has horizontal overflow.");
            if (scroll.ScrollableHeight > 0.5)
            {
                scroll.ScrollToEnd();
                window.UpdateLayout();
                HistoryOverviewChecks.Pump();
                report.VerticalScrollWorked = scroll.VerticalOffset > 0.5;
                if (!report.VerticalScrollWorked) throw new InvalidOperationException("The overflowing Today page did not scroll vertically.");
                Capture(window, screenshots, "760x600-today-bottom.png");
                scroll.ScrollToTop();
                window.UpdateLayout();
                HistoryOverviewChecks.Pump();
            }
            else report.VerticalScrollWorked = true; // Natural rows fit; fixture tests cover overflowing lists.
            report.NaturalListRequiredScrolling = scroll.ScrollableHeight > 0.5;
            Capture(window, screenshots, "760x600-today-return.png");
            report.QueryCount = client.QueryCount;
            report.MaximumConcurrentQueries = client.MaximumConcurrent;
            report.NoOverlap = client.MaximumConcurrent == 1;
            if (!report.NoOverlap) throw new InvalidOperationException("Today refresh requests overlapped.");
            report.QueryStarts = client.QueryStarts;
            report.Success = true;
            return Finish(output, report);
        }
        catch (Exception exception)
        {
            report.QueryStarts = client?.QueryStarts;
            report.Error = exception.Message;
            Console.Error.WriteLine("Today page live validation failed: " + exception.Message);
            return Finish(output, report);
        }
        finally
        {
            window?.Close();
            monitoring?.Dispose();
            Application.Current?.Shutdown();
        }
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
        if (!items.IsVisible || items.Items.Count != result.Applications.Count)
            throw new InvalidOperationException("The application list did not bind the authenticated Today rows.");
        var rows = items.Items.Cast<AppUsageApplicationRow>().ToArray();
        for (var index = 0; index < rows.Length; index++)
        {
            var expected = result.Applications[index];
            var row = rows[index];
            if (row.ApplicationIdentity != expected.ApplicationIdentity || row.DisplayName != expected.DisplayName ||
                row.DownloadText != ByteFormatter.Format(expected.DownloadBytes) ||
                row.UploadText != ByteFormatter.Format(expected.UploadBytes) || row.TotalText != ByteFormatter.Format(expected.TotalBytes))
                throw new InvalidOperationException("A real application row changed IPC ordering or cumulative values.");
        }
        window.UpdateLayout();
        var rendered = HistoryOverviewChecks.Descendants(items).OfType<TextBlock>().Select(text => text.Text).ToArray();
        foreach (var app in result.Applications)
            if (!rendered.Contains(app.DisplayName, StringComparer.Ordinal))
                throw new InvalidOperationException("A returned application display name was absent from the real rows.");
    }

    private static int Finish(string output, LiveReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        var temporary = output + ".writing";
        File.WriteAllText(temporary, JsonSerializer.Serialize(report, options));
        File.Move(temporary, output, overwrite: true);
        Console.WriteLine($"Normal-token Today page validation: {(report.Success ? "passed" : "failed")}; queries={report.QueryCount}; report={output}");
        return report.Success ? 0 : 1;
    }

    private static void Select(MainWindow window, string name)
    {
        var tab = Control<TabItem>(window, name);
        Control<TabControl>(window, "MainNavigation").SelectedItem = tab;
        // An activated native TabControl also tracks keyboard focus. Move it
        // with selection so activation cannot restore the previously focused tab.
        tab.Focus();
        window.UpdateLayout();
        HistoryOverviewChecks.Pump();
    }

    private static void Capture(MainWindow window, string? directory, string name)
    {
        if (directory is not null) HistoryOverviewChecks.SavePreview(window, Path.Combine(directory, name));
    }

    private static void Wait(Func<bool> predicate, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > timeout) throw new TimeoutException("The bounded Today page observation did not reach its expected state.");
            HistoryOverviewChecks.Pump();
        }
        HistoryOverviewChecks.Pump();
    }

    private static T Control<T>(MainWindow window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException("Missing Today control: " + name);
    private static string Text(MainWindow window, string name) => Control<TextBlock>(window, name).Text;
    private static DisplayedSummary ReadSummary(MainWindow window) => new(
        Text(window, "AppUsageTrackedAppsText"), Text(window, "AppUsageDownloadText"),
        Text(window, "AppUsageUploadText"), Text(window, "AppUsageLogicalTotalText"));
    private sealed record DisplayedSummary(string TrackedApps, string Download, string Upload, string LogicalTotal);

    private sealed class ObservedTodayClient(IAppUsageTodayClient inner) : IAppUsageTodayClient
    {
        private int queries;
        private int completed;
        private int active;
        private int maximum;
        private AppUsageQueryResult? latest;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<QueryStart> starts = [];
        public double ElapsedSeconds => clock.Elapsed.TotalSeconds;
        public QueryStart[] QueryStarts { get { lock (starts) return starts.ToArray(); } }
        public int QueryCount => Volatile.Read(ref queries);
        public int CompletedCount => Volatile.Read(ref completed);
        public int MaximumConcurrent => Volatile.Read(ref maximum);
        public AppUsageQueryResult? LatestAvailable => Volatile.Read(ref latest);
        public async Task<IpcResult<AppUsageQueryResult>> GetAppUsageRangeAsync(AppUsageRange range, CancellationToken cancellationToken)
        {
            lock (starts) starts.Add(new(clock.Elapsed.TotalSeconds, cancellationToken.IsCancellationRequested));
            Interlocked.Increment(ref queries);
            var concurrency = Interlocked.Increment(ref active);
            if (concurrency > maximum) Interlocked.Exchange(ref maximum, concurrency);
            try
            {
                var result = await inner.GetAppUsageRangeAsync(range, cancellationToken).ConfigureAwait(false);
                if (result.State == IpcState.Connected && result.Value?.Availability == AppUsageAvailability.Available)
                    Volatile.Write(ref latest, result.Value);
                Interlocked.Increment(ref completed);
                return result;
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed record QueryStart(double ElapsedSeconds, bool CancellationRequested);

    private sealed class LiveReport
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public bool ClientElevated { get; set; }
        public bool AbsentProbe { get; set; }
        public bool PageLoaded { get; set; }
        public bool SummaryBound { get; set; }
        public bool UnavailableDisplayed { get; set; }
        public int FirstRenderedApplicationCount { get; set; }
        public AppUsageQueryResult? FirstToday { get; set; }
        public DisplayedSummary? FirstDisplayedSummary { get; set; }
        public AppUsageQueryResult? AfterPassiveTraffic { get; set; }
        public AppUsageQueryResult? FinalToday { get; set; }
        public DisplayedSummary? FinalDisplayedSummary { get; set; }
        public bool LogicalTotalIncreased { get; set; }
        public double HiddenDurationSeconds { get; set; }
        public double HiddenStartedSeconds { get; set; }
        public bool HiddenPageSelected { get; set; }
        public bool HiddenContentVisible { get; set; }
        public QueryStart[]? QueryStarts { get; set; }
        public int HiddenQueriesBefore { get; set; }
        public int HiddenQueriesAfter { get; set; }
        public bool ReturnFresh { get; set; }
        public bool CompactNoHorizontalOverflow { get; set; }
        public bool NaturalListRequiredScrolling { get; set; }
        public bool VerticalScrollWorked { get; set; }
        public int QueryCount { get; set; }
        public int MaximumConcurrentQueries { get; set; }
        public bool NoOverlap { get; set; }
    }
}
