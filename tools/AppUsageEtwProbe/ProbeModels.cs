using System.Net;

namespace AppUsageEtwProbe;

public enum TrafficDirection { Send, Receive }
public enum IpFamily { Ipv4, Ipv6 }
public enum TrafficClass { Loopback, LocalToLocal, NonLoopback, Unknown }

public sealed record RawNetworkEvent(int EventId, int Version, int HeaderPid,
    DateTime TimestampUtc, IReadOnlyDictionary<string, object?> Payload);

public sealed record ProcessIdentity(int Pid, string Name, string? ExecutablePath,
    DateTime? StartTimeUtc, string AttributionStatus)
{
    // An unresolved PID is explicitly provisional, never a durable application identity.
    public string ApplicationKey => ExecutablePath is not null ? "path:" + ExecutablePath.ToUpperInvariant()
        : Name is "System" or "Unknown / Unattributed" ? Name
        : "name:" + Name.ToUpperInvariant();
    public string LifetimeKey => $"{ApplicationKey}|{Pid}|{StartTimeUtc?.Ticks.ToString() ?? "unverified"}";
}

public sealed record NetworkActivity(int Pid, DateTime TimestampUtc, long Bytes,
    TrafficDirection Direction, IpFamily Family, TrafficClass TrafficClass,
    IPAddress? SourceAddress, IPAddress? DestinationAddress, int? InterfaceIndex);

public sealed class ByteTotals
{
    public long Receive { get; private set; }
    public long Send { get; private set; }
    public long Total => SaturatingAdd(Receive, Send);
    public void Add(TrafficDirection direction, long bytes)
    {
        if (bytes < 0) return;
        if (direction == TrafficDirection.Receive) Receive = SaturatingAdd(Receive, bytes);
        else Send = SaturatingAdd(Send, bytes);
    }
    public static long SaturatingAdd(long a, long b) => b > long.MaxValue - a ? long.MaxValue : a + b;
}

public sealed class ProcessObservation(ProcessIdentity identity)
{
    public ProcessIdentity Identity { get; } = identity;
    public long EventCount { get; private set; }
    public ByteTotals Bytes { get; } = new();
    public Dictionary<string, ByteTotals> Breakdown { get; } = new();
    public DateTime? FirstEventUtc { get; private set; }
    public DateTime? LastEventUtc { get; private set; }
    public void Add(NetworkActivity activity)
    {
        EventCount++;
        FirstEventUtc = FirstEventUtc is null || activity.TimestampUtc < FirstEventUtc ? activity.TimestampUtc : FirstEventUtc;
        LastEventUtc = LastEventUtc is null || activity.TimestampUtc > LastEventUtc ? activity.TimestampUtc : LastEventUtc;
        Bytes.Add(activity.Direction, activity.Bytes);
        var key = $"{activity.Family}/{activity.TrafficClass}";
        if (!Breakdown.TryGetValue(key, out var totals)) Breakdown[key] = totals = new();
        totals.Add(activity.Direction, activity.Bytes);
    }
}

public sealed record ApplicationObservation(string Key, string Name, string? ExecutablePath,
    IReadOnlyList<ProcessObservation> Processes, ByteTotals Bytes);
