using System.Diagnostics;
using AppUsageCollectorCore;
using AppUsageEtwProbe;
using NetTrace.AppUsageService;

internal static class ServiceLifecycleTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Service host becomes running only after worker readiness and stops with resource disposal", StartStop),
        ("Repeated service starts create new workers and fresh aggregation lifetimes", RestartLifetimes),
        ("A duplicate service start cannot create a competing collector worker", DuplicateStart),
        ("A worker startup fault leaves the host stopped and disposes its resources", StartFault),
        ("Worker readiness timeout is bounded and cancels and disposes startup resources", StartTimeout),
        ("A stubborn startup worker is not disposed concurrently after readiness timeout", StubbornStartTimeout),
        ("An unhandled running worker fault invokes fatal policy and clears healthy state", WorkerFault),
        ("Unexpected successful worker completion is treated as a fatal capture failure", UnexpectedCompletion),
        ("Stopping a stubborn worker remains bounded without racing resource disposal", BoundedStop),
        ("A capture cleanup fault reaches service stop after completed-worker disposal", StopCleanupFault),
        ("A blocked worker disposal cannot hold Windows shutdown indefinitely", BoundedDispose),
        ("A missing capture cleanup completion fails within its deadline", CaptureCleanupTimeout),
        ("Null and dirty capture cleanup results cannot report successful shutdown", CaptureCleanupResults),
        ("A failed capture cleanup task preserves its failure", CaptureCleanupFault),
        ("Suspend and resume replace the worker lifetime without losing resume after repeated suspend", SuspendResume),
        ("Resume before suspend does not start an unintended service worker", ResumeWithoutSuspend),
        ("Shared continuous ETW runtime exposes live health and stops cleanly on cancellation", RuntimeCancellation),
        ("Shared ETW worker failure is visible in final capture result and health", RuntimeFault),
        ("A denied ETW startup reports failed readiness and disposes its owned session", RuntimeStartDenied),
        ("A continuous collector runtime cannot be reused for a second lifetime", RuntimeSingleLifetime),
    ];

    private static async Task StartStop()
    {
        var worker = new FakeWorker(); int fatal = 0;
        await using var lifecycle = new ServiceLifecycle(() => worker, _ => fatal++);
        Check.True(!lifecycle.Running, "initially stopped"); await lifecycle.StartAsync();
        Check.True(lifecycle.Running && worker.Started, "Running follows readiness"); await lifecycle.StopAsync();
        Check.True(!lifecycle.Running && worker.Cancelled, "SCM stop cancels capture");
        Check.Equal(1, worker.DisposeCount, "worker resources disposed once");
        Check.Equal(0, fatal, "normal stop never invokes fatal restart policy"); await lifecycle.StopAsync();
        Check.Equal(1, worker.DisposeCount, "stop is idempotent");
    }
    private static async Task RestartLifetimes()
    {
        var workers = new List<FakeWorker>();
        await using var lifecycle = new ServiceLifecycle(() => { var worker = new FakeWorker(); workers.Add(worker); return worker; }, _ => { });
        for (int i = 0; i < 3; i++)
        {
            await lifecycle.StartAsync(); workers[i].Aggregation.Add(Fixtures.Raw(bytes: 100)); await lifecycle.StopAsync();
        }
        Check.Equal(3, workers.Count, "new worker created for each start");
        Check.Equal(3, workers.Select(worker => worker.Aggregation.CollectorInstanceId).Distinct().Count(), "each start has distinct aggregation lifetime");
        Check.True(workers.All(worker => worker.DisposeCount == 1), "each stopped lifetime disposed");
    }
    private static async Task DuplicateStart()
    {
        int created = 0; await using var lifecycle = new ServiceLifecycle(() => { created++; return new FakeWorker(); }, _ => { });
        await lifecycle.StartAsync();
        await Check.ThrowsAsync<InvalidOperationException>(() => lifecycle.StartAsync(), "duplicate SCM start");
        Check.Equal(1, created, "duplicate start never creates a competing ETW worker");
    }
    private static async Task StartFault()
    {
        var worker = new FakeWorker { StartFailure = new InvalidOperationException("Worker startup failure") };
        await using var lifecycle = new ServiceLifecycle(() => worker, _ => { });
        await Check.ThrowsAsync<InvalidOperationException>(() => lifecycle.StartAsync(), "failed startup");
        Check.True(!lifecycle.Running, "startup failure cannot report service healthy");
        Check.Equal(1, worker.DisposeCount, "startup failure still disposes resources");
    }
    private static async Task StartTimeout()
    {
        var worker = new FakeWorker { SignalReady = false };
        await using var lifecycle = new ServiceLifecycle(() => worker, _ => { }, startTimeout: TimeSpan.FromMilliseconds(80), stopTimeout: TimeSpan.FromMilliseconds(200));
        var watch = Stopwatch.StartNew();
        await Check.ThrowsAsync<TimeoutException>(() => lifecycle.StartAsync(), "bounded readiness");
        Check.True(watch.Elapsed < TimeSpan.FromSeconds(1), "startup cannot wait indefinitely");
        Check.True(worker.Cancelled && !lifecycle.Running, "startup timeout cancels worker");
        Check.Equal(1, worker.DisposeCount, "timed-out startup disposed");
    }
    private static async Task WorkerFault()
    {
        var worker = new FakeWorker(); var fatal = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var lifecycle = new ServiceLifecycle(() => worker, exception => fatal.TrySetResult(exception));
        await lifecycle.StartAsync(); worker.Fail(new InvalidOperationException("ETW consumption stopped"));
        var error = await fatal.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check.True(error is InvalidOperationException && !lifecycle.Running, "fatal worker exception clears healthy state");
        await lifecycle.StopAsync(); Check.Equal(1, worker.DisposeCount, "faulted lifetime disposed on stop");
    }
    private static async Task StubbornStartTimeout()
    {
        var worker = new FakeWorker { SignalReady = false, IgnoreCancellation = true };
        await using var lifecycle = new ServiceLifecycle(() => worker, _ => { },
            startTimeout: TimeSpan.FromMilliseconds(40), stopTimeout: TimeSpan.FromMilliseconds(80));
        try
        {
            await Check.ThrowsAsync<TimeoutException>(() => lifecycle.StartAsync(), "stubborn startup cleanup");
            Check.True(worker.Cancelled && !lifecycle.Running, "failed startup remains stopped");
            Check.Equal(0, worker.DisposeCount, "active callbacks retain their resources until fatal host termination");
        }
        finally { worker.Finish(); }
    }
    private static async Task UnexpectedCompletion()
    {
        var worker = new FakeWorker(); var fatal = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var lifecycle = new ServiceLifecycle(() => worker, exception => fatal.TrySetResult(exception));
        await lifecycle.StartAsync(); worker.Finish();
        await fatal.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check.True(!lifecycle.Running, "silent ETW completion is not healthy service operation");
    }
    private static async Task BoundedStop()
    {
        var worker = new FakeWorker { IgnoreCancellation = true };
        await using var lifecycle = new ServiceLifecycle(() => worker, _ => { }, stopTimeout: TimeSpan.FromMilliseconds(180));
        await lifecycle.StartAsync(); var watch = Stopwatch.StartNew();
        await Check.ThrowsAsync<TimeoutException>(() => lifecycle.StopAsync(), "stubborn ETW worker stop");
        Check.True(watch.Elapsed < TimeSpan.FromSeconds(1), "SCM stop deadline is finite");
        Check.Equal(0, worker.DisposeCount, "active callbacks are not raced by resource disposal");
        Check.True(!lifecycle.Running, "timed-out worker never remains healthy"); worker.Finish();
    }
    private static async Task StopCleanupFault()
    {
        var worker = new FakeWorker { CancelFailure = new InvalidOperationException("Capture cleanup did not complete") };
        await using var lifecycle = new ServiceLifecycle(() => worker, _ => { });
        await lifecycle.StartAsync();
        await Check.ThrowsAsync<InvalidOperationException>(() => lifecycle.StopAsync(), "cleanup fault reaches SCM stop fatal policy");
        Check.True(!lifecycle.Running, "cleanup failure never remains healthy");
        Check.Equal(1, worker.DisposeCount, "completed failed worker can dispose without racing capture");
    }
    private static EtwCaptureResult CleanCaptureResult() => new("owned-session", true, false, true, true,
        0, 0, 0, 0, DateTime.UtcNow, DateTime.UtcNow, null);
    private static async Task CaptureCleanupTimeout()
    {
        var pending = new TaskCompletionSource<EtwCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = Stopwatch.StartNew();
        await Check.ThrowsAsync<TimeoutException>(() => CollectorServiceWorker.AwaitCaptureCleanupAsync(
            pending.Task, TimeSpan.FromMilliseconds(40)), "no completed capture must not silently succeed");
        Check.True(watch.Elapsed < TimeSpan.FromSeconds(1) && !pending.Task.IsCompleted,
            "bounded cleanup failure preserves evidence that capture remains active");
        pending.SetResult(CleanCaptureResult());
    }
    private static async Task CaptureCleanupResults()
    {
        foreach (var result in new[] { null, CleanCaptureResult() with { ShutdownClean = false },
            CleanCaptureResult() with { Error = "Owned ETW session did not stop" } })
            await Check.ThrowsAsync<InvalidOperationException>(() => CollectorServiceWorker.AwaitCaptureCleanupAsync(
                Task.FromResult(result!), TimeSpan.FromMilliseconds(40)), "unclean capture result cannot report resources disposed");
        var clean = CleanCaptureResult();
        Check.Equal(clean, await CollectorServiceWorker.AwaitCaptureCleanupAsync(Task.FromResult(clean), TimeSpan.FromSeconds(1)),
            "clean completed capture preserves normal shutdown");
    }
    private static async Task CaptureCleanupFault()
    {
        await Check.ThrowsAsync<IOException>(() => CollectorServiceWorker.AwaitCaptureCleanupAsync(
            Task.FromException<EtwCaptureResult>(new IOException("Capture cleanup failure")), TimeSpan.FromMilliseconds(40)),
            "cleanup task failure remains visible");
    }
    private static async Task BoundedDispose()
    {
        var worker = new FakeWorker { BlockDispose = true };
        await using var lifecycle = new ServiceLifecycle(() => worker, _ => { }, stopTimeout: TimeSpan.FromMilliseconds(180));
        await lifecycle.StartAsync(); var watch = Stopwatch.StartNew();
        try
        {
            await Check.ThrowsAsync<TimeoutException>(() => lifecycle.StopAsync(), "blocked disposal");
            Check.True(watch.Elapsed < TimeSpan.FromSeconds(1), "resource disposal cannot hold shutdown forever");
            Check.Equal(1, worker.DisposeCount, "disposal attempted once");
        }
        finally { worker.ReleaseDispose(); }
    }
    private static async Task SuspendResume()
    {
        var workers = new List<FakeWorker>();
        await using var lifecycle = new ServiceLifecycle(() => { var worker = new FakeWorker(); workers.Add(worker); return worker; }, _ => { });
        await lifecycle.StartAsync(); workers[0].Aggregation.Add(Fixtures.Raw(bytes: 100));
        var before = workers[0].Aggregation.GetSnapshot(); await lifecycle.SuspendAsync(); await lifecycle.SuspendAsync();
        Check.True(!lifecycle.Running && workers[0].DisposeCount == 1, "suspend disposes old capture");
        await lifecycle.ResumeAsync(); Check.True(lifecycle.Running, "resume starts capture again");
        var after = workers[1].Aggregation.GetSnapshot();
        Check.True(before.CollectorInstanceId != after.CollectorInstanceId, "resume is an explicit new lifetime");
        Check.Equal(0L, after.EventCount, "resume begins fresh in-memory counters");
        await lifecycle.ResumeAsync(); Check.Equal(2, workers.Count, "repeated resume cannot duplicate capture");
    }
    private static async Task ResumeWithoutSuspend()
    {
        int created = 0; await using var lifecycle = new ServiceLifecycle(() => { created++; return new FakeWorker(); }, _ => { });
        await lifecycle.ResumeAsync(); Check.Equal(0, created, "resume has no effect on a never-started service");
    }
    private static CollectorRuntime Runtime(FakeCaptureSession session) => new(Fixtures.Schema, privileged: true,
        identityName: "LocalSystem", enforceOwnership: false,
        capture: new EtwCapture(Fixtures.Schema, (_, _, accept) => { session.Accept = accept; return session; },
            exactSessionName: SessionOwnership.SessionName, exactSessionGuid: SessionOwnership.SessionGuid),
        resolver: new Fixtures.Resolver((pid, _) => Fixtures.Identity(pid)), localAddresses: Fixtures.Local);
    private static async Task RuntimeCancellation()
    {
        var session = new FakeCaptureSession(); using var runtime = Runtime(session); using var stop = new CancellationTokenSource();
        var running = runtime.RunAsync(stop.Token); await runtime.Ready.WaitAsync(TimeSpan.FromSeconds(2));
        session.Emit(Fixtures.Raw(bytes: 100, timestamp: DateTime.UtcNow));
        var snapshot = runtime.GetSnapshot(countRequest: false);
        Check.True(snapshot.Health!.CaptureRunning && snapshot.Health.CollectorPrivileged, "runtime reports real active capture state");
        Check.Equal(SessionOwnership.SessionName, snapshot.Health.EtwSessionName, "exact deterministic ETW identity");
        Check.Equal(runtime.StartedAtUtc, snapshot.Health.StartedAtUtc, "start timestamp identifies counter lifetime");
        Check.Equal(1L, snapshot.EventCount, "continuous fake ETW event reaches shared aggregation");
        Check.Equal(7, snapshot.LostEventCount, "loss query survives runtime health snapshot");
        Check.True(snapshot.Health.LastEventAtUtc is not null, "last accepted event timestamp visible");
        stop.Cancel(); var result = await running.WaitAsync(TimeSpan.FromSeconds(2));
        Check.True(result.Cancelled && result.ShutdownClean && session.Stopped && session.Disposed, "continuous capture clean cancellation");
        Check.True(!runtime.GetSnapshot(countRequest: false).Health!.CaptureRunning, "completed runtime cannot report capture healthy");
    }
    private static async Task RuntimeFault()
    {
        var session = new FakeCaptureSession(); using var runtime = Runtime(session); using var stop = new CancellationTokenSource();
        var running = runtime.RunAsync(stop.Token); await runtime.Ready.WaitAsync(TimeSpan.FromSeconds(2));
        session.Fail(new InvalidOperationException("Owned ETW consumer failure"));
        var result = await running.WaitAsync(TimeSpan.FromSeconds(2));
        Check.True(result.Error is not null && !result.Cancelled, "capture error is returned for service fatal policy");
        Check.True(!runtime.GetSnapshot(countRequest: false).Health!.CaptureRunning, "failed capture is visible to health");
        Check.True(session.Stopped && session.Disposed, "faulted capture resources disposed");
    }
    private static async Task RuntimeStartDenied()
    {
        var session = new FakeCaptureSession { StartFailure = new UnauthorizedAccessException("ETW rights denied") };
        using var runtime = Runtime(session); var running = runtime.RunAsync(CancellationToken.None);
        await Check.ThrowsAsync<InvalidOperationException>(() => runtime.Ready, "failed ETW startup cannot signal readiness");
        var result = await running;
        Check.True(result.AccessDenied && !result.SessionStarted && session.Disposed, "denied session is fully disposed");
    }
    private static async Task RuntimeSingleLifetime()
    {
        var session = new FakeCaptureSession(); using var runtime = Runtime(session); using var stop = new CancellationTokenSource();
        var running = runtime.RunAsync(stop.Token); await runtime.Ready.WaitAsync(TimeSpan.FromSeconds(2)); stop.Cancel(); await running;
        await Check.ThrowsAsync<InvalidOperationException>(() => runtime.RunAsync(CancellationToken.None), "runtime cannot silently retain counters on restart");
    }
    private sealed class FakeWorker : IServiceWorker
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource disposeDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Ready => ready.Task;
        public bool SignalReady { get; init; } = true;
        public bool IgnoreCancellation { get; init; }
        public bool BlockDispose { get; init; }
        public Exception? StartFailure { get; init; }
        public Exception? CancelFailure { get; init; }
        public bool Started { get; private set; }
        public bool Cancelled { get; private set; }
        public int DisposeCount { get; private set; }
        public AppUsageElevatedCollector.CollectorAggregation Aggregation { get; } = Fixtures.Aggregation();
        public async Task RunAsync(CancellationToken token)
        {
            Started = true; if (StartFailure is not null) throw StartFailure;
            if (SignalReady) ready.TrySetResult();
            using var registration = token.Register(() =>
            {
                Cancelled = true;
                if (!IgnoreCancellation)
                {
                    if (CancelFailure is not null) done.TrySetException(CancelFailure);
                    else done.TrySetResult();
                }
            });
            await done.Task;
        }
        public void Fail(Exception error) => done.TrySetException(error);
        public void Finish() => done.TrySetResult();
        public void ReleaseDispose() => disposeDone.TrySetResult();
        public async ValueTask DisposeAsync()
        {
            DisposeCount++; if (BlockDispose) await disposeDone.Task; done.TrySetResult();
        }
    }
    private sealed class FakeCaptureSession : IEtwCaptureSession, IEtwCaptureStatusSession
    {
        private readonly ManualResetEventSlim done = new();
        private Exception? error;
        public Action<RawNetworkEvent>? Accept { get; set; }
        public Exception? StartFailure { get; init; }
        public bool Stopped { get; private set; }
        public bool Disposed { get; private set; }
        public int EventsLost => 7;
        public long ProviderEventCount => 100;
        public long UnsupportedEventCount => 1;
        public long MalformedEventCount => 0;
        public void Start() { if (StartFailure is not null) throw StartFailure; }
        public void Process() { done.Wait(); if (error is not null) throw error; }
        public void Emit(RawNetworkEvent raw) => Accept!(raw);
        public void Fail(Exception exception) { error = exception; done.Set(); }
        public void Stop() { Stopped = true; done.Set(); }
        public bool TryGetEventsLost(out int count) { count = 7; return true; }
        public void Dispose() { Disposed = true; done.Dispose(); }
    }
}
