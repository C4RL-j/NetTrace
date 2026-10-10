namespace NetTrace.Models;

public sealed record MonitoringSnapshot(
    CurrentConnectionState Connection,
    ConnectionSession? Session,
    IReadOnlyList<AdapterInfo> Adapters,
    AdapterInfo? SelectedAdapter,
    bool IsSuspended,
    bool IsRunning,
    string Status,
    double DownloadBytesPerSecond,
    double UploadBytesPerSecond,
    long SessionDownloaded,
    long SessionUploaded,
    long PendingDownloaded,
    long PendingUploaded,
    DateOnly PendingDay,
    string? PersistenceMessage)
{
    public NetworkAdapterDetails? SelectedAdapterDetails { get; init; }
    public DateOnly PeakDay { get; init; }
    public double PeakDownloadBytesPerSecond { get; init; }
    public double PeakUploadBytesPerSecond { get; init; }
}
