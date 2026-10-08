namespace NetTrace.Models;

// A recognition key for the physical network, separate from a telco/device profile.
public sealed record NetworkIdentity(
    string NetworkType,
    string NetworkIdentifier,
    string FriendlyName,
    string AdapterId)
{
    // SSIDs are exact identities: an all-space SSID is valid and must not be trimmed.
    public bool HasValidIdentifier => !string.IsNullOrWhiteSpace(NetworkType) &&
        !string.IsNullOrEmpty(NetworkIdentifier);

    public string TypeDisplayName => NetworkType is "WiFi" or "WiFiBytes" ? "Wi-Fi" : NetworkType;
    public string DisplayName => $"{TypeDisplayName} • {FriendlyName}";
}
