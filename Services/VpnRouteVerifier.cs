using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Numerics;
using NetTrace.Models;

namespace NetTrace.Services;

// Local routing evidence only; never probes destinations or changes Windows routes.
public sealed class VpnRouteVerifier
{
    private readonly VpnDetectionService _detection;
    private readonly IVpnRouteSource _source;

    public VpnRouteVerifier(VpnDetectionService? detection = null, IVpnRouteSource? source = null)
    {
        _detection = detection ?? new VpnDetectionService();
        _source = source ?? new WindowsVpnRouteNative();
    }

    public VpnRoutingState GetCurrentState()
    {
        var detection = _detection.GetCurrentState();
        if (detection.Status != VpnStatus.TunnelDetected || detection.Interfaces.Count == 0)
            return new(detection, Array.Empty<VpnTunnelRoutingResult>());
        return Verify(detection, Read(AddressFamily.InterNetwork), Read(AddressFamily.InterNetworkV6));
    }

    private VpnRouteInventory Read(AddressFamily family)
    {
        try { return _source.Read(family); }
        catch (Exception exception)
        {
            return new(family, Array.Empty<VpnRouteEvidence>(), false,
                $"Windows could not read {family} routing: {exception.Message}");
        }
    }

    public static VpnRoutingState Verify(VpnState detection, VpnRouteInventory ipv4, VpnRouteInventory ipv6)
    {
        ArgumentNullException.ThrowIfNull(detection);
        ArgumentNullException.ThrowIfNull(ipv4);
        ArgumentNullException.ThrowIfNull(ipv6);
        if (detection.Status != VpnStatus.TunnelDetected)
            return new(detection, Array.Empty<VpnTunnelRoutingResult>());

        var tunnels = detection.Interfaces.Distinct()
            .OrderBy(x => x.InterfaceIndex).ThenBy(x => x.InterfaceGuid)
            .ThenBy(x => x.InterfaceName, StringComparer.Ordinal).Select(tunnel =>
            {
                var v4 = VerifyFamily(tunnel, ipv4, AddressFamily.InterNetwork);
                var v6 = VerifyFamily(tunnel, ipv6, AddressFamily.InterNetworkV6);
                return new VpnTunnelRoutingResult(tunnel, Combine(v4, v6), v4, v6);
            }).ToArray();
        return new(detection, Array.AsReadOnly(tunnels));
    }

    private static VpnRoutingMode Combine(params VpnFamilyRoutingResult[] families)
    {
        var applicable = families.Where(x => x.Availability != IpFamilyAvailability.Unavailable).ToArray();
        if (applicable.Length == 0 || applicable.Any(x => x.Availability != IpFamilyAvailability.Usable ||
            x.Mode == VpnRoutingMode.Unverified))
            return VpnRoutingMode.Unverified;
        return applicable.All(x => x.Mode == VpnRoutingMode.FullTunnel)
            ? VpnRoutingMode.FullTunnel : VpnRoutingMode.SplitTunnel;
    }

