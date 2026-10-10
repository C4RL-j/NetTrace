using System.Diagnostics;
using NetTrace.Models;

namespace NetTrace.Services;

// Application lifetime owner. No dashboard, dispatcher, rendering or power window is required
// to sample, persist, resolve profiles, or maintain physical/VPN history.
public sealed class MonitoringService : IDisposable
{
    private readonly object _gate = new();
    private readonly object _stopGate = new();
    private readonly INetworkMonitor _monitor;
    private readonly UsageStore _store;
    private readonly ProfileStore _profiles;
    private readonly ConnectionHistoryStore _history;
    private readonly ConnectionSessionTracker _sessions;
    private readonly ConnectionProfileMonitor _profileMonitor;
    private readonly VpnSessionMonitor _vpnSessions;
    private readonly IcmpMonitor _icmpMonitor;
    private readonly InternetReachabilityMonitor _reachabilityMonitor;
    private readonly DatabaseRunLease _runLease;
    private readonly TimeProvider _timeProvider;
    private readonly Timer _timer;
    private bool _started;
    private bool _stopping;
    private bool _disposed;
    private bool _suspended;
    private bool _sessionPersistenceFailed;
    private bool _persistNextObservation;
    private string? _usagePersistenceMessage;
    private string? _trafficPeaksPersistenceMessage;
    private string? _historyPersistenceMessage;
    private long _lastHandledObservationSequence;
    private long _sampleSequence;
    private NetworkIdentity? _lastPhysicalNetwork;
    private string? _lastDetectionMessage;
    private ConnectionStatus? _lastConnectionStatus;
    private IReadOnlyList<AdapterInfo> _adapters = Array.Empty<AdapterInfo>();
    private AdapterInfo? _selectedAdapter;
    private NetworkSnapshot? _previousSnapshot;
    private long _sessionDownloaded;
    private long _sessionUploaded;
    private long _pendingDownloaded;
    private long _pendingUploaded;
    private DateTimeOffset _lastFlush;
    private DateOnly _pendingDay;
    private string _status = "Monitoring";
    private double _downloadRate;
    private double _uploadRate;
    private readonly Dictionary<DateOnly, (double DownloadBytesPerSecond, double UploadBytesPerSecond)> _trafficPeaks = new();
    private readonly HashSet<DateOnly> _dirtyTrafficPeakDays = new();

    public event EventHandler? Changed;
    public UsageStore Usage => _store;
    public ProfileStore Profiles => _profiles;
    public ConnectionHistoryStore History => _history;
    public string DatabasePath => _store.DatabasePath;
    public IcmpProbeResult? LatestIcmpMeasurement => _icmpMonitor.LatestResult;
    public IReadOnlyList<IcmpProbeResult> RollingIcmpSamples => _icmpMonitor.RollingSamples;
    public InternetReachabilityResult? LatestInternetReachability => _reachabilityMonitor.LatestResult;

    public static MonitoringService CreateForDataRoot(string localApplicationDataDirectory,
        INetworkIdentityService? identityService = null, VpnRouteVerifier? vpnVerifier = null,
        INetworkMonitor? networkMonitor = null, TimeProvider? timeProvider = null,
        IcmpMonitor? icmpMonitor = null, InternetReachabilityMonitor? reachabilityMonitor = null)
    {
        DatabaseRunLease? lease = null;
        var transferred = false;
        try
        {
            lease = new DatabaseRunLease(UsageStore.GetDatabasePath(localApplicationDataDirectory));
            var store = new UsageStore(localApplicationDataDirectory, lease);
            transferred = true;
            return new MonitoringService(store, identityService ?? new NetworkIdentityService(),
                lease, vpnVerifier, networkMonitor, timeProvider, icmpMonitor, reachabilityMonitor);
        }
        catch
        {
            try
            {
                if (!transferred)
                {
                    try
                    {
                        try { icmpMonitor?.Dispose(); }
                        finally { reachabilityMonitor?.Dispose(); }
                    }
                    finally { identityService?.Dispose(); }
                }
            }
            catch (Exception exception) { Trace.TraceWarning($"Could not clean up network identity detection after startup failure: {exception.Message}"); }
            finally { lease?.Dispose(); }
            throw;
        }
    }

