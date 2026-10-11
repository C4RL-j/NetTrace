using System.Globalization;
using System.Net.NetworkInformation;
using NetTrace.Models;
using NetTrace.Utils;

namespace NetTrace.Presentation;

public sealed record WifiRadioPresentation(bool Show, string SignalQuality, string Rssi,
    string Channel, string BandFrequency, string Bssid, string Phy, string ReceiveLinkRate,
    string TransmitLinkRate, bool HasSeparateLinkRates)
{
    private const string Unavailable = "Unavailable";
    public static WifiRadioPresentation Hidden { get; } = new(false, Unavailable, Unavailable,
        Unavailable, Unavailable, Unavailable, Unavailable, Unavailable, Unavailable, false);

    public static WifiRadioPresentation Create(AdapterInfo? adapter, NetworkAdapterDetails? details,
        WifiRadioSnapshot? radio)
    {
        if (adapter is null || !string.Equals(adapter.Type, "Wireless80211", StringComparison.OrdinalIgnoreCase) ||
            details?.Status != OperationalStatus.Up || radio is not { IsConnected: true } ||
            !string.Equals(adapter.Id, details.AdapterId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(adapter.Id, radio.AdapterId, StringComparison.OrdinalIgnoreCase)) return Hidden;
        var quality = radio.SignalQualityPercent is { } percent
            ? Math.Clamp(percent, 0, 100).ToString(CultureInfo.InvariantCulture) + "%" : Unavailable;
        var rssi = radio.RssiDbm is >= -200 and <= 0
            ? radio.RssiDbm.Value.ToString(CultureInfo.InvariantCulture) + " dBm" : Unavailable;
        var frequency = radio.FrequencyMHz is > 0 ? radio.FrequencyMHz : null;
        var mapping = frequency is { } mhz ? WifiRadioFrequency.Classify(mhz) : (Band: (string?)null, Channel: (int?)null);
        var channelNumber = radio.Channel is > 0 and <= 233 ? radio.Channel : mapping.Channel;
        var channel = channelNumber?.ToString(CultureInfo.InvariantCulture) ?? Unavailable;
        var bandFrequency = frequency is { } knownFrequency
            ? (mapping.Band is null ? "" : mapping.Band + " • ") + knownFrequency.ToString(CultureInfo.InvariantCulture) + " MHz"
            : Unavailable;
        var bssid = FormatBssid(radio.Bssid);
        var phy = radio.PhyType switch
        {
            1 => "802.11 (FHSS)", 2 => "802.11 (DSSS)", 3 => "802.11 (infrared)",
            4 => "802.11a", 5 => "802.11b", 6 => "802.11g", 7 => "802.11n", 8 => "802.11ac",
            9 => "802.11ad", 10 => "802.11ax", 11 => "802.11be", _ => Unavailable
        };
        var receive = FormatLinkRate(radio.ReceiveLinkBitsPerSecond);
        var transmit = FormatLinkRate(radio.TransmitLinkBitsPerSecond);
        var useful = new[] { quality, rssi, channel, bandFrequency, bssid, phy, receive, transmit }
            .Any(value => value != Unavailable);
        return new(useful, quality, rssi, channel, bandFrequency, bssid, phy, receive, transmit,
            receive != Unavailable && transmit != Unavailable);
    }

    private static string FormatBssid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Unavailable;
        var compact = value.Replace(":", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        if (compact.Length != 12 || !compact.All(char.IsAsciiHexDigit)) return Unavailable;
        var bytes = Convert.FromHexString(compact);
        if (bytes.All(part => part == 0) || (bytes[0] & 1) != 0) return Unavailable;
        return string.Join(":", bytes.Select(part => part.ToString("X2", CultureInfo.InvariantCulture)));
    }

    private static string FormatLinkRate(long? bitsPerSecond)
    {
        if (bitsPerSecond is null or <= 0) return Unavailable;
        var gigabits = bitsPerSecond >= 1_000_000_000;
        var rate = bitsPerSecond.Value / (gigabits ? 1_000_000_000d : 1_000_000d);
        return rate < 0.005 ? "<0.01 Mbps" : rate.ToString("0.##", CultureInfo.InvariantCulture) +
            (gigabits ? " Gbps" : " Mbps");
    }
}
