using NetTrace.Models;
using NetTrace.Services;

namespace NetTrace.Presentation;

public sealed record IcmpStabilityChartPoint(double Position, double Milliseconds);
public sealed record IcmpPacketLossChartPoint(double Position, double? Percent);

// A bounded presentation snapshot, not a second rolling-history store.
public sealed class IcmpStabilityChartData
{
    public IReadOnlyList<IcmpProbeResult> Samples { get; }
    public int Count => Samples.Count;
    public double MaxMilliseconds { get; }
    public IReadOnlyList<IReadOnlyList<IcmpStabilityChartPoint>> LatencySegments { get; }
    public IReadOnlyList<IReadOnlyList<IcmpStabilityChartPoint>> JitterSegments { get; }
    public IReadOnlyList<IcmpPacketLossChartPoint> PacketLossPoints { get; }

    private IcmpStabilityChartData(IReadOnlyList<IcmpProbeResult> samples,
        IReadOnlyList<IReadOnlyList<IcmpStabilityChartPoint>> latencySegments,
        IReadOnlyList<IReadOnlyList<IcmpStabilityChartPoint>> jitterSegments,
        IReadOnlyList<IcmpPacketLossChartPoint> packetLossPoints,
        double maxMilliseconds)
    {
        Samples = samples;
        LatencySegments = latencySegments;
        JitterSegments = jitterSegments;
        PacketLossPoints = packetLossPoints;
        MaxMilliseconds = maxMilliseconds;
    }

    public static IcmpStabilityChartData Create(IReadOnlyList<IcmpProbeResult> samples,
        InternetReachabilityStatus reachability = InternetReachabilityStatus.Checking)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var count = Math.Min(samples.Count, IcmpMonitor.HistoryCapacity);
        var snapshot = new IcmpProbeResult[count];
        var offset = samples.Count - count;
        for (var i = 0; i < count; i++) snapshot[i] = samples[offset + i];

        var latency = CreateSegments(snapshot, sample => sample.SuccessfulReplies > 0
            ? sample.AverageLatencyMs : null);
        var jitter = CreateSegments(snapshot, sample => sample.SuccessfulReplies >= 2
            ? sample.JitterMs : null);
        var packetLoss = snapshot.Select((sample, index) => new IcmpPacketLossChartPoint(
            SamplePosition(index, count), IcmpPacketLossPresentation.Percent(sample, reachability))).ToArray();
        var max = latency.Concat(jitter).SelectMany(segment => segment)
            .Select(point => point.Milliseconds).DefaultIfEmpty(0).Max();
        return new IcmpStabilityChartData(Array.AsReadOnly(snapshot), latency, jitter,
            Array.AsReadOnly(packetLoss), NiceMaximum(max));
    }

    private static IReadOnlyList<IReadOnlyList<IcmpStabilityChartPoint>> CreateSegments(
        IReadOnlyList<IcmpProbeResult> samples, Func<IcmpProbeResult, double?> selectValue)
    {
        var segments = new List<IReadOnlyList<IcmpStabilityChartPoint>>();
        var current = new List<IcmpStabilityChartPoint>();
        for (var i = 0; i < samples.Count; i++)
        {
            if (selectValue(samples[i]) is { } value && double.IsFinite(value) && value >= 0)
            {
                // Keep every completed cycle's x-slot, including gaps. A single cycle is newest.
                var position = SamplePosition(i, samples.Count);
                current.Add(new IcmpStabilityChartPoint(position, value));
            }
            else if (current.Count > 0)
            {
                segments.Add(Array.AsReadOnly(current.ToArray()));
                current.Clear();
            }
        }
        if (current.Count > 0) segments.Add(Array.AsReadOnly(current.ToArray()));
        return Array.AsReadOnly(segments.ToArray());
    }

    private static double SamplePosition(int index, int count) => count == 1 ? 1 : (double)index / (count - 1);

    private static double NiceMaximum(double maximum)
    {
        if (maximum <= 0) return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(maximum)));
        if (magnitude == 0 || !double.IsFinite(magnitude)) return maximum;
        var normalized = maximum / magnitude;
        var step = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        var scale = step * magnitude;
        // Even extreme finite inputs must keep a finite, positive plotting scale.
        return double.IsFinite(scale) && scale >= maximum ? scale : maximum;
    }
}