    public MonitoringService(UsageStore store, INetworkIdentityService identityService,
        DatabaseRunLease? runLease = null, VpnRouteVerifier? vpnVerifier = null,
        INetworkMonitor? networkMonitor = null, TimeProvider? timeProvider = null,
        IcmpMonitor? icmpMonitor = null, InternetReachabilityMonitor? reachabilityMonitor = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(identityService);
        _store = store;
        _monitor = networkMonitor ?? new NetworkMonitor();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastFlush = _timeProvider.GetUtcNow();
        _pendingDay = DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
        try
        {
            _runLease = runLease ?? new DatabaseRunLease(store.DatabasePath);
            _runLease.EnsureOwns(store.DatabasePath);
            _icmpMonitor = icmpMonitor ?? new IcmpMonitor(timeProvider: _timeProvider);
            _reachabilityMonitor = reachabilityMonitor ?? new InternetReachabilityMonitor(timeProvider: _timeProvider);
            _trafficPeaks[_pendingDay] = store.GetTrafficPeaks(_pendingDay);
            _history = new ConnectionHistoryStore(store);
            _sessions = new ConnectionSessionTracker(_history, _timeProvider);
            _profiles = new ProfileStore(store);
            _profileMonitor = new ConnectionProfileMonitor(identityService, new ProfileResolver(_profiles), _timeProvider);
            _vpnSessions = new VpnSessionMonitor(_sessions, new VpnSessionStore(store), vpnVerifier, _timeProvider);
            _timer = new Timer(_ => SampleNow(), null, Timeout.Infinite, Timeout.Infinite);
            RefreshAdapters();
        }
        catch
        {
            try { (_icmpMonitor ?? icmpMonitor)?.Dispose(); }
            catch (Exception exception) { Trace.TraceWarning($"Could not stop ICMP monitoring after startup failure: {exception.Message}"); }
            try { (_reachabilityMonitor ?? reachabilityMonitor)?.Dispose(); }
            catch (Exception exception) { Trace.TraceWarning($"Could not stop internet reachability after startup failure: {exception.Message}"); }
            try { _timer?.Dispose(); }
            catch (Exception exception) { Trace.TraceWarning($"Could not stop sampling after startup failure: {exception.Message}"); }
            try { _vpnSessions?.Dispose(); }
            catch (Exception exception) { Trace.TraceWarning($"Could not stop VPN detection after startup failure: {exception.Message}"); }
            try
            {
                if (_profileMonitor is not null) _profileMonitor.Dispose();
                else identityService.Dispose();
            }
            catch (Exception exception) { Trace.TraceWarning($"Could not clean up network identity detection after startup failure: {exception.Message}"); }
            finally { _runLease?.Dispose(); }
            throw;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed || _stopping, this);
            if (_started) return;
            _started = true;
            _profileMonitor.ObservationReceived += ProfileMonitor_OnObservationReceived;
            _profileMonitor.Start();
            _vpnSessions.Start();
            _icmpMonitor.Start();
            _reachabilityMonitor.Start();
            if (!_suspended) _timer.Change(1000, 1000);
        }
        RaiseChanged();
    }

    public MonitoringSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            // A new local date starts at zero even while offline or asleep. This is
            // an in-memory read; the dashboard never queries SQLite for live peaks.
            var today = DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
            var peaks = _trafficPeaks.GetValueOrDefault(today);
            return new(_profileMonitor.Current, _sessions.IsSleeping ? null : _sessions.Current,
                _adapters, _selectedAdapter, _suspended, _started && !_stopping && !_disposed,
                _status, _downloadRate, _uploadRate, _sessionDownloaded, _sessionUploaded,
                _pendingDownloaded, _pendingUploaded, _pendingDay,
                _historyPersistenceMessage ?? _trafficPeaksPersistenceMessage ?? _usagePersistenceMessage)
            {
                PeakDay = today,
                PeakDownloadBytesPerSecond = peaks.DownloadBytesPerSecond,
                PeakUploadBytesPerSecond = peaks.UploadBytesPerSecond
            };
        }
    }

    // Visible dashboards request these queries. A concurrent flush cannot double-count
    // a pending batch because stored and pending totals share the sampling gate.
    public ((long Downloaded, long Uploaded) Today, (long Downloaded, long Uploaded) Week,
        (long Downloaded, long Uploaded) Month) GetUsageTotals()
    {
        lock (_gate)
        {
            var today = DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
            (long Downloaded, long Uploaded) Totals(DateOnly start)
            {
                var stored = _store.GetTotals(start, today);
                var includePending = _pendingDay >= start && _pendingDay <= today;
                return (stored.Downloaded + (includePending ? _pendingDownloaded : 0),
                    stored.Uploaded + (includePending ? _pendingUploaded : 0));
            }
            return (Totals(today), Totals(today.AddDays(-6)), Totals(today.AddDays(-29)));
        }
    }

    public void SelectAdapter(string? adapterId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed || _stopping, this);
            var selected = adapterId is null ? null : _adapters.FirstOrDefault(adapter => adapter.Id == adapterId)
                ?? throw new ArgumentException("The selected network adapter is not available.", nameof(adapterId));
            if (selected?.Id == _selectedAdapter?.Id) return;
            FlushPending();
            _selectedAdapter = selected;
            _previousSnapshot = null;
        }
        RaiseChanged();
    }

    public void RefreshRecognition() => _profileMonitor.Refresh();

    private void ProfileMonitor_OnObservationReceived(object? sender, ConnectionObservationEventArgs observation)
    {
        lock (_gate)
        {
            if (_stopping || _disposed || _suspended || observation.Sequence <= _lastHandledObservationSequence) return;
            _lastHandledObservationSequence = observation.Sequence;
            var state = observation.State;
            var previousSessionId = _sessions.Current?.Id;
            var physicalChanged = !SamePhysicalNetwork(state.Network, _lastPhysicalNetwork);
            if (physicalChanged) _previousSnapshot = null;
            UpdateSessionHistory(() =>
            {
                _sessions.ObserveConnection(state, observation.ObservedAtUtc);
                if (_persistNextObservation)
                {
                    _sessions.Flush();
                    _persistNextObservation = false;
                }
            });
            _vpnSessions.Refresh();
            if (state.Network != _lastPhysicalNetwork || state.Message != _lastDetectionMessage
                || state.Status != _lastConnectionStatus || _selectedAdapter is null)
            {
                _lastPhysicalNetwork = state.Network;
                _lastDetectionMessage = state.Message;
                _lastConnectionStatus = state.Status;
                try { RefreshAdapters(physicalChanged ? state.Network?.AdapterId : null); }
                catch (Exception exception) { ReportUsageFailure(exception); }
            }
            if (physicalChanged || previousSessionId != _sessions.Current?.Id) _previousSnapshot = null;
        }
        RaiseChanged();
    }

    private void RefreshAdapters(string? preferredAdapterId = null)
    {
        var adapters = _monitor.GetActiveAdapters();
        var selected = adapters.FirstOrDefault(adapter => string.Equals(adapter.Id, preferredAdapterId, StringComparison.OrdinalIgnoreCase))
            ?? adapters.FirstOrDefault(adapter => adapter.Id == _selectedAdapter?.Id)
            ?? adapters.FirstOrDefault();
        if (selected?.Id != _selectedAdapter?.Id)
        {
            FlushPending();
            _previousSnapshot = null;
        }
        _adapters = Array.AsReadOnly(adapters.ToArray());
        _selectedAdapter = selected;
        if (selected is null) SetOffline("No active network adapter");
    }

    public void SampleNow()
    {
        lock (_gate)
        {
            if (!_started || _stopping || _disposed || _suspended) return;
            try
            {
                if (_selectedAdapter is null)
                {
                    SetOffline("No adapter selected");
                    PersistWhenDue();
                    return;
                }
                var today = DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
                if (today != _pendingDay)
                {
                    FlushPending();
                    _pendingDay = today;
                }
                var current = _monitor.Capture(_selectedAdapter.Id);
                if (current is null)
                {
                    SetOffline("Adapter disconnected");
                    _previousSnapshot = null;
                    PersistWhenDue();
                    return;
                }
                _status = "Monitoring";
                var latest = _profileMonitor.Current;
                var pendingPhysicalChange = latest.Status != ConnectionStatus.Detecting
                    && !SamePhysicalNetwork(latest.Network, _lastPhysicalNetwork);
                var delta = BandwidthDeltaCalculator.Calculate(pendingPhysicalChange ? null : _previousSnapshot, current);
                if (delta is not null)
                {
                    _sessionDownloaded += delta.DownloadBytes;
                    _sessionUploaded += delta.UploadBytes;
                    _pendingDownloaded += delta.DownloadBytes;
                    _pendingUploaded += delta.UploadBytes;
                }
                _downloadRate = delta is null ? 0 : delta.DownloadBytes / delta.ElapsedSeconds;
                _uploadRate = delta is null ? 0 : delta.UploadBytes / delta.ElapsedSeconds;
                if (delta is not null) RecordTrafficPeaks(today, _downloadRate, _uploadRate);
                if (!pendingPhysicalChange && _sessions.Current is { } session
                    && latest.Network is { } network && network.NetworkType == session.NetworkType
                    && network.NetworkIdentifier == session.NetworkIdentifier
                    && string.Equals(network.AdapterId, session.AdapterId, StringComparison.OrdinalIgnoreCase))
                    _sessions.RecordSample(session.Id, _selectedAdapter.Id, ++_sampleSequence,
                        current.Timestamp.ToUniversalTime(), delta?.DownloadBytes ?? 0, delta?.UploadBytes ?? 0);
                _previousSnapshot = current;
                PersistWhenDue();
            }
            catch (Exception exception) { ReportUsageFailure(exception); }
        }
    }

    private void PersistWhenDue()
    {
        if (_timeProvider.GetUtcNow() - _lastFlush < TimeSpan.FromSeconds(30)) return;
        var retryObservation = _sessionPersistenceFailed;
        FlushPending();
        var retryResume = _sessions.IsSleeping && !_suspended;
        if (retryResume) UpdateSessionHistory(_sessions.Resume);
        _persistNextObservation = true;
        var cutoff = _profileMonitor.Refresh(invalidatePending: retryObservation || retryResume);
        if (retryResume) _lastHandledObservationSequence = Math.Max(_lastHandledObservationSequence, cutoff);
    }

    private void FlushPending()
    {
        Exception? usageFailure = null;
        try
        {
            if (_pendingDownloaded > 0 || _pendingUploaded > 0)
            {
                _store.AddUsage(_pendingDay, _pendingDownloaded, _pendingUploaded);
                _pendingDownloaded = 0;
                _pendingUploaded = 0;
            }
            _usagePersistenceMessage = null;
        }
        catch (Exception exception) { usageFailure = exception; }

        // These independent saves must still run if a daily-usage write fails.
        // MAX UPSERT makes a retried peak safe, while successful usage is cleared
        // immediately so another failure cannot cause that usage to be added twice.
        FlushTrafficPeaks();
        UpdateSessionHistory(_sessions.Flush);
        _lastFlush = _timeProvider.GetUtcNow();
        if (usageFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(usageFailure).Throw();
    }

    private void RecordTrafficPeaks(DateOnly day, double downloadBytesPerSecond, double uploadBytesPerSecond)
    {
        // The same sampled delta supplies live traffic, usage totals and this
        // passive peak. A baseline/reset sample can never manufacture a peak.
        var previous = _trafficPeaks.GetValueOrDefault(day);
        var download = Math.Max(previous.DownloadBytesPerSecond, downloadBytesPerSecond);
        var upload = Math.Max(previous.UploadBytesPerSecond, uploadBytesPerSecond);
        if (download == previous.DownloadBytesPerSecond && upload == previous.UploadBytesPerSecond) return;
        _trafficPeaks[day] = (download, upload);
        _dirtyTrafficPeakDays.Add(day);
    }

    private void FlushTrafficPeaks()
    {
        Exception? failure = null;
        foreach (var day in _dirtyTrafficPeakDays.Order().ToArray())
        {
            try
            {
                var peaks = _trafficPeaks[day];
                _store.SaveTrafficPeaks(day, peaks.DownloadBytesPerSecond, peaks.UploadBytesPerSecond);
                _dirtyTrafficPeakDays.Remove(day);
            }
            catch (Exception exception) { failure ??= exception; }
        }
        _trafficPeaksPersistenceMessage = failure is null ? null
            : $"Daily traffic peaks could not be saved: {failure.Message}";
        if (_trafficPeaksPersistenceMessage is not null) Trace.TraceError(_trafficPeaksPersistenceMessage);
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (_disposed || _stopping) return;
            FlushPending();
        }
        RaiseChanged();
    }

    private void FlushPendingForLifecycle()
    {
        try { FlushPending(); }
        catch (Exception exception) { ReportUsageFailure(exception); }
    }

    private void UpdateSessionHistory(Action action)
    {
        try
        {
            action();
            _sessionPersistenceFailed = false;
            _historyPersistenceMessage = null;
        }
        catch (Exception exception)
        {
            _sessionPersistenceFailed = true;
            _historyPersistenceMessage = $"Connection history could not be saved: {exception.Message}";
            Trace.TraceError(_historyPersistenceMessage);
        }
    }

    private void ReportUsageFailure(Exception exception)
    {
        _usagePersistenceMessage = $"Usage history could not be saved: {exception.Message}";
        Trace.TraceError(_usagePersistenceMessage);
    }

    public void Suspend()
    {
        lock (_gate)
        {
            if (_stopping || _disposed || _suspended) return;
            _suspended = true;
            SetOffline("Paused for sleep");
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            _previousSnapshot = null;
            _vpnSessions.Suspend();
            _icmpMonitor.Suspend();
            _reachabilityMonitor.Suspend();
            FlushPendingForLifecycle();
            UpdateSessionHistory(_sessions.Sleep);
            _profileMonitor.Refresh(invalidatePending: true);
        }
        RaiseChanged();
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_stopping || _disposed || !_suspended) return;
            _lastHandledObservationSequence = _profileMonitor.Refresh(invalidatePending: true);
            UpdateSessionHistory(_sessions.Resume);
            _previousSnapshot = null;
            _lastPhysicalNetwork = null;
            _suspended = false;
            _vpnSessions.Resume();
            _icmpMonitor.Resume();
            _reachabilityMonitor.Resume();
            if (_started) _timer.Change(1000, 1000);
        }
        RaiseChanged();
    }

    public void Stop()
    {
        lock (_stopGate)
        {
            lock (_gate)
            {
                if (_disposed) return;
                _stopping = true;
                _timer.Dispose();
                _profileMonitor.ObservationReceived -= ProfileMonitor_OnObservationReceived;
            }
            // No application lock is held while joining native recognition/VPN workers.
            try
            {
                try
                {
                    try { _reachabilityMonitor.Dispose(); }
                    finally { _icmpMonitor.Dispose(); }
                }
                finally { _vpnSessions.Dispose(); }
            }
            finally
            {
                try { _profileMonitor.Dispose(); }
                finally
                {
                    try
                    {
                        lock (_gate)
                        {
                            FlushPendingForLifecycle();
                            UpdateSessionHistory(_sessions.Close);
                        }
                    }
                    finally
                    {
                        _runLease.Dispose();
                        lock (_gate) _disposed = true;
                    }
                }
            }
        }
        RaiseChanged();
    }

    public void Dispose() => Stop();

    private void SetOffline(string message)
    {
        _status = message;
        _downloadRate = _uploadRate = 0;
    }

    private void RaiseChanged()
    {
        if (Changed is not { } changed) return;
        foreach (EventHandler handler in changed.GetInvocationList())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception exception) { Trace.TraceWarning($"Could not refresh the dashboard: {exception.Message}"); }
        }
    }

    private static bool SamePhysicalNetwork(NetworkIdentity? left, NetworkIdentity? right) =>
        left is null ? right is null : right is not null
        && left.NetworkType == right.NetworkType && left.NetworkIdentifier == right.NetworkIdentifier
        && string.Equals(left.AdapterId, right.AdapterId, StringComparison.OrdinalIgnoreCase);
}
