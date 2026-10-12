using System.Globalization;
using NetTrace.Models;
using NetTrace.Utils;

namespace NetTrace.Presentation;

public enum HistoryUsageRange { SevenDays = 7, ThirtyDays = 30 }

public sealed record DailyUsagePoint(DateOnly LocalDate, long DownloadBytes, long UploadBytes)
{
    public long TotalBytes => HistoryUsageChartData.AddBytes(DownloadBytes, UploadBytes);
    public string ToolTipText => LocalDate.ToString("MMM d, yyyy", CultureInfo.InvariantCulture) +
        (TotalBytes == 0 ? "\nNo recorded data usage" :
            $"\nDownload: {ByteFormatter.Format(DownloadBytes)}\nUpload: {ByteFormatter.Format(UploadBytes)}\nTotal: {ByteFormatter.Format(TotalBytes)}");
}

public sealed record HistoryUsageAxisLabel(int Index, string Text);

// Byte totals follow History 001: completed and active session usage belongs to
// the local start day. There is no traffic estimate across a midnight boundary.
public sealed class HistoryUsageChartData
{
    public HistoryUsageRange Range { get; }
    public IReadOnlyList<DailyUsagePoint> Points { get; }
    public long MaximumTotalBytes { get; }
    public string MaximumText { get; }
    public string MiddleText { get; }
    public bool HasUsage => MaximumTotalBytes > 0;
    public IReadOnlyList<HistoryUsageAxisLabel> AxisLabels { get; }

    private HistoryUsageChartData(HistoryUsageRange range, IReadOnlyList<DailyUsagePoint> points,
        IReadOnlyList<HistoryUsageAxisLabel> axisLabels)
    {
        Range = range;
        Points = points;
        AxisLabels = axisLabels;
        MaximumTotalBytes = points.Max(point => point.TotalBytes);
        MaximumText = ByteFormatter.Format(MaximumTotalBytes);
        MiddleText = ByteFormatter.Format(MaximumTotalBytes / 2);
    }

    public static HistoryUsageChartData Create(IReadOnlyList<ConnectionSession> sessions,
        ConnectionSession? activeSession, DateTimeOffset nowUtc, TimeZoneInfo zone, HistoryUsageRange range)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(zone);
        if (range is not HistoryUsageRange.SevenDays and not HistoryUsageRange.ThirtyDays)
            throw new ArgumentOutOfRangeException(nameof(range));
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime);
        var firstDay = today.AddDays(1 - (int)range);
        var points = Enumerable.Range(0, (int)range)
            .Select(index => new DailyUsagePoint(firstDay.AddDays(index), 0, 0)).ToArray();
        var distinct = new Dictionary<long, ConnectionSession>();
        foreach (var session in sessions) distinct.TryAdd(session.Id, session);
        if (activeSession is not null) distinct[activeSession.Id] = activeSession;
        foreach (var session in distinct.Values)
        {
            if (session.StartedAtUtc > nowUtc) continue;
            var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(session.StartedAtUtc, zone).DateTime);
            if (date < firstDay || date > today) continue;
            var index = date.DayNumber - firstDay.DayNumber;
            var point = points[index];
            points[index] = point with
            {
                DownloadBytes = AddBytes(point.DownloadBytes, session.DownloadBytes),
                UploadBytes = AddBytes(point.UploadBytes, session.UploadBytes)
            };
        }

        var indices = range == HistoryUsageRange.SevenDays
            ? Enumerable.Range(0, 7) : new[] { 0, 5, 10, 15, 20, 25, 29 };
        var labels = indices.Select(index => new HistoryUsageAxisLabel(index,
            index == points.Length - 1 ? "Today" : points[index].LocalDate.ToString(
                range == HistoryUsageRange.SevenDays ? "ddd" : "MMM d", CultureInfo.CurrentCulture))).ToArray();
        return new(range, Array.AsReadOnly(points), Array.AsReadOnly(labels));
    }

    internal static long AddBytes(long total, long value)
    {
        total = Math.Max(0, total);
        value = Math.Max(0, value);
        return total > long.MaxValue - value ? long.MaxValue : total + value;
    }
}
