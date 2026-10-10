using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Numerics;
using NetTrace.Models;

namespace NetTrace.Services;

// Interprets local route/interface inventories only. No probes, binding or routing changes.
public static class VpnRouteClassifier
{
    private static readonly HashSet<string> OrdinaryVirtualDrivers = new(StringComparer.OrdinalIgnoreCase)
    {
        "vms_mp", "ms_vms_mp", "vmsmp", "vmnetadapter", "vmxnet3", "vboxnetadp",
        "vboxnetadp6", "sun_vboxnetadp", "ms_loop"
    };

    public static VpnRouteSnapshot Classify(VpnInterfaceInventory interfaces,
        VpnRouteInventory ipv4, VpnRouteInventory ipv6, DateTimeOffset checkedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(interfaces);
        ArgumentNullException.ThrowIfNull(ipv4);
        ArgumentNullException.ThrowIfNull(ipv6);
        var detection = VpnDetectionService.Classify(interfaces);
        var families = new[]
        {
            ClassifyFamily(interfaces, detection, ipv4, AddressFamily.InterNetwork),
            ClassifyFamily(interfaces, detection, ipv6, AddressFamily.InterNetworkV6)
        };
        var reason = string.Join(" ", families.Select(family => family.Reason));
        var vpn = families.Where(family => family.Kind == InternetRouteKind.Vpn).ToArray();
        if (vpn.Length > 0)
        {
            var participants = vpn.SelectMany(family => family.Participants)
                .DistinctBy(item => (item.InterfaceIndex, item.InterfaceGuid)).ToArray();
            return new(VpnTunnelStatus.Active, InternetRouteKind.Vpn,
                participants.Length == 1 ? participants[0].InterfaceName : "Multiple tunnel interfaces",
                participants.Length == 1 ? participants[0].Description : null,
                checkedAtUtc.ToUniversalTime(), reason + " VPN denotes proven general routing for at least one IP family, not all destinations.")
            {
                IPv4RouteKind = families[0].Kind,
                IPv6RouteKind = families[1].Kind
            };
        }
        var applicable = families.Where(family => family.Availability != IpFamilyAvailability.Unavailable).ToArray();
        if (interfaces.IsComplete && applicable.Length > 0 && applicable.All(family => family.Kind == InternetRouteKind.Direct))
            return new(VpnTunnelStatus.Inactive, InternetRouteKind.Direct, null, null,
                checkedAtUtc.ToUniversalTime(), reason)
            {
                IPv4RouteKind = families[0].Kind,
                IPv6RouteKind = families[1].Kind
            };
        return new(VpnTunnelStatus.Unknown, InternetRouteKind.Unknown, null, null,
            checkedAtUtc.ToUniversalTime(), reason)
        {
            IPv4RouteKind = families[0].Kind,
            IPv6RouteKind = families[1].Kind
        };
    }

