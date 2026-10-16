using NetTrace.Models;

namespace NetTrace.Presentation;

// Interpret raw ICMP loss using independently measured internet reachability.
public static class IcmpPacketLossPresentation
{
    public const string UnavailableMessage = "Packet loss can't be measured for these samples.";
    public const string VpnExplanation = "Ping replies may be blocked by the active VPN.";

    public static bool IsUnavailable(IcmpProbeResult result, InternetReachabilityStatus reachability) =>
        result.SuccessfulReplies == 0 && reachability is
            InternetReachabilityStatus.Available or InternetReachabilityStatus.Limited;

    public static double? Percent(IcmpProbeResult result, InternetReachabilityStatus reachability) =>
        IsUnavailable(result, reachability) || !double.IsFinite(result.PacketLossPercent)
            ? null : Math.Clamp(result.PacketLossPercent, 0, 100);

    public static string Text(IcmpProbeResult result, InternetReachabilityStatus reachability) =>
        Percent(result, reachability) is { } percent ? $"{percent:0.##}%" : "Unavailable";

    public static string Explanation(IcmpProbeResult result, InternetReachabilityStatus reachability,
        VpnTunnelStatus vpn) => !IsUnavailable(result, reachability) ? "" :
        vpn == VpnTunnelStatus.Active ? $"{UnavailableMessage} {VpnExplanation}" : UnavailableMessage;
}
