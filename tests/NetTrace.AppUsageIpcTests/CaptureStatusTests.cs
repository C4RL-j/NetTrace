using AppUsageEtwProbe;

internal static class CaptureStatusTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("A running collector exposes live loss and provider counts, then cancels and disposes its owned session", KnownLoss),
        ("An unavailable ETW loss query remains explicitly unavailable during and after capture", UnavailableLoss),
        ("A failing live loss query cannot tear down aggregation or cleanup", FailedLossQuery),
        ("A pre-cancelled collector capture never creates an ETW session", PreCancelled),
        ("Invalid owned ETW session prefixes fail before any session is created", InvalidPrefixes),
    ];

    private static async Task KnownLoss()
    {
        var session = new FakeStatusSession();
        using var cancel = new CancellationTokenSource();
        var capture = new EtwCapture(AggregationTests.Schema, (_, _, _) => session, "NetTrace-AppUsage002-Test");
        Check.True(!capture.GetLiveStatus().IsCapturing, "initial status is inactive");
        var result = await capture.CaptureAsync(TimeSpan.FromMinutes(1), cancel.Token, _ => { }, _ =>
        {
            var status = capture.GetLiveStatus();
            Check.True(status.IsCapturing && status.SessionStarted, "live capture status");
            Check.True(status.SessionName!.StartsWith("NetTrace-AppUsage002-Test-", StringComparison.Ordinal), "owned collector namespace");
            Check.True(status.LostEventCountAvailable, "loss availability");
            Check.Equal<int?>(7, status.EventsLost, "actual live loss count");
            Check.Equal(100L, status.ProviderEventCount, "live provider count");
            Check.Equal(4L, status.UnsupportedEventCount, "live unsupported count");
            Check.Equal(2L, status.MalformedEventCount, "live malformed count");
            cancel.Cancel();
            return Task.CompletedTask;
        }).WaitAsync(TimeSpan.FromSeconds(3));
        Check.True(result.Cancelled && result.ShutdownClean, "cancellation has a clean ETW shutdown");
        Check.True(session.Stopped && session.Disposed, "owned session stopped and disposed");
        var final = capture.GetLiveStatus();
        Check.True(!final.IsCapturing && final.SessionStarted, "completed status preserves start proof");
        Check.Equal<int?>(7, final.EventsLost, "loss survives disposal");
    }

    private static async Task UnavailableLoss() => await LossAvailability(new FakeStatusSession { LossAvailable = false });
    private static async Task FailedLossQuery() => await LossAvailability(new FakeStatusSession { ThrowLoss = true });
    private static async Task LossAvailability(FakeStatusSession session)
    {
        using var cancel = new CancellationTokenSource();
        var capture = new EtwCapture(AggregationTests.Schema, (_, _, _) => session, "NetTrace-AppUsage002-Test");
        var result = await capture.CaptureAsync(TimeSpan.FromMinutes(1), cancel.Token, _ => { }, _ =>
        {
            var status = capture.GetLiveStatus();
            Check.True(!status.LostEventCountAvailable && status.EventsLost is null, "no zero is invented for unavailable loss");
            Check.Equal(100L, status.ProviderEventCount, "loss query failure does not erase provider metadata");
            cancel.Cancel(); return Task.CompletedTask;
        }).WaitAsync(TimeSpan.FromSeconds(3));
        Check.True(result.ShutdownClean && session.Disposed, "loss query failure cannot bypass cleanup");
        Check.True(!capture.GetLiveStatus().LostEventCountAvailable, "final status still reports unavailable loss");
    }

    private static async Task PreCancelled()
    {
        int created = 0; using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var capture = new EtwCapture(AggregationTests.Schema, (_, _, _) => { created++; return new FakeStatusSession(); }, "NetTrace-AppUsage002-Test");
        var result = await capture.CaptureAsync(TimeSpan.FromMinutes(1), cancel.Token, _ => { });
        Check.True(result.Cancelled && !result.SessionStarted, "pre-cancelled request is explicit");
        Check.Equal(0, created, "no owned ETW resource was created");
        Check.True(!capture.GetLiveStatus().IsCapturing, "no false live status");
    }

    private static async Task InvalidPrefixes()
    {
        foreach (string prefix in new[] { "", " ", "arbitrary\\session", "session;command", new string('x', 97) })
        {
            int created = 0;
            var capture = new EtwCapture(AggregationTests.Schema, (_, _, _) => { created++; return new FakeStatusSession(); }, prefix);
            await Check.ThrowsAsync<ArgumentException>(() => capture.CaptureAsync(TimeSpan.FromSeconds(1), CancellationToken.None, _ => { }),
                "owned prefix validation");
            Check.Equal(0, created, "invalid prefix never reaches a controller");
        }
    }

    private sealed class FakeStatusSession : IEtwCaptureSession, IEtwCaptureStatusSession
    {
        private readonly ManualResetEventSlim done = new();
        public bool LossAvailable { get; init; } = true;
        public bool ThrowLoss { get; init; }
        public bool Stopped { get; private set; }
        public bool Disposed { get; private set; }
        public int EventsLost => 7;
        public long ProviderEventCount => 100;
        public long UnsupportedEventCount => 4;
        public long MalformedEventCount => 2;
        public void Start() { }
        public void Process() => done.Wait();
        public void Stop() { Stopped = true; done.Set(); }
        public bool TryGetEventsLost(out int count)
        {
            if (ThrowLoss) throw new InvalidOperationException("Fixture query failed.");
            count = 7; return LossAvailable;
        }
        public void Dispose() { Disposed = true; done.Dispose(); }
    }
}
