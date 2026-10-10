using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading.Channels;
using NetTrace.Models;
using NetTrace.Services;

internal static class IcmpRollingTests
{
    public static (string Name, Action Run)[] Cases =>
    [
        ("An unstarted ICMP monitor has no invented jitter or rolling samples", Defaults),
        ("Identical successful ICMP latencies produce zero jitter", Run(ZeroJitter)),
        ("Stable successful ICMP latencies produce small jitter", Run(LowJitter)),
        ("ICMP jitter is the mean absolute successive successful RTT difference", Run(VaryingJitter)),
        ("One successful ICMP reply has latency but insufficient data for jitter", Run(InsufficientSuccessfulReplies)),
        ("An all-failed ICMP cycle has no invented latency or jitter", Run(AllFailedJitter)),
        ("Failed replies are excluded from both ICMP latency and jitter arithmetic", Run(FailedRepliesExcluded)),
        ("Invalid negative successful RTTs cannot enter latency or jitter arithmetic", Run(NegativeRepliesExcluded)),
        ("ICMP jitter starts fresh for each completed burst", Run(PerCycleJitter)),
        ("Rolling ICMP history retains only the newest 60 completed cycles in observation order", Run(BoundedOrderedHistory)),
        ("Rolling ICMP snapshots remain stable and cannot alter worker history", Run(DefensiveSnapshots)),
        ("Sleep discards incomplete ICMP history and resume uses only fresh successful RTTs", Run(SleepAndResume)),
        ("Shutdown cannot append an incomplete ICMP cycle to rolling history", Run(ShutdownDiscardsPartialCycle)),
    ];

    private static Action Run(Func<Task> test) => () => test().GetAwaiter().GetResult();

    private static void Defaults()
    {
        using var monitor = new IcmpMonitor(new ChannelPingSource());
        Equal(60, IcmpMonitor.HistoryCapacity, "bounded history capacity");
        Equal(0, monitor.RollingSamples.Count, "history before monitoring starts");
        Check(monitor.LatestResult is null, "An unstarted worker must not fabricate a result.");
    }

    private static async Task ZeroJitter()
    {
        var result = await ProbeAsync(Success(42), Success(42), Success(42), Success(42));
        AssertMeasurement(result, 4, 0, 42, 0, 0);
    }

    private static async Task LowJitter()
    {
        var result = await ProbeAsync(Success(40), Success(41), Success(40), Success(41));
        AssertMeasurement(result, 4, 0, 40.5, 0, 1);
    }

    private static async Task VaryingJitter()
    {
        var result = await ProbeAsync(Success(10), Success(20), Success(5), Success(15));
        AssertMeasurement(result, 4, 0, 12.5, 0, 35.0 / 3);
    }

    private static async Task InsufficientSuccessfulReplies()
    {
        var result = await ProbeAsync(Failure(), Success(30), Failure(), Failure());
        AssertMeasurement(result, 1, 3, 30, 75, null);
    }

    private static async Task AllFailedJitter()
    {
        var result = await ProbeAsync(Failure(), Failure(), Failure(), Failure());
        AssertMeasurement(result, 0, 4, null, 100, null);
    }

    private static async Task FailedRepliesExcluded()
    {
        var result = await ProbeAsync(Success(10), Failure(99999), Success(30),
            new IcmpPingReply(IPStatus.DestinationHostUnreachable, 88888));
        // After filtering failures, the successful sequence is [10, 30].
        AssertMeasurement(result, 2, 2, 20, 50, 20);
    }

    private static async Task NegativeRepliesExcluded()
    {
        var result = await ProbeAsync(Success(-1000), Success(10), Success(30), Failure());
        AssertMeasurement(result, 2, 2, 20, 50, 20);
    }

    private static async Task PerCycleJitter()
    {
        var source = new ChannelPingSource();
        using var monitor = CreateMonitor(source);
        try
        {
            monitor.Start();
            source.WriteCycle(Success(1000), Success(1000), Success(1000), Success(1000));
            var first = await NextResultAsync(monitor);
            source.WriteCycle(Success(10), Success(20), Success(30), Success(40));
            var second = await NextResultAsync(monitor, first);
            AssertMeasurement(first, 4, 0, 1000, 0, 0);
            AssertMeasurement(second, 4, 0, 25, 0, 10);
            Equal(2, monitor.RollingSamples.Count, "completed per-burst results retained");
            Check(ReferenceEquals(second, monitor.RollingSamples[^1]),
                "Latest and rolling history must expose the same completed observation.");
        }
        finally { await StopAsync(monitor); }
    }

