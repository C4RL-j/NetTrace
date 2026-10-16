namespace NetTrace.Models;

public enum VpnTunnelStatus { Unknown, Inactive, Active }
public enum InternetRouteKind { Unknown, Direct, Vpn, Unavailable }
public enum VpnTransportFamily { Unknown, Inactive, IPv4, IPv6, Mixed }

// Effective local general-route context; not a reachability or all-traffic encryption claim.
// RouteKind summarizes VPN participation; presentation uses the independent family fields.
public sealed record VpnRouteSnapshot(
    VpnTunnelStatus Status,
    InternetRouteKind RouteKind,
    string? InterfaceName,
    string? InterfaceDescription,
    DateTimeOffset? CheckedAtUtc,
    string Reason)
{
    public InternetRouteKind IPv4RouteKind { get; init; } = InternetRouteKind.Unknown;
    public InternetRouteKind IPv6RouteKind { get; init; } = InternetRouteKind.Unknown;
    public VpnTransportFamily TransportFamily { get; init; } = VpnTransportFamily.Unknown;
    public string TransportReason { get; init; } = "The VPN outer transport has not been established.";

    // Connected tunnels include private/split routes and connected RAS sessions.
    // This is independent of general internet-route ownership and reachability.
    public IReadOnlyList<string> ConnectedTunnelNames { get; init; } = Array.Empty<string>();
    public bool HasActiveTunnel => ConnectedTunnelNames.Count > 0;

    public static VpnRouteSnapshot Unknown { get; } = new(VpnTunnelStatus.Unknown,
        InternetRouteKind.Unknown, null, null, null, "Local internet routing has not been checked.");
}

// The existing history classification and current route context share one local observation.
public sealed record VpnRouteObservation(VpnRoutingState Routing, VpnRouteSnapshot Snapshot);
