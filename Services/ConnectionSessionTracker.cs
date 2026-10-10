using NetTrace.Models;

namespace NetTrace.Services;

// Consumes resolved physical identities and the dashboard's existing byte deltas.
// SQLite receives absolute session totals only on a flush or meaningful transition.
public sealed class ConnectionSessionTracker
{
    private readonly ConnectionHistoryStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private ConnectionSession? _current;
    private string? _profileDisplayName;
    private ObservationState _observation;
    private DateTimeOffset _lastTimestampUtc;
    private long _lastSampleSequence = long.MinValue;
    private DateTimeOffset? _lastSampledAtUtc;
    private bool _attributionPaused;
    private bool _sleeping;
    private DateTimeOffset? _pendingSleepAtUtc;
    private bool _closed;

    public ConnectionSessionTracker(ConnectionHistoryStore store, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastTimestampUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        // A previous run cannot confirm connectivity beyond its final persisted observation.
        _store.RecoverUnfinishedSessions(_lastTimestampUtc);
    }

    public ConnectionSession? Current
    {
        get { lock (_gate) return _current; }
    }

    // A failed physical transition can retain Current for a safe persistence retry.
    // Dependent histories must not attach new observations to that uncertain parent.
    public ConnectionSession? ConfirmedSession
    {
        get { lock (_gate) return _closed || _sleeping || _attributionPaused ? null : _current; }
    }

    public bool IsSleeping
    {
        get { lock (_gate) return _sleeping; }
    }

