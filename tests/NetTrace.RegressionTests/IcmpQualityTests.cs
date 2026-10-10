using NetTrace.Models;
using NetTrace.Presentation;

internal static class IcmpQualityTests
{
    public static (string Name, Action Run)[] Cases =>
    [
        ("Zero completed ICMP cycles show Measuring without fake metrics", EmptyHistory),
        ("One completed ICMP cycle remains Measuring", OneCycle),
        ("Two completed ICMP cycles remain Measuring even when all pings fail", TwoCycles),
        ("Recent excellent latency, jitter and loss produce Excellent ICMP quality", Excellent),
        ("Recent good latency, jitter and loss produce Good ICMP quality", Good),
        ("Fair recent latency independently produces Fair ICMP quality", FairLatency),
        ("Fair recent jitter independently produces Fair ICMP quality", FairJitter),
        ("Fair recent packet loss independently produces Fair ICMP quality", FairLoss),
        ("Poor recent latency independently produces Poor ICMP quality", PoorLatency),
        ("Poor recent jitter independently produces Poor ICMP quality", PoorJitter),
        ("Poor recent packet loss independently produces Poor ICMP quality", PoorLoss),
        ("Excellent latency, Good jitter and Fair loss produce overall Fair", WorstFair),
        ("Good latency, Poor jitter and Excellent loss produce overall Poor", WorstPoor),
        ("At least three all-failed ICMP cycles are Unavailable rather than Poor", AllFailed),
        ("Mixed successful and all-failed ICMP cycles retain loss and average only valid RTTs", MixedAvailability),
        ("Unavailable per-cycle latency and jitter are excluded without fake zeros", MissingMetrics),
        ("ICMP quality requires successful-reply evidence for latency and jitter", ReplyEvidence),
        ("Negative and nonfinite latency or jitter cannot establish ICMP quality", InvalidMetrics),
        ("Finite out-of-range loss is bounded without modifying completed results", FiniteLossBounds),
        ("Nonfinite packet loss is unavailable rather than a fake zero-loss metric", NonfiniteLoss),
        ("ICMP quality uses only the newest twelve cycles from the existing sixty-cycle history", NewestWindow),
        ("The thirteenth completed cycle removes only the oldest from the quality window", WindowRollover),
        ("Old successful cycles outside the quality window cannot hide unavailable recent ICMP", RecentUnavailable),
        ("Quality window selection follows observation order instead of sorting timestamps", ObservationOrder),
        ("Quality evaluation is immutable and deterministic without retaining input history", ImmutableAndDeterministic),
        ("Recent latency is an average of completed-cycle means rather than reply-weighted data", PerCycleMeans),
        ("Finite extreme latency and jitter values retain finite averages", ExtremeFiniteMeans),
        .. BoundaryCases(),
    ];

    private static readonly DateTimeOffset Origin =
        new(2026, 10, 11, 5, 0, 0, TimeSpan.Zero);

    private static void EmptyHistory()
    {
        var summary = IcmpQualityEvaluator.Evaluate(Array.Empty<IcmpProbeResult>());
        Equal(IcmpQuality.Measuring, summary.Quality, "initial quality");
        Equal(0, summary.SampleCount, "completed-cycle count");
        Missing(summary.RecentLatencyMs, "latency");
        Missing(summary.RecentJitterMs, "jitter");
        Missing(summary.RecentPacketLossPercent, "loss");
    }

    private static void OneCycle()
    {
        var summary = IcmpQualityEvaluator.Evaluate([Sample(0, 48, 6, 0)]);
        Equal(IcmpQuality.Measuring, summary.Quality, "one cycle cannot make a strong judgment");
        Equal(1, summary.SampleCount, "one completed cycle");
        Near(48, summary.RecentLatencyMs, "existing latency remains measurable");
    }

    private static void TwoCycles()
    {
        var summary = IcmpQualityEvaluator.Evaluate([Failed(0), Failed(1)]);
        Equal(IcmpQuality.Measuring, summary.Quality, "minimum-data rule precedes unavailability");
        Equal(2, summary.SampleCount, "two completed failures still count");
        Near(100, summary.RecentPacketLossPercent, "completed all-failed loss");
        Missing(summary.RecentLatencyMs, "no zero RTT for failures");
    }

    private static void Excellent() => AssertQuality(IcmpQuality.Excellent, 48, 6, 0);
    private static void Good() => AssertQuality(IcmpQuality.Good, 75, 15, 1);
    private static void FairLatency() => AssertQuality(IcmpQuality.Fair, 150, 6, 0);
    private static void FairJitter() => AssertQuality(IcmpQuality.Fair, 48, 30, 0);
    private static void FairLoss() => AssertQuality(IcmpQuality.Fair, 48, 6, 3);
    private static void PoorLatency() => AssertQuality(IcmpQuality.Poor, 201, 6, 0);
    private static void PoorJitter() => AssertQuality(IcmpQuality.Poor, 48, 41, 0);
    private static void PoorLoss() => AssertQuality(IcmpQuality.Poor, 48, 6, 6);
    private static void WorstFair() => AssertQuality(IcmpQuality.Fair, 48, 15, 3);
    private static void WorstPoor() => AssertQuality(IcmpQuality.Poor, 75, 41, 0);

