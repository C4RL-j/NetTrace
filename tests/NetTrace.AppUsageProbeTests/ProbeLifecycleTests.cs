using System.Collections.Concurrent;
using AppUsageEtwProbe;

internal static class ProbeLifecycleTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Cancellation stops and disposes an active consumer without leaving processing running", Cancellation),
        ("A bounded observation window stops and disposes its consumer", WindowCompletion),
        ("Access denial is reported clearly and the failed session is disposed", AccessDenied),
        ("The capture forwards provider events without replacing their payload PID", ForwardedEvents),
        ("A pre-cancelled capture never allocates or starts a consumer", PreCancelled),
        ("A consumer processing failure still stops and disposes the session", ProcessingFailure),
        ("Capture result boundaries use the exact physical counter callback timestamps", CounterBoundaryTimestamps),
        ("Buffered events outside the counter window are excluded while in-window events survive shutdown", BufferedWindowFiltering),
    ];

    private static async Task Cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new FakeSession();
        var capture = NewCapture(session);
        try
        {
            var result = await capture.CaptureAsync(TimeSpan.FromMinutes(1), cancellation.Token, _ => { },
                _ =>
                {
                    cancellation.Cancel();
                    return Task.CompletedTask;
                }).WaitAsync(TimeSpan.FromSeconds(5));
            Check.True(result.SessionStarted, "fake session started before cancellation");
            Check.True(result.Cancelled, "cancellation is recorded");
            Check.True(result.ShutdownClean, "consumer shutdown completes");
            Check.Equal(1, session.StartCount, "session starts once");
            Check.True(session.StopCount > 0, "cancellation calls Stop");
            Check.Equal(1, session.DisposeCount, "session disposed once");
            await session.ProcessingCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            session.Release();
        }
    }

    private static async Task WindowCompletion()
    {
        var session = new FakeSession();
        int endingCallbacks = 0;
        try
        {
            var result = await NewCapture(session).CaptureAsync(TimeSpan.FromMilliseconds(30),
                CancellationToken.None, _ => { }, onWindowEnding: () => Interlocked.Increment(ref endingCallbacks))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Check.True(result.SessionStarted, "observation started");
            Check.True(result.ShutdownClean, "elapsed window stops cleanly");
            Check.True(!result.Cancelled, "normal completion is distinct from cancellation");
            Check.Equal(1, endingCallbacks, "adapter boundary callback runs once");
            Check.True(session.StopCount > 0, "elapsed window stops consumer");
            Check.Equal(1, session.DisposeCount, "consumer disposed once");
            await session.ProcessingCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            session.Release();
        }
    }

    private static async Task AccessDenied()
    {
        var session = new FakeSession { StartException = new UnauthorizedAccessException("ETW administrator rights required") };
        var result = await NewCapture(session).CaptureAsync(TimeSpan.FromMilliseconds(30),
            CancellationToken.None, _ => { }).WaitAsync(TimeSpan.FromSeconds(5));
        Check.True(!result.SessionStarted, "access-denied session never reports a successful start");
        Check.True(result.AccessDenied, "access denial is explicit");
        Check.Equal(1, session.DisposeCount, "failed session is disposed");
        Check.True(!session.Calls.Contains("Process"), "denied startup does not start processing");
    }

    private static async Task ForwardedEvents()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new FakeSession
        {
            Event = new RawNetworkEvent(700, 1, 4, DateTime.UnixEpoch,
                new Dictionary<string, object?> { ["PID"] = 4321U, ["size"] = 512U })
        };
        RawNetworkEvent? forwarded = null;
        try
        {
            var result = await NewCapture(session).CaptureAsync(TimeSpan.FromSeconds(4),
                cancellation.Token, activity =>
                {
                    forwarded = activity;
                    cancellation.Cancel();
                }, _ =>
                {
                    session.Emit();
                    return Task.CompletedTask;
                }).WaitAsync(TimeSpan.FromSeconds(5));
            Check.True(result.ShutdownClean, "event forwarding retains clean shutdown");
            Check.True(forwarded is not null, "provider event reaches the consumer callback");
            Check.Equal(4, forwarded!.HeaderPid, "header PID retained separately");
            Check.Equal(4321U, forwarded.Payload["PID"], "payload attribution PID retained");
        }
        finally
        {
            session.Release();
        }
    }

    private static async Task PreCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var session = new FakeSession();
        var result = await NewCapture(session).CaptureAsync(TimeSpan.FromMilliseconds(30),
            cancellation.Token, _ => { }).WaitAsync(TimeSpan.FromSeconds(5));
        Check.True(result.Cancelled, "pre-existing cancellation reported");
        Check.True(!result.SessionStarted, "pre-cancelled run never starts ETW");
        Check.Equal(0, session.StartCount, "no start after cancellation");
        Check.Equal(0, session.DisposeCount, "no consumer allocation after cancellation");
    }

    private static async Task ProcessingFailure()
    {
        var session = new FakeSession { ProcessException = new InvalidOperationException("Controlled consumer failure") };
        var result = await NewCapture(session).CaptureAsync(TimeSpan.FromMilliseconds(30),
            CancellationToken.None, _ => { }).WaitAsync(TimeSpan.FromSeconds(5));
        Check.True(result.SessionStarted, "consumer starts before processing error");
        Check.True(result.Error?.Contains("Controlled consumer failure", StringComparison.Ordinal) == true,
            "processing failure retains useful diagnostic context");
        Check.True(session.StopCount > 0, "failed consumer still receives Stop");
        Check.Equal(1, session.DisposeCount, "failed consumer disposed once");
    }

    private static async Task CounterBoundaryTimestamps()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddSeconds(2);
        int openingCount = 0, closingCount = 0;
        var session = new FakeSession();
        try
        {
            var result = await NewCapture(session).CaptureAsync(TimeSpan.FromMilliseconds(30),
                CancellationToken.None, _ => { }, onWindowOpening: () =>
                {
                    openingCount++;
                    return start;
                }, onWindowClosing: () =>
                {
                    closingCount++;
                    return end;
                }).WaitAsync(TimeSpan.FromSeconds(5));
            Check.True(result.ShutdownClean, "counter-boundary capture stops cleanly");
            Check.Equal(1, openingCount, "opening counter snapshot is taken once");
            Check.Equal(1, closingCount, "closing counter snapshot is taken once");
            Check.Equal<DateTime?>(start, result.WindowStartUtc, "physical counter start timestamp retained exactly");
            Check.Equal<DateTime?>(end, result.WindowEndUtc, "physical counter end timestamp retained exactly");
        }
        finally
        {
            session.Release();
        }
    }

    private static async Task BufferedWindowFiltering()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddSeconds(2);
        var session = new FakeSession
        {
            BufferedEvents =
            [
                WindowEvent(start.AddTicks(-1), 100), WindowEvent(start, 101),
                WindowEvent(start.AddSeconds(1), 102), WindowEvent(end, 103),
                WindowEvent(end.AddTicks(1), 104)
            ]
        };
        var accepted = new List<RawNetworkEvent>();
        bool closingSnapshotTaken = false;
        try
        {
            var result = await NewCapture(session).CaptureAsync(TimeSpan.FromMilliseconds(30),
                CancellationToken.None, value =>
                {
                    Check.True(closingSnapshotTaken, "fake buffered event is delivered after the closing counter read");
                    accepted.Add(value);
                }, onWindowOpening: () => start, onWindowClosing: () =>
                {
                    closingSnapshotTaken = true;
                    return end;
                }).WaitAsync(TimeSpan.FromSeconds(5));
            Check.True(result.ShutdownClean, "buffered flush completes cleanly");
            Check.Equal(3, accepted.Count, "only timestamps within inclusive counter boundaries survive");
            Check.True(accepted.Select(value => (uint)value.Payload["PID"]!).SequenceEqual(new uint[] { 101, 102, 103 }),
                "pre-window and post-window events are excluded while buffered in-window events remain");
            Check.Equal<DateTime?>(start, result.WindowStartUtc, "filter start matches counter read");
            Check.Equal<DateTime?>(end, result.WindowEndUtc, "filter end matches counter read");
        }
        finally
        {
            session.Release();
        }
    }

    private static RawNetworkEvent WindowEvent(DateTime timestamp, uint pid) =>
        new(700, 1, 4, timestamp, new Dictionary<string, object?> { ["PID"] = pid, ["size"] = 64U });

    private static EtwCapture NewCapture(FakeSession session) => new(
        new ProviderSchema(true, 1,
            [new NetworkEventSchema(700, 1, TrafficDirection.Send, IpFamily.Ipv4,
                [new PayloadField("PID", "win:UInt32"), new PayloadField("size", "win:UInt32")])]),
        (_, _, callback) =>
        {
            session.Callback = callback;
            return session;
        });

    private sealed class FakeSession : IEtwCaptureSession
    {
        private readonly ManualResetEventSlim _stop = new(false);
        private readonly ManualResetEventSlim _emit = new(false);
        private int _startCount;
        private int _stopCount;
        private int _disposeCount;
        public Exception? StartException { get; init; }
        public Exception? ProcessException { get; init; }
        public RawNetworkEvent? Event { get; init; }
        public IReadOnlyList<RawNetworkEvent> BufferedEvents { get; init; } = [];
        public Action<RawNetworkEvent>? Callback { get; set; }
        public ConcurrentQueue<string> Calls { get; } = new();
        public TaskCompletionSource ProcessingCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StartCount => Volatile.Read(ref _startCount);
        public int StopCount => Volatile.Read(ref _stopCount);
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public int EventsLost => 0;
        public long ProviderEventCount => (Event is null ? 0 : 1) + BufferedEvents.Count;
        public long UnsupportedEventCount => 0;
        public long MalformedEventCount => 0;

        public void Start()
        {
            Calls.Enqueue("Start");
            Interlocked.Increment(ref _startCount);
            if (StartException is not null) throw StartException;
        }

        public void Process()
        {
            Calls.Enqueue("Process");
            try
            {
                if (ProcessException is not null) throw ProcessException;
                if (Event is not null)
                {
                    _emit.Wait();
                    Callback!(Event with { TimestampUtc = DateTime.UtcNow });
                }
                _stop.Wait();
            }
            finally
            {
                ProcessingCompleted.TrySetResult();
            }
        }

        public void Stop()
        {
            Calls.Enqueue("Stop");
            Interlocked.Increment(ref _stopCount);
            try
            {
                foreach (var value in BufferedEvents) Callback!(value);
            }
            finally
            {
                _stop.Set();
                _emit.Set();
            }
        }

        public void Dispose()
        {
            Calls.Enqueue("Dispose");
            Interlocked.Increment(ref _disposeCount);
            _stop.Set();
            _emit.Set();
        }

        public void Emit() => _emit.Set();
        public void Release()
        {
            _stop.Set();
            _emit.Set();
        }
    }
}
