using System.Collections.Concurrent;
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

// Explicit production observation. The external lifecycle workflow performs
// service operations; this normal-token client uses only the existing UI timer.
internal static class AppUsageLifecycleLiveValidation
{
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();
    private static readonly string[] AllowedEvents = ["Update", "Restart", "ForcedRecovery", "Sleep", "Startup"];

    public static int Run(string[] arguments)
    {
        if (arguments.Length != 6 || arguments[0] != "--directory" || !Path.IsPathFullyQualified(arguments[1])
            || arguments[2] != "--events" || arguments[4] != "--range")
        {
            Console.Error.WriteLine("Lifecycle observation requires --directory ABSOLUTE_PATH --events Update,Restart,ForcedRecovery --range Today|7D|30D.");
            return 1;
        }
        var events = arguments[3].Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (events.Length == 0 || events.Distinct(StringComparer.Ordinal).Count() != events.Length
            || events.Any(value => !AllowedEvents.Contains(value, StringComparer.Ordinal)))
            return 1;
        var range = arguments[5] switch
        {
            "Today" => AppUsageRange.Today, "7D" => AppUsageRange.SevenDays, "30D" => AppUsageRange.ThirtyDays,
            _ => (AppUsageRange)(-1)
        };
        if (!Enum.IsDefined(range)) return 1;
        var directory = Path.GetFullPath(arguments[1]);
        Directory.CreateDirectory(directory);
        var report = new LifecycleReport { Events = events, SelectedRange = range, ClientProcessId = Environment.ProcessId };
        MainWindow? window = null;
        MonitoringService? monitoring = null;
        var client = new ObservedClient(new AppUsageTodayClient());
        var setup = new ReadOnlyService(new AppTrackingSetupService());
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            report.ClientElevatedBefore = IsElevated(identity);
            if (report.ClientElevatedBefore) throw new InvalidOperationException("Lifecycle observation requires the normal user token.");
            var sid = identity.User?.Value;
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            monitoring = HistoryOverviewChecks.CreateMonitoring(new UsageStore(Path.Combine(directory, "OwnedPhysicalLifecycleShell")));
            window = new MainWindow(monitoring, appUsageClient: client, appTrackingSetupService: setup)
            {
                Width = 760, Height = 600, ShowActivated = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            window.Show();
            var navigation = Control<TabControl>(window, "MainNavigation");
            var usageTab = Control<TabItem>(window, "AppUsageTab");
            navigation.SelectedItem = usageTab;
            SelectRange(window, range);
            var controller = Controller(window);
            var updateFirst = events[0] == "Update";
            Wait(() => IsFreshVisible(window, controller, client, range, DateTimeOffset.MinValue)
                || updateFirst && IsRepairableUnavailable(controller, client, DateTimeOffset.MinValue), TimeSpan.FromSeconds(30));
            report.InitialRange = client.Latest?.Counters;
            report.InitialCollector = ReadSnapshot();
            report.InitialPageUnavailable = controller.Current.State == AppUsageTodayState.Unavailable;
            if (report.InitialPageUnavailable || report.InitialCollector.State != IpcState.Connected)
            {
                if (!updateFirst || !IsRepairableState(report.InitialCollector.State)
                    || setup.GetStateAsync(CancellationToken.None).GetAwaiter().GetResult() != AppTrackingServiceState.Running)
                    throw new InvalidOperationException("Only an initial explicit Update may repair a Running collector with a typed transport disconnect.");
                report.InitialUnavailableRepair = true;
            }
            else RequireCollector(report.InitialCollector);

            // A full period while hidden exercises the actual window timer,
            // rather than manually invoking controller Tick or a fake event.
            Wait(() => !controller.IsRefreshing, TimeSpan.FromSeconds(10));
            navigation.SelectedItem = Control<TabItem>(window, "SettingsTab");
            HistoryOverviewChecks.Pump();
            var hiddenQueries = client.QueryCount;
            var hiddenStarted = Stopwatch.StartNew();
            Wait(() => hiddenStarted.Elapsed >= TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(9));
            report.HiddenQueryDelta = client.QueryCount - hiddenQueries;
            if (report.HiddenQueryDelta != 0) throw new InvalidOperationException("Hidden App Usage sent a refresh request.");
            navigation.SelectedItem = usageTab;
            var returnedAt = DateTimeOffset.UtcNow;
            Wait(() => IsFreshVisible(window, controller, client, range, returnedAt)
                || report.InitialUnavailableRepair && IsRepairableUnavailable(controller, client, returnedAt), TimeSpan.FromSeconds(30));

            foreach (var name in events)
            {
                var observation = new EventObservation { Event = name, RangeBefore = client.Latest?.Counters,
                    CollectorBefore = ReadSnapshot(), ReadyAtUtc = DateTimeOffset.UtcNow };
                observation.UnavailableBaselineRepair = name == "Update" && report.InitialUnavailableRepair;
                if (observation.UnavailableBaselineRepair)
                {
                    if (!IsRepairableState(observation.CollectorBefore.State)
                        || setup.GetStateAsync(CancellationToken.None).GetAwaiter().GetResult() != AppTrackingServiceState.Running)
                        throw new InvalidOperationException("Initial update baseline is not a Running owned collector with a transport disconnect.");
                }
                else RequireCollector(observation.CollectorBefore);
                report.Observations.Add(observation);
                observation.QueryCountBefore = client.QueryCount;
                Write(Path.Combine(directory, "ready-" + name + ".json"), new
                {
                    Event = name, observation.ReadyAtUtc, SelectedRange = range, ClientProcessId = Environment.ProcessId,
                    ClientElevated = false, Collector = observation.CollectorBefore, Range = observation.RangeBefore
                });
                Write(Path.Combine(directory, "lifecycle-client.json"), report);

                var finishedPath = Path.Combine(directory, "event-finished-" + name + ".json");
                var deadline = name == "Sleep" ? TimeSpan.FromSeconds(420) : TimeSpan.FromSeconds(210);
                Wait(() => File.Exists(finishedPath), deadline, () => TrackPage(window, controller, observation));
                using (var marker = JsonDocument.Parse(File.ReadAllText(finishedPath)))
                {
                    var element = marker.RootElement;
                    if (!element.GetProperty("Success").GetBoolean())
                        throw new InvalidOperationException("The external " + name + " lifecycle action failed.");
                    observation.EventCompletedAtUtc = element.GetProperty("CompletedAtUtc").GetDateTimeOffset();
                }
                // Require a new request begun after the actual external action,
                // avoiding an old response completed during installer shutdown.
                var reconnectWatch = Stopwatch.StartNew();
                Wait(() => IsFreshVisible(window, controller, client, range, observation.EventCompletedAtUtc),
                    TimeSpan.FromSeconds(45), () => TrackPage(window, controller, observation));
                observation.ReconnectWaitSeconds = reconnectWatch.Elapsed.TotalSeconds;
                observation.FreshQueryStartedAtUtc = client.Latest!.StartedAtUtc;
                observation.FreshQueryCompletedAtUtc = client.Latest.CompletedAtUtc;
                observation.RangeAfter = client.Latest.Counters;
                observation.CollectorAfter = ReadSnapshot();
                RequireCollector(observation.CollectorAfter);
                observation.FreshCollectorInstance = name is "Sleep" or "Startup" || observation.CollectorBefore.Instance is null
                    ? null : observation.CollectorAfter.Instance != observation.CollectorBefore.Instance;
                observation.RangePreserved = controller.SelectedRange == range;
                // The elevated workflow checks every persisted row before/after
                // an unavailable old collector. Do not invent an old IPC total.
                observation.CountersMonotonic = observation.RangeBefore is null ? null
                    : observation.RangeAfter!.DownloadBytes >= observation.RangeBefore.DownloadBytes
                        && observation.RangeAfter.UploadBytes >= observation.RangeBefore.UploadBytes;
                observation.ReconnectedWithoutNavigation = usageTab.IsSelected && Control<FrameworkElement>(window, "AppUsageContent").IsVisible;
                observation.QueryCountAfter = client.QueryCount;
                observation.Queries = client.Queries.Where(query => query.CompletedAtUtc >= observation.ReadyAtUtc).ToArray();
                observation.Diagnostics = Diagnose(window, controller, client);
                observation.Success = observation.RangePreserved && observation.CountersMonotonic is not false
                    && observation.ReconnectedWithoutNavigation && observation.FreshCollectorInstance is not false
                    && observation.Queries.All(query => query.Counters?.AllNonnegative is not false);
                window.UpdateLayout();
                HistoryOverviewChecks.SavePreview(window, Path.Combine(directory, "760x600-after-" + name + ".png"));
                Write(Path.Combine(directory, "observed-" + name + ".json"), observation);
                Write(Path.Combine(directory, "lifecycle-client.json"), report);
                if (!observation.Success) throw new InvalidOperationException(name + " did not preserve range, counters or a fresh authenticated collector.");
            }
            using var afterIdentity = WindowsIdentity.GetCurrent();
            report.ClientElevatedAfter = IsElevated(afterIdentity);
            report.ClientIdentityRetained = sid == afterIdentity.User?.Value;
            report.MutationCalls = setup.MutationCalls;
            report.Success = !report.ClientElevatedAfter && report.ClientIdentityRetained && report.MutationCalls == 0
                && report.HiddenQueryDelta == 0 && report.Observations.All(value => value.Success);
        }
        catch (Exception exception)
        {
            report.Error = exception.ToString();
            if (window is not null)
                report.Diagnostics = Diagnose(window, Controller(window), client);
        }
        finally
        {
            report.Completed = true;
            report.MutationCalls = setup.MutationCalls;
            report.Queries = client.Queries;
            Write(Path.Combine(directory, "lifecycle-client.json"), report);
            window?.Close();
            monitoring?.Dispose();
            Application.Current?.Shutdown();
        }
        Console.WriteLine($"Production App Usage lifecycle observation: {(report.Success ? "passed" : "failed")}; {report.Observations.Count}/{events.Length} events; directory={directory}");
        if (report.Error is not null) Console.Error.WriteLine(report.Error);
        return report.Success ? 0 : 1;
    }

