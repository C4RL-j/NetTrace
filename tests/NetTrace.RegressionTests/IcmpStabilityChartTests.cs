using NetTrace.Models;
using NetTrace.Presentation;

internal static class IcmpStabilityChartTests
{
    public static (string Name, Action Run)[] Cases =>
    [
        ("Empty ICMP history has no invented stability chart measurements", EmptyHistory),
        ("One completed ICMP cycle appears at the right edge of the stability chart", OneCycle),
        ("A partial stability history retains every completed cycle in order", BelowCapacity),
        ("Exactly 60 completed ICMP cycles fill the stability chart without trimming", FullCapacity),
        ("Stability chart rollover retains the newest 60 completed cycles only", Rollover),
        ("Stability chart order follows observations even after a clock rollback", ObservationOrder),
        ("A successful ICMP cycle plots its existing latency and jitter", SuccessfulCycle),
        ("Partial packet loss retains valid latency and jitter without a loss series", PartialPacketLoss),
        ("An all-failed ICMP cycle produces gaps rather than excellent zero measurements", AllFailedCycle),
        ("Missing latency and jitter split their stability series at the original cycle", MissingMeasurements),
        ("One successful reply plots latency but cannot invent a jitter point", InsufficientJitter),
        ("Legitimate zero-millisecond measurements remain visible chart points", ValidZero),
        ("Invalid negative and nonfinite ICMP measurements cannot enter chart lines", InvalidMeasurements),
        ("Latency and jitter share completed-cycle positions through missing measurements", SharedPositions),
        ("Stability chart scaling includes both series and stays finite", SharedScale),
        ("Stability chart snapshots and their line segments cannot mutate monitoring history", ImmutableSnapshots),
    ];

    private static readonly DateTimeOffset Origin =
        new(2026, 10, 11, 5, 0, 0, TimeSpan.Zero);

    private static void EmptyHistory()
    {
        var data = IcmpStabilityChartData.Create(Array.Empty<IcmpProbeResult>());
        Equal(0, data.Count, "empty chart count");
        Equal(0, data.Samples.Count, "empty chart history");
        Equal(0, data.LatencySegments.Count, "no latency lines");
        Equal(0, data.JitterSegments.Count, "no jitter lines");
        Near(1, data.MaxMilliseconds, "empty scale remains usable");
    }

    private static void OneCycle()
    {
        var sample = Sample(1, 42, 6);
        var data = IcmpStabilityChartData.Create(new[] { sample });
        Equal(1, data.Count, "one completed cycle");
        Check(ReferenceEquals(sample, data.Samples[0]), "Chart consumes the original immutable result.");
        AssertPoint(data.LatencySegments.Single().Single(), 1, 42, "singleton latency");
        AssertPoint(data.JitterSegments.Single().Single(), 1, 6, "singleton jitter");
    }

    private static void BelowCapacity()
    {
        var samples = MakeSamples(17);
        var data = IcmpStabilityChartData.Create(samples);
        Equal(17, data.Count, "all available history retained");
        AssertRetainedSamples(samples, data, 0);
        AssertContinuousPositions(data, 17);
    }

    private static void FullCapacity()
    {
        var samples = MakeSamples(60);
        var data = IcmpStabilityChartData.Create(samples);
        Equal(60, data.Count, "history at capacity");
        AssertRetainedSamples(samples, data, 0);
        AssertContinuousPositions(data, 60);
    }

    private static void Rollover()
    {
        foreach (var cycles in new[] { 61, 65 })
        {
            var samples = MakeSamples(cycles);
            var data = IcmpStabilityChartData.Create(samples);
            Equal(60, data.Count, "presentation capacity after rollover");
            AssertRetainedSamples(samples, data, cycles - 60);
            AssertContinuousPositions(data, 60);
            Equal(samples[^1].TimestampUtc, data.Samples[^1].TimestampUtc,
                "newest completed cycle remains on the right");
            Equal(samples[cycles - 60].TimestampUtc, data.Samples[0].TimestampUtc,
                "oldest entries are removed");
        }
    }

    private static void ObservationOrder()
    {
        var samples = new[]
        {
            Sample(2, 22, 2),
            Sample(1, 11, 1),
            Sample(3, 33, 3),
        };
        var data = IcmpStabilityChartData.Create(samples);
        AssertRetainedSamples(samples, data, 0);
        var line = data.LatencySegments.Single();
        AssertPoint(line[0], 0, 22, "first observed cycle");
        AssertPoint(line[1], 0.5, 11, "later observation after clock rollback");
        AssertPoint(line[2], 1, 33, "newest observed cycle");
    }

    private static void SuccessfulCycle()
    {
        var data = IcmpStabilityChartData.Create(new[] { Sample(1, 12.5, 35.0 / 3) });
        AssertPoint(data.LatencySegments.Single().Single(), 1, 12.5, "existing mean latency");
        AssertPoint(data.JitterSegments.Single().Single(), 1, 35.0 / 3, "existing calculated jitter");
    }

