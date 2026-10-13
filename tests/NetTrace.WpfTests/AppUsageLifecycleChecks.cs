using System.Diagnostics;
using System.Windows.Threading;
using AppUsageIpc;
using NetTrace.Presentation;
using NetTrace.Services;
using NetTrace.Utils;

internal static class AppUsageLifecycleChecks
{
    public static void Run(string root, Action<bool, string> check)
    {
        // No production database, SCM, ETW, UAC or power action belongs to these
        // deterministic controller checks. Real lifecycle evidence is separate.
        foreach (var range in Enum.GetValues<AppUsageRange>()) CheckRecovery(range, check);
        CheckClosedRecovery(check);
    }

    private static void CheckRecovery(AppUsageRange range, Action<bool, string> check)
    {
        var client = new ControlledClient();
        var service = new ControlledService();
        var clock = new OwnedClock();
        var applied = new List<AppUsageTodayPresentation>();
        var selection = new AppUsageRangeSelection();
        var controller = new AppUsageTodayController(Dispatcher.CurrentDispatcher, client, applied.Add,
            clock, selection, service);
        try
        {
            controller.SelectRange(range);
            controller.SetActive(true);
            Wait(() => client.Calls == 1);
            Complete(client, controller, 0, Result(range, 10_000, 500));
            check(controller.SelectedRange == range && controller.Current.DownloadText == ByteFormatter.Format(10_000),
                $"{range} lifecycle starts with its retained persisted range rather than a session delta");

            foreach (var disconnectedState in new[] { IpcState.Disconnected, IpcState.TimedOut, IpcState.CollectorUnavailable })
            {
                clock.Advance(TimeSpan.FromSeconds(4));
                var request = client.Calls;
                controller.Tick();
                Wait(() => client.Calls == request + 1);
                client.Complete(request, new(disconnectedState, null));
                Wait(() => !controller.IsRefreshing);
                check(controller.Current.State == AppUsageTodayState.Unavailable && controller.SelectedRange == range
                    && controller.Current.Applications.Count == 0,
                    $"{range} {disconnectedState} during service recovery clears unavailable rows and preserves the selected range");
                clock.Advance(TimeSpan.FromSeconds(4));
                var prior = client.Calls;
                controller.Tick();
                Wait(() => client.Calls == prior + 1);
                check(client.Range(prior) == range, $"{range} periodic retry uses the selected range after {disconnectedState}");
                Complete(client, controller, prior, Result(range, 10_100, 510));
                check(controller.Current.State == AppUsageTodayState.Available
                    && controller.Current.DownloadText == ByteFormatter.Format(10_100),
                    $"{range} recovery displays the fresh persisted cumulative value without adding pre-disconnect totals");
            }

            service.State = AppTrackingServiceState.Stopped;
            var beforeStopped = client.Calls;
            clock.Advance(TimeSpan.FromSeconds(4));
            controller.Tick();
            Wait(() => !controller.IsRefreshing && controller.Current.State == AppUsageTodayState.Stopped);
            check(client.Calls == beforeStopped && controller.SelectedRange == range,
                $"{range} SCM Stopped state preserves selection and sends no query to a stopped collector");

            service.State = AppTrackingServiceState.Running;
            clock.Advance(TimeSpan.FromHours(8));
            for (var tick = 0; tick < 30; tick++) controller.Tick();
            Wait(() => client.Calls == beforeStopped + 1);
            check(client.MaxActive == 1 && client.Range(beforeStopped) == range,
                $"{range} a long resume clock gap produces one current-range refresh without a catch-up burst");
            Complete(client, controller, beforeStopped, Result(range, 10_200, 520));
            check(controller.Current.DownloadText == ByteFormatter.Format(10_200)
                && controller.Current.UploadText == ByteFormatter.Format(520)
                && controller.Current.LogicalTotalText == ByteFormatter.Format(10_720),
                $"{range} resumed persisted totals are displayed once without duplicate or giant calculated deltas");

            clock.Advance(TimeSpan.FromSeconds(4));
            var unchanged = client.Calls;
            controller.Tick();
            Wait(() => client.Calls == unchanged + 1);
            Complete(client, controller, unchanged, Result(range, 10_200, 520));
            check(controller.Current.LogicalTotalText == ByteFormatter.Format(10_720),
                $"{range} repeated cumulative history after resume never accumulates the same bytes twice in the page");

            clock.Advance(TimeSpan.FromSeconds(4));
            var stale = client.Calls;
            controller.Tick();
            Wait(() => client.Calls == stale + 1);
            service.Notify();
            Wait(() => client.Token(stale).IsCancellationRequested);
            service.Notify();
            HistoryOverviewChecks.Pump();
            check(client.Calls == stale + 1 && client.MaxActive == 1,
                $"{range} overlapping management recovery notifications cancel and coalesce a pending old request");
            client.Complete(stale, Connected(Result(range, 999_999_999, 123)));
            Wait(() => client.Calls == stale + 2);
            check(applied.All(page => page.DownloadText != ByteFormatter.Format(999_999_999)),
                $"{range} a late pre-recovery response cannot overwrite newer service lifecycle generation");
            Complete(client, controller, stale + 1, Result(range, 10_300, 530));
            check(controller.SelectedRange == range && controller.Current.DownloadText == ByteFormatter.Format(10_300),
                $"{range} coalesced lifecycle completion refreshes the selected range without navigation");

            controller.SetActive(false);
            var hidden = client.Calls;
            var renders = applied.Count;
            clock.Advance(TimeSpan.FromDays(1));
            service.Notify();
            for (var tick = 0; tick < 30; tick++) controller.Tick();
            HistoryOverviewChecks.Pump();
            check(client.Calls == hidden && applied.Count == renders && controller.SelectedRange == range,
                $"{range} hidden recovery and resume generate zero refresh requests and preserve range");
            controller.SetActive(true);
            Wait(() => client.Calls == hidden + 1);
            Complete(client, controller, hidden, Result(range, 10_400, 540));
            check(client.Range(hidden) == range && controller.Current.DownloadText == ByteFormatter.Format(10_400),
                $"{range} navigating back after hidden recovery immediately loads the retained range");
            check(service.MutationCalls == 0,
                $"{range} lifecycle detection and reconnect never install, elevate or change service state");
        }
        finally
        {
            client.CompletePending();
            WaitTask(controller.DisposeAsync().AsTask());
        }
    }

