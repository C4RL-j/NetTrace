using NetTrace.Models;

namespace NetTrace.Services;

// Network events and user edits drive recognition; bandwidth sampling has its own timer.
public sealed class ConnectionProfileMonitor : IDisposable
{
    private readonly INetworkIdentityService _identityService;
    private readonly ProfileResolver _resolver;
    private readonly Timer _refreshTimer;
    private readonly object _gate = new();
    private readonly object _workerGate = new();
    private bool _started;
    private bool _disposed;
    private CurrentConnectionState _current = new(ConnectionStatus.Detecting, null, null,
        "Detecting physical connection…");

    public event EventHandler? StateChanged;

    public CurrentConnectionState Current
    {
        get { lock (_gate) return _current; }
    }

    public ConnectionProfileMonitor(INetworkIdentityService identityService, ProfileResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(identityService);
        ArgumentNullException.ThrowIfNull(resolver);
        _identityService = identityService;
        _resolver = resolver;
        _refreshTimer = new Timer(RefreshState, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _started = true;
            _identityService.NetworkChanged += IdentityService_OnNetworkChanged;
        }
        Refresh();
    }

    public void Refresh()
    {
        lock (_gate)
        {
            if (!_disposed && _started)
                _refreshTimer.Change(200, Timeout.Infinite);
        }
    }

    private void IdentityService_OnNetworkChanged(object? sender, EventArgs e) => Refresh();

    private void RefreshState(object? state)
    {
        lock (_workerGate)
        {
            lock (_gate) { if (_disposed) return; }
            CurrentConnectionState next;
            try
            {
                var detection = _identityService.GetCurrentNetwork();
                next = ResolveState(detection);
            }
            catch (Exception exception)
            {
                // Recognition failure must not stop the independent usage sampler.
                next = new(ConnectionStatus.ConnectedIdentityUnavailable, null, null,
                    $"Windows could not provide the network identity: {exception.Message}");
            }

            lock (_gate)
            {
                if (_disposed || next == _current) return;
                _current = next;
            }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private CurrentConnectionState ResolveState(NetworkIdentityResult detection)
    {
        if (detection.Availability == NetworkIdentityAvailability.Disconnected)
            return new(ConnectionStatus.Disconnected, null, null,
                detection.Message ?? "No active physical Wi-Fi or Ethernet connection.");

        // Failed native queries must never expose a stale, fabricated or empty mapping key.
        if (detection.Availability != NetworkIdentityAvailability.Available ||
            detection.Network is not { HasValidIdentifier: true } network)
            return new(ConnectionStatus.ConnectedIdentityUnavailable, null, null,
                detection.Message ?? "Windows could not provide the network identity. Profile assignment is unavailable.");

        var profile = _resolver.Resolve(network);
        return new(profile is null ? ConnectionStatus.ConnectedUnassigned : ConnectionStatus.ConnectedRecognized,
            network, profile, detection.Message);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _identityService.NetworkChanged -= IdentityService_OnNetworkChanged;
            _refreshTimer.Dispose();
        }
        lock (_workerGate) _identityService.Dispose();
    }
}