    private static FamilyResult ClassifyFamily(VpnInterfaceInventory interfaces, VpnState detection,
        VpnRouteInventory inventory, AddressFamily family)
    {
        var label = family == AddressFamily.InterNetwork ? "IPv4" : "IPv6";
        FamilyResult Unknown(string why) => new(InternetRouteKind.Unknown, IpFamilyAvailability.Unknown,
            [], $"{label}: Unknown — {why}");
        if (!inventory.IsComplete || inventory.AddressFamily != family)
            return Unknown(inventory.Diagnostic ?? "the route inventory is incomplete.");
        if (inventory.Availability is not (IpFamilyAvailability.Unknown or IpFamilyAvailability.Unavailable or IpFamilyAvailability.Usable))
            return Unknown("the IP-family availability value is invalid.");
        var bits = family == AddressFamily.InterNetwork ? 32 : 128;
        // Route expiry and default eligibility do not make an operational family unavailable.
        // Preserve interface usability before discarding ineligible forwarding routes.
        var forwardingInterfaces = inventory.Routes.Where(route => !route.IsLoopback).ToArray();
        var familyUsable = inventory.Availability == IpFamilyAvailability.Usable ||
            forwardingInterfaces.Any(route => route.InterfaceUsable == true);
        var familyUncertain = forwardingInterfaces.Any(route => route.InterfaceUsable is null);
        if (inventory.Availability == IpFamilyAvailability.Unavailable && (familyUsable || familyUncertain))
            return Unknown("family unavailability contradicts usable or uncertain interface evidence.");
        var rows = new List<VpnRouteEvidence>();
        foreach (var route in inventory.Routes.Distinct())
        {
            if (route.InterfaceUsable == false || route.ValidLifetime == 0 || route.IsLoopback) continue;
            if (route.InterfaceUsable is null || route.EffectiveMetric is null || route.InterfaceIndex == 0 ||
                route.InterfaceGuid is null || route.InterfaceGuid == Guid.Empty ||
                route.PrefixAddress?.AddressFamily != family || route.NextHop?.AddressFamily != family ||
                route.PrefixLength > bits || !IsCanonical(route.PrefixAddress, route.PrefixLength, bits))
                return Unknown("a forwarding route has missing or invalid interface, prefix or metric evidence.");
            if (route.PrefixLength == 0)
            {
                if (route.DefaultRoutesEnabled == false) continue;
                if (route.DefaultRoutesEnabled is null) return Unknown("default-route eligibility is unknown.");
            }
            rows.Add(route);
        }
        if (rows.Count == 0)
        {
            if (familyUsable || familyUncertain)
                return Unknown("the family has usable or uncertain interfaces but no eligible general-route evidence.");
            return new(InternetRouteKind.Unavailable, IpFamilyAvailability.Unavailable, [], $"{label}: Unavailable — no usable forwarding interfaces.");
        }
        if (!interfaces.IsComplete) return Unknown(interfaces.Diagnostic ?? "the interface inventory is incomplete.");

        var halves = new[] { BigInteger.Zero, BigInteger.One << (bits - 1) };
        var owners = new List<VpnInterfaceEvidence>();
        foreach (var half in halves)
        {
            var matches = rows.Where(route => route.PrefixLength <= 1 && Contains(route, half, bits)).ToArray();
            if (matches.Length == 0) return Unknown("usable default or paired /1 routes do not cover both general-route halves.");
            var longest = matches.Max(route => route.PrefixLength);
            var specific = matches.Where(route => route.PrefixLength == longest).ToArray();
            var metric = specific.Min(route => route.EffectiveMetric!.Value);
            var winners = specific.Where(route => route.EffectiveMetric == metric)
                .DistinctBy(route => (route.InterfaceIndex, route.InterfaceGuid)).ToArray();
            if (winners.Length != 1) return Unknown("interfaces tie for an effective general route.");
            var owner = interfaces.Interfaces.Where(item => item.InterfaceIndex == winners[0].InterfaceIndex &&
                item.InterfaceGuid == winners[0].InterfaceGuid).Distinct().ToArray();
            if (owner.Length != 1 || !IsOperational(owner[0]))
                return Unknown("the effective route owner has missing, stale or non-operational interface evidence.");
            owners.Add(owner[0]);
        }
        var participants = owners.DistinctBy(item => (item.InterfaceIndex, item.InterfaceGuid)).ToArray();
        var kinds = participants.Select(item => Kind(item, interfaces, detection)).ToArray();
        if (kinds.Any(kind => kind == InternetRouteKind.Unknown))
            return Unknown("an effective general-route owner cannot reliably be identified as physical or VPN.");
        if (kinds.Distinct().Count() != 1)
            return Unknown("the general-route halves use a mixture of physical and tunnel interfaces.");
        var kind = kinds[0];
        // A winning more-specific public VPN route can make a Direct claim misleading.
        // Private/internal routes alone never promote an installed adapter to an internet VPN.
        var forwarding = PublicForwarding(rows, interfaces, detection, family, bits);
        if (kind == InternetRouteKind.Direct && !forwarding.AllDirect)
            return Unknown("a more-specific public route has a tunnel, ambiguous or unsupported forwarding owner.");
        if (kind == InternetRouteKind.Vpn)
        {
            // Installed broad routes can be completely shadowed by more-specific routes.
            // Only actual public forwarding participants belong in the active snapshot.
            if (forwarding.VpnParticipants.Count > 0)
                participants = forwarding.VpnParticipants.ToArray();
            else if (forwarding.AllDirect)
            {
                var physicalNames = string.Join(", ", forwarding.DirectParticipants.Select(item => item.InterfaceName));
                return new(InternetRouteKind.Direct, IpFamilyAvailability.Usable, forwarding.DirectParticipants,
                    $"{label}: Direct via {physicalNames}; physical routes override the tunnel's general routes across every public forwarding range.");
            }
            else return Unknown("the tunnel's general routes have no confirmed effective public forwarding ranges.");
        }
        var names = string.Join(", ", participants.Select(item => item.InterfaceName));
        return new(kind, IpFamilyAvailability.Usable, participants,
            $"{label}: {(kind == InternetRouteKind.Vpn ? "VPN" : "Direct")} via {names}; general routing and effective public forwarding are compared by longest prefix and combined route/interface metric.");
    }