    private static void AllFailed()
    {
        foreach (var count in new[] { 3, 4, 12, 60 })
        {
            var summary = IcmpQualityEvaluator.Evaluate(Enumerable.Range(0, count).Select(Failed).ToArray());
            Equal(IcmpQuality.Unavailable, summary.Quality, "no successful recent ICMP latency");
            Equal(Math.Min(count, 12), summary.SampleCount, "completed failures participate in the bounded window");
            Near(100, summary.RecentPacketLossPercent, "all-failed average loss");
            Missing(summary.RecentLatencyMs, "all-failed latency");
            Missing(summary.RecentJitterMs, "all-failed jitter");
        }
    }

    private static void MixedAvailability()
    {
        var summary = IcmpQualityEvaluator.Evaluate([
            Sample(0, 20, 4, 0), Failed(1), Sample(2, 40, null, 75, successes: 1), Failed(3),
        ]);
        Equal(IcmpQuality.Poor, summary.Quality, "successful latency permits normal loss classification");
        Near(30, summary.RecentLatencyMs, "only valid cycle latencies");
        Near(4, summary.RecentJitterMs, "only valid cycle jitter");
        Near(68.75, summary.RecentPacketLossPercent, "all completed cycles including complete failures");
    }

    private static void MissingMetrics()
    {
        var summary = IcmpQualityEvaluator.Evaluate([
            Sample(0, 48, null, 0), Sample(1, null, null, 0), Sample(2, 48, null, 0),
        ]);
        Equal(IcmpQuality.Excellent, summary.Quality, "missing jitter does not penalize valid latency and loss");
        Near(48, summary.RecentLatencyMs, "missing latency never substitutes zero");
        Missing(summary.RecentJitterMs, "no zero jitter is invented");
        Near(0, summary.RecentPacketLossPercent, "valid loss remains available");

        var zero = EvaluateThree(0, 0, 0);
        Equal(IcmpQuality.Excellent, zero.Quality, "legitimate successful zero measurements remain valid");
        Near(0, zero.RecentLatencyMs, "valid zero RTT");
        Near(0, zero.RecentJitterMs, "valid zero jitter");
    }

    private static void ReplyEvidence()
    {
        var bogus = Enumerable.Range(0, 3)
            .Select(index => Sample(index, 0, 0, 100, successes: 0)).ToArray();
        var unavailable = IcmpQualityEvaluator.Evaluate(bogus);
        Equal(IcmpQuality.Unavailable, unavailable.Quality, "zero success cannot prove a zero-millisecond latency");
        Missing(unavailable.RecentLatencyMs, "no successful-reply evidence");
        Missing(unavailable.RecentJitterMs, "no two-success evidence");

        var oneReply = Enumerable.Range(0, 3)
            .Select(index => Sample(index, 48, 1000, 0, successes: 1)).ToArray();
        var summary = IcmpQualityEvaluator.Evaluate(oneReply);
        Equal(IcmpQuality.Excellent, summary.Quality, "one success permits latency but not supplied jitter");
        Missing(summary.RecentJitterMs, "jitter requires at least two successful replies");
    }

    private static void InvalidMetrics()
    {
        var summary = IcmpQualityEvaluator.Evaluate([
            Sample(0, 48, 6, 0),
            Sample(1, double.NaN, double.PositiveInfinity, 0),
            Sample(2, double.PositiveInfinity, double.NaN, 0),
            Sample(3, double.NegativeInfinity, double.NegativeInfinity, 0),
            Sample(4, -1, -1, 0),
        ]);
        Equal(IcmpQuality.Excellent, summary.Quality, "invalid metrics are excluded individually");
        Near(48, summary.RecentLatencyMs, "finite nonnegative RTT only");
        Near(6, summary.RecentJitterMs, "finite nonnegative jitter only");

        var noLatency = IcmpQualityEvaluator.Evaluate([
            Sample(0, -1, 6, 0), Sample(1, double.NaN, 6, 0), Sample(2, double.PositiveInfinity, 6, 0),
        ]);
        Equal(IcmpQuality.Unavailable, noLatency.Quality, "no valid successful latency cannot imply good ICMP");
        Missing(noLatency.RecentLatencyMs, "invalid latency is unavailable");
    }

