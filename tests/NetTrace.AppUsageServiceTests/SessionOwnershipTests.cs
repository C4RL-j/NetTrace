using System.Security.AccessControl;
using AppUsageCollectorCore;
using AppUsageEtwProbe;

internal static class SessionOwnershipTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("An absent exact ETW session requires no recovery", Check.Sync(Missing)),
        ("Only the exact project name, GUID, real-time mode, and live handle are eligible for stale recovery", Check.Sync(ExactOwned)),
        ("The exact observed Session 0 hybrid-persist mode is eligible for legacy orphan recovery", Check.Sync(LegacyHybridMode)),
        ("A bare real-time session without the verified hybrid policy is ineligible", Check.Sync(BareMode)),
        ("Contradictory stop and persist hybrid flags cannot identify an owned session", Check.Sync(ConflictingHybridFlags)),
        ("Additional unapproved ETW mode bits cannot become eligible through flag masking", Check.Sync(AdditionalModeBits)),
        ("Unrelated, prefix-matching, and case-variant ETW session names remain ineligible", Check.Sync(ForeignNames)),
        ("A matching session name with another GUID is never eligible for cleanup", Check.Sync(ForeignGuid)),
        ("File-backed, differently configured, and invalid-handle sessions fail closed", Check.Sync(ForeignModes)),
        ("The machine-wide ownership mutex ACL excludes ordinary clients and broad principals", Check.Sync(OwnershipAcl)),
    ];
    private static NativeEtwSessionDescriptor Owned() => new(SessionOwnership.SessionName, SessionOwnership.SessionGuid,
        NativeEtwController.ExpectedLogFileMode, false, 123, 0);
    private static void Missing() => Check.Equal(StaleSessionDecision.Missing, SessionOwnership.Evaluate(null), "no session present");
    private static void ExactOwned() => Check.Equal(StaleSessionDecision.RecoverOwned, SessionOwnership.Evaluate(Owned()), "exact project ownership signature");
    private static void LegacyHybridMode() => Check.Equal(StaleSessionDecision.RecoverOwned,
        SessionOwnership.Evaluate(Owned() with { LogFileMode = 0x08800100 }), "exact legacy mode observed on Session 0");
    private static void BareMode() => Check.Equal(StaleSessionDecision.RejectForeign,
        SessionOwnership.Evaluate(Owned() with { LogFileMode = NativeEtwController.RealTimeMode | NativeEtwController.IndependentMode }),
        "no generic real-time mode fallback");
    private static void ConflictingHybridFlags() => Check.Equal(StaleSessionDecision.RejectForeign,
        SessionOwnership.Evaluate(Owned() with { LogFileMode = 0x08c00100 }), "mutually inconsistent hybrid policy is rejected");
    private static void AdditionalModeBits()
    {
        foreach (uint mode in new[] { 0x08400101U, 0x08800101U, 0x08400102U, 0x08800104U, 0x18400100U })
            Check.Equal(StaleSessionDecision.RejectForeign, SessionOwnership.Evaluate(Owned() with { LogFileMode = mode }),
                "ownership checks compare approved complete modes, never mask foreign bits");
    }
    private static void ForeignNames()
    {
        foreach (string name in new[] { "NT Kernel Logger", "NetTrace", SessionOwnership.SessionName + "-other", SessionOwnership.SessionName.ToLowerInvariant() })
            Check.Equal(StaleSessionDecision.RejectForeign, SessionOwnership.Evaluate(Owned() with { Name = name }), "never stop another trace based on a prefix or generic name");
    }
    private static void ForeignGuid()
    {
        foreach (Guid guid in new[] { Guid.Empty, ProviderSchema.ProviderGuid, Guid.NewGuid() })
            Check.Equal(StaleSessionDecision.RejectForeign, SessionOwnership.Evaluate(Owned() with { SessionGuid = guid }), "project GUID is mandatory even for exact name");
    }
    private static void ForeignModes()
    {
        Check.Equal(StaleSessionDecision.RejectForeign, SessionOwnership.Evaluate(Owned() with { HasLogFile = true }), "never recover file-backed session");
        Check.Equal(StaleSessionDecision.RejectForeign, SessionOwnership.Evaluate(Owned() with { SessionHandle = 0 }), "invalid controller handle");
        foreach (uint mode in new[] { 0U, NativeEtwController.RealTimeMode, NativeEtwController.RealTimeMode | NativeEtwController.IndependentMode | 1 })
            Check.Equal(StaleSessionDecision.RejectForeign, SessionOwnership.Evaluate(Owned() with { LogFileMode = mode }), "session configuration must match exactly");
    }
    private static void OwnershipAcl()
    {
        var descriptor = new RawSecurityDescriptor(ServiceOwnershipLease.SecurityDescriptorSddl);
        Check.True((descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0, "protected ownership mutex DACL");
        var aces = descriptor.DiscretionaryAcl!.Cast<GenericAce>().OfType<CommonAce>().ToArray();
        Check.Equal(2, aces.Length, "only built-in service identities admitted");
        Check.True(aces.All(ace => ace.AceQualifier == AceQualifier.AccessAllowed), "only expected allow rules");
        Check.True(aces.Select(ace => ace.SecurityIdentifier.Value).ToHashSet().SetEquals(new[] { "S-1-5-18", "S-1-5-19" }),
            "normal clients cannot acquire ownership or replace the mutex DACL");
    }
}
