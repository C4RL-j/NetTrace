using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using NetTrace.Models;
using NetTrace.Services;

internal static class IcmpHealthTests
{
    public static (string Name, Action Run)[] Cases =>
    [
        ("ICMP defaults use one configurable target, four attempts and a 15-second cadence", Defaults),
        ("Four successful ICMP replies report their average latency and zero packet loss", Run(FourSuccessfulReplies)),
        ("Partial ICMP failure reports loss from all attempts and latency from successful replies only", Run(PartialFailures)),
        ("All failed ICMP replies report 100 percent loss and no invented latency", Run(AllFailures)),
        ("A valid zero-millisecond ICMP reply remains a real latency measurement", Run(ZeroMillisecondReplies)),
        ("ICMP API exceptions count as failures without terminating the probe worker", Run(ApiExceptions)),
        ("A pre-cancelled Windows ping request exits before sending real ICMP traffic", Run(PreCancelledWindowsPing)),
        ("Per-attempt ICMP timeouts complete safely without a bogus latency", Run(AttemptTimeouts)),
        ("ICMP results carry the configured target and UTC observation timestamp", Run(ResultMetadata)),
        ("Stopping a pending ICMP cycle cancels it promptly without publishing partial results", Run(CancelPendingCycle)),
        ("ICMP shutdown delivers source cancellation callbacks without blocking the caller", Run(CancellationCallbackCleanup)),
        ("A non-cooperative ICMP source cannot overlap probes or delay shutdown", Run(NonCooperativeShutdown)),
        ("Repeated ICMP Start calls and interval ticks cannot overlap probe cycles", Run(NoOverlappingCycles)),
        ("ICMP suspension cancels and discards a partial cycle until explicitly resumed", Run(SuspendAndResume)),
        ("ICMP partial observations never replace a previously completed result", Run(PreserveCompletedResult)),
        ("Background ICMP probing completes multiple cycles without a dashboard", Run(BackgroundCycles)),
        ("ICMP stop and disposal are safe before starting and when repeated", Run(IdempotentCleanup)),
    ];

    private static Action Run(Func<Task> test) => () => test().GetAwaiter().GetResult();

    private static void Defaults()
    {
        using var monitor = new IcmpMonitor(new ScriptedPingSource(_ => Success(1)));
        Equal(IPAddress.Parse("1.1.1.1"), monitor.Target, "default public target");
        Equal(TimeSpan.FromSeconds(15), monitor.Interval, "default probe interval");
        Equal(TimeSpan.FromSeconds(1), monitor.Timeout, "default per-attempt timeout");
        Equal(4, IcmpMonitor.AttemptsPerCycle, "attempts in one burst");
        Check(monitor.LatestResult is null, "An unstarted monitor must not fabricate a measurement.");
    }

    private static async Task FourSuccessfulReplies()
    {
        long[] latencies = [10, 20, 5, 15];
        var source = new ScriptedPingSource(call => Success(latencies[call - 1]));
        using var monitor = new IcmpMonitor(source);
        monitor.Start();
        var result = await ResultAsync(monitor);
        AssertResult(result, 4, 0, 12.5, 0);
        Equal(4, source.Calls, "one complete four-ping burst");
        await StopAsync(monitor);
    }

    private static async Task PartialFailures()
    {
        IcmpPingReply[] replies =
        [
            Success(10),
            new(IPStatus.TimedOut, 9999),
            Success(30),
            new(IPStatus.DestinationHostUnreachable, 9999),
        ];
        using var monitor = new IcmpMonitor(new ScriptedPingSource(call => replies[call - 1]));
        monitor.Start();
        AssertResult(await ResultAsync(monitor), 2, 2, 20, 50);
        await StopAsync(monitor);
    }

    private static async Task AllFailures()
    {
        IPStatus[] failures =
        [
            IPStatus.TimedOut,
            IPStatus.DestinationHostUnreachable,
            IPStatus.DestinationNetworkUnreachable,
            IPStatus.Unknown,
        ];
        using var monitor = new IcmpMonitor(new ScriptedPingSource(call => new(failures[call - 1], 12345)));
        monitor.Start();
        AssertResult(await ResultAsync(monitor), 0, 4, null, 100);
        await StopAsync(monitor);
    }

    private static async Task ZeroMillisecondReplies()
    {
        using var monitor = new IcmpMonitor(new ScriptedPingSource(_ => Success(0)));
        monitor.Start();
        AssertResult(await ResultAsync(monitor), 4, 0, 0, 0);
        await StopAsync(monitor);
    }