    private static InternetRouteKind Kind(VpnInterfaceEvidence item, VpnInterfaceInventory inventory, VpnState detection)
    {
        if (OrdinaryVirtualDrivers.Contains(item.DriverComponentId ?? "") || OrdinaryVirtualDrivers.Contains(item.DriverService ?? ""))
            return InternetRouteKind.Unknown;
        if (item.TapMediaAlwaysConnected &&
            (string.Equals(item.DriverComponentId, "tap0901", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(item.DriverService, "tap0901", StringComparison.OrdinalIgnoreCase)))
            return InternetRouteKind.Unknown;
        if (detection.Status == VpnStatus.TunnelDetected && detection.Interfaces.Any(candidate => candidate.InterfaceIndex == item.InterfaceIndex &&
            candidate.InterfaceGuid == item.InterfaceGuid && candidate.OperationalStatus == OperationalStatus.Up))
            return InternetRouteKind.Vpn;
        if ((item.StatusFlags & 1) != 0 && item.InterfaceType is 6 or 71)
            return InternetRouteKind.Direct;
        if (item.InterfaceType == 23)
        {
            var vpn = inventory.RasConnections.Where(connection => IsConnectedRas(connection) &&
                connection.DeviceType.Equals("vpn", StringComparison.OrdinalIgnoreCase)).ToArray();
            var otherRas = inventory.RasConnections.Any(connection => IsConnectedRas(connection) &&
                !connection.DeviceType.Equals("vpn", StringComparison.OrdinalIgnoreCase));
            var ppp = inventory.Interfaces.Where(candidate => candidate.InterfaceType == 23 && IsOperational(candidate)).ToArray();
            if (!otherRas && vpn.Length == 1 && ppp.Length == 1 &&
                (string.Equals(item.InterfaceName, vpn[0].ConnectionName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(item.Description, vpn[0].DeviceName, StringComparison.OrdinalIgnoreCase)))
                return InternetRouteKind.Vpn;
            return InternetRouteKind.Unknown;
        }
        // A name is fallback evidence only when native virtual interface state and
        // effective forwarding ownership also agree; arbitrary installed names do not count.
        if ((item.StatusFlags & 1) == 0 && item.MediaConnectState == 1 && item.InterfaceType is 6 or 53 &&
            !HasInternalVirtualSignature(item) && HasTunnelWord(item))
            return InternetRouteKind.Vpn;
        return InternetRouteKind.Unknown;
    }

    private static bool HasTunnelWord(VpnInterfaceEvidence item) =>
        (item.InterfaceName + " " + item.Description).Split([' ', '-', '_', '(', ')', '.', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Any(word => word.Equals("vpn", StringComparison.OrdinalIgnoreCase) ||
                word.Equals("tun", StringComparison.OrdinalIgnoreCase) || word.Equals("tap", StringComparison.OrdinalIgnoreCase) ||
                word.Equals("tunnel", StringComparison.OrdinalIgnoreCase));

    private static bool HasInternalVirtualSignature(VpnInterfaceEvidence item)
    {
        var text = item.InterfaceName + " " + item.Description;
        return new[] { "hyper-v", "vmware", "virtualbox", "vbox", "docker", "host-only", "host only", "container" }
            .Any(signature => text.Contains(signature, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsConnectedRas(VpnRasConnectionEvidence connection) => connection.ConnectionState == 0x2000 &&
        connection.ConnectionSubstate is 0 or 0x2000 && connection.ErrorCode == 0;

    private static bool IsOperational(VpnInterfaceEvidence item) => item.InterfaceIndex != 0 &&
        item.InterfaceGuid != Guid.Empty && item.OperationalStatus == OperationalStatus.Up &&
        item.MediaConnectState is 0 or 1 && item.InterfaceType != 24 && (item.StatusFlags & (2 | 8 | 16 | 32 | 64 | 128)) == 0;

    private static PublicForwardingResult PublicForwarding(IReadOnlyList<VpnRouteEvidence> routes,
        VpnInterfaceInventory interfaces, VpnState detection, AddressFamily family, int bits)
    {
        var excluded = ExcludedInternetRanges(family, bits);
        var boundaries = new SortedSet<BigInteger> { BigInteger.Zero, BigInteger.One << bits };
        foreach (var range in routes.Select(route => (Start: Number(route.PrefixAddress),
            End: Number(route.PrefixAddress) + (BigInteger.One << (bits - route.PrefixLength)))).Concat(excluded))
        {
            boundaries.Add(range.Start);
            boundaries.Add(range.End);
        }
        var vpn = new List<VpnInterfaceEvidence>();
        var physical = new List<VpnInterfaceEvidence>();
        var allDirect = true;
        foreach (var point in boundaries.Take(boundaries.Count - 1))
        {
            if (excluded.Any(range => range.Start <= point && point < range.End)) continue;
            var matching = routes.Where(candidate => Contains(candidate, point, bits)).ToArray();
            if (matching.Length == 0) { allDirect = false; continue; }
            var prefix = matching.Max(candidate => candidate.PrefixLength);
            var metric = matching.Where(candidate => candidate.PrefixLength == prefix).Min(candidate => candidate.EffectiveMetric!.Value);
            var winners = matching.Where(candidate => candidate.PrefixLength == prefix && candidate.EffectiveMetric == metric)
                .DistinctBy(candidate => (candidate.InterfaceIndex, candidate.InterfaceGuid)).ToArray();
            if (winners.Length != 1) { allDirect = false; continue; }
            var winner = winners[0];
            var owners = interfaces.Interfaces.Where(candidate => candidate.InterfaceIndex == winner.InterfaceIndex &&
                candidate.InterfaceGuid == winner.InterfaceGuid).Distinct().ToArray();
            if (owners.Length != 1 || !IsOperational(owners[0])) { allDirect = false; continue; }
            var kind = Kind(owners[0], interfaces, detection);
            if (kind == InternetRouteKind.Vpn) vpn.Add(owners[0]);
            else if (kind == InternetRouteKind.Direct) physical.Add(owners[0]);
            if (kind != InternetRouteKind.Direct) allDirect = false;
        }
        VpnInterfaceEvidence[] Ordered(IEnumerable<VpnInterfaceEvidence> items) => items
            .DistinctBy(item => (item.InterfaceIndex, item.InterfaceGuid)).OrderBy(item => item.InterfaceIndex)
            .ThenBy(item => item.InterfaceGuid).ToArray();
        return new(Ordered(vpn), Ordered(physical), allDirect && physical.Count > 0);
    }

    private static (BigInteger Start, BigInteger End)[] ExcludedInternetRanges(AddressFamily family, int bits) =>
        (family == AddressFamily.InterNetwork
            ? new[] { "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.168.0.0/16", "224.0.0.0/4", "240.0.0.0/4" }
            : new[] { "::/128", "::1/128", "fc00::/7", "fe80::/10", "ff00::/8" })
        .Select(prefix =>
        {
            var parts = prefix.Split('/');
            var start = Number(IPAddress.Parse(parts[0]));
            var length = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            return (start, start + (BigInteger.One << (bits - length)));
        }).ToArray();

    private static BigInteger Number(IPAddress address) => new(address.GetAddressBytes(), isUnsigned: true, isBigEndian: true);
    private static bool IsCanonical(IPAddress address, byte prefix, int bits) =>
        Number(address) % (BigInteger.One << (bits - prefix)) == 0;
    private static bool Contains(VpnRouteEvidence route, BigInteger destination, int bits)
    {
        var start = Number(route.PrefixAddress);
        return start <= destination && destination < start + (BigInteger.One << (bits - route.PrefixLength));
    }

    private sealed record FamilyResult(InternetRouteKind Kind, IpFamilyAvailability Availability,
        IReadOnlyList<VpnInterfaceEvidence> Participants, string Reason);
    private sealed record PublicForwardingResult(IReadOnlyList<VpnInterfaceEvidence> VpnParticipants,
        IReadOnlyList<VpnInterfaceEvidence> DirectParticipants, bool AllDirect);
}