    private static void CheckClosedRecovery(Action<bool, string> check)
    {
        var client = new ControlledClient();
        var service = new ControlledService();
        var rendered = 0;
        var controller = new AppUsageTodayController(Dispatcher.CurrentDispatcher, client, _ => rendered++, setupService: service);
        controller.SetActive(true);
        Wait(() => client.Calls == 1);
        var disposal = controller.DisposeAsync().AsTask();
        var before = rendered;
        service.Notify();
        client.Complete(0, Connected(Result(AppUsageRange.Today, 100, 10)));
        WaitTask(disposal);
        HistoryOverviewChecks.Pump();
        check(rendered == before && client.Calls == 1 && client.Disposed,
            "Closing NetTrace during collector recovery ignores late responses and removes management observers");
        check(service.MutationCalls == 0, "Closing during recovery does not stop the collector or request elevation");
    }

    private static AppUsageQueryResult Result(AppUsageRange range, long download, long upload)
    {
        var date = new DateOnly(2026, 10, 13);
        return new(range, AppUsageRangeRules.GetStartLocalDate(range, date), date, 1, download, upload, download + upload,
            [new("owned-lifecycle", "Owned lifecycle traffic", "owned.exe", null, download, upload, download + upload, false, false)]);
    }
    private static IpcResult<AppUsageQueryResult> Connected(AppUsageQueryResult result) => new(IpcState.Connected, result);
    private static void Complete(ControlledClient client, AppUsageTodayController controller, int index, AppUsageQueryResult result)
    {
        client.Complete(index, Connected(result));
        Wait(() => !controller.IsRefreshing);
    }
    private static void WaitTask(Task task) { Wait(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Wait(Func<bool> predicate)
    {
        var elapsed = Stopwatch.StartNew();
        while (!predicate())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Owned App Usage lifecycle condition did not complete.");
            HistoryOverviewChecks.Pump();
        }
        HistoryOverviewChecks.Pump();
    }
    private sealed class OwnedClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 13, 4, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan interval) => now += interval;
    }
    private sealed class ControlledService : IAppTrackingManagementService
    {
        public AppTrackingServiceState State { get; set; } = AppTrackingServiceState.Running;
        public int MutationCalls { get; private set; }
        public event EventHandler? StateChanged;
        public void Notify() => StateChanged?.Invoke(this, EventArgs.Empty);
        public Task<AppTrackingServiceState> GetStateAsync(CancellationToken token) => Task.FromResult(State);
        public Task<AppTrackingInstallOutcome> InstallAsync(CancellationToken token) { MutationCalls++; throw new InvalidOperationException(); }
        public Task<AppTrackingUninstallOutcome> UninstallAsync(CancellationToken token) { MutationCalls++; throw new InvalidOperationException(); }
    }
    private sealed class ControlledClient : IAppUsageTodayClient
    {
        private readonly object gate = new();
        private readonly List<(AppUsageRange Range, CancellationToken Token, TaskCompletionSource<IpcResult<AppUsageQueryResult>> Completion)> requests = [];
        private int active;
        private int maxActive;
        public int Calls { get { lock (gate) return requests.Count; } }
        public int MaxActive { get { lock (gate) return maxActive; } }
        public bool Disposed { get; private set; }
        public Task<IpcResult<AppUsageQueryResult>> GetAppUsageRangeAsync(AppUsageRange range, CancellationToken token)
        {
            var completion = new TaskCompletionSource<IpcResult<AppUsageQueryResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                requests.Add((range, token, completion));
                active++;
                maxActive = Math.Max(active, maxActive);
            }
            return Observe(completion.Task);
        }
        private async Task<IpcResult<AppUsageQueryResult>> Observe(Task<IpcResult<AppUsageQueryResult>> completion)
        { try { return await completion.ConfigureAwait(false); } finally { lock (gate) active--; } }
        public AppUsageRange Range(int index) { lock (gate) return requests[index].Range; }
        public CancellationToken Token(int index) { lock (gate) return requests[index].Token; }
        public void Complete(int index, IpcResult<AppUsageQueryResult> result) { lock (gate) requests[index].Completion.TrySetResult(result); }
        public void CompletePending() { lock (gate) foreach (var request in requests) request.Completion.TrySetResult(new(IpcState.Cancelled, null)); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