    private static void FiniteLossBounds()
    {
        var samples = new[]
        {
            Sample(0, 48, 6, -double.MaxValue),
            Sample(1, 48, 6, double.MaxValue),
            Sample(2, 48, 6, 50),
        };
        var summary = IcmpQualityEvaluator.Evaluate(samples);
        Near(50, summary.RecentPacketLossPercent, "bounded loss is averaged as zero, one hundred and fifty");
        Equal(IcmpQuality.Poor, summary.Quality, "bounded high loss influences quality");
        Equal(-double.MaxValue, samples[0].PacketLossPercent, "raw negative loss remains unchanged");
        Equal(double.MaxValue, samples[1].PacketLossPercent, "raw excessive loss remains unchanged");
    }

    private static void NonfiniteLoss()
    {
        var samples = new[]
        {
            Sample(0, 48, 6, double.NaN), Sample(1, 48, 6, double.PositiveInfinity),
            Sample(2, 48, 6, double.NegativeInfinity),
        };
        var missing = IcmpQualityEvaluator.Evaluate(samples);
        Equal(IcmpQuality.Excellent, missing.Quality, "available RTT and jitter classify without invalid loss");
        Missing(missing.RecentPacketLossPercent, "all nonfinite loss is unavailable");
        var partial = IcmpQualityEvaluator.Evaluate(samples.Append(Sample(3, 48, 6, 4)).ToArray());
        Near(4, partial.RecentPacketLossPercent, "invalid percentages never dilute known loss with zeros");
        Equal(IcmpQuality.Fair, partial.Quality, "the available loss percentage determines its severity");
    }

    private static void NewestWindow()
    {
        var samples = Enumerable.Range(0, 60)
            .Select(index => index < 48 ? Sample(index, 1000, 1000, 100) : Sample(index, 48, 6, 0)).ToArray();
        var summary = IcmpQualityEvaluator.Evaluate(samples);
        Equal(IcmpQuality.Excellent, summary.Quality, "older poor cycles cannot affect newest twelve");
        Equal(12, summary.SampleCount, "recent-window count");
        Equal(60, samples.Length, "the original monitoring history remains sixty cycles");
        Near(48, summary.RecentLatencyMs, "only recent latency");
        Near(6, summary.RecentJitterMs, "only recent jitter");
        Near(0, summary.RecentPacketLossPercent, "only recent loss");
    }

    private static void WindowRollover()
    {
        var first = new[] { Sample(0, 48, 6, 100) }.Concat(
            Enumerable.Range(1, 11).Select(index => Sample(index, 48, 6, 0))).ToArray();
        Equal(IcmpQuality.Poor, IcmpQualityEvaluator.Evaluate(first).Quality,
            "oldest retained loss still participates in the twelve-cycle mean");
        var next = first.Append(Sample(12, 48, 6, 0)).ToArray();
        Equal(IcmpQuality.Excellent, IcmpQualityEvaluator.Evaluate(next).Quality,
            "only the oldest cycle leaves when the thirteenth arrives");
        Equal(12, first.Length, "evaluation does not alter the previous snapshot");
        Equal(13, next.Length, "evaluation does not trim its input");
    }

    private static void RecentUnavailable()
    {
        var samples = Enumerable.Range(0, 48).Select(index => Sample(index, 48, 6, 0))
            .Concat(Enumerable.Range(48, 12).Select(Failed)).ToArray();
        var summary = IcmpQualityEvaluator.Evaluate(samples);
        Equal(IcmpQuality.Unavailable, summary.Quality, "only successful latency inside the window counts");
        Missing(summary.RecentLatencyMs, "older valid latency is outside the window");
        Near(100, summary.RecentPacketLossPercent, "recent complete failures remain measurable loss");
    }

    private static void ObservationOrder()
    {
        var samples = new[] { Sample(99, 1000, 1000, 100) }.Concat(
            Enumerable.Range(0, 12).Reverse().Select(index => Sample(index, 48, 6, 0))).ToArray();
        Equal(IcmpQuality.Excellent, IcmpQualityEvaluator.Evaluate(samples).Quality,
            "timestamps do not sort an old observation back into the recent window");
        Check(samples[^1].TimestampUtc < samples[0].TimestampUtc,
            "The fixture exercises wall-clock rollback in the observation order.");
    }

    private static void ImmutableAndDeterministic()
    {
        var samples = Enumerable.Range(0, 60).Select(index => Sample(index, index + 1, index % 5, index % 3)).ToArray();
        var original = samples.ToArray();
        var first = IcmpQualityEvaluator.Evaluate(Array.AsReadOnly(samples));
        for (var attempt = 0; attempt < 10; attempt++)
            Equal(first, IcmpQualityEvaluator.Evaluate(samples), "repeated evaluation is value-deterministic");
        for (var index = 0; index < samples.Length; index++)
            Check(ReferenceEquals(samples[index], original[index]), "Evaluation cannot replace or reorder source results.");
        samples[^1] = Failed(99);
        Equal(12, first.SampleCount, "immutable summary does not retain a mutable history collection");
        Near(54.5, first.RecentLatencyMs, "a later input mutation cannot rewrite a previously returned summary");
    }