    private static bool IsFreshVisible(MainWindow window, AppUsageTodayController controller, ObservedClient client,
        AppUsageRange range, DateTimeOffset since) => !controller.IsRefreshing && controller.SelectedRange == range
        && controller.Current.State is AppUsageTodayState.Available or AppUsageTodayState.Empty
        && client.Latest is { Result: { } value } query && query.StartedAtUtc >= since && value.Range == range
        && SummaryMatches(window, value);

    private static bool IsRepairableState(IpcState state) => state is IpcState.CollectorUnavailable
        or IpcState.Disconnected or IpcState.TimedOut;
    private static bool IsRepairableUnavailable(AppUsageTodayController controller, ObservedClient client, DateTimeOffset since) =>
        !controller.IsRefreshing && controller.Current.State == AppUsageTodayState.Unavailable
        && client.Queries.LastOrDefault() is { } query && query.StartedAtUtc >= since && IsRepairableState(query.State);

    private static void TrackPage(MainWindow window, AppUsageTodayController controller, EventObservation observation)
    {
        var state = new PageState(DateTimeOffset.UtcNow, controller.Current.State, controller.SelectedRange,
            controller.IsRefreshing, window.WindowState, window.IsVisible,
            Control<FrameworkElement>(window, "AppUsageContent").IsVisible);
        if (observation.PageStates.LastOrDefault() is not { } previous || previous.State != state.State
            || previous.Range != state.Range || previous.Refreshing != state.Refreshing || previous.WindowState != state.WindowState
            || previous.WindowVisible != state.WindowVisible || previous.PageVisible != state.PageVisible)
            observation.PageStates.Add(state);
    }