    public void ObserveConnection(CurrentConnectionState state, DateTimeOffset? observedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            if (_closed || _sleeping || state.Status == ConnectionStatus.Detecting) return;
            var now = Timestamp(observedAtUtc);
            // A failed persistence transition must never keep collecting for the old identity.
            // A later successful observation can safely resume admission of sampled deltas.
            _attributionPaused = true;

            if (state.Status == ConnectionStatus.Disconnected)
            {
                ObserveDisconnected(now);
                _attributionPaused = false;
                return;
            }

            if (state.Status == ConnectionStatus.ConnectedIdentityUnavailable ||
                state.Network is not { HasValidIdentifier: true } network)
            {
                ObserveUnavailable(now, state.Message);
                _attributionPaused = false;
                return;
            }

            var profile = state.Profile;
            if (_current is null)
            {
                _current = _store.Transition(null, now, null, 0, 0, network, profile?.Id,
                    now, "Connected", $"Connected to {network.DisplayName}.");
                _lastSampleSequence = long.MinValue;
                _lastSampledAtUtc = null;
            }
            else if (!IsSameNetwork(_current, network))
            {
                _current = _store.Transition(_current.Id, now, "NetworkChanged",
                    _current.DownloadBytes, _current.UploadBytes, network, profile?.Id,
                    now, "NetworkChanged", $"Physical connection changed to {network.DisplayName}.");
                _lastSampleSequence = long.MinValue;
                _lastSampledAtUtc = null;
            }
            else
            {
                if (_current.ProfileId != profile?.Id ||
                    !string.Equals(_profileDisplayName, profile?.DisplayName, StringComparison.Ordinal))
                {
                    var eventType = _current.ProfileId is null && profile is not null
                        ? "ProfileAssigned" : "ProfileChanged";
                    var message = profile is null
                        ? "The active physical network is now unassigned."
                        : $"The active physical network is associated with {profile.DisplayName}.";
                    // Only this unfinished session changes association; completed history is immutable.
                    _store.UpdateActiveProfile(_current.Id, profile?.Id, now, eventType, message);
                    _current = _current with { ProfileId = profile?.Id, ProfileDisplayName = profile?.DisplayName };
                }

                _current = _current with { LastConfirmedAtUtc = now };
            }

            _profileDisplayName = profile?.DisplayName;
            _observation = ObservationState.Connected;
            _attributionPaused = false;
        }
    }

    // The caller captures this session's ID when establishing its sampling baseline.
    // Stale adapter/session deltas and repeated sample sequences cannot be attributed twice.
    public bool RecordSample(long expectedSessionId, string adapterId, long sampleSequence,
        DateTimeOffset sampledAtUtc, long downloadBytes, long uploadBytes)
    {
        lock (_gate)
        {
            var sampleUtc = sampledAtUtc.ToUniversalTime();
            if (_closed || _sleeping || _attributionPaused || _current is null || _current.Id != expectedSessionId ||
                string.IsNullOrWhiteSpace(adapterId) ||
                !string.Equals(_current.AdapterId, adapterId, StringComparison.OrdinalIgnoreCase) ||
                sampleSequence < 0 || sampleSequence <= _lastSampleSequence ||
                sampleUtc < _current.StartedAtUtc ||
                (_lastSampledAtUtc is { } previousSample && sampleUtc <= previousSample) ||
                downloadBytes < 0 || uploadBytes < 0)
                return false;

            var downloaded = checked(_current.DownloadBytes + downloadBytes);
            var uploaded = checked(_current.UploadBytes + uploadBytes);
            var confirmed = Timestamp(sampleUtc);
            _current = _current with
            {
                DownloadBytes = downloaded,
                UploadBytes = uploaded,
                LastConfirmedAtUtc = confirmed
            };
            _lastSampleSequence = sampleSequence;
            _lastSampledAtUtc = sampleUtc;
            return true;
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (_closed || _current is null) return;
            _store.Confirm(_current.Id, _current.LastConfirmedAtUtc,
                _current.DownloadBytes, _current.UploadBytes);
        }
    }

    public void Sleep()
    {
        lock (_gate)
        {
            if (_closed || _sleeping) return;
            var now = Timestamp();
            _attributionPaused = true;
            _sleeping = true;
            _pendingSleepAtUtc = now;
            _observation = ObservationState.None;
            PersistSleepBoundary();
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_closed || !_sleeping) return;
            // A failed suspend write retains its original boundary instead of bridging downtime.
            PersistSleepBoundary();
            _store.RecordEvent(Timestamp(), "Resume", null, null, null,
                "Windows resumed; waiting for a fresh physical connection observation.");
            _sleeping = false;
            _observation = ObservationState.None;
            // The owner must refresh native detection and reject observations queued before resume.
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            if (_closed) return;
            PersistSleepBoundary();
            if (_current is not null)
                EndCurrent(Timestamp(), "ApplicationExit", "ApplicationExit",
                    "NetTrace exited normally; the connection session ended.");
            _closed = true;
        }
    }

    private void PersistSleepBoundary()
    {
        if (_pendingSleepAtUtc is not { } sleepAt) return;
        if (_current is not null)
            EndCurrent(sleepAt, "Sleep", "Sleep", "Windows suspended; the connection session ended.");
        else
            _store.RecordEvent(sleepAt, "Sleep", null, null, null, "Windows suspended.");
        _pendingSleepAtUtc = null;
    }

    private void ObserveDisconnected(DateTimeOffset now)
    {
        if (_current is not null)
            EndCurrent(now, "Disconnected", "Disconnected", "The physical network disconnected.");
        else if (_observation == ObservationState.Unavailable)
            _store.RecordEvent(now, "Disconnected", null, null, null, "The physical network disconnected.");
        _observation = ObservationState.Disconnected;
    }

    private void ObserveUnavailable(DateTimeOffset now, string? message)
    {
        if (_current is not null)
        {
            var confirmed = _current.LastConfirmedAtUtc;
            // The unseen interval is not attributed to a guessed identity or connection.
            _store.Transition(_current.Id, confirmed, "IdentityUnavailable",
                _current.DownloadBytes, _current.UploadBytes, null, null,
                now, "NetworkIdentityUnavailable",
                message ?? "Windows could not provide the physical network identity.");
            _current = null;
            _profileDisplayName = null;
        }
        else if (_observation != ObservationState.Unavailable)
        {
            _store.RecordEvent(now, "NetworkIdentityUnavailable", null, null, null,
                message ?? "Windows could not provide the physical network identity.");
        }
        _observation = ObservationState.Unavailable;
    }

    private void EndCurrent(DateTimeOffset endedAtUtc, string endReason, string eventType, string message)
    {
        var current = _current!;
        _store.Transition(current.Id, endedAtUtc, endReason, current.DownloadBytes,
            current.UploadBytes, null, null, endedAtUtc, eventType, message);
        _current = null;
        _profileDisplayName = null;
    }

    private DateTimeOffset Timestamp(DateTimeOffset? timestamp = null)
    {
        var utc = (timestamp ?? _timeProvider.GetUtcNow()).ToUniversalTime();
        // Wall-clock adjustments and callback delivery order must not produce negative durations.
        if (utc < _lastTimestampUtc) return _lastTimestampUtc;
        _lastTimestampUtc = utc;
        return utc;
    }

    private static bool IsSameNetwork(ConnectionSession session, NetworkIdentity network) =>
        string.Equals(session.NetworkType, network.NetworkType, StringComparison.Ordinal) &&
        string.Equals(session.NetworkIdentifier, network.NetworkIdentifier, StringComparison.Ordinal) &&
        string.Equals(session.AdapterId, network.AdapterId, StringComparison.OrdinalIgnoreCase);

    private enum ObservationState { None, Connected, Unavailable, Disconnected }
}
