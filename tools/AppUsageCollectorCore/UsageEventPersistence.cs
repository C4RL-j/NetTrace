using System.Runtime.CompilerServices;
using AppUsageEtwProbe;
using AppUsageStorage;
using ApplicationIdentity = AppUsageStorage.ApplicationIdentity;

namespace AppUsageCollectorCore;

// Invoked only for an accepted event, with the identity already resolved by live aggregation.
public sealed class UsageEventPersistence(UsagePersistence persistence, TimeZoneInfo? localTimeZone = null)
{
    private readonly UsagePersistence persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
    private readonly TimeZoneInfo? localTimeZone = localTimeZone;
    private readonly ConditionalWeakTable<ProcessIdentity, ApplicationIdentity> identities = new();

    public void RecordActivity(NetworkActivity activity, ProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(identity);
        if (activity.Bytes < 0) throw new ArgumentOutOfRangeException(nameof(activity), "Observed usage cannot be negative.");
        // Local-to-local and unknown-endpoint diagnostics cannot prove qualifying external usage.
        if (activity.Bytes == 0 || activity.TrafficClass != TrafficClass.NonLoopback) return;
        var application = identities.GetValue(identity, ResolveIdentity);
        var observedAt = new DateTimeOffset(DateTime.SpecifyKind(activity.TimestampUtc, DateTimeKind.Utc));
        persistence.Record(UsageDelta.FromObservation(observedAt, localTimeZone ?? TimeZoneInfo.Local, application,
            activity.Direction == TrafficDirection.Receive ? activity.Bytes : 0,
            activity.Direction == TrafficDirection.Send ? activity.Bytes : 0));
    }

    private static ApplicationIdentity ResolveIdentity(ProcessIdentity identity)
    {
        if (identity.Pid == 4) return ApplicationIdentity.System;
        if (identity.Pid == 0 || identity.ExecutablePath is null || identity.StartTimeUtc is null)
            return ApplicationIdentity.Unknown;
        try
        {
            return ApplicationIdentity.FromExecutablePath(identity.ExecutablePath,
                string.IsNullOrWhiteSpace(identity.Name) ? null : identity.Name);
        }
        catch (ArgumentException)
        {
            // Unusable path metadata never discards accepted non-loopback bytes.
            return ApplicationIdentity.Unknown;
        }
    }
}
