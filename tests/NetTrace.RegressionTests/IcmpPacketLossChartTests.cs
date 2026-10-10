using NetTrace.Models;
using NetTrace.Presentation;

internal static class IcmpPacketLossChartTests
{
    public static (string Name, Action Run)[] Cases =>
    [
        ("Empty ICMP history has no invented packet-loss bars", EmptyHistory),
        ("A single zero-loss cycle remains a valid point at the shared newest position", OneCycle),
        ("Packet-loss history preserves percentages including partial losses", LossPercentages),
        ("An all-failed cycle plots 100 percent loss beside latency and jitter gaps", AllFailedCycle),
        ("Finite out-of-range packet loss is bounded for presentation without rewriting samples", BoundFiniteValues),
        ("Nonfinite packet loss is unavailable rather than a fake zero-loss cycle", NonfiniteValues),
        ("Packet-loss history below capacity retains every observed cycle", BelowCapacity),
        ("Exactly 60 cycles occupy the same packet-loss and latency history", FullCapacity),
        ("Packet-loss rollover drops only the oldest cycles across all series", Rollover),
        ("Packet-loss history follows observed order after a clock rollback", ObservationOrder),
        ("Packet-loss points retain the same cycle slots through latency and jitter gaps", SharedPositions),
        ("Packet-loss presentation snapshots reject mutation of samples, points and nested lines", ImmutableSnapshots),
        ("Packet-loss percentage values cannot distort the milliseconds scale", IndependentScale),
    ];

    private static readonly DateTimeOffset Origin =
        new(2026, 10, 11, 5, 0, 0, TimeSpan.Zero);

    private static void EmptyHistory()
    {
        var data = IcmpStabilityChartData.Create(Array.Empty<IcmpProbeResult>());
        Equal(0, data.Count, "empty completed-cycle history");
        Equal(0, data.PacketLossPoints.Count, "no fabricated loss measurements");
        Equal(0, data.LatencySegments.Count, "no fabricated latency line");
        Equal(0, data.JitterSegments.Count, "no fabricated jitter line");
    }

    private static void OneCycle()
    {
        var sample = Sample(1, 0);
        var data = IcmpStabilityChartData.Create(new[] { sample });
        Equal(1, data.Count, "one shared completed cycle");
        Check(ReferenceEquals(sample, data.Samples[0]), "The chart consumes the original immutable result.");
        AssertPoint(data.PacketLossPoints.Single(), 1, 0, "zero-loss newest cycle");
        Near(data.LatencySegments.Single().Single().Position,
            data.PacketLossPoints.Single().Position, "singleton latency and loss positions");
        Near(data.JitterSegments.Single().Single().Position,
            data.PacketLossPoints.Single().Position, "singleton jitter and loss positions");
    }

    private static void LossPercentages()
    {
        var percentages = new[] { 0.0, 25, 50, 75, 100, 12.5, 33.3 };
        var samples = percentages.Select((loss, index) => Sample(index, loss)).ToArray();
        var data = IcmpStabilityChartData.Create(samples);
        Equal(percentages.Length, data.PacketLossPoints.Count, "one loss point per completed cycle");
        for (var index = 0; index < percentages.Length; index++)
            AssertPoint(data.PacketLossPoints[index], (double)index / (percentages.Length - 1),
                percentages[index], "existing packet-loss percentage");
    }

    private static void AllFailedCycle()
    {
        var samples = new[]
        {
            Sample(0, 0),
            Sample(1, 100, latency: null, jitter: null, successes: 0, failures: 4),
            Sample(2, 0),
        };
        var data = IcmpStabilityChartData.Create(samples);
        Equal(3, data.Count, "the all-failed cycle is completed, not missing");
        Equal(3, data.PacketLossPoints.Count, "all completed cycles retain loss points");
        AssertPoint(data.PacketLossPoints[1], 0.5, 100, "failed cycle's full loss");
        Equal(2, data.LatencySegments.Count, "latency gaps at the failed cycle");
        Equal(2, data.JitterSegments.Count, "jitter gaps at the failed cycle");
        Check(data.LatencySegments.SelectMany(segment => segment)
                .Concat(data.JitterSegments.SelectMany(segment => segment))
                .All(point => point.Position != data.PacketLossPoints[1].Position),
            "The 100-percent loss bar shares the missing latency/jitter slot without inventing zero milliseconds.");
    }

