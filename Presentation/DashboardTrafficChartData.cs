using System.Globalization;
using NetTrace.Utils;

namespace NetTrace.Presentation;

public sealed record DashboardTrafficSample(DateTimeOffset TimestampUtc, double? DownloadBytesPerSecond,
    double? UploadBytesPerSecond, bool BreakBefore = false)
{
    public string ToolTipText
    {
        get
        {
            var total = DownloadBytesPerSecond is { } download && UploadBytesPerSecond is { } upload
                ? (double?)Math.Min(double.MaxValue, ByteRateFormatter.Normalize(download) + ByteRateFormatter.Normalize(upload))
                : null;
            static string Rate(double? value) => value is { } rate ? ByteRateFormatter.Format(rate) : "Unavailable";
            return TimestampUtc.ToLocalTime().ToString("MMM d, yyyy HH:mm:ss", CultureInfo.CurrentCulture) +
                $"\nDownload: {Rate(DownloadBytesPerSecond)}\nUpload: {Rate(UploadBytesPerSecond)}\nTotal: {Rate(total)}";
        }
    }
}

public sealed record DashboardTrafficChartPoint(double Position, double BytesPerSecond);

// A visible chart projection of the monitor's rolling samples, never usage accounting.
public sealed class DashboardTrafficChartData
{
    public const int WindowSeconds = 60;
    public IReadOnlyList<DashboardTrafficSample> Samples { get; }
    public DateTimeOffset WindowStartUtc { get; }
    public DateTimeOffset WindowEndUtc { get; }
    public double MaximumBytesPerSecond { get; }
    public string MaximumText => ByteRateFormatter.Format(MaximumBytesPerSecond);
    public string MiddleText => ByteRateFormatter.Format(MaximumBytesPerSecond / 2);
    public IReadOnlyList<IReadOnlyList<DashboardTrafficChartPoint>> DownloadSegments { get; }
    public IReadOnlyList<IReadOnlyList<DashboardTrafficChartPoint>> UploadSegments { get; }
    public static DashboardTrafficChartData Empty { get; } = Create([], DateTimeOffset.UtcNow);

    private DashboardTrafficChartData(IReadOnlyList<DashboardTrafficSample> samples, DateTimeOffset endUtc)
    {
        Samples = samples;
        WindowEndUtc = endUtc;
        WindowStartUtc = endUtc.UtcTicks >= TimeSpan.FromSeconds(WindowSeconds).Ticks
            ? endUtc.AddSeconds(-WindowSeconds) : DateTimeOffset.MinValue;
        var maximum = samples.SelectMany(sample => new[] { sample.DownloadBytesPerSecond, sample.UploadBytesPerSecond })
            .Where(value => value.HasValue).Select(value => value!.Value).DefaultIfEmpty(0).Max();
        MaximumBytesPerSecond = NiceMaximum(maximum);
        DownloadSegments = CreateSegments(sample => sample.DownloadBytesPerSecond);
        UploadSegments = CreateSegments(sample => sample.UploadBytesPerSecond);
    }

    public static DashboardTrafficChartData Create(IReadOnlyList<DashboardTrafficSample> samples, DateTimeOffset endUtc)
    {
        ArgumentNullException.ThrowIfNull(samples);
        endUtc = endUtc.ToUniversalTime();
        var startTicks = Math.Max(0, endUtc.UtcTicks - TimeSpan.FromSeconds(WindowSeconds).Ticks);
        var snapshot = samples.Where(sample => sample.TimestampUtc.UtcTicks >= startTicks && sample.TimestampUtc <= endUtc)
            .GroupBy(sample => sample.TimestampUtc).Select(group => group.Last()).OrderBy(sample => sample.TimestampUtc)
            .Select(sample => sample with
            {
                TimestampUtc = sample.TimestampUtc.ToUniversalTime(),
                DownloadBytesPerSecond = sample.DownloadBytesPerSecond is { } download ? ByteRateFormatter.Normalize(download) : null,
                UploadBytesPerSecond = sample.UploadBytesPerSecond is { } upload ? ByteRateFormatter.Normalize(upload) : null
            }).ToArray();
        return new(Array.AsReadOnly(snapshot), endUtc);
    }

    private IReadOnlyList<IReadOnlyList<DashboardTrafficChartPoint>> CreateSegments(Func<DashboardTrafficSample, double?> select)
    {
        var segments = new List<IReadOnlyList<DashboardTrafficChartPoint>>();
        var current = new List<DashboardTrafficChartPoint>();
        DateTimeOffset? previous = null;
        void Finish()
        {
            if (current.Count == 0) return;
            segments.Add(Array.AsReadOnly(current.ToArray()));
            current.Clear();
        }
        foreach (var sample in Samples)
        {
            if (sample.BreakBefore || previous is { } last && (sample.TimestampUtc - last).TotalSeconds > 2.5) Finish();
            if (select(sample) is { } rate)
                current.Add(new(Math.Clamp((sample.TimestampUtc - WindowStartUtc).TotalSeconds / WindowSeconds, 0, 1), rate));
            else Finish();
            previous = sample.TimestampUtc;
        }
        Finish();
        return Array.AsReadOnly(segments.ToArray());
    }

    private static double NiceMaximum(double maximum)
    {
        if (maximum <= 0) return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(maximum)));
        if (magnitude == 0 || !double.IsFinite(magnitude)) return maximum;
        var scaled = maximum / magnitude;
        var step = scaled <= 1 ? 1 : scaled <= 2 ? 2 : scaled <= 5 ? 5 : 10;
        var result = step * magnitude;
        return double.IsFinite(result) && result >= maximum ? result : maximum;
    }
}
