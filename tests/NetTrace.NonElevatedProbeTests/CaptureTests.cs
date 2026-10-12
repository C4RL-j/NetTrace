using AppUsageEtwProbe;
using AppUsageNonElevatedProbe;

internal static class CaptureTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("An elevated token is rejected before a candidate session can be created", ElevatedGuard),
        ("A permitted normal-token session with no events shuts down cleanly and remains unsuitable", PermittedNoEvents),
        ("Denied session start and denied provider enable retain distinct permission stages", PermissionStages),
        ("Cancellation releases and disposes a running normal-token consumer", Cancellation),
    ];

    private static async Task ElevatedGuard()
    {
        var allocations = 0;
        var capture = new CandidateCapture(Guid.NewGuid(), 1, SafeFields(), (_, _) =>
        {
            allocations++;
            throw new InvalidOperationException("An elevated run must not create a session.");
        }, isElevated: () => true);
        var result = await capture.CaptureAsync(TimeSpan.FromMilliseconds(30), CancellationToken.None, _ => { });
        Check.Equal(0, allocations, "elevated-token guard executes before session allocation");
        Check.True(!result.ConsumerStarted && !result.SessionStartPermitted && !result.ProviderEnablePermitted,
            "elevated execution cannot be presented as a non-elevated capability result");
        Check.True(!result.AccessDenied, "refusing the wrong token is distinct from denied permissions");
        Check.True(result.Error?.Contains("Elevated token rejected", StringComparison.Ordinal) == true,
            "the required normal-token run is explained");
    }

    private static async Task PermittedNoEvents()
    {
        var session = new FakeSession();
        try
        {
            var result = await NewCapture(session).CaptureAsync(TimeSpan.FromMilliseconds(30),
                CancellationToken.None, _ => throw new InvalidOperationException("The fake source emits no events."))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Check.True(result.SessionStartPermitted && result.ProviderEnablePermitted && result.ConsumerStarted,
                "normal-token session permission is recorded independently of event emission");
            Check.True(result.ShutdownClean && !result.AccessDenied, "permitted source stops cleanly");
            Check.Equal(0L, result.ProviderEventCount, "empty source does not fabricate events");
            Check.Equal(1, session.DisposeCount, "empty source is disposed once");
            Check.True(result.WindowStartUtc is not null && result.WindowEndUtc >= result.WindowStartUtc,
                "a permitted source records a real observation window");
            var classified = CapabilityClassifier.Classify(new(true, result.SessionStartPermitted,
                result.ProviderEnablePermitted, result.AccessDenied, result.ShutdownClean,
                result.ProviderEventCount, result.EventsLost, false, []));
            Check.Equal(ProviderSuitability.Unsuitable, classified.Suitability,
                "session permission without live events is not accounting success");
        }
        finally
        {
            session.Release();
        }
    }

    private static async Task PermissionStages()
    {
        foreach (var stage in new[] { "SessionStart", "ProviderEnable" })
        {
            var session = new FakeSession { DeniedStage = stage };
            var result = await NewCapture(session).CaptureAsync(TimeSpan.FromMilliseconds(30),
                CancellationToken.None, _ => { }).WaitAsync(TimeSpan.FromSeconds(5));
            Check.True(result.AccessDenied && !result.ConsumerStarted, "denied permissions prevent a consumer start");
            Check.Equal(stage, result.AccessDeniedStage, "failure stage survives shutdown");
            Check.Equal(stage == "ProviderEnable", result.SessionStartPermitted,
                "session start permission is distinguished from provider permission");
            Check.True(!result.ProviderEnablePermitted, "denied source never reports provider enable permission");
            Check.Equal(1, session.DisposeCount, "denied source is disposed once");
            Check.True(result.Error?.Contains("Non-elevated ETW access denied", StringComparison.Ordinal) == true,
                "permission denial has a clear diagnostic");
        }
    }

    private static async Task Cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new FakeSession();
        try
        {
            var result = await NewCapture(session).CaptureAsync(TimeSpan.FromMinutes(1), cancellation.Token, _ => { },
                _ =>
                {
                    cancellation.Cancel();
                    return Task.CompletedTask;
                }).WaitAsync(TimeSpan.FromSeconds(5));
            Check.True(result.Cancelled && result.ConsumerStarted, "active consumer cancellation is recorded");
            Check.True(result.ShutdownClean, "cancelled consumer exits cleanly");
            Check.True(session.StopCount > 0, "cancellation stops the owned consumer");
            Check.Equal(1, session.DisposeCount, "cancelled consumer is disposed once");
            await session.ProcessingCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            session.Release();
        }
    }

    private static CandidateCapture NewCapture(FakeSession session) =>
        new(Guid.NewGuid(), 1, SafeFields(), (_, _) => session, isElevated: () => false);

    private static IReadOnlyDictionary<(int, int), IReadOnlyList<PayloadField>> SafeFields() =>
        new Dictionary<(int, int), IReadOnlyList<PayloadField>>
        {
            [(900, 2)] = [new PayloadField("PID", "win:UInt32"), new PayloadField("size", "win:UInt32")]
        };

    private sealed class FakeSession : ICandidateSession
    {
        private readonly ManualResetEventSlim _stop = new(false);
        private bool _disposed;
        private int _disposeCount;
        private int _stopCount;
        public string? DeniedStage { get; init; }
        public bool SessionStartPermitted { get; private set; }
        public bool ProviderEnablePermitted { get; private set; }
        public string Stage { get; private set; } = "SessionStart";
        public int EventsLost => 0;
        public long ProviderEventCount => 0;
        public long SelectedEventCount => 0;
        public long UnsupportedEventCount => 0;
        public long MalformedEventCount => 0;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public int StopCount => Volatile.Read(ref _stopCount);
        public TaskCompletionSource ProcessingCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Start()
        {
            if (DeniedStage == Stage) throw new UnauthorizedAccessException();
            SessionStartPermitted = true;
            Stage = "ProviderEnable";
            if (DeniedStage == Stage) throw new UnauthorizedAccessException();
            ProviderEnablePermitted = true;
            Stage = "Consumption";
        }

        public void Process()
        {
            try
            {
                _stop.Wait();
            }
            finally
            {
                ProcessingCompleted.TrySetResult();
            }
        }

        public void Stop()
        {
            Stage = "Shutdown";
            Interlocked.Increment(ref _stopCount);
            _stop.Set();
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            _disposed = true;
            _stop.Dispose();
        }

        public void Release()
        {
            if (!_disposed) _stop.Set();
        }
    }
}
