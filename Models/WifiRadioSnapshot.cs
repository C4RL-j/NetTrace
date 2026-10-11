namespace NetTrace.Models;

// Current selected-interface measurements only. Never persisted as history.
public sealed record WifiRadioSnapshot(string AdapterId, bool IsConnected)
{
    public int? SignalQualityPercent { get; init; }
    public int? RssiDbm { get; init; }
    public int? Channel { get; init; }
    public int? FrequencyMHz { get; init; }
    public string? Bssid { get; init; }
    public uint? PhyType { get; init; }
    public long? ReceiveLinkBitsPerSecond { get; init; }
    public long? TransmitLinkBitsPerSecond { get; init; }
    public string? Diagnostic { get; init; }
}