    private static async Task BoundedOrderedHistory()
    {
        var source = new ChannelPingSource();
        var origin = new DateTimeOffset(2026, 10, 11, 3, 0, 0, TimeSpan.Zero);
        var clock = new ControlledClock(origin);
        using var monitor = CreateMonitor(source, clock);
        try
        {
            monitor.Start();
            IcmpProbeResult? previous = null;
            for (var cycle = 1; cycle <= 65; cycle++)
            {
                clock.Advance(TimeSpan.FromSeconds(15));
                source.WriteCycle(Success(cycle), Success(cycle), Success(cycle), Success(cycle));
                previous = await NextResultAsync(monitor, previous);
                Equal(Math.Min(cycle, 60), monitor.RollingSamples.Count, "history remains bounded after every cycle");
            }

            var samples = monitor.RollingSamples;
            Equal(60, samples.Count, "history after exceeding capacity");
            for (var index = 0; index < samples.Count; index++)
            {
                var cycle = index + 6;
                Equal((double?)cycle, samples[index].AverageLatencyMs, "FIFO cycle value");
                Equal(origin.AddSeconds(cycle * 15), samples[index].TimestampUtc, "FIFO observation timestamp");
                Equal((double?)0, samples[index].JitterMs, "completed stable cycle jitter");
                if (index > 0)
                    Check(samples[index - 1].TimestampUtc < samples[index].TimestampUtc,
                        "Rolling history must be ordered from oldest to newest.");
            }
            Check(ReferenceEquals(previous, samples[^1]), "The newest retained cycle must be LatestResult.");
            Equal(1, source.MaximumConcurrent, "one existing ping worker owns every retained sample");
        }
        finally { await StopAsync(monitor); }
    }

    private static async Task DefensiveSnapshots()
    {
        var source = new ChannelPingSource();
        using var monitor = CreateMonitor(source);
        try
        {
            monitor.Start();
            source.WriteCycle(Success(10), Success(10), Success(10), Success(10));
            var first = await NextResultAsync(monitor);
            var snapshot = monitor.RollingSamples;
            source.WriteCycle(Success(20), Success(20), Success(20), Success(20));
            await NextResultAsync(monitor, first);
            Equal(1, snapshot.Count, "an earlier snapshot does not grow with worker history");
            Check(ReferenceEquals(first, snapshot[0]), "An earlier snapshot retains its completed result.");
            Equal(2, monitor.RollingSamples.Count, "worker history receives the later cycle");

            if (snapshot is IList<IcmpProbeResult> mutable)
            {
                Check(mutable.IsReadOnly, "The public rolling snapshot must reject mutations.");
                var rejected = false;
                try { mutable[0] = first with { JitterMs = 12345 }; }
                catch (NotSupportedException) { rejected = true; }
                Check(rejected, "Replacing an observation in the public snapshot must be rejected.");
            }
            Check(ReferenceEquals(first, monitor.RollingSamples[0]),
                "A consumer must not be able to replace an observation in worker-owned history.");
            Equal((double?)0, monitor.RollingSamples[0].JitterMs, "consumer access cannot corrupt completed measurements");
        }
        finally { await StopAsync(monitor); }
    }

    private static async Task SleepAndResume()
    {
        var source = new ChannelPingSource();
        var clock = new ControlledClock(new DateTimeOffset(2026, 10, 11, 4, 0, 0, TimeSpan.Zero));
        using var monitor = CreateMonitor(source, clock);
        try
        {
            monitor.Start();
            source.WriteCycle(Success(50), Success(50), Success(50), Success(50));
            var first = await NextResultAsync(monitor);

            source.Write(Success(1000));
            source.Write(Success(2000));
            await WaitUntilAsync(() => source.Calls == 7 && source.ActiveCalls == 1,
                "the incomplete pre-sleep burst's third attempt");
            monitor.Suspend();
            await WaitUntilAsync(() => source.ActiveCalls == 0, "sleep cancellation of the incomplete burst");
            Equal(1, monitor.RollingSamples.Count, "sleep cannot publish a partial burst");
            Check(ReferenceEquals(first, monitor.LatestResult), "Sleep keeps the last completed observation.");
            await Task.Delay(30);
            Equal(7, source.Calls, "suspended monitoring cannot send another ping");

            clock.Advance(TimeSpan.FromHours(1));
            monitor.Resume();
            source.WriteCycle(Success(10), Success(20), Success(30), Success(40));
            var resumed = await NextResultAsync(monitor, first);
            AssertMeasurement(resumed, 4, 0, 25, 0, 10);
            Equal(clock.GetUtcNow(), resumed.TimestampUtc, "new post-resume observation timestamp");
            var samples = monitor.RollingSamples;
            Equal(2, samples.Count, "history contains the pre-sleep and fresh post-resume completed cycles only");
            Check(ReferenceEquals(first, samples[0]) && ReferenceEquals(resumed, samples[1]),
                "Resume must not mix or append stale incomplete observations.");
            Equal(1, source.MaximumConcurrent, "old and resumed ping attempts cannot overlap");
        }
        finally { await StopAsync(monitor); }
    }