    private static SnapshotObservation ReadSnapshot()
    {
        static async Task<SnapshotObservation> ReadAsync()
        {
            await using var ipc = new IpcClient(ServicePipeSecurity.DefaultPipeName, serviceName: ServicePipeSecurity.ProductionServiceName);
            var connected = await ipc.ConnectAsync().ConfigureAwait(false);
            if (connected.State != IpcState.Connected) return new(connected.State, null, null, null, null, null, null, null, connected.Detail);
            var result = await ipc.GetSnapshotAsync().ConfigureAwait(false);
            var snapshot = result.Value;
            return new(result.State, snapshot?.CollectorInstanceId, snapshot?.Health?.EtwSessionName,
                snapshot?.Health?.ServiceIdentity, snapshot?.Health?.CaptureRunning, snapshot?.LostEventCount,
                snapshot?.ProviderEventCount, snapshot?.EventCount, result.Detail);
        }
        return ReadAsync().GetAwaiter().GetResult();
    }

    private static void RequireCollector(SnapshotObservation snapshot)
    {
        if (snapshot.State != IpcState.Connected || snapshot.Instance is null || snapshot.CaptureRunning != true
            || snapshot.ServiceIdentity != "LocalSystem")
            throw new InvalidOperationException("The real authenticated production collector is not capturing as LocalSystem: " + snapshot.State);
    }

    private static bool SummaryMatches(MainWindow window, AppUsageQueryResult usage) =>
        Control<FrameworkElement>(window, "AppUsageSummaryPanel").IsVisible
        && Text(window, "AppUsageTrackedAppsText") == usage.TrackedApplicationCount.ToString(CultureInfo.CurrentCulture)
        && Text(window, "AppUsageDownloadText") == ByteFormatter.Format(usage.TotalDownloadBytes)
        && Text(window, "AppUsageUploadText") == ByteFormatter.Format(usage.TotalUploadBytes)
        && Text(window, "AppUsageLogicalTotalText") == ByteFormatter.Format(usage.LogicalTotalBytes);

