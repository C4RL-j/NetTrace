using System.Diagnostics;
using System.Net.NetworkInformation;

namespace NetTrace.Services;

// Independent of the bandwidth timer. The store records transitions; no power callbacks or UI.
public sealed class VpnSessionMonitor : IDisposable
{
    private readonly ConnectionSessionTracker _physical;
    private readonly VpnSessionStore _store;
    private readonly VpnRouteVerifier _verifier;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private readonly object _worker = new();
    private long _generation;
    private bool _started;
    private bool _disposed;

    public VpnSessionMonitor(ConnectionSessionTracker physical, VpnSessionStore store, VpnRouteVerifier? verifier = null)
    {
        _physical = physical ?? throw new ArgumentNullException(nameof(physical));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _verifier = verifier ?? new VpnRouteVerifier();
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
            _timer.Change(200, 30_000);
        }
    }

    public void Refresh()
    {
        lock (_gate)
        {
            if (_disposed || !_started) return;
            _generation++;
            _timer.Change(200, 30_000);
        }
    }

    private void NetworkChanged(object? sender, EventArgs args) => Refresh();
    private void AvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args) => Refresh();

    private void Observe(object? state)
    {
        // Timer callback bursts do not queue a second independent observer.
        if (!Monitor.TryEnter(_worker)) return;
        try
        {
            long generation;
            lock (_gate)
            {
                if (_disposed || !_started) return;
                generation = _generation;
            }
            if (_physical.ConfirmedSession is not { } parent) return;
            var observed = DateTimeOffset.UtcNow;
            var routing = _verifier.GetCurrentState();
            lock (_gate)
            {
                if (_disposed || generation != _generation ||
                    _physical.ConfirmedSession?.Id != parent.Id) return;
                // Reuse the existing physical flush and absolute byte totals.
                // Never advance parent liveness based solely on VPN evidence.
                _physical.Flush();
                var current = _physical.ConfirmedSession;
                if (current is null || current.Id != parent.Id) return;
                var confirmed = observed < current.LastConfirmedAtUtc ? observed : current.LastConfirmedAtUtc;
                _store.Observe(parent.Id, routing, confirmed);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError($"VPN history could not be saved: {exception.Message}");
        }
        finally { Monitor.Exit(_worker); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            NetworkChange.NetworkAddressChanged -= NetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= AvailabilityChanged;
            _timer.Dispose();
        }
        // Finish any writer before the application releases its database lease.
        lock (_worker) { }
    }
}
