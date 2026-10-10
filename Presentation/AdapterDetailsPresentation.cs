using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetTrace.Models;

namespace NetTrace.Presentation;

// Formats an existing monitoring observation and mapping; owns no discovery or worker.
public sealed record AdapterDetailsPresentation(
    string Adapter,
    string Status,
    string Network,
    string Profile,
    string Type,
    string IPv4,
    string IPv6,
    string DefaultGateway,
    string Dns,
    string LinkRateLabel,
    string LinkRate,
    string LinkRateToolTip)
{
    public const string Unavailable = "Unavailable";
    public const string Unassigned = "Unassigned";

    public static AdapterDetailsPresentation Create(AdapterInfo? adapter,
        NetworkAdapterDetails? details, CurrentConnectionState connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var wifi = adapter?.Type == "Wireless80211";
        var matchingDetails = adapter is not null && !string.IsNullOrEmpty(adapter.Id) &&
            details is not null && string.Equals(adapter.Id, details.AdapterId, StringComparison.OrdinalIgnoreCase);
        var status = matchingDetails ? StatusText(details!.Status) : "Unknown";
        var connected = status == "Connected";
        var matchingNetwork = connected &&
            connection.Status is ConnectionStatus.ConnectedRecognized or ConnectionStatus.ConnectedUnassigned &&
            connection.Network is { HasValidIdentifier: true } network &&
            string.Equals(adapter!.Id, network.AdapterId, StringComparison.OrdinalIgnoreCase);
        var networkName = matchingNetwork ? ValueOrUnavailable(connection.Network!.FriendlyName) : Unavailable;
        var profile = matchingNetwork && connection.Status == ConnectionStatus.ConnectedRecognized &&
            connection.Profile is not null ? ValueOrUnavailable(connection.Profile.DisplayName) : Unassigned;

        var ipv4 = connected ? OrderedAddresses(details!.UnicastAddresses, allowLoopback: false)
            .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork) : null;
        // The IPv6 row describes global/unique unicast configuration. Link-local-only is unavailable.
        var ipv6 = connected ? OrderedAddresses(details!.UnicastAddresses, allowLoopback: false)
            .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetworkV6 &&
                !address.IsIPv6LinkLocal) : null;
        var gateways = connected ? OrderedAddresses(details!.GatewayAddresses, allowLoopback: false) : [];
        var gatewayFamily = ipv4 is null ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
        var gateway = gateways.FirstOrDefault(address => address.AddressFamily == gatewayFamily) ??
            gateways.FirstOrDefault();
        var dns = connected ? OrderedAddresses(details!.DnsAddresses, allowLoopback: true) : [];
        var dnsText = dns.Length == 0 ? Unavailable : string.Join(", ", dns.Take(2).Select(address => address.ToString()));
        if (dns.Length > 2) dnsText += $" (+{dns.Length - 2} more)";

        return new AdapterDetailsPresentation(
            ValueOrUnavailable(adapter?.Name), status, networkName, profile,
            wifi ? "Wi-Fi" : ValueOrUnavailable(adapter?.Type),
            ipv4?.ToString() ?? Unavailable, ipv6?.ToString() ?? Unavailable,
            gateway?.ToString() ?? Unavailable, dnsText,
            wifi ? "Wi-Fi link rate" : adapter?.Type == "Ethernet" ? "Ethernet link rate" : "Adapter link rate",
            connected ? FormatLinkRate(details!.SpeedBitsPerSecond) : Unavailable,
            wifi ? "The local Wi-Fi link between this PC and its router or hotspot. This is not measured internet speed."
                : "The local rate reported by the monitored adapter. This is not measured internet speed.");
    }

    private static string StatusText(OperationalStatus status) => status switch
    {
        OperationalStatus.Up => "Connected",
        OperationalStatus.Down or OperationalStatus.Testing or OperationalStatus.Dormant or
            OperationalStatus.NotPresent or OperationalStatus.LowerLayerDown => "Disconnected",
        _ => "Unknown"
    };

    private static string ValueOrUnavailable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Unavailable : value;

    private static string FormatLinkRate(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0) return Unavailable;
        var useGigabits = bitsPerSecond >= 1_000_000_000;
        var rate = bitsPerSecond / (useGigabits ? 1_000_000_000d : 1_000_000d);
        return rate < 0.005 ? "<0.01 Mbps" :
            rate.ToString("0.##", CultureInfo.InvariantCulture) + (useGigabits ? " Gbps" : " Mbps");
    }

    private static IPAddress[] OrderedAddresses(IReadOnlyList<IPAddress> addresses, bool allowLoopback) =>
        addresses.Where(address => IsUsable(address, allowLoopback))
            .Select(address => address.AddressFamily == AddressFamily.InterNetworkV6
                ? new IPAddress(address.GetAddressBytes()) : address)
            .Distinct().OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ThenBy(address => Convert.ToHexString(address.GetAddressBytes()), StringComparer.Ordinal)
            .ToArray();

    private static bool IsUsable(IPAddress address, bool allowLoopback)
    {
        if (!allowLoopback && IPAddress.IsLoopback(address)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var firstByte = address.GetAddressBytes()[0];
            return !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.Broadcast) &&
                firstByte < 224;
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 &&
            !address.Equals(IPAddress.IPv6Any) && !address.IsIPv6Multicast && !address.IsIPv4MappedToIPv6;
    }
}
