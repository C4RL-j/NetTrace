using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using NetTrace.Models;

namespace NetTrace.Services;

// One application-owned worker, independent of the dashboard and bandwidth sampler.
public sealed class IcmpMonitor : IDisposable
{
    public const int AttemptsPerCycle = 4;
    public static TimeSpan DefaultInterval => TimeSpan.FromSeconds(15);
    public static TimeSpan DefaultTimeout => TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly IPingSource _pingSource;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _stopCancellation = new();
    private readonly SemaphoreSlim _resumeSignal = new(0, 1);
    private CancellationTokenSource? _cycleCancellation;
    private Task _completion = Task.CompletedTask;
    private IcmpProbeResult? _latestResult;
    private bool _started;
    private bool _suspended;
    private bool _stopped;
    private bool _disposed;
    private long _generation;

    public IPAddress Target { get; }
    public TimeSpan Interval { get; }
    public TimeSpan Timeout { get; }
    public IcmpProbeResult? LatestResult { get { lock (_gate) return _latestResult; } }
    public Task Completion { get { lock (_gate) return _completion; } }

    public IcmpMonitor(IPingSource? pingSource = null, IPAddress? target = null,
        TimeSpan? interval = null, TimeSpan? timeout = null, TimeProvider? timeProvider = null)
    {
        Target = target ?? IPAddress.Parse("1.1.1.1");
        Interval = interval ?? DefaultInterval;
        Timeout = timeout ?? DefaultTimeout;
        if (Interval <= TimeSpan.Zero || Interval.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(interval));
        if (Timeout <= TimeSpan.Zero || Timeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        _pingSource = pingSource ?? new WindowsPingSource();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed || _stopped, this);
            if (_started) return;
            _started = true;
            _completion = Task.Run(RunAsync);
        }
    }

    public void Suspend()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (_disposed || _stopped || _suspended) return;
            _suspended = true;
            _generation++;
            cancellation = _cycleCancellation;
        }
        RequestCancellation(cancellation);
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_disposed || _stopped || !_suspended) return;
            _suspended = false;
            if (_resumeSignal.CurrentCount == 0) _resumeSignal.Release();
        }
    }

    // Never synchronously wait for a ping or worker during application shutdown.
    public void Stop()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            _generation++;
        }
        RequestCancellation(_stopCancellation);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
        // The worker owns cancellation resources until its asynchronous cleanup completes.
        lock (_gate)
        {
            if (!_started)
            {
                _stopCancellation.Dispose();
                _resumeSignal.Dispose();
            }
        }
    }

    private async Task RunAsync()
    {
        var stopToken = _stopCancellation.Token;
        try
        {
            while (!stopToken.IsCancellationRequested)
            {
                CancellationTokenSource? cancellation;
                long generation;
                lock (_gate)
                {
                    if (_stopped) break;
                    generation = _generation;
                    cancellation = _suspended ? null : CancellationTokenSource.CreateLinkedTokenSource(stopToken);
                    _cycleCancellation = cancellation;
                }

                if (cancellation is null)
                {
                    await _resumeSignal.WaitAsync(stopToken).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    var cycleStarted = _timeProvider.GetTimestamp();
                    var result = await ProbeAsync(cancellation.Token, stopToken).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (!_stopped && !_suspended && generation == _generation)
                            _latestResult = result;
                    }

                    // Cadence is measured between starts. A slow cycle is never overlapped.
                    var delay = Interval - _timeProvider.GetElapsedTime(cycleStarted);
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay, _timeProvider, cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    // Sleep discards unfinished cycles; resume starts a fresh burst.
                }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_cycleCancellation, cancellation)) _cycleCancellation = null;
                    }
                    cancellation.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            // An unexpected worker failure must not bring down bandwidth/session monitoring.
            Trace.TraceWarning($"ICMP monitoring stopped: {exception.Message}");
        }
        finally
        {
            lock (_gate) _stopped = true;
            _stopCancellation.Dispose();
            _resumeSignal.Dispose();
        }
    }

    private async Task<IcmpProbeResult> ProbeAsync(CancellationToken cycleToken, CancellationToken stopToken)
    {
        var successful = 0;
        double latencyTotal = 0;
        for (var attempt = 0; attempt < AttemptsPerCycle; attempt++)
        {
            cycleToken.ThrowIfCancellationRequested();
            var reply = await SendAttemptAsync(cycleToken, stopToken).ConfigureAwait(false);
            cycleToken.ThrowIfCancellationRequested();
            if (reply is { Status: IPStatus.Success, RoundtripTimeMs: >= 0 })
            {
                successful++;
                latencyTotal += reply.RoundtripTimeMs;
            }
        }

        cycleToken.ThrowIfCancellationRequested();
        var failed = AttemptsPerCycle - successful;
        return new IcmpProbeResult(_timeProvider.GetUtcNow(), Target.ToString(), successful, failed,
            successful == 0 ? null : latencyTotal / successful, failed * 100.0 / AttemptsPerCycle);
    }

    private async Task<IcmpPingReply?> SendAttemptAsync(CancellationToken cycleToken,
        CancellationToken stopToken)
    {
        using var deadline = new CancellationTokenSource(Timeout, _timeProvider);
        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cycleToken, deadline.Token);
        Task<IcmpPingReply>? pending = null;
        try
        {
            pending = _pingSource.SendAsync(Target, Timeout, attemptCancellation.Token);
            return await pending.WaitAsync(Timeout, _timeProvider, cycleToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cycleToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            // ICMP timeout, blocked ping, API errors, and canceled deadline are failed replies.
            Trace.TraceInformation($"ICMP probe to {Target} did not return a reply: {exception.Message}");
            return null;
        }
        finally
        {
            RequestCancellation(attemptCancellation);
            if (pending is not null)
            {
                // Observe faults even if shutdown abandons a non-cooperative source, or a
                // timeout races with source completion before the drain check below.
                _ = pending.ContinueWith(static task => { _ = task.Exception; },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                // A broken injectable source must not cause concurrent underlying probes.
                // Shutdown can abandon this drain immediately; production Ping honors cancellation.
                if (!pending.IsCompleted)
                {
                    try { await pending.WaitAsync(stopToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (stopToken.IsCancellationRequested) { throw; }
                    catch (Exception) { /* The reply/error has already been counted or canceled. */ }
                }
            }
        }
    }

    private static void RequestCancellation(CancellationTokenSource? cancellation)
    {
        if (cancellation is not null) _ = CancelAndObserveAsync(cancellation);
    }

    private static async Task CancelAndObserveAsync(CancellationTokenSource cancellation)
    {
        try { await cancellation.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        catch (Exception exception) { Trace.TraceWarning($"Could not cancel an ICMP probe: {exception.Message}"); }
    }
}
