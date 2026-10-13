using NetTrace.Utils;

namespace NetTrace.Presentation;

// The monitoring owner supplies existing samples under its sampling lock.
// Raw snapshots and chart projection are cached separately; collection does no drawing.
public sealed class DashboardTrafficTimeline
{
    private readonly List<DashboardTrafficSample> _samples = [];
    private string? _adapterId;
    private bool _hasAdapter;
    private IReadOnlyList<DashboardTrafficSample>? _snapshot;
    private DashboardTrafficChartData? _data;
    public IReadOnlyList<DashboardTrafficSample> Samples => _snapshot ??= Array.AsReadOnly(_samples.ToArray());
    public DashboardTrafficChartData Data => _data ??= _samples.Count == 0
        ? DashboardTrafficChartData.Empty : DashboardTrafficChartData.Create(Samples, _samples[^1].TimestampUtc);

    public void Record(DateTimeOffset timestampUtc, double download, double upload,
        bool available, string? adapterId)
    {
        timestampUtc = new DateTimeOffset(timestampUtc.UtcTicks - timestampUtc.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        if (_samples.Count > 0 && timestampUtc < _samples[^1].TimestampUtc) return;
        if (_hasAdapter && !string.Equals(_adapterId, adapterId, StringComparison.OrdinalIgnoreCase)) _samples.Clear();
        _adapterId = adapterId;
        _hasAdapter = true;
        var previous = _samples.Count > 0 ? _samples[^1] : null;
        var sameSecond = previous?.TimestampUtc == timestampUtc;
        var sample = new DashboardTrafficSample(timestampUtc,
            available ? ByteRateFormatter.Normalize(download) : null,
            available ? ByteRateFormatter.Normalize(upload) : null);
        if (sameSecond)
        {
            if (previous == sample) return;
            _samples[^1] = sample;
        }
        else _samples.Add(sample);
        var oldestTicks = Math.Max(0, timestampUtc.UtcTicks - TimeSpan.FromSeconds(DashboardTrafficChartData.WindowSeconds).Ticks);
        _samples.RemoveAll(point => point.TimestampUtc.UtcTicks < oldestTicks);
        if (_samples.Count > DashboardTrafficChartData.WindowSeconds + 1)
            _samples.RemoveRange(0, _samples.Count - DashboardTrafficChartData.WindowSeconds - 1);
        _snapshot = null;
        _data = null;
    }
}