    private static void BoundFiniteValues()
    {
        var raw = new[] { -double.MaxValue, -1, 0, 37.5, 100, 101, double.MaxValue };
        var expected = new[] { 0.0, 0, 0, 37.5, 100, 100, 100 };
        var samples = raw.Select((loss, index) => Sample(index, loss)).ToArray();
        var data = IcmpStabilityChartData.Create(samples);
        for (var index = 0; index < expected.Length; index++)
        {
            AssertPoint(data.PacketLossPoints[index], (double)index / (expected.Length - 1),
                expected[index], "bounded display percentage");
            Equal(raw[index], data.Samples[index].PacketLossPercent, "raw result remains unchanged");
        }
    }

    private static void NonfiniteValues()
    {
        var raw = new[] { 25.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity, 75 };
        var data = IcmpStabilityChartData.Create(raw.Select((loss, index) => Sample(index, loss)).ToArray());
        Equal(raw.Length, data.PacketLossPoints.Count, "invalid values retain their completed-cycle slots");
        AssertPoint(data.PacketLossPoints[0], 0, 25, "valid oldest percentage");
        for (var index = 1; index <= 3; index++)
        {
            Near((double)index / (raw.Length - 1), data.PacketLossPoints[index].Position,
                "unavailable percentage retains position");
            Check(data.PacketLossPoints[index].Percent is null,
                "Nonfinite loss must not appear as healthy zero packet loss.");
        }
        AssertPoint(data.PacketLossPoints[^1], 1, 75, "later valid loss retains newest position");
        Equal(raw.Length, data.LatencySegments.Single().Count,
            "a bad loss field cannot remove otherwise valid latency measurements");
        Equal(raw.Length, data.JitterSegments.Single().Count,
            "a bad loss field cannot remove otherwise valid jitter measurements");
    }

    private static void BelowCapacity() => AssertSharedRetention(MakeSamples(17), 0);

    private static void FullCapacity() => AssertSharedRetention(MakeSamples(60), 0);

    private static void Rollover()
    {
        foreach (var count in new[] { 61, 65, 121 })
            AssertSharedRetention(MakeSamples(count), count - 60);
    }

    private static void ObservationOrder()
    {
        var samples = new[] { Sample(2, 25), Sample(1, 50), Sample(3, 75) };
        var data = IcmpStabilityChartData.Create(samples);
        for (var index = 0; index < samples.Length; index++)
        {
            Check(ReferenceEquals(samples[index], data.Samples[index]),
                "Clock rollback cannot sort an observation into another cycle slot.");
            AssertPoint(data.PacketLossPoints[index], index / 2.0, samples[index].PacketLossPercent,
                "observed packet-loss order");
        }
        Check(data.Samples[1].TimestampUtc < data.Samples[0].TimestampUtc,
            "The fixture exercises a real timestamp rollback while retaining observation order.");
    }

    private static void SharedPositions()
    {
        var samples = new[]
        {
            Sample(0, 0),
            Sample(1, 75, latency: 33, jitter: null, successes: 1, failures: 3),
            Sample(2, 100, latency: null, jitter: null, successes: 0, failures: 4),
            Sample(3, double.NaN, latency: null, jitter: 2),
            Sample(4, 25),
        };
        var data = IcmpStabilityChartData.Create(samples);
        Equal(samples.Length, data.Count, "one shared history for all three series");
        Equal(samples.Length, data.PacketLossPoints.Count, "loss keeps every completed slot");
        var latency = data.LatencySegments.SelectMany(segment => segment).ToArray();
        var jitter = data.JitterSegments.SelectMany(segment => segment).ToArray();
        Check(latency.Select(point => point.Position).SequenceEqual(new[] { 0.0, 0.25, 1 }),
            "Latency retains its original slots instead of compressing across gaps.");
        Check(jitter.Select(point => point.Position).SequenceEqual(new[] { 0.0, 0.75, 1 }),
            "Jitter retains its independently unavailable slots.");
        foreach (var point in latency.Concat(jitter))
            Check(data.PacketLossPoints.Any(loss => loss.Position == point.Position),
                "All rendered measurements align to a completed cycle in the loss chart.");
        AssertPoint(data.PacketLossPoints[2], 0.5, 100, "complete failure in a latency/jitter gap");
        Check(data.PacketLossPoints[3].Percent is null, "Missing loss does not hide valid jitter.");
    }

