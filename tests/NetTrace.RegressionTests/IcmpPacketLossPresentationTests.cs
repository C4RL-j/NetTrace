using NetTrace.Models;
using NetTrace.Presentation;

internal static class IcmpPacketLossPresentationTests
{
    public static (string Name, Action Run)[] Cases =>
    [
        ("Available internet with zero ICMP replies reports unavailable packet loss", () => ReachableWithoutReplies(InternetReachabilityStatus.Available)),
        ("Limited internet with zero ICMP replies reports unavailable packet loss", () => ReachableWithoutReplies(InternetReachabilityStatus.Limited)),
        ("Unavailable packet loss explains possible active VPN filtering", ActiveVpnExplanation),
        ("Unavailable packet loss avoids an active VPN explanation when none is active", InactiveVpnExplanation),
        ("Three of four successful ICMP replies retain 25 percent loss in every reachability state", () => PartialLoss(3, 25)),
        ("Two of four successful ICMP replies retain 50 percent loss in every reachability state", () => PartialLoss(2, 50)),
        ("Unavailable internet with zero ICMP replies retains 100 percent packet loss", () => FailedWithoutReachability(InternetReachabilityStatus.Unavailable)),
        ("Checking reachability preserves existing zero-reply packet-loss presentation", () => FailedWithoutReachability(InternetReachabilityStatus.Checking)),
        ("Reachable zero-reply histories contain unavailable points instead of false full-loss bars", UnavailableChartHistory),
        ("Mixed reachable packet-loss history preserves partial loss, cycle slots and raw latency and jitter", MixedChartHistory),
        ("Reachability changes reproject packet loss without rewriting completed ICMP measurements", ReachabilityReprojection),
    ];

    private static readonly DateTimeOffset Origin =
        new(2026, 10, 14, 5, 0, 0, TimeSpan.Zero);

    private static void ReachableWithoutReplies(InternetReachabilityStatus reachability)
    {
        var result = Sample(0, 0);
        Check(IcmpPacketLossPresentation.IsUnavailable(result, reachability), "Zero replies cannot measure loss when independent internet probes are reachable.");
        Equal("Unavailable", IcmpPacketLossPresentation.Text(result, reachability), "beginner-friendly packet loss value");
        Equal<double?>(null, IcmpPacketLossPresentation.Percent(result, reachability), "no misleading numeric percentage");
        Equal("Packet loss can't be measured for these samples.",
            IcmpPacketLossPresentation.Explanation(result, reachability, VpnTunnelStatus.Inactive), "neutral explanation");
        Equal(100d, result.PacketLossPercent, "raw failed-reply percentage remains available for independent ICMP evaluation");
        Equal(0, result.SuccessfulReplies, "raw successful replies");
        Equal(4, result.FailedReplies, "raw failed replies");
    }

    private static void ActiveVpnExplanation()
    {
        foreach (var reachability in ReachableStates())
        {
            var result = Sample(0, 0);
            Equal("Unavailable", IcmpPacketLossPresentation.Text(result, reachability), "VPN does not manufacture numeric loss");
            Equal("Packet loss can't be measured for these samples. Ping replies may be blocked by the active VPN.",
                IcmpPacketLossPresentation.Explanation(result, reachability, VpnTunnelStatus.Active), "qualified VPN explanation");
        }
    }

    private static void InactiveVpnExplanation()
    {
        foreach (var vpn in new[] { VpnTunnelStatus.Inactive, VpnTunnelStatus.Unknown })
        foreach (var reachability in ReachableStates())
            Equal("Packet loss can't be measured for these samples.",
                IcmpPacketLossPresentation.Explanation(Sample(0, 0), reachability, vpn), "unconfirmed VPN is not described as active");
    }

    private static void PartialLoss(int replies, double percent)
    {
        var result = Sample(0, replies);
        foreach (var reachability in Enum.GetValues<InternetReachabilityStatus>())
        {
            Check(!IcmpPacketLossPresentation.IsUnavailable(result, reachability), "Successful replies retain measurable partial packet loss.");
            Equal<double?>(percent, IcmpPacketLossPresentation.Percent(result, reachability), "partial percentage");
            Equal($"{percent:0.##}%", IcmpPacketLossPresentation.Text(result, reachability), "partial packet loss text");
            Equal(string.Empty, IcmpPacketLossPresentation.Explanation(result, reachability, VpnTunnelStatus.Active), "measurable loss needs no unavailable explanation");
            Equal<double?>(percent, IcmpStabilityChartData.Create(new[] { result }, reachability).PacketLossPoints.Single().Percent,
                "partial loss remains renderable across independent reachability states");
        }
    }

    private static void FailedWithoutReachability(InternetReachabilityStatus reachability)
    {
        var result = Sample(0, 0);
        Check(!IcmpPacketLossPresentation.IsUnavailable(result, reachability), "The reachable zero-reply exception does not apply.");
        Equal("100%", IcmpPacketLossPresentation.Text(result, reachability), "completed failed ICMP cycle");
        Equal<double?>(100, IcmpPacketLossPresentation.Percent(result, reachability), "full loss stays numeric");
        Equal<double?>(100, IcmpStabilityChartData.Create(new[] { result }, reachability).PacketLossPoints.Single().Percent,
            "full loss stays renderable without independently reachable internet");
        Equal(string.Empty, IcmpPacketLossPresentation.Explanation(result, reachability, VpnTunnelStatus.Active),
            "numeric loss does not use the unavailable packet-loss explanation");
    }