    private static void PartialPacketLoss()
    {
        var sample = Sample(1, 20, 20, successes: 2, failures: 2);
        var data = IcmpStabilityChartData.Create(new[] { sample });
        AssertPoint(data.LatencySegments.Single().Single(), 1, 20, "partial-loss latency");
        AssertPoint(data.JitterSegments.Single().Single(), 1, 20, "partial-loss jitter");
        Equal(50.0, data.Samples[0].PacketLossPercent, "loss remains in the existing observation");
        Equal(1, data.Count, "failed attempts do not manufacture extra chart cycles");
    }

    private static void AllFailedCycle()
    {
        var data = IcmpStabilityChartData.Create(new[] { Sample(1, null, null, successes: 0, failures: 4) });
        Equal(1, data.Count, "completed all-failed cycle retains its history position");
        Equal(0, data.LatencySegments.Count, "all-failed latency is a gap");
        Equal(0, data.JitterSegments.Count, "all-failed jitter is a gap");
        Equal(100.0, data.Samples[0].PacketLossPercent, "all-failed loss remains numeric");

        // A malformed caller cannot override the successful-reply evidence with fake zero fields.
        var inconsistent = IcmpStabilityChartData.Create(new[] { Sample(1, 0, 0, successes: 0, failures: 4) });
        Equal(0, inconsistent.LatencySegments.Count, "zero successes cannot prove zero latency");
        Equal(0, inconsistent.JitterSegments.Count, "zero successes cannot prove zero jitter");
    }

    private static void MissingMeasurements()
    {
        var samples = new[]
        {
            Sample(0, 42, 8),
            Sample(1, 43, null, successes: 1, failures: 3),
            Sample(2, null, null, successes: 0, failures: 4),
            Sample(3, 45, 5),
            Sample(4, 46, 6),
        };
        var data = IcmpStabilityChartData.Create(samples);
        Equal(5, data.Count, "missing measurements retain completed cycles");
        Equal(2, data.LatencySegments.Count, "latency line splits at all-failed cycle");
        Equal(2, data.JitterSegments.Count, "jitter line splits at insufficient successes");
        Equal(2, data.LatencySegments[0].Count, "first latency segment");
        Equal(1, data.JitterSegments[0].Count, "first jitter segment");
        AssertPoint(data.LatencySegments[0][1], 0.25, 43, "latency-only cycle retains position");
        AssertPoint(data.LatencySegments[1][0], 0.75, 45, "latency restarts after missing cycle");
        AssertPoint(data.JitterSegments[1][0], 0.75, 5, "jitter restarts without bridging gaps");

        var unavailableLatency = IcmpStabilityChartData.Create(new[] { Sample(1, null, null) });
        Equal(0, unavailableLatency.LatencySegments.Count, "unavailable latency cannot become zero");
        Equal(0, unavailableLatency.JitterSegments.Count, "unavailable jitter cannot become zero");
        var unavailableJitter = IcmpStabilityChartData.Create(new[] { Sample(1, 42, null) });
        Equal(1, unavailableJitter.LatencySegments.Count, "valid latency survives unavailable jitter");
        Equal(0, unavailableJitter.JitterSegments.Count, "unavailable jitter leaves its own gap");
    }

    private static void InsufficientJitter()
    {
        var data = IcmpStabilityChartData.Create(new[] { Sample(1, 30, null, successes: 1, failures: 3) });
        AssertPoint(data.LatencySegments.Single().Single(), 1, 30, "single-success latency");
        Equal(0, data.JitterSegments.Count, "single success has insufficient jitter data");
        var inconsistent = IcmpStabilityChartData.Create(new[] { Sample(1, 30, 0, successes: 1, failures: 3) });
        Equal(0, inconsistent.JitterSegments.Count, "a supplied jitter cannot bypass the two-success requirement");
    }

    private static void ValidZero()
    {
        var data = IcmpStabilityChartData.Create(new[] { Sample(1, 0, 0) });
        AssertPoint(data.LatencySegments.Single().Single(), 1, 0, "valid zero latency");
        AssertPoint(data.JitterSegments.Single().Single(), 1, 0, "valid zero jitter");
        Near(1, data.MaxMilliseconds, "zero-only scale stays usable");
    }

    private static void InvalidMeasurements()
    {
        var samples = new[]
        {
            Sample(0, 10, 2),
            Sample(1, double.NaN, double.PositiveInfinity),
            Sample(2, -1, -2),
            Sample(3, double.NegativeInfinity, double.NaN),
            Sample(4, 20, 4),
        };
        var data = IcmpStabilityChartData.Create(samples);
        Equal(5, data.Count, "invalid fields retain observed cycles");
        Equal(2, data.LatencySegments.Count, "invalid latency splits line");
        Equal(2, data.JitterSegments.Count, "invalid jitter splits line");
        Check(data.LatencySegments.All(segment => segment.Count == 1),
            "Latency cannot connect across invalid samples.");
        Check(data.JitterSegments.All(segment => segment.Count == 1),
            "Jitter cannot connect across invalid samples.");
        Check(data.LatencySegments.SelectMany(segment => segment)
            .Concat(data.JitterSegments.SelectMany(segment => segment))
            .All(point => double.IsFinite(point.Milliseconds) && point.Milliseconds >= 0),
            "Rendered points must all be finite, nonnegative measurements.");
    }

