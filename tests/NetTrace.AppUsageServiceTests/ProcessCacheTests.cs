using AppUsageEtwProbe;

internal static class ProcessCacheTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Repeated ETW events reuse one process identity lookup and retained lifetime handle", Check.Sync(HotPath)),
        ("The process cache is bounded and evicts the least recently used lifetime", Check.Sync(BoundedLru)),
        ("Denied process identities are negatively cached until a monotonic retry interval expires", Check.Sync(NegativeTtl)),
        ("Exited processes retain attribution for buffered events preceding their exit", Check.Sync(LateEvents)),
        ("PID reuse replaces the old lifetime and never attributes earlier events to the replacement", Check.Sync(PidReuse)),
        ("An inconsistent process lookup PID is discarded and its lifetime handle disposed", Check.Sync(InconsistentLookup)),
        ("A failed lifetime-handle query gracefully refreshes process identity", Check.Sync(FailedLifetimeProbe)),
        ("System and zero PID buckets avoid process lookup entirely", Check.Sync(SpecialPids)),
        ("Resolver disposal releases every cached process handle exactly once", Check.Sync(DisposeCache)),
    ];
    private static void HotPath()
    {
        var lease = new Lease(Fixtures.Identity(123));
        using var resolver = new WindowsProcessResolver(_ => lease);
        for (int i = 0; i < 1000; i++) Check.Equal("client.exe", resolver.Resolve(123, Fixtures.Epoch.AddSeconds(1)).Name, "cached identity");
        Check.Equal(1L, resolver.LookupCount, "no repeated executable/path/start-time lookup per event");
        Check.Equal(1, resolver.CacheCount, "one retained lifetime entry");
        Check.Equal(1, lease.IdentityReads, "identity materialized only when populating cache");
    }
    private static void BoundedLru()
    {
        var leases = new List<Lease>();
        using var resolver = new WindowsProcessResolver(pid =>
        { var lease = new Lease(Fixtures.Identity(pid)); leases.Add(lease); return lease; }, capacity: 2);
        resolver.Resolve(11, Fixtures.Epoch.AddSeconds(1)); resolver.Resolve(22, Fixtures.Epoch.AddSeconds(1));
        resolver.Resolve(11, Fixtures.Epoch.AddSeconds(1)); resolver.Resolve(33, Fixtures.Epoch.AddSeconds(1));
        Check.Equal(2, resolver.CacheCount, "cache never exceeds configured bound");
        Check.Equal(1, leases.Single(lease => lease.Identity.Pid == 22).DisposeCount, "least recently used process handle closed");
        resolver.Resolve(11, Fixtures.Epoch.AddSeconds(1)); Check.Equal(3L, resolver.LookupCount, "recently touched PID remained cached");
        resolver.Resolve(22, Fixtures.Epoch.AddSeconds(1)); Check.Equal(4L, resolver.LookupCount, "evicted PID can be resolved again");
    }
    private static void NegativeTtl()
    {
        var clock = new ManualClock(); bool available = false;
        using var resolver = new WindowsProcessResolver(pid => available ? new Lease(Fixtures.Identity(pid))
            : throw new UnauthorizedAccessException(), timeProvider: clock, unavailableRetryInterval: TimeSpan.FromSeconds(2));
        Check.True(resolver.Resolve(123, Fixtures.Epoch.AddSeconds(1)).ExecutablePath is null, "denied lookup is visibly unresolved");
        available = true; clock.Advance(TimeSpan.FromSeconds(1)); clock.MoveWallClock(TimeSpan.FromDays(-1));
        Check.True(resolver.Resolve(123, Fixtures.Epoch.AddSeconds(1)).ExecutablePath is null, "negative cache suppresses per-event denied lookup");
        Check.Equal(1L, resolver.LookupCount, "TTL uses elapsed monotonic time");
        clock.Advance(TimeSpan.FromSeconds(1));
        Check.True(resolver.Resolve(123, Fixtures.Epoch.AddSeconds(1)).ExecutablePath is not null, "lookup retries at TTL boundary");
        Check.Equal(2L, resolver.LookupCount, "one retry after expiry");
    }
    private static void LateEvents()
    {
        var lease = new Lease(Fixtures.Identity(123)) { Exited = true, ExitAt = Fixtures.Epoch.AddSeconds(2) };
        using var resolver = new WindowsProcessResolver(_ => lease);
        resolver.Resolve(123, Fixtures.Epoch.AddSeconds(1));
        Check.Equal("client.exe", resolver.Resolve(123, Fixtures.Epoch.AddSeconds(2)).Name, "event at exit boundary retains old process identity");
        Check.Equal(1L, resolver.LookupCount, "buffered prior events do not switch PID lifetime");
    }
    private static void PidReuse()
    {
        var first = new Lease(Fixtures.Identity(123));
        var second = new Lease(Fixtures.Identity(123, Fixtures.Epoch.AddSeconds(3)) with { Name = "replacement.exe" });
        int lookups = 0;
        using var resolver = new WindowsProcessResolver(_ => lookups++ == 0 ? first : second);
        var original = resolver.Resolve(123, Fixtures.Epoch.AddSeconds(1));
        first.Exited = true; first.ExitAt = Fixtures.Epoch.AddSeconds(2);
        var replacement = resolver.Resolve(123, Fixtures.Epoch.AddSeconds(4));
        Check.True(original.LifetimeKey != replacement.LifetimeKey, "start time splits reused PID lifetime");
        Check.Equal("replacement.exe", replacement.Name, "replacement resolved after old exit");
        Check.Equal(1, first.DisposeCount, "old process handle closed during replacement");
        Check.True(resolver.Resolve(123, Fixtures.Epoch.AddSeconds(1)).ExecutablePath is null,
            "earlier event is not falsely assigned to replacement process");
    }
    private static void InconsistentLookup()
    {
        var wrong = new Lease(Fixtures.Identity(999)); using var resolver = new WindowsProcessResolver(_ => wrong);
        var unknown = resolver.Resolve(123, Fixtures.Epoch.AddSeconds(1));
        Check.Equal(123, unknown.Pid, "payload PID preserved despite inconsistent lookup");
        Check.True(unknown.ExecutablePath is null, "no wrong application attribution");
        Check.Equal(1, wrong.DisposeCount, "inconsistent lifetime handle closed");
    }
    private static void FailedLifetimeProbe()
    {
        var first = new Lease(Fixtures.Identity(123)) { ThrowExited = true };
        int lookups = 0; using var resolver = new WindowsProcessResolver(pid => lookups++ == 0 ? first : new Lease(Fixtures.Identity(pid)));
        resolver.Resolve(123, Fixtures.Epoch.AddSeconds(1)); resolver.Resolve(123, Fixtures.Epoch.AddSeconds(1));
        Check.Equal(2L, resolver.LookupCount, "failed retained-handle query triggers one fresh lookup");
        Check.Equal(1, first.DisposeCount, "broken lifetime handle disposed");
    }
    private static void SpecialPids()
    {
        using var resolver = new WindowsProcessResolver(_ => throw new InvalidOperationException("Must not resolve special PID"));
        Check.Equal("System", resolver.Resolve(4, Fixtures.Epoch).Name, "System label");
        Check.True(resolver.Resolve(0, Fixtures.Epoch).ExecutablePath is null, "unknown PID zero");
        Check.Equal(0L, resolver.LookupCount, "special buckets bypass process lookup");
        Check.Equal(0, resolver.CacheCount, "special PIDs do not consume cache capacity");
    }
    private static void DisposeCache()
    {
        var leases = new List<Lease>(); var resolver = new WindowsProcessResolver(pid =>
        { var lease = new Lease(Fixtures.Identity(pid)); leases.Add(lease); return lease; });
        resolver.Resolve(11, Fixtures.Epoch); resolver.Resolve(22, Fixtures.Epoch);
        resolver.Dispose(); resolver.Dispose();
        Check.Equal(0, resolver.CacheCount, "disposed cache emptied");
        Check.True(leases.All(lease => lease.DisposeCount == 1), "all handles closed exactly once");
        bool rejected = false; try { resolver.Resolve(11, Fixtures.Epoch); } catch (ObjectDisposedException) { rejected = true; }
        Check.True(rejected, "disposed resolver cannot recreate native handles");
    }
    private sealed class Lease(ProcessIdentity identity) : IProcessIdentityLease
    {
        public bool Exited { get; set; }
        public DateTime? ExitAt { get; set; }
        public bool ThrowExited { get; init; }
        public int IdentityReads { get; private set; }
        public int DisposeCount { get; private set; }
        public ProcessIdentity Identity { get { IdentityReads++; return identity; } }
        public bool HasExited => ThrowExited ? throw new InvalidOperationException("Broken fixture lifetime handle") : Exited;
        public DateTime? ExitTimeUtc => ExitAt;
        public void Dispose() => DisposeCount++;
    }
}