    private static VpnFamilyRoutingResult VerifyFamily(VpnInterfaceInfo tunnel,
        VpnRouteInventory inventory, AddressFamily family)
    {
        // Stable evidence order makes repeated/reordered observations explainable.
        var evidence = inventory.Routes.Distinct().OrderBy(x => x.InterfaceIndex)
            .ThenBy(x => x.InterfaceGuid).ThenBy(x => x.PrefixAddress?.ToString(), StringComparer.Ordinal)
            .ThenBy(x => x.PrefixLength).ThenBy(x => x.NextHop?.ToString(), StringComparer.Ordinal)
            .ThenBy(x => x.RouteMetric).ThenBy(x => x.InterfaceMetric).ThenBy(x => x.InterfaceUsable)
            .ThenBy(x => x.ValidLifetime).ThenBy(x => x.IsLoopback).ThenBy(x => x.DefaultRoutesEnabled).ToArray();
        var availability = FamilyAvailability(inventory, evidence, family);
        VpnFamilyRoutingResult Result(VpnRoutingMode mode, string reason) =>
            new(family, mode, Array.AsReadOnly(evidence), reason, availability);
        VpnFamilyRoutingResult Unknown(string reason) => Result(VpnRoutingMode.Unverified, reason);

        if (inventory.AddressFamily != family || !inventory.IsComplete)
            return Unknown(inventory.Diagnostic ?? "The address-family route inventory is incomplete or mismatched.");
        if (availability == IpFamilyAvailability.Unknown)
            return Unknown("The IP family's usability is unknown or conflicts with its route evidence.");
        if (tunnel.InterfaceIndex is not { } index || index == 0 ||
            tunnel.InterfaceGuid is not { } guid || guid == Guid.Empty ||
            tunnel.OperationalStatus != OperationalStatus.Up)
            return Unknown("The detected tunnel has no confirmed interface GUID/index and operational state.");
        if (evidence.Any(x => x.InterfaceIndex == index && x.InterfaceGuid != guid))
        {
            // A stale identity must not masquerade as proven family absence.
            availability = IpFamilyAvailability.Unknown;
            return Unknown("Route interface identity does not match the detected tunnel; the interface may have changed.");
        }
        if (availability == IpFamilyAvailability.Unavailable)
            return Unknown("The complete route snapshot has no usable non-loopback interfaces for this IP family.");

        var bits = family == AddressFamily.InterNetwork ? 32 : 128;
        var routes = new List<RouteRange>();
        foreach (var row in evidence)
        {
            if (row.InterfaceUsable == false || row.ValidLifetime == 0 || row.IsLoopback) continue;
            if (row.PrefixAddress?.AddressFamily != family || row.NextHop?.AddressFamily != family ||
                row.PrefixLength > bits || row.InterfaceIndex == 0)
                return Unknown("A usable route has invalid address-family, prefix or interface evidence.");
            if (row.PrefixLength == 0 && row.DefaultRoutesEnabled == false) continue;
            if (row.InterfaceUsable is null || row.EffectiveMetric is null ||
                (row.PrefixLength == 0 && row.DefaultRoutesEnabled is null))
                return Unknown("A usable route has an unknown interface state, metric or default-route eligibility.");
            var range = Range(row.PrefixAddress, row.PrefixLength, bits);
            if (Number(row.PrefixAddress) != range.Start)
                return Unknown("A usable route has a noncanonical network prefix.");
            routes.Add(new(row, range.Start, range.End));
        }
        if (!routes.Any(x => x.Row.InterfaceIndex == index))
            return Unknown("No usable routes are associated with this detected VPN interface.");

        // /1 routes outrank /0 regardless of metric. Require coverage of both
        // halves, not an installed default or a few sampled public destinations.
        var broad = routes.Where(x => x.Row.PrefixLength <= 1).ToArray();
        var halves = new[] { Winner(broad, BigInteger.Zero), Winner(broad, BigInteger.One << (bits - 1)) };
        if (halves.Any(x => x.Length == 0))
            return Unknown("Usable /0 or paired /1 general routes do not cover both address-space halves.");
        if (halves.Any(x => x.Contains(index) && x.Length > 1))
            return Unknown("VPN and other interfaces tie for an effective general route.");
        var ownsGeneral = halves.All(x => x.Length == 1 && x[0] == index);

        var local = LocalRanges(family, bits);
        var excludedInternet = local.Concat(PrivateRanges(family, bits)).ToArray();
        var boundaries = new SortedSet<BigInteger> { BigInteger.Zero, BigInteger.One << bits };
        foreach (var range in routes.Select(x => new AddressRange(x.Start, x.End)).Concat(excludedInternet))
        {
            boundaries.Add(range.Start);
            boundaries.Add(range.End);
        }
        var intervals = boundaries.ToArray();
        var effectiveVpnPrefix = false;
        var publicBypass = false;
        // Every route starts/ends at a boundary. Within each interval, longest
        // prefix and combined metric winners are identical for every address.
        for (var i = 0; i < intervals.Length - 1; i++)
        {
            var point = intervals[i];
            var winners = Winner(routes, point);
            var internet = !excludedInternet.Any(x => x.Contains(point));
            if (winners.Contains(index) && winners.Length > 1)
                return Unknown("VPN and another interface tie for an effective routed prefix.");
            var vpnWins = winners.Length == 1 && winners[0] == index;
            if (vpnWins && !local.Any(x => x.Contains(point))) effectiveVpnPrefix = true;
            if (internet && !vpnWins) publicBypass = true;
        }
        if (!effectiveVpnPrefix)
            return Unknown("The detected VPN has no effective forwarding prefixes after competing routes are compared.");
        if (!ownsGeneral)
            return Result(VpnRoutingMode.SplitTunnel,
                "The VPN has effective routed prefixes, while another interface owns at least one general-route range.");
        if (publicBypass)
            return Unknown("The VPN owns general routes but a more-specific public route bypasses it; a peer exception cannot be distinguished here.");
        return Result(VpnRoutingMode.FullTunnel,
            "The VPN wins both general-route halves and all public forwarding ranges by longest prefix and combined route/interface metric.");
    }

