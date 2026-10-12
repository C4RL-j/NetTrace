namespace AppUsageStorage;

public sealed record UsageDelta(
    DateOnly LocalDate,
    ApplicationIdentity Application,
    long DownloadBytes,
    long UploadBytes,
    DateTimeOffset LastSeen)
{
    // The observation's local calendar date supplies the bucket, including across midnight.
    public static UsageDelta FromObservation(DateTimeOffset observedAt, TimeZoneInfo localTimeZone,
        ApplicationIdentity application, long downloadBytes, long uploadBytes)
    {
        ArgumentNullException.ThrowIfNull(localTimeZone);
        ArgumentNullException.ThrowIfNull(application);
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(observedAt, localTimeZone).DateTime);
        return new(localDate, application, downloadBytes, uploadBytes, observedAt);
    }
}

public sealed record DailyApplicationUsage(
    DateOnly LocalDate,
    string ApplicationIdentity,
    string DisplayName,
    string? ExecutableName,
    string? ExecutablePath,
    long DownloadBytes,
    long UploadBytes,
    DateTimeOffset LastSeen);