    private static void UnavailableChartHistory()
    {
        var samples = Enumerable.Range(0, 4).Select(index => Sample(index, 0)).ToArray();
        foreach (var reachability in ReachableStates())
        {
            var data = IcmpStabilityChartData.Create(samples, reachability);
            Equal(samples.Length, data.Count, "completed cycles remain present");
            Check(data.PacketLossPoints.All(point => point.Percent is null), "Reachable zero-reply samples must never generate a 100-percent bar or fake zero loss.");
            Equal(0, data.LatencySegments.Count, "failed replies add no latency");
            Equal(0, data.JitterSegments.Count, "failed replies add no jitter");
            for (var index = 0; index < samples.Length; index++)
            {
                Equal(index / 3d, data.PacketLossPoints[index].Position, "unavailable samples keep their cycle slot");
                Check(ReferenceEquals(samples[index], data.Samples[index]), "Chart presentation retains each original raw sample.");
                Equal(100d, samples[index].PacketLossPercent, "raw zero-reply loss remains unchanged");
            }
        }
    }

    private static void MixedChartHistory()
    {
        var samples = new[] { Sample(0, 0), Sample(1, 3), Sample(2, 2), Sample(3, 4) };
        var baseline = IcmpStabilityChartData.Create(samples);
        foreach (var reachability in ReachableStates())
        {
            var data = IcmpStabilityChartData.Create(samples, reachability);
            Check(data.PacketLossPoints.Select(point => point.Percent).SequenceEqual(new double?[] { null, 25, 50, 0 }),
                "Mixed history suppresses only unmeasurable zero-reply loss and keeps real partial and zero loss.");
            Check(data.PacketLossPoints.Select(point => point.Position).SequenceEqual(baseline.PacketLossPoints.Select(point => point.Position)),
                "Missing loss does not compress the shared cycle positions.");
            SameMilliseconds(baseline, data);
            for (var index = 0; index < samples.Length; index++)
            {
                Check(ReferenceEquals(samples[index], data.Samples[index]), "Contextual loss presentation preserves immutable raw sample identity.");
                Equal(100d - samples[index].SuccessfulReplies * 25d, data.Samples[index].PacketLossPercent, "raw packet loss remains unchanged");
            }
        }
    }

    private static void ReachabilityReprojection()
    {
        var samples = new[] { Sample(0, 0), Sample(1, 3) };
        var unavailable = IcmpStabilityChartData.Create(samples, InternetReachabilityStatus.Unavailable);
        var reachable = IcmpStabilityChartData.Create(samples, InternetReachabilityStatus.Available);
        var limited = IcmpStabilityChartData.Create(samples, InternetReachabilityStatus.Limited);
        Equal<double?>(100, unavailable.PacketLossPoints[0].Percent, "unreachable context allows full loss");
        Equal<double?>(null, reachable.PacketLossPoints[0].Percent, "available context hides misleading full loss without a new cycle");
        Equal<double?>(null, limited.PacketLossPoints[0].Percent, "limited context also hides misleading full loss");
        Equal<double?>(25, reachable.PacketLossPoints[1].Percent, "reachability change preserves measured partial loss");
        SameMilliseconds(unavailable, reachable);
        SameMilliseconds(unavailable, limited);
        Check(unavailable.Samples.SequenceEqual(reachable.Samples) && unavailable.Samples.SequenceEqual(limited.Samples),
            "Reprojection shares the same completed observations without adding historical reachability or altering measurements.");
    }

    private static InternetReachabilityStatus[] ReachableStates() =>
        [InternetReachabilityStatus.Available, InternetReachabilityStatus.Limited];

    private static IcmpProbeResult Sample(int cycle, int successes) =>
        new(Origin.AddSeconds(cycle * 15), "1.1.1.1", successes, 4 - successes,
            successes > 0 ? 30 + cycle : null, (4 - successes) * 25,
            successes >= 2 ? 5 + cycle : null);

    private static void SameMilliseconds(IcmpStabilityChartData expected, IcmpStabilityChartData actual)
    {
        Equal(expected.MaxMilliseconds, actual.MaxMilliseconds, "latency/jitter scale");
        Equal(expected.LatencySegments.Count, actual.LatencySegments.Count, "latency gaps");
        Equal(expected.JitterSegments.Count, actual.JitterSegments.Count, "jitter gaps");
        for (var index = 0; index < expected.LatencySegments.Count; index++)
            Check(expected.LatencySegments[index].SequenceEqual(actual.LatencySegments[index]), "Reachability does not change latency values or positions.");
        for (var index = 0; index < expected.JitterSegments.Count; index++)
            Check(expected.JitterSegments[index].SequenceEqual(actual.JitterSegments[index]), "Reachability does not change jitter values or positions.");
    }

    private static void Equal<T>(T expected, T actual, string description)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{description}: expected {expected}, got {actual}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
