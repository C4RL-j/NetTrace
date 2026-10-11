using System.Net.NetworkInformation;
using NetTrace.Models;

namespace NetTrace.Services;

// Visible Connections requests use its existing view cadence. One bounded local
// read runs off the UI thread; no timer, observer subscription or persistence.
public sealed class WifiRadioService : IDisposable
{
    private readonly object _gate = new();
    private readonly IWifiRadioSource _source;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _refreshInterval;
    private long _generation;
    private string? _adapterId;
    private DateTimeOffset? _lastQuery;
    private WifiRadioSnapshot? _current;
    private bool _inFlight;
    private bool _disposed;

    public WifiRadioService(IWifiRadioSource? source = null, TimeProvider? timeProvider = null,
        TimeSpan? refreshInterval = null)
    {
        _source = source ?? new WindowsWifiRadioSource();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _refreshInterval = refreshInterval ?? TimeSpan.FromSeconds(5);
        if (_refreshInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(refreshInterval));
    }

    public WifiRadioSnapshot? Current { get { lock (_gate) return _current; } }

    public WifiRadioSnapshot? RequestSnapshot(AdapterInfo? adapter, NetworkAdapterDetails? details)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            if (adapter is null || string.IsNullOrWhiteSpace(adapter.Id) ||
                !string.Equals(adapter.Type, "Wireless80211", StringComparison.OrdinalIgnoreCase) ||
                details?.Status != OperationalStatus.Up ||
                !string.Equals(adapter.Id, details.AdapterId, StringComparison.OrdinalIgnoreCase))
            {
                Clear();
                return null;
            }
            if (!string.Equals(_adapterId, adapter.Id, StringComparison.OrdinalIgnoreCase))
            {
                Clear();
                _adapterId = adapter.Id;
            }
            var now = _timeProvider.GetUtcNow();
            if (!_inFlight && (_lastQuery is null || now - _lastQuery >= _refreshInterval))
            {
                var selectedId = _adapterId!;
                var generation = _generation;
                _lastQuery = now;
                _inFlight = true;
                _ = Task.Run(() => ReadAndPublish(selectedId, generation));
            }
            return _current;
        }
    }

    public void Invalidate()
    {
        lock (_gate) Clear();
    }

    private void Clear()
    {
        _generation++;
        _adapterId = null;
        _lastQuery = null;
        _current = null;
    }

    private void ReadAndPublish(string adapterId, long generation)
    {
        WifiRadioSnapshot? result = null;
        try { result = _source.Read(adapterId); }
        catch { /* Missing or denied WLAN metadata does not interrupt monitoring. */ }
        finally
        {
            lock (_gate)
            {
                if (!_disposed && generation == _generation &&
                    string.Equals(_adapterId, adapterId, StringComparison.OrdinalIgnoreCase))
                    _current = result is not null && string.Equals(result.AdapterId, adapterId,
                        StringComparison.OrdinalIgnoreCase) ? result : null;
                _inFlight = false;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Clear();
        }
        // An already running native read frees its own handle and buffers. Its
        // invalidated result cannot publish or render after this view closes.
    }
}