    private static void SharedPositions()
    {
        var data = IcmpStabilityChartData.Create(new[]
        {
            Sample(0, 10, 1),
            Sample(1, 20, null),
            Sample(2, null, null, successes: 0, failures: 4),
            Sample(3, 30, 3),
        });
        var latency = data.LatencySegments.SelectMany(segment => segment).ToArray();
        var jitter = data.JitterSegments.SelectMany(segment => segment).ToArray();
        Near(0, latency[0].Position, "first latency x position");
        Near(1.0 / 3, latency[1].Position, "latency-only x position");
        Near(1, latency[2].Position, "last latency x position");
        Near(latency[0].Position, jitter[0].Position, "shared oldest x position");
        Near(latency[^1].Position, jitter[^1].Position, "shared newest x position");
        Check(jitter.All(point => point.Position != latency[1].Position),
            "Unavailable jitter leaves its position empty instead of shifting later points.");
    }

    private static void SharedScale()
    {
        var data = IcmpStabilityChartData.Create(new[] { Sample(1, 12, 115) });
        Check(double.IsFinite(data.MaxMilliseconds) && data.MaxMilliseconds >= 115,
            "The shared axis must include the larger jitter measurement.");
        var latencyDominant = IcmpStabilityChartData.Create(new[] { Sample(1, 175, 5) });
        Check(double.IsFinite(latencyDominant.MaxMilliseconds) && latencyDominant.MaxMilliseconds >= 175,
            "The shared axis must include the larger latency measurement.");
        var extreme = IcmpStabilityChartData.Create(new[] { Sample(1, double.MaxValue, double.MaxValue) });
        Check(double.IsFinite(extreme.MaxMilliseconds) && extreme.MaxMilliseconds >= double.MaxValue,
            "Readable-scale rounding cannot overflow a finite measurement into infinity.");
    }

    private static void ImmutableSnapshots()
    {
        var samples = MakeSamples(3);
        var original = samples[0];
        var data = IcmpStabilityChartData.Create(samples);
        samples[0] = Sample(99, 9999, 9999);
        Check(ReferenceEquals(original, data.Samples[0]),
            "Replacing an input array entry cannot rewrite an existing presentation snapshot.");
        RejectMutation(data.Samples, Sample(100, 10000, 10000), "history snapshot");
        RejectMutation(data.LatencySegments, Array.Empty<IcmpStabilityChartPoint>(), "latency segments");
        RejectMutation(data.JitterSegments, Array.Empty<IcmpStabilityChartPoint>(), "jitter segments");
        RejectMutation(data.LatencySegments[0], data.LatencySegments[0][^1], "latency points");
        RejectMutation(data.JitterSegments[0], data.JitterSegments[0][^1], "jitter points");
        AssertPoint(data.LatencySegments[0][0], 0, original.AverageLatencyMs!.Value,
            "immutable original point");
    }

    private static IcmpProbeResult Sample(int cycle, double? latency, double? jitter,
        int successes = 4, int failures = 0) =>
        new(Origin.AddSeconds(cycle * 15), "1.1.1.1", successes, failures, latency,
            (successes + failures) == 0 ? 0 : failures * 100.0 / (successes + failures), jitter);

    private static IcmpProbeResult[] MakeSamples(int count) =>
        Enumerable.Range(1, count).Select(cycle => Sample(cycle, cycle, cycle / 2.0)).ToArray();

    private static void AssertRetainedSamples(IReadOnlyList<IcmpProbeResult> input,
        IcmpStabilityChartData data, int offset)
    {
        Equal(input.Count - offset, data.Samples.Count, "retained sample count");
        for (var index = 0; index < data.Samples.Count; index++)
            Check(ReferenceEquals(input[index + offset], data.Samples[index]),
                "Retained immutable observations must preserve oldest-to-newest order.");
    }

    private static void AssertContinuousPositions(IcmpStabilityChartData data, int count)
    {
        var latency = data.LatencySegments.Single();
        var jitter = data.JitterSegments.Single();
        Equal(count, latency.Count, "continuous latency points");
        Equal(count, jitter.Count, "continuous jitter points");
        for (var index = 0; index < count; index++)
        {
            var position = (double)index / (count - 1);
            Near(position, latency[index].Position, "latency relative history position");
            Near(position, jitter[index].Position, "jitter relative history position");
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

    private static void AssertPoint(IcmpStabilityChartPoint point, double position, double milliseconds,
        string description)
    {
        Near(position, point.Position, $"{description} position");
        Near(milliseconds, point.Milliseconds, $"{description} milliseconds");
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
