using System.Net;
using System.Net.Sockets;

namespace NetTrace.Models;

public enum VpnRoutingMode { Unverified, FullTunnel, SplitTunnel }

// Family usability is distinct from whether its VPN routing has been verified.
public enum IpFamilyAvailability { Unknown, Unavailable, Usable }

// A read-only route-table observation, including competing non-VPN interfaces.
public sealed record VpnRouteEvidence(
    uint InterfaceIndex,
    Guid? InterfaceGuid,
    IPAddress PrefixAddress,
    byte PrefixLength,
    IPAddress NextHop,
    uint RouteMetric,
    uint? InterfaceMetric,
    bool? InterfaceUsable,
    uint ValidLifetime = uint.MaxValue,
    bool IsLoopback = false,
    bool? DefaultRoutesEnabled = true)
{
    // Windows uses -1 when the route metric is unspecified, not a comparable cost.
    public ulong? EffectiveMetric => RouteMetric != uint.MaxValue && InterfaceMetric is { } metric
        ? (ulong)RouteMetric + metric : null;
    public string Prefix => $"{PrefixAddress}/{PrefixLength}";
}

public sealed record VpnRouteInventory(
    AddressFamily AddressFamily,
    IReadOnlyList<VpnRouteEvidence> Routes,
    bool IsComplete = true,
    string? Diagnostic = null,
    IpFamilyAvailability Availability = IpFamilyAvailability.Unknown);

public sealed record VpnFamilyRoutingResult(
    AddressFamily AddressFamily,
    VpnRoutingMode Mode,
    IReadOnlyList<VpnRouteEvidence> Routes,
    string Reason,
    IpFamilyAvailability Availability = IpFamilyAvailability.Unknown);

public sealed record VpnTunnelRoutingResult(
    VpnInterfaceInfo Tunnel,
    VpnRoutingMode Mode,
    VpnFamilyRoutingResult IPv4,
    VpnFamilyRoutingResult IPv6);

public sealed record VpnRoutingState(
    VpnState Detection,
    IReadOnlyList<VpnTunnelRoutingResult> Tunnels);