    private static IpFamilyAvailability FamilyAvailability(VpnRouteInventory inventory,
        IReadOnlyList<VpnRouteEvidence> routes, AddressFamily family)
    {
        // Absence is meaningful only after a successful, matching family read.
        // Failed queries stay unknown; no VPN routes alone says nothing about
        // whether physical interfaces can still carry this family.
        if (!inventory.IsComplete || inventory.AddressFamily != family)
            return IpFamilyAvailability.Unknown;
        var bits = family == AddressFamily.InterNetwork ? 32 : 128;
        if (routes.Any(x => x.PrefixAddress?.AddressFamily != family || x.NextHop?.AddressFamily != family ||
            x.PrefixLength > bits || x.InterfaceIndex == 0 ||
            Number(x.PrefixAddress) != Range(x.PrefixAddress, x.PrefixLength, bits).Start))
            return IpFamilyAvailability.Unknown;
        var forwarding = routes.Where(x => !x.IsLoopback).ToArray();
        var usable = forwarding.Any(x => x.InterfaceUsable == true);
        var uncertain = forwarding.Any(x => x.InterfaceUsable is null);
        if (inventory.Availability == IpFamilyAvailability.Unavailable)
            return usable || uncertain ? IpFamilyAvailability.Unknown : IpFamilyAvailability.Unavailable;
        if (usable || inventory.Availability == IpFamilyAvailability.Usable)
            return IpFamilyAvailability.Usable;
        return uncertain ? IpFamilyAvailability.Unknown : IpFamilyAvailability.Unavailable;
    }

    private static uint[] Winner(IEnumerable<RouteRange> routes, BigInteger destination)
    {
        var matching = routes.Where(x => x.Start <= destination && destination < x.End).ToArray();
        if (matching.Length == 0) return Array.Empty<uint>();
        var prefix = matching.Max(x => x.Row.PrefixLength);
        var longest = matching.Where(x => x.Row.PrefixLength == prefix).ToArray();
        var metric = longest.Min(x => x.Row.EffectiveMetric!.Value);
        return longest.Where(x => x.Row.EffectiveMetric == metric)
            .Select(x => x.Row.InterfaceIndex).Distinct().OrderBy(x => x).ToArray();
    }

    private static BigInteger Number(IPAddress address) => new(address.GetAddressBytes(), isUnsigned: true, isBigEndian: true);
    private static AddressRange Range(IPAddress address, int prefix, int bits)
    {
        var width = BigInteger.One << (bits - prefix);
        var start = Number(address) / width * width;
        return new(start, start + width);
    }

    private static AddressRange[] LocalRanges(AddressFamily family, int bits) => Ranges(bits,
        family == AddressFamily.InterNetwork
            ? new[] { "0.0.0.0/8", "127.0.0.0/8", "169.254.0.0/16", "224.0.0.0/4", "240.0.0.0/4" }
            : new[] { "::/128", "::1/128", "fe80::/10", "ff00::/8" });

    private static AddressRange[] PrivateRanges(AddressFamily family, int bits) => Ranges(bits,
        family == AddressFamily.InterNetwork
            ? new[] { "10.0.0.0/8", "100.64.0.0/10", "172.16.0.0/12", "192.168.0.0/16" }
            : new[] { "fc00::/7" });

    private static AddressRange[] Ranges(int bits, string[] prefixes) => prefixes.Select(prefix =>
    {
        var parts = prefix.Split('/');
        return Range(IPAddress.Parse(parts[0]), int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), bits);
    }).ToArray();

    private sealed record RouteRange(VpnRouteEvidence Row, BigInteger Start, BigInteger End);
    private sealed record AddressRange(BigInteger Start, BigInteger End)
    {
        internal bool Contains(BigInteger address) => Start <= address && address < End;
    }
}
