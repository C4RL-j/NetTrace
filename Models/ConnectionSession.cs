namespace NetTrace.Models;

// Network context is captured when a session starts; profile names are resolved by stable ID.
public sealed record ConnectionSession(
    long Id,
    long? ProfileId,
    string NetworkType,
    string NetworkIdentifier,
    string NetworkFriendlyName,
    string AdapterId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset LastConfirmedAtUtc,
    DateTimeOffset? EndedAtUtc,
    string? EndReason,
    long DownloadBytes,
    long UploadBytes,
    string? ProfileDisplayName)
{
    public string NetworkDisplayName => $"{(NetworkType is "WiFi" or "WiFiBytes" ? "Wi-Fi" : NetworkType)} • {NetworkFriendlyName}";
}
