using System.Net;
using AppUsageCollectorCore;
using AppUsageElevatedCollector;
using AppUsageEtwProbe;
using AppUsageStorage;
using ApplicationIdentity = AppUsageStorage.ApplicationIdentity;

internal static class Fixtures
{
    public static readonly DateTime Epoch = new(2026, 10, 13, 8, 0, 0, DateTimeKind.Utc);
    public static readonly DateOnly Today = new(2026, 10, 13);
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("CheckpointTestUtcPlus8",
        TimeSpan.FromHours(8), "Checkpoint test local", "Checkpoint test local");
    public static readonly ProviderSchema Schema = new(true, 4,
    [
        new(700, 1, TrafficDirection.Send, IpFamily.Ipv4, []),
        new(701, 1, TrafficDirection.Receive, IpFamily.Ipv4, []),
        new(800, 1, TrafficDirection.Send, IpFamily.Ipv6, []),
        new(801, 1, TrafficDirection.Receive, IpFamily.Ipv6, [])
    ]);
    public static readonly IReadOnlySet<IPAddress> Local = new HashSet<IPAddress>
    {
        IPAddress.Parse("192.0.2.10"), IPAddress.Parse("192.0.2.11"),
        IPAddress.Parse("2001:db8::10"), IPAddress.Parse("2001:db8::11")
    };
    public static ApplicationIdentity Brave(string? name = "Brave") =>
        ApplicationIdentity.FromExecutablePath(@"C:\Apps\Brave\brave.exe", name);
    public static ApplicationIdentity Editor() => ApplicationIdentity.FromExecutablePath(@"C:\Apps\Editor\editor.exe", "Editor");
    public static UsageDelta Delta(long download = 10, long upload = 0, ApplicationIdentity? app = null,
        DateOnly? date = null, DateTimeOffset? seen = null) => new(date ?? Today, app ?? Brave(), download, upload, seen ?? new(Epoch));
    public static ProcessIdentity Identity(int pid, string? path = @"C:\Apps\Brave\brave.exe", DateTime? start = null) =>
        new(pid, "Brave", path, start ?? Epoch.AddHours(-1), "Fixture cached process identity");
    public static RawNetworkEvent Raw(int id = 701, int pid = 123, long bytes = 64,
        DateTime? timestamp = null, TrafficClass traffic = TrafficClass.NonLoopback)
    {
        bool ipv6 = id >= 800;
        IPAddress? source = ipv6 ? IPAddress.Parse("2001:db8::10") : IPAddress.Parse("192.0.2.10");
        IPAddress? destination = ipv6 ? IPAddress.Parse("2001:db8:1::20") : IPAddress.Parse("198.51.100.20");
        if (traffic == TrafficClass.Loopback) source = destination = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        if (traffic == TrafficClass.LocalToLocal) destination = ipv6 ? IPAddress.Parse("2001:db8::11") : IPAddress.Parse("192.0.2.11");
        if (traffic == TrafficClass.Unknown) source = null;
        return new(id, 1, 4, timestamp ?? Epoch, new Dictionary<string, object?>
        { ["PID"] = pid, ["size"] = bytes, ["saddr"] = source, ["daddr"] = destination });
    }
    public static CollectorAggregation Aggregation(UsagePersistence persistence, CountingResolver? resolver = null) =>
        new(Schema, resolver ?? new CountingResolver((pid, _) => Identity(pid)), Local,
            acceptedActivity: new UsageEventPersistence(persistence, Zone).RecordActivity);
}

internal sealed class CountingResolver(Func<int, DateTime, ProcessIdentity> resolve) : IProcessResolver
{
    public int Calls { get; private set; }
    public ProcessIdentity Resolve(int pid, DateTime eventTimeUtc) { Calls++; return resolve(pid, eventTimeUtc); }
}

