using NetTrace.Models;
using NetTrace.Presentation;

namespace NetTrace.Services;

// Reads the existing store; live refreshes reuse cached bounded history inputs.
public sealed class HistoryOverviewService
{
    private readonly object _gate = new();
    private readonly ConnectionHistoryStore _store;
    private readonly TimeProvider _timeProvider;
    private IReadOnlyList<ConnectionSession>? _today;
    private IReadOnlyList<ConnectionSession>? _recent;
    private DateTimeOffset _dayStart;
    private DateTimeOffset _dayEnd;
    private TimeZoneInfo? _zone;
    private long? _activeId;

    public HistoryOverviewService(ConnectionHistoryStore store, TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public HistoryOverview GetOverview(ConnectionSession? activeSession, bool refresh = true,
        DateTimeOffset? observedAtUtc = null, TimeZoneInfo? localTimeZone = null)
    {
        lock (_gate)
        {
            var now = observedAtUtc ?? _timeProvider.GetUtcNow();
            var zone = localTimeZone ?? _timeProvider.LocalTimeZone;
            var (dayStart, dayEnd) = HistoryOverview.GetLocalDayBounds(now, zone);
            if (refresh || _today is null || _recent is null || _dayStart != dayStart ||
                _dayEnd != dayEnd || _zone?.Equals(zone) != true || _activeId != activeSession?.Id)
            {
                // Publish both inputs only after both reads succeed so a failed read
                // cannot leave a partially refreshed summary available to later ticks.
                var today = _store.GetSessions(dayStart, dayEnd);
                var recent = _store.GetRecentSessions(4);
                _today = today;
                _recent = recent;
                _dayStart = dayStart;
                _dayEnd = dayEnd;
                _zone = zone;
                _activeId = activeSession?.Id;
            }
            return HistoryOverview.Create(_today, _recent, activeSession, now, zone);
        }
    }
}
