using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using NetTrace.Models;

namespace NetTrace.Services;

// One application-owned worker, independent from the dashboard and ICMP worker.
// Only the latest completed result is retained; there is no reachability history.
public sealed class InternetReachabilityMonitor : IDisposable
{
    public static TimeSpan DefaultInterval => TimeSpan.FromSeconds(60);
    public static TimeSpan DefaultTimeout => TimeSpan.FromSeconds(4);

    // Small unauthenticated connectivity endpoints from independent providers.
    public static IReadOnlyList<string> DefaultDnsHostnames { get; } =
        Array.AsReadOnly(new[] { "cp.cloudflare.com", "www.gstatic.com" });
    public static IReadOnlyList<Uri> DefaultHttpsEndpoints { get; } =
        Array.AsReadOnly(new[]
        {
            new Uri("https://cp.cloudflare.com/generate_204"),
            new Uri("https://www.gstatic.com/generate_204")
        });

    private readonly object _gate = new();
    private readonly IInternetReachabilityProbeSource _probeSource;
    private readonly bool _ownsProbeSource;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _stopCancellation = new();
    private readonly SemaphoreSlim _resumeSignal = new(0, 1);
    private CancellationTokenSource? _cycleCancellation;
    private Task _completion = Task.CompletedTask;
    private InternetReachabilityResult? _latestResult;
    private bool _started;
    private bool _suspended;
    private bool _stopped;
    private bool _disposed;
    private long _generation;

    public TimeSpan Interval { get; }
    public TimeSpan Timeout { get; }
    public IReadOnlyList<string> DnsHostnames { get; }
    public IReadOnlyList<Uri> HttpsEndpoints { get; }
    public InternetReachabilityResult? LatestResult { get { lock (_gate) return _latestResult; } }
    public Task Completion { get { lock (_gate) return _completion; } }

    public InternetReachabilityMonitor(IInternetReachabilityProbeSource? probeSource = null,
        TimeSpan? interval = null, TimeSpan? timeout = null, TimeProvider? timeProvider = null,
        IReadOnlyList<string>? dnsHostnames = null, IReadOnlyList<Uri>? httpsEndpoints = null)
    {
        Interval = interval ?? DefaultInterval;
        Timeout = timeout ?? DefaultTimeout;
        if (Interval <= TimeSpan.Zero || Interval.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(interval));
        if (Timeout <= TimeSpan.Zero || Timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var hostnames = (dnsHostnames ?? DefaultDnsHostnames).ToArray();
        var endpoints = (httpsEndpoints ?? DefaultHttpsEndpoints).ToArray();
        if (hostnames.Length == 0 || hostnames.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one DNS hostname is required.", nameof(dnsHostnames));
        if (endpoints.Length == 0 || endpoints.Any(endpoint => endpoint is null || !endpoint.IsAbsoluteUri ||
            endpoint.Scheme != Uri.UriSchemeHttps || endpoint.UserInfo.Length != 0))
            throw new ArgumentException("At least one HTTPS endpoint without credentials is required.", nameof(httpsEndpoints));
        DnsHostnames = Array.AsReadOnly(hostnames);
        HttpsEndpoints = Array.AsReadOnly(endpoints);
        _probeSource = probeSource ?? new SystemInternetReachabilityProbeSource();
        _ownsProbeSource = probeSource is null;
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

    // Shutdown requests cancellation immediately without blocking on networking.
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
        lock (_gate)
        {
            if (!_started)
            {
                DisposeOwnedProbeSource();
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
                    await CompleteCycleAsync(cancellation.Token, stopToken, generation).ConfigureAwait(false);
                    // Start-to-start cadence; an unusually slow cycle never overlaps.
                    var delay = Interval - _timeProvider.GetElapsedTime(cycleStarted);
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay, _timeProvider, cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
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
            Trace.TraceWarning($"Internet reachability monitoring stopped unexpectedly: {exception.Message}");
        }
        finally
        {
            lock (_gate) _stopped = true;
            DisposeOwnedProbeSource();
            _stopCancellation.Dispose();
            _resumeSignal.Dispose();
        }
    }

    private async Task CompleteCycleAsync(CancellationToken cycleToken, CancellationToken stopToken, long generation)
    {
        using var deadline = new CancellationTokenSource(Timeout, _timeProvider);
        using var probesCancellation = CancellationTokenSource.CreateLinkedTokenSource(cycleToken, deadline.Token);
        var dns = DnsHostnames.Select(hostname => RunProbeAsync(
            () => _probeSource.ResolveDnsAsync(hostname, probesCancellation.Token), probesCancellation.Token)).ToArray();
        var https = HttpsEndpoints.Select(endpoint => RunProbeAsync(
            () => _probeSource.ProbeHttpsAsync(endpoint, probesCancellation.Token), probesCancellation.Token)).ToArray();
        var pending = Task.WhenAll(dns.Concat(https));
        try
        {
            try { await pending.WaitAsync(Timeout, _timeProvider, cycleToken).ConfigureAwait(false); }
            catch (TimeoutException) { /* The overall deadline preserves any already completed success. */ }
            cycleToken.ThrowIfCancellationRequested();
            var dnsSucceeded = dns.Any(task => task.IsCompletedSuccessfully && task.Result);
            var httpsSucceeded = https.Any(task => task.IsCompletedSuccessfully && task.Result);
            var status = httpsSucceeded ? InternetReachabilityStatus.Available :
                dnsSucceeded ? InternetReachabilityStatus.Limited : InternetReachabilityStatus.Unavailable;
            var result = new InternetReachabilityResult(status, _timeProvider.GetUtcNow(), dnsSucceeded, httpsSucceeded);
            lock (_gate)
            {
                // Sleep/shutdown cannot publish incomplete work from an old generation.
                if (!_stopped && !_suspended && generation == _generation) _latestResult = result;
            }
        }
        finally
        {
            await CancelAndObserveAsync(probesCancellation).ConfigureAwait(false);
            // Observe faults even after shutdown abandons a broken injectable source.
            _ = pending.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (!pending.IsCompleted)
            {
                // Cancellation-aware system APIs finish promptly. A noncooperative
                // source must drain before another cycle, while shutdown may abandon it.
                try { await pending.WaitAsync(stopToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stopToken.IsCancellationRequested) { throw; }
                catch (Exception) { /* Individual probe errors were already translated. */ }
            }
        }
    }

    private static async Task<bool> RunProbeAsync(Func<Task<bool>> probe, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var succeeded = await probe().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return succeeded;
        }
        catch (OperationCanceledException) { return false; }
        catch (HttpRequestException) { return false; }
        catch (SocketException) { return false; }
        catch (IOException) { return false; }
        catch (AuthenticationException) { return false; }
        catch (TimeoutException) { return false; }
        catch (Exception exception)
        {
            Trace.TraceWarning($"An internet reachability probe failed unexpectedly: {exception.Message}");
            return false;
        }
    }

    private void DisposeOwnedProbeSource()
    {
        if (_ownsProbeSource && _probeSource is IDisposable disposable) disposable.Dispose();
    }

    private static void RequestCancellation(CancellationTokenSource? cancellation)
    {
        if (cancellation is not null) _ = CancelAndObserveAsync(cancellation);
    }

    private static async Task CancelAndObserveAsync(CancellationTokenSource cancellation)
    {
        try { await cancellation.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Could not cancel an internet reachability probe: {exception.Message}");
        }
    }
}
