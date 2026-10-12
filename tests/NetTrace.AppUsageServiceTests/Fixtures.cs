using System.Net;
using AppUsageElevatedCollector;
using AppUsageEtwProbe;
using AppUsageIpc;

internal static class Fixtures
{
    public static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public static readonly ProviderSchema Schema = new(true, 3,
    [
        new(700, 1, TrafficDirection.Send, IpFamily.Ipv4, []),
        new(701, 1, TrafficDirection.Receive, IpFamily.Ipv4, []),
        new(800, 1, TrafficDirection.Send, IpFamily.Ipv6, []),
        new(801, 1, TrafficDirection.Receive, IpFamily.Ipv6, [])
    ]);
    public static readonly IReadOnlySet<IPAddress> Local = new HashSet<IPAddress>
        { IPAddress.Parse("192.0.2.10"), IPAddress.Parse("192.0.2.11") };
    public static ProcessIdentity Identity(int pid, DateTime? start = null) =>
        new(pid, "client.exe", @"C:\Apps\client.exe", start ?? Epoch, "Fixture executable resolved");
    public static CollectorAggregation Aggregation(TimeProvider? clock = null) =>
        new(Schema, new Resolver((pid, _) => Identity(pid)), Local, timeProvider: clock);
    public static CollectorCapabilities Capabilities => Aggregation().GetCapabilities() with
        { CollectorElevated = true, ServiceIdentity = "LocalSystem" };
    public static CollectorSnapshot Snapshot() => Aggregation().GetSnapshot() with { Capabilities = Capabilities };
    public static string PipeName() => "NetTrace.AppUsage003.Tests." + Guid.NewGuid().ToString("N");
    public static RawNetworkEvent Raw(int id = 700, int pid = 123, long bytes = 64, DateTime? timestamp = null) =>
        new(id, 1, 4, timestamp ?? Epoch.AddSeconds(1), new Dictionary<string, object?>
        {
            ["PID"] = pid, ["size"] = bytes,
            ["saddr"] = id >= 800 ? IPAddress.IPv6Loopback : IPAddress.Loopback,
            ["daddr"] = id >= 800 ? IPAddress.IPv6Loopback : IPAddress.Parse("127.1.2.3")
        });
    public sealed class Resolver(Func<int, DateTime, ProcessIdentity> resolve) : IProcessResolver
    {
        public ProcessIdentity Resolve(int pid, DateTime eventUtc) => resolve(pid, eventUtc);
    }
}

internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset utc = new(Fixtures.Epoch);
    private long timestamp;
    public override DateTimeOffset GetUtcNow() => utc;
    public override long GetTimestamp() => timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public void Advance(TimeSpan elapsed) { timestamp += elapsed.Ticks; utc += elapsed; }
    public void MoveWallClock(TimeSpan change) => utc += change;
}