    private static void PerCycleMeans()
    {
        var summary = IcmpQualityEvaluator.Evaluate([
            Sample(0, 100, null, 75, successes: 1),
            Sample(1, 10, 4, 0), Sample(2, 10, 8, 0),
        ]);
        Near(40, summary.RecentLatencyMs, "each valid completed-cycle mean has equal weight");
        Near(6, summary.RecentJitterMs, "only cycles with valid jitter participate");
        Near(25, summary.RecentPacketLossPercent, "all cycle percentages receive equal weight");
    }

    private static void ExtremeFiniteMeans()
    {
        var summary = IcmpQualityEvaluator.Evaluate([
            Sample(0, double.MaxValue, double.MaxValue, 0),
            Sample(1, double.MaxValue, double.MaxValue, 0),
            Sample(2, 0, 0, 0),
        ]);
        Equal(IcmpQuality.Poor, summary.Quality, "extreme finite measurements remain poor rather than unavailable");
        Check(summary.RecentLatencyMs is { } latency && double.IsFinite(latency) && latency > 200,
            "Valid finite latency means must not overflow to infinity.");
        Check(summary.RecentJitterMs is { } jitter && double.IsFinite(jitter) && jitter > 40,
            "Valid finite jitter means must not overflow to infinity.");
    }

    private static (string Name, Action Run)[] BoundaryCases()
    {
        var metrics = new[]
        {
            (Name: "latency", Values: new[] { 50.0, 100, 200 }),
            (Name: "jitter", Values: new[] { 10.0, 20, 40 }),
            (Name: "loss", Values: new[] { 0.0, 1, 5 }),
        };
        var cases = new List<(string Name, Action Run)>();
        foreach (var metric in metrics)
        {
            for (var boundary = 0; boundary < metric.Values.Length; boundary++)
            {
                var exact = metric.Values[boundary];
                var exactQuality = (IcmpQuality)boundary;
                var aboveQuality = (IcmpQuality)(boundary + 1);
                cases.Add(($"ICMP {metric.Name} exact boundary {exact} retains {exactQuality}",
                    () => AssertMetricBoundary(metric.Name, exact, exactQuality)));
                cases.Add(($"ICMP {metric.Name} immediately above {exact} becomes {aboveQuality}",
                    () => AssertMetricBoundary(metric.Name, exact + 0.000001, aboveQuality)));
            }
        }
        return cases.ToArray();
    }

    private static void AssertMetricBoundary(string metric, double value, IcmpQuality expected) =>
        AssertQuality(expected, metric == "latency" ? value : 0,
            metric == "jitter" ? value : 0, metric == "loss" ? value : 0);

    private static void AssertQuality(IcmpQuality expected, double? latency, double? jitter, double loss)
    {
        var summary = EvaluateThree(latency, jitter, loss);
        Equal(expected, summary.Quality, "worst available recent metric");
        Equal(3, summary.SampleCount, "minimum completed window");
        if (latency is { } latencyValue) Near(latencyValue, summary.RecentLatencyMs, "per-cycle latency mean");
        if (jitter is { } jitterValue) Near(jitterValue, summary.RecentJitterMs, "per-cycle jitter mean");
        Near(loss, summary.RecentPacketLossPercent, "per-cycle loss mean");
    }

    private static IcmpQualitySummary EvaluateThree(double? latency, double? jitter, double loss) =>
        IcmpQualityEvaluator.Evaluate(Enumerable.Range(0, 3)
            .Select(index => Sample(index, latency, jitter, loss)).ToArray());

    private static IcmpProbeResult Sample(int cycle, double? latency, double? jitter, double loss, int successes = 4) =>
        new(Origin.AddSeconds(cycle * 15), "1.1.1.1", successes, 4 - successes, latency, loss, jitter);

    private static IcmpProbeResult Failed(int cycle) => Sample(cycle, null, null, 100, successes: 0);

    private static void Missing(double? value, string description) =>
        Check(value is null, $"{description} must be unavailable rather than zero.");

    private static void Near(double expected, double? actual, string description) =>
        Check(actual is { } value && double.IsFinite(value) && Math.Abs(expected - value) < 0.0000001,
            $"{description}: expected {expected}, actual {actual?.ToString() ?? "unavailable"}.");

    private static void Equal<T>(T expected, T actual, string description) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual),
            $"{description}: expected {expected}, actual {actual}.");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