    private static void ImmutableSnapshots()
    {
        var samples = MakeSamples(3);
        var original = samples[0];
        var data = IcmpStabilityChartData.Create(samples);
        samples[0] = Sample(99, 100);
        Check(ReferenceEquals(original, data.Samples[0]),
            "Replacing an input entry cannot rewrite an already displayed snapshot.");
        RejectMutation(data.Samples, samples[0], "completed sample snapshot");
        RejectMutation(data.PacketLossPoints, new IcmpPacketLossChartPoint(0, 100), "packet-loss points");
        RejectMutation(data.LatencySegments, Array.Empty<IcmpStabilityChartPoint>(), "latency segments");
        RejectMutation(data.JitterSegments, Array.Empty<IcmpStabilityChartPoint>(), "jitter segments");
        RejectMutation(data.LatencySegments[0], new IcmpStabilityChartPoint(0, 999), "nested latency points");
        RejectMutation(data.JitterSegments[0], new IcmpStabilityChartPoint(0, 999), "nested jitter points");
        AssertPoint(data.PacketLossPoints[0], 0, original.PacketLossPercent, "immutable original percentage");
    }

    private static void IndependentScale()
    {
        var zeroLoss = IcmpStabilityChartData.Create(new[] { Sample(1, 0, latency: 12, jitter: 3) });
        var fullLoss = IcmpStabilityChartData.Create(new[] { Sample(1, 100, latency: 12, jitter: 3) });
        Near(zeroLoss.MaxMilliseconds, fullLoss.MaxMilliseconds,
            "percentage values cannot enter the latency/jitter milliseconds scale");
        var noLatency = IcmpStabilityChartData.Create(new[]
        {
            Sample(1, 100, latency: null, jitter: null, successes: 0, failures: 4),
        });
        AssertPoint(noLatency.PacketLossPoints.Single(), 1, 100, "loss available without latency");
        Near(1, noLatency.MaxMilliseconds, "all-failed cycle cannot create a 100-ms fake scale");
    }

    private static IcmpProbeResult Sample(int cycle, double loss, double? latency = 42, double? jitter = 6,
        int successes = 4, int failures = 0) =>
        new(Origin.AddSeconds(cycle * 15), "1.1.1.1", successes, failures, latency, loss, jitter);

    private static IcmpProbeResult[] MakeSamples(int count) =>
        Enumerable.Range(1, count).Select(cycle => Sample(cycle, cycle % 101)).ToArray();

    private static void AssertSharedRetention(IReadOnlyList<IcmpProbeResult> samples, int offset)
    {
        var data = IcmpStabilityChartData.Create(samples);
        var count = samples.Count - offset;
        Equal(count, data.Count, "shared retained completed-cycle count");
        Equal(count, data.PacketLossPoints.Count, "one retained loss point per cycle");
        var latency = data.LatencySegments.Single();
        var jitter = data.JitterSegments.Single();
        Equal(count, latency.Count, "same latency window");
        Equal(count, jitter.Count, "same jitter window");
        for (var index = 0; index < count; index++)
        {
            Check(ReferenceEquals(samples[offset + index], data.Samples[index]),
                "Only the oldest cycles can be removed; the common snapshot remains oldest to newest.");
            var position = (double)index / (count - 1);
            AssertPoint(data.PacketLossPoints[index], position, samples[offset + index].PacketLossPercent,
                "retained loss point");
            Near(position, latency[index].Position, "aligned retained latency");
            Near(position, jitter[index].Position, "aligned retained jitter");
        }
    }

    private static void RejectMutation<T>(IReadOnlyList<T> items, T replacement, string description)
    {
        if (items is not IList<T> mutable) return;
        Check(mutable.IsReadOnly, $"{description} must be read-only.");
        var rejected = false;
        try { mutable[0] = replacement; }
        catch (NotSupportedException) { rejected = true; }
        Check(rejected, $"{description} must reject replacement.");
    }

    private static void AssertPoint(IcmpPacketLossChartPoint point, double position, double percent,
        string description)
    {
        Near(position, point.Position, $"{description} position");
        Check(point.Percent.HasValue, $"{description} must have a measured percentage.");
        Near(percent, point.Percent!.Value, $"{description} percent");
    }

    private static void Near(double expected, double actual, string description)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > 1e-10)
            throw new InvalidOperationException($"{description}: expected {expected}, got {actual}.");
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
