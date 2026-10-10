using NetTrace.Models;

namespace NetTrace.Presentation;

// Presents the cached local route and existing physical identity without discovery.
public sealed record VpnRoutePresentation(string VpnStatus, string IPv4Route, string IPv6Route,
    string PhysicalAdapter, string DetailToolTip)
{
    public static VpnRoutePresentation Create(VpnRouteSnapshot route, NetworkIdentity? physicalNetwork,
        IReadOnlyList<AdapterInfo> adapters)
    {
        var status = route.Status switch
        {
            VpnTunnelStatus.Active => "Active",
            VpnTunnelStatus.Inactive => "Inactive",
            _ => "Unknown"
        };
        var physical = physicalNetwork is null ? "Unavailable" :
            adapters.FirstOrDefault(adapter => string.Equals(adapter.Id, physicalNetwork.AdapterId,
                StringComparison.OrdinalIgnoreCase))?.Name ?? physicalNetwork.TypeDisplayName;
        if (string.IsNullOrWhiteSpace(physical)) physical = "Unavailable";
        var tooltip = string.IsNullOrWhiteSpace(route.Reason)
            ? "Local Windows route observation. VPN state does not determine internet availability."
            : route.Reason;
        if (!string.IsNullOrWhiteSpace(route.InterfaceName)) tooltip += $"\nVPN interface: {route.InterfaceName}";
        if (!string.IsNullOrWhiteSpace(route.InterfaceDescription) && route.InterfaceDescription != route.InterfaceName)
            tooltip += $"\n{route.InterfaceDescription}";
        return new(status, RouteName(route.IPv4RouteKind), RouteName(route.IPv6RouteKind), physical, tooltip);
    }

    private static string RouteName(InternetRouteKind kind) => kind switch
    {
        InternetRouteKind.Vpn => "VPN",
        InternetRouteKind.Direct => "Direct",
        InternetRouteKind.Unavailable => "Unavailable",
        _ => "Unknown"
    };
}
