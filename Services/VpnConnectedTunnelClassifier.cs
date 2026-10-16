using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Numerics;
using NetTrace.Models;

namespace NetTrace.Services;

// Passive connected-tunnel evidence for the header. Unlike general-route
// awareness, a private split route is enough; internet availability is irrelevant.
// An active local tunnel is not evidence of encryption, security or peer reachability.
public static class VpnConnectedTunnelClassifier
{
    private static readonly HashSet<string> TunnelDrivers = new(StringComparer.OrdinalIgnoreCase)
    {
        "wireguard", "wintun", "tap0901", "ovpn-dco"
    };

    private static readonly HashSet<string> OrdinaryVirtualDrivers = new(StringComparer.OrdinalIgnoreCase)
    {
        "vms_mp", "ms_vms_mp", "vmsmp", "vmnetadapter", "vmxnet3", "vboxnetadp",
        "vboxnetadp6", "sun_vboxnetadp", "ms_loop"
    };

    public static IReadOnlyList<string> Classify(VpnInterfaceInventory inventory,
        VpnRouteInventory ipv4, VpnRouteInventory ipv6)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(ipv4);
        ArgumentNullException.ThrowIfNull(ipv6);
        var names = new List<string>();
        foreach (var connection in inventory.RasConnections)
        {
            if (connection.ConnectionState == 0x2000 && connection.ErrorCode == 0 &&
                connection.ConnectionSubstate is 0 or 0x2000 &&
                connection.DeviceType.Equals("vpn", StringComparison.OrdinalIgnoreCase))
                names.Add(DisplayName(connection.ConnectionName, connection.DeviceName));
        }

        foreach (var item in inventory.Interfaces)
        {
            if (!IsTunnel(item) || !IsOperational(item)) continue;
            // Conflicting inventory identities can be a disconnect/reconnect race.
            if (inventory.Interfaces.Any(other => other.InterfaceIndex == item.InterfaceIndex &&
                other.InterfaceGuid != item.InterfaceGuid)) continue;
            var addresses = item.AssignedAddresses.Where(IsUsableAddress).ToArray();
            if (addresses.Length == 0) continue;
            if (HasActiveRoute(item, addresses, ipv4, AddressFamily.InterNetwork) ||
                HasActiveRoute(item, addresses, ipv6, AddressFamily.InterNetworkV6))
                names.Add(DisplayName(item.InterfaceName, item.Description));
        }