    private static async Task ShutdownDiscardsPartialCycle()
    {
        var source = new ChannelPingSource();
        using var monitor = CreateMonitor(source);
        try
        {
            monitor.Start();
            source.WriteCycle(Failure(), Failure(), Failure(), Failure());
            var completed = await NextResultAsync(monitor);
            source.Write(Success(100));
            source.Write(Success(200));
            await WaitUntilAsync(() => source.Calls == 7 && source.ActiveCalls == 1,
                "a pending incomplete cycle before shutdown");
            await StopAsync(monitor);
            await WaitUntilAsync(() => source.ActiveCalls == 0,
                "the cancelled source continuation to release after shutdown");
            Equal(0, source.ActiveCalls, "shutdown releases the pending injected probe");
            Equal(1, monitor.RollingSamples.Count, "shutdown retains completed cycles only");
            Check(ReferenceEquals(completed, monitor.LatestResult), "Shutdown cannot replace the last completed result.");
            AssertMeasurement(monitor.RollingSamples[0], 0, 4, null, 100, null);
        }
        finally { await StopAsync(monitor); }
    }

    private static IcmpMonitor CreateMonitor(ChannelPingSource source, TimeProvider? clock = null) =>
        new(source, interval: TimeSpan.FromMilliseconds(5), timeout: TimeSpan.FromMinutes(5), timeProvider: clock);

    private static async Task<IcmpProbeResult> ProbeAsync(params IcmpPingReply[] replies)
    {
        var source = new ChannelPingSource();
        using var monitor = CreateMonitor(source);
        try
        {
            source.WriteCycle(replies);
            monitor.Start();
            var result = await NextResultAsync(monitor);
            Equal(1, monitor.RollingSamples.Count, "one completed injected cycle");
            Check(ReferenceEquals(result, monitor.RollingSamples[0]),
                "Every completed observation, including an all-failed cycle, must enter rolling history.");
            return result;
        }
        finally { await StopAsync(monitor); }
    }

    private static async Task<IcmpProbeResult> NextResultAsync(IcmpMonitor monitor, IcmpProbeResult? previous = null)
    {
        await WaitUntilAsync(() => monitor.LatestResult is { } current && !ReferenceEquals(previous, current),
            "the next completed ICMP observation");
        return monitor.LatestResult!;
    }

    private static async Task StopAsync(IcmpMonitor monitor)
    {
        monitor.Stop();
        await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Check(!monitor.Completion.IsFaulted, "The existing ICMP worker must shut down without a fault.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5))
                throw new InvalidOperationException($"Timed out waiting for {description}.");
            await Task.Delay(2);
        }
    }

    private static IcmpPingReply Success(long latency) => new(IPStatus.Success, latency);
    private static IcmpPingReply Failure(long latency = 0) => new(IPStatus.TimedOut, latency);

    private static void AssertMeasurement(IcmpProbeResult result, int successes, int failures,
        double? latency, double loss, double? jitter)
    {
        Equal(successes, result.SuccessfulReplies, "successful replies");
        Equal(failures, result.FailedReplies, "failed replies");
        Equal(4, successes + failures, "attempts per completed burst");
        Equal(latency, result.AverageLatencyMs, "successful RTT average");
        Equal(loss, result.PacketLossPercent, "loss percentage");
        Equal(jitter, result.JitterMs, "successful RTT jitter");
    }

    private static void Equal<T>(T expected, T actual, string description)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Unexpected {description}: expected {expected}, actual {actual}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ChannelPingSource : IPingSource
    {
        private readonly Channel<IcmpPingReply> _replies = Channel.CreateUnbounded<IcmpPingReply>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });
        private int _calls;
        private int _activeCalls;
        private int _maximumConcurrent;
        public int Calls => Volatile.Read(ref _calls);
        public int ActiveCalls => Volatile.Read(ref _activeCalls);
        public int MaximumConcurrent => Volatile.Read(ref _maximumConcurrent);

        public void Write(IcmpPingReply reply)
        {
            if (!_replies.Writer.TryWrite(reply))
                throw new InvalidOperationException("The owned fake ping source stopped accepting replies.");
        }

        public void WriteCycle(params IcmpPingReply[] replies)
        {
            Equal(4, replies.Length, "injected burst length");
            foreach (var reply in replies) Write(reply);
        }

        public async Task<IcmpPingReply> SendAsync(IPAddress target, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var active = Interlocked.Increment(ref _activeCalls);
            while (true)
            {
                var previous = Volatile.Read(ref _maximumConcurrent);
                if (active <= previous || Interlocked.CompareExchange(ref _maximumConcurrent, active, previous) == previous)
                    break;
            }
            try { return await _replies.Reader.ReadAsync(cancellationToken).ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref _activeCalls); }
        }
    }

    private sealed class ControlledClock(DateTimeOffset initial) : TimeProvider
    {
        private long _utcTicks = initial.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _utcTicks, elapsed.Ticks);
    }
}
