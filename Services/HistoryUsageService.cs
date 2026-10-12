using NetTrace.Models;
using NetTrace.Presentation;

namespace NetTrace.Services;

// A range-bounded read over existing history, with live counters applied in memory.
public sealed class HistoryUsageService
{
    private readonly object _gate = new();
    private readonly ConnectionHistoryStore _store;
    private readonly TimeProvider _timeProvider;
    private IReadOnlyList<ConnectionSession>? _sessions;
    private DateTimeOffset _fromUtc;
    private DateTimeOffset _toUtc;
    private TimeZoneInfo? _zone;
    private HistoryUsageRange? _range;
    private long? _activeId;
    private HistoryUsageChartData? _data;

    public HistoryUsageService(ConnectionHistoryStore store, TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public HistoryUsageChartData GetChartData(HistoryUsageRange range,
        ConnectionSession? activeSession, bool refresh = true,
        DateTimeOffset? observedAtUtc = null, TimeZoneInfo? localTimeZone = null)
    {
        if (range is not HistoryUsageRange.SevenDays and not HistoryUsageRange.ThirtyDays)
            throw new ArgumentOutOfRangeException(nameof(range));
        lock (_gate)
        {
            var now = observedAtUtc ?? _timeProvider.GetUtcNow();
            var zone = localTimeZone ?? _timeProvider.LocalTimeZone;
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
            var fromUtc = HistoryOverview.GetLocalDayBounds(today.AddDays(1 - (int)range), zone).StartUtc;
            var toUtc = HistoryOverview.GetLocalDayBounds(today, zone).EndUtc;
            if (refresh || _sessions is null || _fromUtc != fromUtc || _toUtc != toUtc ||
                _zone?.Equals(zone) != true || _range != range || _activeId != activeSession?.Id)
            {
                var sessions = _store.GetSessionsStartedBetween(fromUtc, toUtc);
                _sessions = sessions;
                _fromUtc = fromUtc;
                _toUtc = toUtc;
                _zone = zone;
                _range = range;
                _activeId = activeSession?.Id;
            }
            var data = HistoryUsageChartData.Create(_sessions, activeSession, now, zone, range);
            // Reuse the visual snapshot when bytes/dates have not changed. The
            // existing visible-page cadence can run without redundant bar drawing.
            if (_data is not null && _data.Range == data.Range && _data.Points.SequenceEqual(data.Points) &&
                _data.AxisLabels.SequenceEqual(data.AxisLabels) && _data.MaximumText == data.MaximumText &&
                _data.MiddleText == data.MiddleText) return _data;
            _data = data;
            return data;
        }
    }
}
