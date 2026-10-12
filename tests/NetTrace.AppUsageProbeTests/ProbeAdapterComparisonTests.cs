using AppUsageEtwProbe;

internal static class ProbeAdapterComparisonTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly VpnObservation NoVpn = new("NoVpn", 0, [], "Deterministic fixture");

    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Physical adapter comparison uses byte-counter deltas over their sampling timestamps", Check.Sync(CounterDeltas)),
        ("Changed physical interfaces cannot create a comparable usage delta", Check.Sync(ChangedInterface)),
        ("Reset and negative physical counters never manufacture usage", Check.Sync(CounterResets)),
        ("Unavailable or disconnected physical counters produce an explanatory missing comparison", Check.Sync(UnavailableCounters)),
        ("Non-advancing physical sample timestamps cannot produce a coverage comparison", Check.Sync(InvalidTimestamps)),
        ("A physical delta larger than the numeric range is reported unavailable", Check.Sync(DeltaOverflow)),
        ("Coverage ratio is computed only for confirmed matching non-VPN interface evidence", Check.Sync(MeaningfulCoverage)),
        ("VPN, missing interface, and invalid denominator evidence suppress misleading coverage percentages", Check.Sync(AmbiguousCoverage)),
    ];

    private static void CounterDeltas()
    {
        var start = Snapshot(1000, 200, Epoch);
        var end = Snapshot(2200, 550, Epoch.AddSeconds(2)) with { CapturedAtUtc = Epoch.AddSeconds(5), AdapterId = "PHYSICAL-1" };
        var comparison = AdapterObservation.Compare(start, end);
        Check.True(comparison.Valid, "unchanged operational physical adapter compares");
        Check.Equal<long?>(1200, comparison.RxBytes, "physical receive delta");
        Check.Equal<long?>(350, comparison.TxBytes, "physical send delta");
        Check.Equal<long?>(1550, comparison.TotalBytes, "physical combined delta");
        Check.Equal(2.0, comparison.ObservationSeconds, "counter sample window excludes read callback delay");
    }

    private static void ChangedInterface()
    {
        var start = Snapshot(1000, 200, Epoch);
        var end = Snapshot(2200, 550, Epoch.AddSeconds(2));
        Unavailable(AdapterObservation.Compare(start, end with { AdapterId = "physical-2" }), "changed adapter ID");
        Unavailable(AdapterObservation.Compare(start, end with { InterfaceIndex = 8 }), "changed interface index");
    }

    private static void CounterResets()
    {
        var start = Snapshot(1000, 200, Epoch);
        var end = Snapshot(2200, 550, Epoch.AddSeconds(2));
        Unavailable(AdapterObservation.Compare(start, end with { RxBytes = 999 }), "receive counter reset");
        Unavailable(AdapterObservation.Compare(start, end with { TxBytes = 199 }), "send counter reset");
        Unavailable(AdapterObservation.Compare(start with { RxBytes = -1 }, end), "invalid receive baseline");
        Unavailable(AdapterObservation.Compare(start with { TxBytes = -1 }, end), "invalid send baseline");
    }

    private static void UnavailableCounters()
    {
        var start = Snapshot(1000, 200, Epoch);
        var end = Snapshot(2200, 550, Epoch.AddSeconds(2));
        Unavailable(AdapterObservation.Compare(start with { Operational = false }, end), "adapter down at start");
        Unavailable(AdapterObservation.Compare(start, end with { Operational = false }), "adapter down at end");
        Unavailable(AdapterObservation.Compare(start with { RxBytes = null }, end), "receive baseline unavailable");
        Unavailable(AdapterObservation.Compare(start, end with { TxBytes = null }), "send end count unavailable");
    }

    private static void InvalidTimestamps()
    {
        var start = Snapshot(1000, 200, Epoch);
        Unavailable(AdapterObservation.Compare(start, Snapshot(2200, 550, Epoch)), "same timestamp");
        Unavailable(AdapterObservation.Compare(start, Snapshot(2200, 550, Epoch.AddSeconds(-1))), "reversed timestamp");
    }

    private static void DeltaOverflow() =>
        Unavailable(AdapterObservation.Compare(Snapshot(0, 0, Epoch),
            Snapshot(long.MaxValue, long.MaxValue, Epoch.AddSeconds(2))), "combined delta overflow");

    private static void MeaningfulCoverage()
    {
        var coverage = AdapterObservation.AssessCoverage(new(true, 800, 200, 1000, 2, null),
            NoVpn, NoVpn, true, 800);
        Check.Equal<double?>(0.8, coverage.Ratio, "matching-interface diagnostic coverage ratio");
        Check.True(coverage.Reason.Contains("semantics", StringComparison.OrdinalIgnoreCase),
            "ratio retains an accounting-semantics caveat");
    }

    private static void AmbiguousCoverage()
    {
        var valid = new AdapterComparison(true, 800, 200, 1000, 2, null);
        var tunnel = NoVpn with { Status = "TunnelDetected", ActiveCandidates = 1 };
        var unknown = NoVpn with { Status = "Indeterminate" };
        foreach (var assessment in new[]
        {
            AdapterObservation.AssessCoverage(valid, tunnel, NoVpn, true, 800),
            AdapterObservation.AssessCoverage(valid, NoVpn, tunnel, true, 800),
            AdapterObservation.AssessCoverage(valid, unknown, NoVpn, true, 800),
            AdapterObservation.AssessCoverage(valid, NoVpn, NoVpn, false, 800),
            AdapterObservation.AssessCoverage(valid, NoVpn, NoVpn, true, -1),
            AdapterObservation.AssessCoverage(valid with { TotalBytes = 0 }, NoVpn, NoVpn, true, 0),
            AdapterObservation.AssessCoverage(valid with { Valid = false }, NoVpn, NoVpn, true, 800),
        })
        {
            Check.Equal<double?>(null, assessment.Ratio, "ambiguous physical/logical evidence suppresses ratio");
            Check.True(!string.IsNullOrWhiteSpace(assessment.Reason), "suppressed ratio has an explanation");
        }
    }

    private static AdapterCounterSnapshot Snapshot(long rx, long tx, DateTimeOffset timestamp) =>
        new(timestamp, "physical-1", 7, timestamp, rx, tx, true, NoVpn, null);

    private static void Unavailable(AdapterComparison comparison, string context)
    {
        Check.True(!comparison.Valid, context);
        Check.Equal<long?>(null, comparison.TotalBytes, context + " has no fabricated usage");
        Check.True(!string.IsNullOrWhiteSpace(comparison.Reason), context + " has an explanation");
    }
}