    private static async Task ApiExceptions()
    {
        var source = new ScriptedPingSource(call => call switch
        {
            1 => throw new PingException("Injected native ping failure"),
            2 => throw new TimeoutException("Injected timeout"),
            3 => throw new InvalidOperationException("Injected unavailable ping API"),
            _ => Success(42),
        });
        using var monitor = new IcmpMonitor(source);
        monitor.Start();
        AssertResult(await ResultAsync(monitor), 1, 3, 42, 75);
        Check(!monitor.Completion.IsFaulted, "Expected ping failures must not fault the monitoring worker.");
        await StopAsync(monitor);
    }

    private static async Task AttemptTimeouts()
    {
        var source = new TrackingPingSource(async (_, token) =>
        {
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
            return Success(0);
        });
        using var monitor = new IcmpMonitor(source, timeout: TimeSpan.FromMilliseconds(30));
        monitor.Start();
        AssertResult(await ResultAsync(monitor), 0, 4, null, 100);
        Equal(4, source.Calls, "four timed-out attempts");
        Equal(1, source.MaximumConcurrent, "timeouts must not leave overlapping probes");
        await StopAsync(monitor);
        Equal(0, source.ActiveCalls, "all timed-out fake pings released");
    }

    private static async Task PreCancelledWindowsPing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await new WindowsPingSource().SendAsync(IPAddress.Parse("1.1.1.1"), TimeSpan.FromMinutes(5), cancellation.Token);
        }
        catch (OperationCanceledException exception)
        {
            Equal(cancellation.Token, exception.CancellationToken, "the cancelled caller's token");
            return;
        }
        throw new InvalidOperationException("A pre-cancelled ping must not send or return an ICMP measurement.");
    }

    private static async Task ResultMetadata()
    {
        var target = IPAddress.Parse("9.9.9.9");
        var timeout = TimeSpan.FromMilliseconds(456);
        var timestamp = new DateTimeOffset(2026, 10, 10, 4, 5, 6, TimeSpan.Zero);
        var clock = new FixedClock(timestamp);
        var source = new CapturingPingSource();
        using var monitor = new IcmpMonitor(source, target, TimeSpan.FromSeconds(30), timeout, clock);
        monitor.Start();
        var result = await ResultAsync(monitor);
        Equal(target.ToString(), result.Target, "configured target in result");
        Equal(timestamp, result.TimestampUtc, "UTC observation timestamp");
        Equal(TimeSpan.Zero, result.TimestampUtc.Offset, "UTC timestamp offset");
        Equal(target, source.LastTarget, "configured target passed to ping source");
        Equal(timeout, source.LastTimeout, "configured timeout passed to ping source");
        AssertResult(result, 4, 0, 8, 0);
        await StopAsync(monitor);
    }

    private static async Task CancelPendingCycle()
    {
        var entered = NewSignal();
        var cancelled = NewSignal();
        var source = new TrackingPingSource(async (_, token) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
                return Success(0);
            }
            finally
            {
                if (token.IsCancellationRequested)
                    cancelled.TrySetResult();
            }
        });
        using var monitor = new IcmpMonitor(source, timeout: TimeSpan.FromMinutes(5));
        monitor.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var elapsed = Stopwatch.StartNew();
        await StopAsync(monitor);
        Check(elapsed.Elapsed < TimeSpan.FromSeconds(1), "Stop must not wait for the long ping timeout.");
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Equal(1, source.Calls, "cancelled burst must not start another ping");
        Check(monitor.LatestResult is null, "A cancelled partial burst is not a completed cycle.");
    }

    private static async Task NonCooperativeShutdown()
    {
        var pending = new TaskCompletionSource<IcmpPingReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = NewSignal();
        var source = new TrackingPingSource(async (_, _) =>
        {
            entered.TrySetResult();
            return await pending.Task; // Deliberately ignores cancellation to exercise shutdown safeguards.
        });
        using var monitor = new IcmpMonitor(source, interval: TimeSpan.FromMilliseconds(10), timeout: TimeSpan.FromMilliseconds(20));
        try
        {
            monitor.Start();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(100);
            Equal(1, source.Calls, "a source that refuses to release cannot start overlapping attempts");
            Equal(1, source.MaximumConcurrent, "one outstanding ping source operation");
            var elapsed = Stopwatch.StartNew();
            await StopAsync(monitor);
            Check(elapsed.Elapsed < TimeSpan.FromSeconds(1), "A non-cooperative ping must not hold up shutdown.");
            Check(monitor.LatestResult is null, "An incomplete burst must not manufacture a result.");
        }
        finally
        {
            pending.TrySetResult(new(IPStatus.TimedOut));
            await WaitUntilAsync(() => source.ActiveCalls == 0, "the owned fake operation to finish");
        }
    }

    private static async Task CancellationCallbackCleanup()
    {
        var source = new CallbackGatedPingSource();
        using var monitor = new IcmpMonitor(source, timeout: TimeSpan.FromMinutes(5));
        try
        {
            monitor.Start();
            await source.ProbeEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var elapsed = Stopwatch.StartNew();
            monitor.Stop();
            monitor.Stop();
            Check(elapsed.Elapsed < TimeSpan.FromSeconds(1), "Stop must return without synchronously joining a held cancellation callback.");
            await source.CallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Check(!source.SourceReleased.Task.IsCompleted, "The gated callback must still own the pending source operation.");
            source.ReleaseCallback();
            await source.SourceReleased.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(1));
            Equal(1, source.CancellationCallbacks, "source cancellation callback delivery");
            Check(source.CallerTokenWasCancelled, "The source must receive cancellation through its supplied token.");
            Check(monitor.LatestResult is null, "Shutdown must not publish an incomplete burst as an ICMP measurement.");
        }
        finally
        {
            source.ReleaseCallback();
            source.CancelOwnedTask();
        }
    }

    private static async Task NoOverlappingCycles()
    {
        using var permits = new SemaphoreSlim(0);
        var source = new TrackingPingSource(async (_, token) =>
        {
            await permits.WaitAsync(token);
            return Success(7);
        });
        using var monitor = new IcmpMonitor(source, interval: TimeSpan.FromMilliseconds(20), timeout: TimeSpan.FromSeconds(3));
        monitor.Start();
        await WaitUntilAsync(() => source.Calls == 1, "the first gated ping");
        for (var i = 0; i < 25; i++)
            monitor.Start();
        await Task.Delay(100); // Several intervals elapse while the first attempt is still pending.
        Equal(1, source.Calls, "repeated starts and cadence ticks must not launch another worker");
        permits.Release(4);
        AssertResult(await ResultAsync(monitor), 4, 0, 7, 0);
        await StopAsync(monitor);
        Equal(1, source.MaximumConcurrent, "all attempts and cycles remain serialized");
        // Worker shutdown is prompt; cooperative source cancellation unwinds asynchronously.
        await WaitUntilAsync(() => source.ActiveCalls == 0, "next-cycle source cancellation to finish");
        Equal(0, source.ActiveCalls, "shutdown releases any next-cycle ping");
    }

    private static async Task SuspendAndResume()
    {
        var blocked = NewSignal();
        var source = new TrackingPingSource(async (call, token) =>
        {
            if (call == 3)
            {
                blocked.TrySetResult();
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
            }
            return Success(call <= 3 ? 5 : 40);
        });
        using var monitor = new IcmpMonitor(source, interval: TimeSpan.FromMilliseconds(30), timeout: TimeSpan.FromMinutes(5));
        monitor.Start();
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
        monitor.Suspend();
        await WaitUntilAsync(() => source.ActiveCalls == 0, "the suspended ping to cancel");
        var countWhileSuspended = source.Calls;
        await Task.Delay(100);
        Equal(countWhileSuspended, source.Calls, "suspension pauses the background worker");
        Check(monitor.LatestResult is null, "suspension must discard the two successful partial replies");
        monitor.Resume();
        AssertResult(await ResultAsync(monitor), 4, 0, 40, 0);
        await StopAsync(monitor);
        Equal(1, source.MaximumConcurrent, "resume does not race an old outstanding ping");
    }

    private static async Task PreserveCompletedResult()
    {
        var nextCycleBlocked = NewSignal();
        var source = new TrackingPingSource(async (call, token) =>
        {
            if (call == 7)
            {
                nextCycleBlocked.TrySetResult();
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
            }
            return Success(call <= 4 ? 10 : 80);
        });
        using var monitor = new IcmpMonitor(source, interval: TimeSpan.FromMilliseconds(30), timeout: TimeSpan.FromMinutes(5));
        monitor.Start();
        var completed = await ResultAsync(monitor);
        AssertResult(completed, 4, 0, 10, 0);
        await nextCycleBlocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(ReferenceEquals(completed, monitor.LatestResult), "A partial next cycle must not replace the completed measurement.");
        monitor.Suspend();
        await WaitUntilAsync(() => source.ActiveCalls == 0, "partial cycle cancellation");
        Check(ReferenceEquals(completed, monitor.LatestResult), "Cancelling a later partial cycle must preserve the last completed result.");
        await StopAsync(monitor);
    }

    private static async Task BackgroundCycles()
    {
        var source = new ScriptedPingSource(_ => Success(6));
        using var monitor = new IcmpMonitor(source, interval: TimeSpan.FromMilliseconds(25));
        monitor.Start();
        var first = await ResultAsync(monitor);
        await WaitUntilAsync(() => source.Calls >= 8 && !ReferenceEquals(first, monitor.LatestResult), "two dashboard-independent completed cycles");
        await StopAsync(monitor);
        Check(source.Calls >= 8, "Background operation must keep running after the first cycle.");
        AssertResult(monitor.LatestResult!, 4, 0, 6, 0);
    }

    private static async Task IdempotentCleanup()
    {
        var source = new ScriptedPingSource(_ => Success(1));
        var monitor = new IcmpMonitor(source);
        monitor.Stop();
        monitor.Stop();
        monitor.Dispose();
        monitor.Dispose();
        await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        Equal(0, source.Calls, "cleanup alone must never start an ICMP probe");
    }

    private static IcmpPingReply Success(long latency) => new(IPStatus.Success, latency);

    private static async Task<IcmpProbeResult> ResultAsync(IcmpMonitor monitor)
    {
        await WaitUntilAsync(() => monitor.LatestResult is not null, "a completed ICMP result");
        return monitor.LatestResult!;
    }

    private static async Task StopAsync(IcmpMonitor monitor)
    {
        monitor.Stop();
        await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        Check(!monitor.Completion.IsFaulted, "The probe worker must stop without an unhandled exception.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(3))
                throw new InvalidOperationException($"Timed out waiting for {description}.");
            await Task.Delay(5);
        }
    }

    private static void AssertResult(IcmpProbeResult result, int successes, int failures, double? latency, double loss)
    {
        Equal(successes, result.SuccessfulReplies, "successful reply count");
        Equal(failures, result.FailedReplies, "failed reply count");
        Equal(4, result.SuccessfulReplies + result.FailedReplies, "total attempts in completed cycle");
        Equal(latency, result.AverageLatencyMs, "successful-reply average latency");
        Equal(loss, result.PacketLossPercent, "packet loss percentage");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string description)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Unexpected {description}: expected {expected}, actual {actual}.");
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ScriptedPingSource(Func<int, IcmpPingReply> reply) : IPingSource
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task<IcmpPingReply> SendAsync(IPAddress target, TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(reply(Interlocked.Increment(ref _calls)));
        }
    }

    private sealed class TrackingPingSource(Func<int, CancellationToken, Task<IcmpPingReply>> reply) : IPingSource
    {
        private int _calls;
        private int _activeCalls;
        private int _maximumConcurrent;
        public int Calls => Volatile.Read(ref _calls);
        public int ActiveCalls => Volatile.Read(ref _activeCalls);
        public int MaximumConcurrent => Volatile.Read(ref _maximumConcurrent);

        public async Task<IcmpPingReply> SendAsync(IPAddress target, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            var active = Interlocked.Increment(ref _activeCalls);
            while (true)
            {
                var previous = Volatile.Read(ref _maximumConcurrent);
                if (active <= previous || Interlocked.CompareExchange(ref _maximumConcurrent, active, previous) == previous)
                    break;
            }
            try
            {
                return await reply(call, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }
    }

    private sealed class CapturingPingSource : IPingSource
    {
        public IPAddress? LastTarget { get; private set; }
        public TimeSpan LastTimeout { get; private set; }

        public Task<IcmpPingReply> SendAsync(IPAddress target, TimeSpan timeout, CancellationToken cancellationToken)
        {
            LastTarget = target;
            LastTimeout = timeout;
            return Task.FromResult(Success(8));
        }
    }

    private sealed class CallbackGatedPingSource : IPingSource
    {
        private readonly TaskCompletionSource _releaseCallback = NewSignal();
        private readonly TaskCompletionSource<IcmpPingReply> _reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callbacks;
        public TaskCompletionSource ProbeEntered { get; } = NewSignal();
        public TaskCompletionSource CallbackEntered { get; } = NewSignal();
        public TaskCompletionSource SourceReleased { get; } = NewSignal();
        public int CancellationCallbacks => Volatile.Read(ref _callbacks);
        public bool CallerTokenWasCancelled { get; private set; }
        public void ReleaseCallback() => _releaseCallback.TrySetResult();
        public void CancelOwnedTask() => _reply.TrySetCanceled();

        public async Task<IcmpPingReply> SendAsync(IPAddress target, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var registration = cancellationToken.Register(() =>
            {
                Interlocked.Increment(ref _callbacks);
                CallbackEntered.TrySetResult();
                _releaseCallback.Task.GetAwaiter().GetResult();
                CallerTokenWasCancelled = cancellationToken.IsCancellationRequested;
                _reply.TrySetCanceled(cancellationToken);
            });
            ProbeEntered.TrySetResult();
            try { return await _reply.Task.ConfigureAwait(false); }
            finally
            {
                registration.Dispose();
                SourceReleased.TrySetResult();
            }
        }
    }

    private sealed class FixedClock(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }
}
