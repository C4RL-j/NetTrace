using System.Diagnostics;
using System.Net.NetworkInformation;
using NetTrace.Models;

namespace NetTrace.Services;

// Independent of the bandwidth timer. The existing application lifecycle pauses/resumes
// this observer; it does not register its own power callbacks or create physical sessions.
public sealed class VpnSessionMonitor : IDisposable
{
    private readonly ConnectionSessionTracker _physical;
    private readonly VpnSessionStore _store;
    private readonly VpnRouteVerifier _verifier;
    private readonly TimeProvider _timeProvider;
    private readonly bool _observeRoutesWithoutPhysicalSession;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private readonly object _worker = new();
    private long _generation;
    private bool _started;
    private bool _suspended;
    private bool _disposed;
    private volatile VpnRouteSnapshot _latestRouteSnapshot = VpnRouteSnapshot.Unknown;

    public event EventHandler? Changed;
    // Immutable cache reads must not wait for the observer's SQLite history writes.
    public VpnRouteSnapshot LatestRouteSnapshot => _latestRouteSnapshot;

    public VpnSessionMonitor(ConnectionSessionTracker physical, VpnSessionStore store, VpnRouteVerifier? verifier = null,
        TimeProvider? timeProvider = null, bool observeRoutesWithoutPhysicalSession = false)
    {
        _physical = physical ?? throw new ArgumentNullException(nameof(physical));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _verifier = verifier ?? new VpnRouteVerifier();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _observeRoutesWithoutPhysicalSession = observeRoutesWithoutPhysicalSession;
        _timer = new Timer(Observe, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _started = true;
            NetworkChange.NetworkAddressChanged += NetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += AvailabilityChanged;
            if (!_suspended) _timer.Change(200, 30_000);
        }
    }

    public void Refresh()
    {
        lock (_gate)
        {
            if (_disposed || !_started || _suspended) return;
            _generation++;
            _timer.Change(200, 30_000);
        }
    }

    public void Suspend()
    {
        lock (_gate)
        {
            if (_disposed || _suspended) return;
            _suspended = true;
            _generation++;
            _latestRouteSnapshot = VpnRouteSnapshot.Unknown;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        // Any admitted write finishes before this method returns. Native queries can finish
        // asynchronously, but their generation can no longer write across the sleep boundary.
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_disposed || !_suspended) return;
            _suspended = false;
            _generation++;
            _latestRouteSnapshot = VpnRouteSnapshot.Unknown;
            if (_started) _timer.Change(200, 30_000);
        }
        // ConfirmedSession stays absent until fresh physical recognition establishes a parent.
    }

    private void NetworkChanged(object? sender, EventArgs args) => Refresh();
    private void AvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args) => Refresh();

    private void Observe(object? state)
    {
        // Timer callback bursts do not queue a second independent observer.
        if (!Monitor.TryEnter(_worker)) return;
        long generation = -1;
        var changed = false;
        try
        {
            lock (_gate)
            {
                if (_disposed || !_started || _suspended) return;
                generation = _generation;
            }
            var parent = _physical.ConfirmedSession;
            if (parent is null && !_observeRoutesWithoutPhysicalSession) return;
            var observed = _timeProvider.GetUtcNow().ToUniversalTime();
            var observation = _verifier.GetCurrentObservation(observed);
            lock (_gate)
            {
                if (_disposed || _suspended || generation != _generation ||
                    _physical.ConfirmedSession?.Id != parent?.Id) return;
                // Route context is independent of physical identity permissions and history writes.
                _latestRouteSnapshot = observation.Snapshot;
                changed = true;
                if (parent is null) return;
                // Reuse the existing physical flush and absolute byte totals.
                // Never advance parent liveness based solely on VPN evidence.
                _physical.Flush();
                var current = _physical.ConfirmedSession;
                if (current is null || current.Id != parent.Id) return;
                var confirmed = observed < current.LastConfirmedAtUtc ? observed : current.LastConfirmedAtUtc;
                _store.Observe(parent.Id, observation.Routing, confirmed);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError($"VPN history could not be saved: {exception.Message}");
        }
        finally
        {
            lock (_gate)
            {
                // A refresh may have fired while a pre-sleep query still occupied the worker.
                // Obtain fresh evidence promptly after that stale query completes.
                if (!_disposed && _started && !_suspended && generation != _generation)
                    _timer.Change(200, 30_000);
            }
            Monitor.Exit(_worker);
            // Consumers may read MonitoringService; never invoke them while holding observer locks.
            if (changed) RaiseChanged();
        }
    }

    private void RaiseChanged()
    {
        if (Changed is not { } changed) return;
        foreach (EventHandler handler in changed.GetInvocationList())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception exception) { Trace.TraceWarning($"Could not publish local route context: {exception.Message}"); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _latestRouteSnapshot = VpnRouteSnapshot.Unknown;
            NetworkChange.NetworkAddressChanged -= NetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= AvailabilityChanged;
            _timer.Dispose();
        }
        // Finish any writer before the application releases its database lease.
        lock (_worker) { }
    }
}
