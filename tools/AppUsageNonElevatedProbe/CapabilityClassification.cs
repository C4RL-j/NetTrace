namespace AppUsageNonElevatedProbe;

public enum ProviderSuitability { FullySuitable, PartiallySuitable, Unsuitable }

public sealed record ObservedEventCapability(int EventId, int Version, long EventCount,
    bool HasPid, bool HasApplicationIdentity, bool HasByteCount, bool HasDirection,
    bool IsTransferAccounting, bool IsConnectionActivity = false);

public sealed record ProviderCapabilityEvidence(bool ProviderAvailable, bool SessionStartPermitted,
    bool ProviderEnablePermitted, bool AccessDenied, bool ShutdownClean, long EventsReceived,
    long EventsLost, bool ControlledAccountingVerified, IReadOnlyList<ObservedEventCapability> ObservedEvents);

public sealed record CapabilityClassification(ProviderSuitability Suitability, string Reason,
    bool HasAttribution, bool HasByteCount, bool HasDirection, bool HasCoherentAccounting);

public static class CapabilityClassifier
{
    public static CapabilityClassification Classify(ProviderCapabilityEvidence evidence)
    {
        var observed = evidence.ObservedEvents.Where(e => e.EventCount > 0).ToArray();
        bool attribution = observed.Any(e => e.HasPid || e.HasApplicationIdentity);
        bool bytes = observed.Any(e => e.HasByteCount);
        bool direction = observed.Any(e => e.HasDirection);
        // Fields on unrelated connection/counter events cannot be combined into invented attribution.
        bool coherent = observed.Any(e => (e.HasPid || e.HasApplicationIdentity) && e.HasByteCount && e.IsTransferAccounting);
        bool complete = observed.Any(e => (e.HasPid || e.HasApplicationIdentity) && e.HasByteCount
            && e.HasDirection && e.IsTransferAccounting);
        CapabilityClassification Result(ProviderSuitability suitability, string reason) =>
            new(suitability, reason, attribution, bytes, direction, coherent);
        if (!evidence.ProviderAvailable) return Result(ProviderSuitability.Unsuitable, "Provider unavailable on this installation.");
        if (evidence.AccessDenied || !evidence.SessionStartPermitted || !evidence.ProviderEnablePermitted)
            return Result(ProviderSuitability.Unsuitable, "The unchanged normal process cannot start and enable this ETW source.");
        if (!evidence.ShutdownClean) return Result(ProviderSuitability.Unsuitable, "Session shutdown was not verified cleanly.");
        if (evidence.EventsReceived <= 0) return Result(ProviderSuitability.Unsuitable, "No live events were observed; emission and accounting remain unproven.");
        if (complete && evidence.ControlledAccountingVerified && evidence.EventsLost == 0)
            return Result(ProviderSuitability.FullySuitable, "Attributed directional byte events passed the controlled non-elevated transfer checks without event loss.");
        if (attribution || bytes || observed.Any(e => e.IsConnectionActivity))
            return Result(ProviderSuitability.PartiallySuitable, evidence.EventsLost > 0
                ? "Useful events were observed, but event loss prevents reliable accounting."
                : !coherent ? "Useful activity was observed, but attributed transfer byte accounting is missing."
                : !complete ? "Attributed transfer bytes were observed, but direction on the same accounting event is missing."
                : "Accounting fields were observed, but controlled byte validation did not establish reliability.");
        return Result(ProviderSuitability.Unsuitable, "Observed events provide no useful application/network accounting information.");
    }
}