internal sealed class StorageScope : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "NetTrace.AppUsageCheckpointTests", Guid.NewGuid().ToString("N"));
    public string DatabasePath => DailyAppUsageStore.GetDatabasePath(Root);
    public StorageScope() => Directory.CreateDirectory(Root);
    public DailyAppUsageStore Open() => new(Root);
    public void Dispose()
    {
        var resolved = Path.GetFullPath(Root);
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NetTrace.AppUsageCheckpointTests")) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Checkpoint test cleanup escaped isolated storage.");
        Directory.Delete(resolved, recursive: true);
    }
}

// This backend delegates every durable write to 004.1. Gates inject failures and
// slow I/O without a native ETW session, service installation, or external traffic.
internal sealed class TestBackend : IUsageCheckpointStore
{
    private readonly DailyAppUsageStore? sqlite;
    public TestBackend(DailyAppUsageStore? sqlite = null) => this.sqlite = sqlite;
    public int InitializeCalls { get; private set; }
    public int RetentionCalls { get; private set; }
    public int FailuresRemaining { get; set; }
    public string FailureMessage { get; set; } = "Injected batch rollback";
    public int InitializationFailuresRemaining { get; set; }
    public int RetentionFailuresRemaining { get; set; }
    public int AddCalls { get; private set; }
    public List<UsageDelta[]> Attempts { get; } = [];
    public List<UsageDelta[]> Committed { get; } = [];
    public DateOnly? RetentionCutoff { get; private set; }
    public TaskCompletionSource? Entered { get; set; }
    public TaskCompletionSource? Release { get; set; }
    public CancellationToken DispatchToken { get; private set; }
    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InitializeCalls++;
        if (InitializationFailuresRemaining-- > 0) throw new IOException("Injected initialization failure");
        sqlite?.Initialize();
        return Task.CompletedTask;
    }
    public async Task AddUsageDeltasAsync(IReadOnlyCollection<UsageDelta> deltas, CancellationToken cancellationToken)
    {
        AddCalls++;
        DispatchToken = cancellationToken;
        var batch = deltas.ToArray();
        Attempts.Add(batch);
        Entered?.TrySetResult();
        if (Release is not null) await Release.Task;
        if (FailuresRemaining-- > 0) throw new IOException(FailureMessage);
        sqlite?.AddUsageDeltas(batch);
        Committed.Add(batch);
    }
    public Task<int> RetentionCleanupAsync(DateOnly cutoffDate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RetentionCalls++;
        RetentionCutoff = cutoffDate;
        if (RetentionFailuresRemaining-- > 0) throw new IOException("Injected retention failure");
        return Task.FromResult(sqlite?.RetentionCleanup(cutoffDate) ?? 0);
    }
}

internal sealed class ManualClock : TimeProvider
{
    private readonly object gate = new();
    private DateTimeOffset utc = new(Fixtures.Epoch);
    private long timestamp;
    private readonly List<ManualTimer> timers = [];
    public override DateTimeOffset GetUtcNow() { lock (gate) return utc; }
    public override long GetTimestamp() { lock (gate) return timestamp; }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public int ActiveTimers { get { lock (gate) return timers.Count(x => !x.Disposed); } }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (gate)
        {
            var timer = new ManualTimer(this, callback, state);
            timers.Add(timer); timer.Change(dueTime, period); return timer;
        }
    }
    public void Advance(TimeSpan elapsed)
    {
        List<(TimerCallback Callback, object? State)> fire = [];
        lock (gate)
        {
            timestamp += elapsed.Ticks; utc += elapsed;
            foreach (var timer in timers.Where(x => !x.Disposed))
                if (timer.DueTimestamp is long due && due <= timestamp)
                {
                    fire.Add((timer.Callback, timer.State));
                    timer.DueTimestamp = timer.Period > TimeSpan.Zero ? timestamp + timer.Period.Ticks : null;
                }
        }
        foreach (var (callback, state) in fire) callback(state);
    }
    private sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public long? DueTimestamp { get; set; }
        public TimeSpan Period { get; private set; }
        public bool Disposed { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                if (Disposed) return false;
                DueTimestamp = dueTime == Timeout.InfiniteTimeSpan ? null : owner.timestamp + dueTime.Ticks;
                Period = period; return true;
            }
        }
        public void Dispose() { lock (owner.gate) Disposed = true; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
