using AppUsageNonElevatedProbe;

internal static class ClassificationTests
{
    private static readonly ObservedEventCapability Complete = new(900, 2, 10, true, false, true, true, true);

    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("An unavailable provider is unsuitable even when other supplied evidence appears complete", Check.Sync(ProviderUnavailable)),
        ("Session-start or provider-enable permission denial makes a source unsuitable", Check.Sync(PermissionDenied)),
        ("A permitted provider with no live events cannot prove accounting suitability", Check.Sync(NoEvents)),
        ("Missing PID and application identity leave observed bytes only partially suitable", Check.Sync(MissingPid)),
        ("Missing byte counts leave attributed activity only partially suitable", Check.Sync(MissingBytes)),
        ("Missing send/receive direction leaves attributed byte accounting only partially suitable", Check.Sync(MissingDirection)),
        ("Coherent directional accounting verified by a controlled transfer is fully suitable", Check.Sync(FullySuitable)),
        ("Observed application identity can provide attribution when a PID field is absent", Check.Sync(ApplicationIdentity)),
        ("Attributed connection activity without transferred bytes is partially suitable", Check.Sync(ConnectionOnly)),
        ("PID, bytes, and direction on unrelated events cannot invent coherent accounting", Check.Sync(UnrelatedFields)),
        ("Direction on an unrelated event cannot complete an attributed byte event", Check.Sync(UnrelatedDirection)),
        ("Event loss prevents a fully suitable accounting claim", Check.Sync(EventLoss)),
        ("Complete fields without controlled byte validation remain partially suitable", Check.Sync(UnverifiedAccounting)),
        ("A session without clean shutdown is unsuitable for a reliable prototype", Check.Sync(UncleanShutdown)),
        ("Unobserved schema descriptors do not count as live capability evidence", Check.Sync(UnobservedDescriptors)),
        ("Events without useful accounting or connection information are unsuitable", Check.Sync(UselessEvents)),
    ];

    private static void ProviderUnavailable() =>
        Expect(Evidence() with { ProviderAvailable = false }, ProviderSuitability.Unsuitable);

    private static void PermissionDenied()
    {
        Expect(Evidence() with { SessionStartPermitted = false, AccessDenied = true }, ProviderSuitability.Unsuitable);
        Expect(Evidence() with { ProviderEnablePermitted = false, AccessDenied = true }, ProviderSuitability.Unsuitable);
        Expect(Evidence() with { SessionStartPermitted = false, AccessDenied = false }, ProviderSuitability.Unsuitable);
        Expect(Evidence() with { ProviderEnablePermitted = false, AccessDenied = false }, ProviderSuitability.Unsuitable);
    }

    private static void NoEvents()
    {
        var result = Expect(Evidence() with { EventsReceived = 0, ObservedEvents = [] }, ProviderSuitability.Unsuitable);
        Check.True(!result.HasAttribution && !result.HasByteCount && !result.HasDirection,
            "no events cannot expose observed accounting fields");
    }

    private static void MissingPid()
    {
        var result = Expect(Evidence(Complete with { HasPid = false }), ProviderSuitability.PartiallySuitable);
        Check.True(!result.HasAttribution, "attribution is explicitly missing");
        Check.True(!result.HasCoherentAccounting, "byte totals without application attribution are not coherent per-app accounting");
    }

    private static void MissingBytes()
    {
        var result = Expect(Evidence(Complete with { HasByteCount = false }), ProviderSuitability.PartiallySuitable);
        Check.True(!result.HasByteCount && !result.HasCoherentAccounting, "missing bytes cannot prove transfer accounting");
    }

    private static void MissingDirection()
    {
        var result = Expect(Evidence(Complete with { HasDirection = false }), ProviderSuitability.PartiallySuitable);
        Check.True(result.HasCoherentAccounting && !result.HasDirection, "attributed bytes remain useful but direction is missing");
        Check.True(result.Reason.Contains("direction", StringComparison.OrdinalIgnoreCase), "missing direction is explained");
    }

    private static void FullySuitable()
    {
        var result = Expect(Evidence(), ProviderSuitability.FullySuitable);
        Check.True(result.HasAttribution && result.HasByteCount && result.HasDirection && result.HasCoherentAccounting,
            "full suitability includes all observed coherent accounting capabilities");
    }

    private static void ApplicationIdentity()
    {
        var result = Expect(Evidence(Complete with { HasPid = false, HasApplicationIdentity = true }), ProviderSuitability.FullySuitable);
        Check.True(result.HasAttribution && result.HasCoherentAccounting, "actual application identity supports attribution");
    }

    private static void ConnectionOnly()
    {
        var connection = Complete with { HasByteCount = false, HasDirection = false, IsTransferAccounting = false, IsConnectionActivity = true };
        var result = Expect(Evidence(connection), ProviderSuitability.PartiallySuitable);
        Check.True(!result.HasByteCount && !result.HasCoherentAccounting,
            "connection lifecycle metadata is not claimed as transferred bytes");
    }

    private static void UnrelatedFields()
    {
        var unrelated = new[]
        {
            new ObservedEventCapability(901, 0, 10, true, false, false, false, false, true),
            new ObservedEventCapability(902, 0, 10, false, false, true, false, true),
            new ObservedEventCapability(903, 0, 10, false, false, false, true, false)
        };
        var result = Expect(Evidence() with { ObservedEvents = unrelated }, ProviderSuitability.PartiallySuitable);
        Check.True(result.HasAttribution && result.HasByteCount && result.HasDirection,
            "individual observed fields are still reported honestly");
        Check.True(!result.HasCoherentAccounting, "a union across unrelated events cannot attribute byte counts");
    }

    private static void UnrelatedDirection()
    {
        var evidence = Evidence() with
        {
            ObservedEvents =
            [
                Complete with { HasDirection = false },
                new ObservedEventCapability(903, 0, 10, false, false, false, true, false)
            ]
        };
        var result = Expect(evidence, ProviderSuitability.PartiallySuitable);
        Check.True(result.HasCoherentAccounting, "the first event has coherent attribution and bytes");
        Check.True(result.HasDirection, "a separate event does expose some direction metadata");
        Check.True(result.Suitability != ProviderSuitability.FullySuitable,
            "direction must be part of the attributed accounting event to establish full suitability");
    }

    private static void EventLoss() =>
        Expect(Evidence() with { EventsLost = 1 }, ProviderSuitability.PartiallySuitable);

    private static void UnverifiedAccounting() =>
        Expect(Evidence() with { ControlledAccountingVerified = false }, ProviderSuitability.PartiallySuitable);

    private static void UncleanShutdown() =>
        Expect(Evidence() with { ShutdownClean = false }, ProviderSuitability.Unsuitable);

    private static void UnobservedDescriptors()
    {
        var result = Expect(Evidence(Complete with { EventCount = 0 }), ProviderSuitability.Unsuitable);
        Check.True(!result.HasAttribution && !result.HasByteCount && !result.HasDirection && !result.HasCoherentAccounting,
            "metadata alone does not prove actual event payload availability");
    }

    private static void UselessEvents() =>
        Expect(Evidence(new(950, 0, 10, false, false, false, false, false)), ProviderSuitability.Unsuitable);

    private static ProviderCapabilityEvidence Evidence(ObservedEventCapability? observed = null) =>
        new(true, true, true, false, true, 10, 0, true, [observed ?? Complete]);

    private static CapabilityClassification Expect(ProviderCapabilityEvidence evidence, ProviderSuitability expected)
    {
        var result = CapabilityClassifier.Classify(evidence);
        Check.Equal(expected, result.Suitability, "provider capability classification");
        Check.True(!string.IsNullOrWhiteSpace(result.Reason), "classification includes a concise diagnostic explanation");
        return result;
    }
}
