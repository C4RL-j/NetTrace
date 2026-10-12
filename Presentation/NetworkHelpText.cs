using System.Collections.ObjectModel;

namespace NetTrace.Presentation;

// Short reusable network and transfer-rate definitions, independent of live values.
public static class NetworkHelpText
{
    public static IReadOnlyDictionary<string, string> Definitions { get; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Transfer rate"] = "Shows how many bytes are transferred each second. KB/s and MB/s are commonly used by download apps. Internet speeds are often shown in Mbps; 1 MB/s equals 8 Mbps.",
            ["Signal quality"] = "An easy-to-read estimate of how strong the current Wi-Fi signal is. Higher percentages usually mean a more reliable wireless connection.",
            ["RSSI"] = "Received Signal Strength Indicator. It measures Wi-Fi signal strength in dBm. Values closer to 0 are stronger, so -50 dBm is stronger than -80 dBm.",
            ["Channel"] = "The radio channel your Wi-Fi connection is currently using. Nearby networks using the same or overlapping channels can sometimes cause interference.",
            ["Band / Frequency"] = "The wireless frequency range used by the connection. 2.4 GHz usually reaches farther, while 5 GHz and 6 GHz can offer more speed with shorter range.",
            ["BSSID"] = "The identifier of the specific Wi-Fi access point or radio you are connected to. It is different from the Wi-Fi network name (SSID).",
            ["PHY / radio type"] = "The Wi-Fi radio type Windows reports for the current connection, such as 802.11n, 802.11ac, or 802.11ax. Some Wi-Fi drivers may occasionally report a type that does not perfectly match the current band or frequency.",
            ["Receive link rate"] = "The negotiated wireless rate from the access point to this PC. It is a Wi-Fi link rate, not the same as your actual internet download speed.",
            ["Transmit link rate"] = "The negotiated wireless rate from this PC to the access point. It is not the same as your actual internet upload speed.",
            ["Link rate"] = "The local connection rate reported by the adapter. It is a link rate, not the same as your actual internet download or upload speed.",
            ["IPv4"] = "One version of Internet Protocol used to address devices and route traffic. IPv4 addresses look like 192.168.1.10.",
            ["IPv6"] = "A newer Internet Protocol with a much larger address space. IPv6 addresses use hexadecimal values separated by colons.",
            ["Default gateway"] = "The router or network device your PC normally sends traffic to when the destination is outside the local network.",
            ["DNS"] = "DNS translates names such as example.com into IP addresses that computers can use.",
            ["VPN transport"] = "The network path used by the VPN itself to reach its server. This is separate from which application traffic NetTrace detects as routed through the VPN."
        });

    public static string Get(string term) => Definitions.TryGetValue(term, out var definition) ? definition : "";
}
