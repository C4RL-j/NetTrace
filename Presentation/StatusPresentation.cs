using NetTrace.Models;

namespace NetTrace.Presentation;

// Resource selection keeps independent observations independent. Colors supplement text.
public static class StatusPresentation
{
    public const string PositiveBrushKey = "StatusPositiveBrush";
    public const string CautionBrushKey = "StatusCautionBrush";
    public const string NegativeBrushKey = "StatusNegativeBrush";
    public const string InfoBrushKey = "StatusInfoBrush";
    public const string NeutralBrushKey = "StatusNeutralBrush";

    public const string GeneralHealthExplanation = "ICMP replies can be blocked; ICMP quality does not determine internet availability. Reachability is checked separately using DNS and HTTPS.";

    public static string ForReachability(InternetReachabilityStatus status) => status switch
    {
        InternetReachabilityStatus.Available => PositiveBrushKey,
        InternetReachabilityStatus.Limited => CautionBrushKey,
        InternetReachabilityStatus.Unavailable => NegativeBrushKey,
        _ => NeutralBrushKey
    };

    public static string ForIcmpQuality(IcmpQuality quality) => quality switch
    {
        IcmpQuality.Excellent or IcmpQuality.Good => PositiveBrushKey,
        IcmpQuality.Fair => CautionBrushKey,
        IcmpQuality.Poor => NegativeBrushKey,
        IcmpQuality.Measuring => InfoBrushKey,
        _ => NeutralBrushKey
    };

    public static string ForVpn(VpnTunnelStatus status) =>
        status == VpnTunnelStatus.Active ? InfoBrushKey : NeutralBrushKey;

    public static string ForRoute(InternetRouteKind route) =>
        route == InternetRouteKind.Vpn ? InfoBrushKey : NeutralBrushKey;

    public static string ForAdapter(string status) => status switch
    {
        "Connected" => PositiveBrushKey,
        "Disconnected" => NegativeBrushKey,
        _ => NeutralBrushKey
    };

    public static string ForMonitoring(MonitoringSnapshot snapshot) =>
        snapshot.IsSuspended || !snapshot.IsRunning ? NeutralBrushKey :
        snapshot.Status == "Monitoring" ? PositiveBrushKey : NegativeBrushKey;

    public static string HealthExplanation(VpnTunnelStatus vpn, InternetReachabilityStatus reachability,
        IcmpQuality quality)
    {
        if (vpn != VpnTunnelStatus.Active || quality != IcmpQuality.Unavailable)
            return GeneralHealthExplanation;
        return reachability == InternetReachabilityStatus.Available
            ? "ICMP may be blocked or filtered by the active VPN. Internet reachability remains available. Reachability is checked separately using DNS and HTTPS."
            : "ICMP may be blocked or filtered by the active VPN. ICMP quality does not determine internet availability. Reachability is checked separately using DNS and HTTPS.";
    }
}
