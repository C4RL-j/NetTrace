using NetTrace.Models;

namespace NetTrace.Presentation;

public enum IcmpQuality
{
    Excellent,
    Good,
    Fair,
    Poor,
    Measuring,
    Unavailable,
}

public sealed record IcmpQualitySummary(
    IcmpQuality Quality,
    int SampleCount,
    double? RecentLatencyMs,
    double? RecentJitterMs,
    double? RecentPacketLossPercent);

// Interprets the existing completed-cycle snapshot; owns no rolling history or worker.
// ICMP quality does not determine whether the internet is available.
public static class IcmpQualityEvaluator
{
    public const int WindowSize = 12;
    public const int MinimumCompletedCycles = 3;

    public const double ExcellentLatencyMaximumMs = 50;
    public const double GoodLatencyMaximumMs = 100;
    public const double FairLatencyMaximumMs = 200;
    public const double ExcellentJitterMaximumMs = 10;
    public const double GoodJitterMaximumMs = 20;
    public const double FairJitterMaximumMs = 40;
    public const double ExcellentLossMaximumPercent = 0;
    public const double GoodLossMaximumPercent = 1;
    public const double FairLossMaximumPercent = 5;

    public static IcmpQualitySummary Evaluate(IReadOnlyList<IcmpProbeResult> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var count = Math.Min(samples.Count, WindowSize);
        var latency = new MetricMean();
        var jitter = new MetricMean();
        var loss = new MetricMean();
        // Observation order is authoritative, including after a wall-clock rollback.
        for (var index = samples.Count - count; index < samples.Count; index++)
        {
            var sample = samples[index];
            if (sample.SuccessfulReplies > 0 && IsValidMilliseconds(sample.AverageLatencyMs))
                latency.Add(sample.AverageLatencyMs!.Value);
            if (sample.SuccessfulReplies >= 2 && IsValidMilliseconds(sample.JitterMs))
                jitter.Add(sample.JitterMs!.Value);
            if (double.IsFinite(sample.PacketLossPercent))
                loss.Add(Math.Clamp(sample.PacketLossPercent, 0, 100));
        }

        var recentLatency = latency.Value;
        var recentJitter = jitter.Value;
        var recentLoss = loss.Value;
        IcmpQuality quality;
        if (count < MinimumCompletedCycles)
            quality = IcmpQuality.Measuring;
        else if (recentLatency is null)
            quality = IcmpQuality.Unavailable;
        else
        {
            quality = Classify(recentLatency.Value, ExcellentLatencyMaximumMs,
                GoodLatencyMaximumMs, FairLatencyMaximumMs);
            if (recentJitter is { } jitterValue)
                quality = Worse(quality, Classify(jitterValue, ExcellentJitterMaximumMs,
                    GoodJitterMaximumMs, FairJitterMaximumMs));
            if (recentLoss is { } lossValue)
                quality = Worse(quality, Classify(lossValue, ExcellentLossMaximumPercent,
                    GoodLossMaximumPercent, FairLossMaximumPercent));
        }

        return new IcmpQualitySummary(quality, count, recentLatency, recentJitter, recentLoss);
    }

    private static bool IsValidMilliseconds(double? value) =>
        value is { } milliseconds && double.IsFinite(milliseconds) && milliseconds >= 0;

    private static IcmpQuality Classify(double value, double excellentMaximum,
        double goodMaximum, double fairMaximum) =>
        value <= excellentMaximum ? IcmpQuality.Excellent :
        value <= goodMaximum ? IcmpQuality.Good :
        value <= fairMaximum ? IcmpQuality.Fair : IcmpQuality.Poor;

    private static IcmpQuality Worse(IcmpQuality first, IcmpQuality second) =>
        (IcmpQuality)Math.Max((int)first, (int)second);

    private struct MetricMean
    {
        private int _count;
        private double _mean;
        public readonly double? Value => _count == 0 ? null : _mean;

        public void Add(double value)
        {
            // A running mean avoids overflowing a sum of otherwise valid finite values.
            _mean += (value - _mean) / ++_count;
        }
    }
}