    private static object Diagnose(MainWindow window, AppUsageTodayController controller, ObservedClient client) => new
    {
        PageState = controller.Current.State, SelectedRange = controller.SelectedRange, controller.IsRefreshing,
        AppUsageTabSelected = Control<TabItem>(window, "AppUsageTab").IsSelected,
        PageVisible = Control<FrameworkElement>(window, "AppUsageContent").IsVisible,
        SummaryVisible = Control<FrameworkElement>(window, "AppUsageSummaryPanel").IsVisible,
        WindowVisible = window.IsVisible, window.WindowState, client.QueryCount, LastQuery = client.Queries.LastOrDefault(),
        TrackedAppsText = Text(window, "AppUsageTrackedAppsText"), DownloadText = Text(window, "AppUsageDownloadText"),
        UploadText = Text(window, "AppUsageUploadText"), TotalText = Text(window, "AppUsageLogicalTotalText")
    };

    private static void SelectRange(MainWindow window, AppUsageRange range) => Control<RadioButton>(window, range switch
    {
        AppUsageRange.Today => "AppUsageTodayButton", AppUsageRange.SevenDays => "AppUsage7DButton",
        _ => "AppUsage30DButton"
    }).IsChecked = true;
    private static AppUsageTodayController Controller(MainWindow window) =>
        (AppUsageTodayController)typeof(MainWindow).GetField("_appUsageToday", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static T Control<T>(MainWindow window, string name) where T : class => window.FindName(name) as T
        ?? throw new InvalidOperationException("Missing lifecycle control: " + name);
    private static string Text(MainWindow window, string name) => Control<TextBlock>(window, name).Text;
    private static bool IsElevated(WindowsIdentity identity) => new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    private static void Wait(Func<bool> predicate, TimeSpan timeout, Action? sample = null)
    {
        var elapsed = Stopwatch.StartNew();
        while (!predicate())
        {
            sample?.Invoke();
            if (elapsed.Elapsed > timeout) throw new TimeoutException("Bounded production lifecycle observation timed out.");
            HistoryOverviewChecks.Pump();
        }
        sample?.Invoke();
        HistoryOverviewChecks.Pump();
    }
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
    private static void Write(string path, object value)
    {
        File.WriteAllText(path + ".writing", JsonSerializer.Serialize(value, JsonOptions));
        File.Move(path + ".writing", path, overwrite: true);
    }

    private sealed class ObservedClient(IAppUsageTodayClient inner) : IAppUsageTodayClient
    {
        private int queryCount;
        private QueryObservation? latest;
        private readonly ConcurrentQueue<QueryObservation> queries = new();
        public int QueryCount => Volatile.Read(ref queryCount);
        public QueryObservation? Latest => Volatile.Read(ref latest);
        public QueryObservation[] Queries => queries.ToArray();
        public async Task<IpcResult<AppUsageQueryResult>> GetAppUsageRangeAsync(AppUsageRange range, CancellationToken token)
        {
            var sequence = Interlocked.Increment(ref queryCount);
            var started = DateTimeOffset.UtcNow;
            var result = await inner.GetAppUsageRangeAsync(range, token).ConfigureAwait(false);
            var observation = new QueryObservation(sequence, range, result.State, result.Value?.Availability,
                started, DateTimeOffset.UtcNow, result.Value is null ? null : Counters(result.Value)) { Result = result.Value };
            queries.Enqueue(observation);
            if (result.State == IpcState.Connected && result.Value?.Availability == AppUsageAvailability.Available)
                Volatile.Write(ref latest, observation);
            return result;
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    // Both mutation paths and observer signals are deliberately excluded. SCM
    // events can only recover through the normal page polling implementation.
    private sealed class ReadOnlyService(IAppTrackingUpdateService inner) : IAppTrackingUpdateService
    {
        private int mutationCalls;
        public int MutationCalls => Volatile.Read(ref mutationCalls);
        public event EventHandler? StateChanged { add { } remove { } }
        public event EventHandler? HistoryCleared { add { } remove { } }
        public Task<AppTrackingServiceState> GetStateAsync(CancellationToken token) => inner.GetStateAsync(token);
        public Task<AppTrackingUpdateStatus> GetUpdateStatusAsync(CancellationToken token) => inner.GetUpdateStatusAsync(token);
        public Task<AppUsageStorageState> GetStorageStateAsync(CancellationToken token) => inner.GetStorageStateAsync(token);
        public Task<AppTrackingInstallOutcome> InstallAsync(CancellationToken token) { Interlocked.Increment(ref mutationCalls); throw new InvalidOperationException("Read-only lifecycle client cannot install."); }
        public Task<AppTrackingUpdateOutcome> UpdateAsync(CancellationToken token) { Interlocked.Increment(ref mutationCalls); throw new InvalidOperationException("Read-only lifecycle client cannot update."); }
        public Task<AppTrackingUninstallOutcome> UninstallAsync(CancellationToken token) { Interlocked.Increment(ref mutationCalls); throw new InvalidOperationException("Read-only lifecycle client cannot uninstall."); }
        public Task<AppUsageHistoryClearOutcome> ClearHistoryAsync(CancellationToken token) { Interlocked.Increment(ref mutationCalls); throw new InvalidOperationException("Read-only lifecycle client cannot clear history."); }
    }

    private static RangeCounters Counters(AppUsageQueryResult value) => new(value.Range, value.TrackedApplicationCount,
        value.TotalDownloadBytes, value.TotalUploadBytes, value.LogicalTotalBytes,
        value.TotalDownloadBytes >= 0 && value.TotalUploadBytes >= 0 && value.LogicalTotalBytes >= 0
        && value.Applications.All(app => app.DownloadBytes >= 0 && app.UploadBytes >= 0 && app.TotalBytes >= 0));
    private sealed record RangeCounters(AppUsageRange Range, int Applications, long DownloadBytes, long UploadBytes,
        long LogicalTotalBytes, bool AllNonnegative);
    private sealed record QueryObservation(int Sequence, AppUsageRange RequestedRange, IpcState State,
        AppUsageAvailability? Availability, DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc, RangeCounters? Counters)
    {
        [JsonIgnore] public AppUsageQueryResult? Result { get; init; }
    }
    private sealed record SnapshotObservation(IpcState State, Guid? Instance, string? EtwSessionName,
        string? ServiceIdentity, bool? CaptureRunning, int? LostEventCount, long? ProviderEventCount, long? EventCount, string? Detail);
    private sealed record PageState(DateTimeOffset AtUtc, AppUsageTodayState State, AppUsageRange Range,
        bool Refreshing, WindowState WindowState, bool WindowVisible, bool PageVisible);
    private sealed class EventObservation
    {
        public string Event { get; init; } = "";
        public bool Success { get; set; }
        public DateTimeOffset ReadyAtUtc { get; set; }
        public DateTimeOffset EventCompletedAtUtc { get; set; }
        public DateTimeOffset FreshQueryStartedAtUtc { get; set; }
        public DateTimeOffset FreshQueryCompletedAtUtc { get; set; }
        public double ReconnectWaitSeconds { get; set; }
        public int QueryCountBefore { get; set; }
        public int QueryCountAfter { get; set; }
        public bool RangePreserved { get; set; }
        public bool? CountersMonotonic { get; set; }
        public bool UnavailableBaselineRepair { get; set; }
        public bool ReconnectedWithoutNavigation { get; set; }
        public bool? FreshCollectorInstance { get; set; }
        public RangeCounters? RangeBefore { get; set; }
        public RangeCounters? RangeAfter { get; set; }
        public SnapshotObservation? CollectorBefore { get; set; }
        public SnapshotObservation? CollectorAfter { get; set; }
        public List<PageState> PageStates { get; } = [];
        public QueryObservation[] Queries { get; set; } = [];
        public object? Diagnostics { get; set; }
    }
    private sealed class LifecycleReport
    {
        public bool Completed { get; set; }
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string[] Events { get; init; } = [];
        public AppUsageRange SelectedRange { get; init; }
        public int ClientProcessId { get; init; }
        public bool ClientElevatedBefore { get; set; }
        public bool ClientElevatedAfter { get; set; }
        public bool ClientIdentityRetained { get; set; }
        public int MutationCalls { get; set; }
        public int HiddenQueryDelta { get; set; }
        public RangeCounters? InitialRange { get; set; }
        public bool InitialPageUnavailable { get; set; }
        public bool InitialUnavailableRepair { get; set; }
        public SnapshotObservation? InitialCollector { get; set; }
        public List<EventObservation> Observations { get; } = [];
        public QueryObservation[] Queries { get; set; } = [];
        public object? Diagnostics { get; set; }
    }
}
