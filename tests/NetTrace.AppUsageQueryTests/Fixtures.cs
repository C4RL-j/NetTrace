using AppUsageCollectorCore;
using AppUsageIpc;
using AppUsageStorage;
using ApplicationIdentity = AppUsageStorage.ApplicationIdentity;

internal static class Fixtures
{
    public static readonly DateOnly Today = new(2026, 10, 13);
    public static readonly DateTimeOffset Now = new(2026, 10, 12, 20, 15, 0, TimeSpan.Zero);
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("QueryTestUtcPlus8",
        TimeSpan.FromHours(8), "Query test local", "Query test local");
    public static readonly ApplicationIdentity Brave = ApplicationIdentity.FromExecutablePath(@"C:\Apps\Brave\brave.exe", "Brave");
    public static readonly ApplicationIdentity NetMod = ApplicationIdentity.FromExecutablePath(@"C:\Apps\NetMod\NetMod.exe", "NetMod");
    public static readonly UsagePersistenceHealth Healthy = new(true, Now, false, 1, 1, 0, 0, null, 1);
    public static IReadOnlyList<DailyApplicationUsage> Rows =>
    [
        Row(Today, Brave, 1000, 200), Row(Today, NetMod, 500, 500),
        Row(Today.AddDays(-1), Brave, 2000, 300)
    ];
    public static DailyApplicationUsage Row(DateOnly date, ApplicationIdentity app, long download, long upload,
        DateTimeOffset? lastSeen = null) => new(date, app.Key, app.DisplayName, app.ExecutableName,
            app.ExecutablePath, download, upload, lastSeen ?? Now);
    public static UsageDelta Delta(DateOnly date, ApplicationIdentity app, long download, long upload) =>
        new(date, app, download, upload, Now);
    public static AppUsageQueryService Service(IReadOnlyList<DailyApplicationUsage>? rows = null,
        UsagePersistenceHealth? health = null, TimeProvider? clock = null) => new(
            (start, end) => (rows ?? Rows).Where(row => row.LocalDate >= start && row.LocalDate <= end).ToArray(),
            () => health ?? Healthy, clock ?? new FixedClock(Now), Zone);
    public static AppUsageQueryService Database(StorageScope scope, UsagePersistenceHealth? health = null) =>
        AppUsageQueryService.ForDatabase(scope.Root, () => health ?? Healthy, new FixedClock(Now), Zone);
    public static string PipeName() => "NetTrace.AppUsage0043.Tests." + Guid.NewGuid().ToString("N");
    public static CollectorCapabilities Capabilities => new(false, true, true, true, false, true,
        false, "Microsoft-Windows-Kernel-Network", "Logical application usage") { ServiceIdentity = "LocalSystem" };
    public static CollectorSnapshot Snapshot()
    {
        var counters = new TrafficCounters(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        return new(Guid.NewGuid(), Now.UtcDateTime, TimeSpan.Zero, 0, -1, 0, 0, 0, [],
            new("system", "System", null, 0, counters, []),
            new("unknown", "Unknown / Unattributed", null, 0, counters, []), Capabilities,
            new(0, 0, 0)) { Health = new(Now.UtcDateTime, true,
                "NetTrace-AppUsage0043-Fixture", null, 0, "LocalSystem", true) };
    }
    public static IpcClient Client(string name, int version = Protocol.ServiceVersion) =>
        new(name, serverVerifier: _ => true, protocolVersion: version);
    public static IpcServer Server(string name, Func<AppUsageRange, CancellationToken, Task<AppUsageQueryResult>> query,
        Func<System.IO.Pipes.NamedPipeServerStream, bool>? verifier = null) =>
        new(name, () => Capabilities, Snapshot, clientVerifier: verifier,
            protocolVersion: Protocol.ServiceVersion, appUsageQuery: query);
}

internal sealed class FixedClock(DateTimeOffset value) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => value;
}

internal sealed class StorageScope : IDisposable
{
    private static readonly string Parent = Path.Combine(Path.GetTempPath(), "NetTrace.AppUsageQueryTests");
    public string Root { get; } = Path.Combine(Parent, Guid.NewGuid().ToString("N"));
    public string DatabasePath => DailyAppUsageStore.GetDatabasePath(Root);
    public DailyAppUsageStore Open() => new(Root);
    public void Seed()
    {
        var store = Open();
        store.AddUsageDeltas(Fixtures.Rows.Select(row => new UsageDelta(row.LocalDate,
            row.ApplicationIdentity == Fixtures.Brave.Key ? Fixtures.Brave : Fixtures.NetMod,
            row.DownloadBytes, row.UploadBytes, row.LastSeen)).ToArray());
    }
    public void Dispose()
    {
        var resolved = Path.GetFullPath(Root);
        if (!resolved.StartsWith(Path.GetFullPath(Parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Query test cleanup escaped isolated storage.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
