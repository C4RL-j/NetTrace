using NetTrace.Utils;

namespace NetTrace.Presentation;

public enum DashboardUsageRange
{
    Today,
    SevenDays,
    ThirtyDays
}

public sealed record DashboardUsagePresentation(string TitleText, string TotalText, string BreakdownText)
{
    public string DownloadText { get; init; } = string.Empty;
    public string UploadText { get; init; } = string.Empty;

    public static DashboardUsageRange NextRange(DashboardUsageRange range) => range switch
    {
        DashboardUsageRange.Today => DashboardUsageRange.SevenDays,
        DashboardUsageRange.SevenDays => DashboardUsageRange.ThirtyDays,
        DashboardUsageRange.ThirtyDays => DashboardUsageRange.Today,
        _ => throw new ArgumentOutOfRangeException(nameof(range))
    };

    public static DashboardUsagePresentation Create(DashboardUsageRange range,
        (long Downloaded, long Uploaded) today, (long Downloaded, long Uploaded) week,
        (long Downloaded, long Uploaded) month)
    {
        var (title, usage) = range switch
        {
            DashboardUsageRange.Today => ("TODAY", today),
            DashboardUsageRange.SevenDays => ("LAST 7 DAYS", week),
            DashboardUsageRange.ThirtyDays => ("LAST 30 DAYS", month),
            _ => throw new ArgumentOutOfRangeException(nameof(range))
        };
        var download = $"↓ {ByteFormatter.Format(usage.Downloaded)}";
        var upload = $"↑ {ByteFormatter.Format(usage.Uploaded)}";
        return new(title, ByteFormatter.Format(usage.Downloaded + usage.Uploaded), $"{download}   {upload}")
        {
            DownloadText = download,
            UploadText = upload
        };
    }
}
