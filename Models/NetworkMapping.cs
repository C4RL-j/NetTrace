namespace NetTrace.Models;

public sealed record NetworkMapping(
    long Id,
    long ProfileId,
    string NetworkType,
    string NetworkIdentifier,
    string? FriendlyName,
    DateTimeOffset CreatedAt)
{
    public string TypeDisplayName => NetworkType is "WiFi" or "WiFiBytes" ? "Wi-Fi" : NetworkType;
    public string DisplayName => NetworkType == "WiFi" ? NetworkIdentifier : FriendlyName ?? NetworkIdentifier;
}