        return Array.AsReadOnly(names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(name => name, StringComparer.Ordinal).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static bool IsOperational(VpnInterfaceEvidence item) => item.InterfaceIndex != 0 &&
        item.InterfaceGuid != Guid.Empty && item.OperationalStatus == OperationalStatus.Up &&
        item.MediaConnectState is 0 or 1 && (item.StatusFlags & (2 | 8 | 16 | 32 | 64 | 128)) == 0;

    private static bool IsTunnel(VpnInterfaceEvidence item)
    {
        if (item.InterfaceType is not (6 or 23 or 53 or 131) || (item.StatusFlags & 1) != 0) return false;
        var driver = Matches(TunnelDrivers, item);
        if (driver) return true;
        if (Matches(OrdinaryVirtualDrivers, item) || HasInternalVirtualSignature(item)) return false;
        // Exclude automatic IPv6 transition interfaces (6to4, ISATAP, Teredo).
        if (item.InterfaceType == 131) return item.TunnelType is 1 or 2 or 15;
        // Fallback requires virtual interface state, usable IP and an active route;
        // an editable VPN-like alias alone can never activate a physical NIC.
        var words = (item.InterfaceName + " " + item.Description)
            .Split([' ', '-', '_', '(', ')', '.', '/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return words.Any(word => word.Equals("vpn", StringComparison.OrdinalIgnoreCase) ||
            word.Equals("tun", StringComparison.OrdinalIgnoreCase) || word.Equals("tap", StringComparison.OrdinalIgnoreCase) ||
            word.Equals("tunnel", StringComparison.OrdinalIgnoreCase) || word.Equals("wireguard", StringComparison.OrdinalIgnoreCase) ||
            word.Equals("openvpn", StringComparison.OrdinalIgnoreCase) || word.Equals("wintun", StringComparison.OrdinalIgnoreCase) ||
            word.Equals("tailscale", StringComparison.OrdinalIgnoreCase) || word.Equals("zerotier", StringComparison.OrdinalIgnoreCase) ||
            word.Equals("netbird", StringComparison.OrdinalIgnoreCase) || word.Equals("ikev2", StringComparison.OrdinalIgnoreCase) ||
            word.Equals("l2tp", StringComparison.OrdinalIgnoreCase) || word.Equals("sstp", StringComparison.OrdinalIgnoreCase) ||
            word.Equals("pptp", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasInternalVirtualSignature(VpnInterfaceEvidence item)
    {
        var text = item.InterfaceName + " " + item.Description;
        return new[] { "hyper-v", "hyperv", "vmware", "virtualbox", "vbox", "docker", "wsl", "vethernet",
            "host-only", "host only", "container" }
            .Any(signature => text.Contains(signature, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasActiveRoute(VpnInterfaceEvidence item, IReadOnlyList<IPAddress> addresses,
        VpnRouteInventory inventory, AddressFamily family)
    {
        if (inventory.AddressFamily != family || inventory.Availability == IpFamilyAvailability.Unavailable) return false;
        var assigned = addresses.Where(address => address.AddressFamily == family).ToArray();
        if (assigned.Length == 0) return false;
        var bits = family == AddressFamily.InterNetwork ? 32 : 128;
        foreach (var route in inventory.Routes)
        {
            if (route.InterfaceIndex != item.InterfaceIndex || route.InterfaceGuid != item.InterfaceGuid ||
                route.InterfaceUsable != true || route.ValidLifetime == 0 || route.IsLoopback ||
                route.PrefixAddress?.AddressFamily != family || route.NextHop?.AddressFamily != family ||
                route.PrefixLength > bits || !IsCanonical(route.PrefixAddress, route.PrefixLength, bits) ||
                (route.PrefixLength == 0 && route.DefaultRoutesEnabled != true)) continue;
            // Self-address host, multicast, loopback and link-local routes accompany
            // installed interfaces; they do not establish a connected VPN tunnel.
            if ((route.PrefixLength == bits && assigned.Contains(route.PrefixAddress)) ||
                IsLocalOnlyPrefix(route.PrefixAddress, route.PrefixLength)) continue;
            if (!IsUnspecified(route.NextHop) &&
                ((!IsUsableAddress(route.NextHop) && !route.NextHop.IsIPv6LinkLocal) || assigned.Contains(route.NextHop))) continue;
            if (item.TapMediaAlwaysConnected && MatchesTap(item) && route.PrefixLength != 0 &&
                IsUnspecified(route.NextHop) && assigned.Any(address => Contains(route, address, bits))) continue;
            return true;
        }
        return false;
    }

    private static bool IsUsableAddress(IPAddress address)
    {
        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6) ||
            IsUnspecified(address) || IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal ||
            address.IsIPv6Multicast || address.IsIPv4MappedToIPv6) return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return true;
        var bytes = address.GetAddressBytes();
        return bytes[0] is > 0 and < 224 && !(bytes[0] == 169 && bytes[1] == 254);
    }

    private static bool IsLocalOnlyPrefix(IPAddress address, byte prefix)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return (prefix >= 8 && bytes[0] is 0 or 127) || (prefix >= 4 && bytes[0] >= 224) ||
                (prefix >= 16 && bytes[0] == 169 && bytes[1] == 254);
        }
        return (prefix >= 10 && address.IsIPv6LinkLocal) || (prefix >= 8 && address.IsIPv6Multicast) ||
            (prefix == 128 && (IsUnspecified(address) || IPAddress.IsLoopback(address)));
    }

    private static bool IsUnspecified(IPAddress address) => address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
    private static bool Matches(HashSet<string> drivers, VpnInterfaceEvidence item) =>
        drivers.Contains(item.DriverComponentId ?? "") || drivers.Contains(item.DriverService ?? "");
    private static bool MatchesTap(VpnInterfaceEvidence item) =>
        string.Equals(item.DriverComponentId, "tap0901", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(item.DriverService, "tap0901", StringComparison.OrdinalIgnoreCase);
    private static string DisplayName(string name, string description) => !string.IsNullOrWhiteSpace(name) ? name.Trim()
        : !string.IsNullOrWhiteSpace(description) ? description.Trim() : "VPN tunnel";
    private static BigInteger Number(IPAddress address) => new(address.GetAddressBytes(), isUnsigned: true, isBigEndian: true);
    private static bool IsCanonical(IPAddress address, byte prefix, int bits) =>
        Number(address) % (BigInteger.One << (bits - prefix)) == 0;
    private static bool Contains(VpnRouteEvidence route, IPAddress address, int bits)
    {
        var start = Number(route.PrefixAddress);
        var value = Number(address);
        return start <= value && value < start + (BigInteger.One << (bits - route.PrefixLength));
    }
}
