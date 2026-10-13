using NetTrace.AppUsageInstaller;
using AppUsageCollectorCore;
using AppUsageIpc;

internal static class HistoryClearTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Clear-history CLI accepts only the fixed command", Check.Sync(FixedInvocation)),
        ("Clear-history CLI rejects arbitrary roots, paths and options", Check.Sync(RejectOptions)),
        ("Running clear delegates only to management without offline storage writes", Running),
        ("Running management failure cannot fall back to an offline database write", RunningFailure),
        ("Absent storage reports missing without creating a lease or database", MissingStorage),
        ("Stopped owned service clears under lease without service control actions", Stopped),
        ("Absent service clears retained history without installing tracking", Absent),
        ("Stopped legacy collector cannot race offline clear by starting without the lease", LegacyStopped),
        ("Pending service state refuses offline writes", Pending),
        ("Offline clear rechecks SCM after acquiring ownership", StartedDuringLease),
        ("Stopped registration with a remaining process is refused", RemainingProcess),
        ("Foreign running registration cannot reach the management mutation", ForeignRegistration),
        ("Non-elevated history clear fails before inspecting storage or SCM", NonElevated),
        ("Invalid clear environment fails before SCM or mutation", InvalidEnvironment),
        ("Lease failure cannot clear history", LeaseFailure),
        ("Another collector or stale owned ETW session prevents offline clear", OfflineCollector),
        ("Offline storage disappearing safely reports missing after the lease", DisappearingStorage),
        ("Offline clear failure releases its lease and permits retry", OfflineFailure),
        ("Pre-cancelled clear performs no privileged or file operations", PreCancelled),
        ("Ordinary-token clear-history CLI reports elevation before mutation", NonElevatedCli),
        ("History file ownership excludes concurrent service and offline access", ExclusiveLease),
        ("History file ownership can be reacquired without deleting the lock", ReacquireLease),
        ("History ownership uses only the fixed NetTrace lock file", LeasePath),
        ("History ownership rejects relative roots and invalid deadlines", InvalidLease),
        ("Pre-cancelled ownership does not create a data directory", CancelledLease),
        ("Different isolated data roots have independent storage ownership", IndependentLeases),
    ];

    private static void FixedInvocation()
    {
        var value = InstallerInvocation.Parse(["clear-history"]);
        Check.Equal("clear-history", value.Operation, "fixed operation");
        Check.True(value.SourceDirectory is null && value.ClientSid is null, "no privileged caller inputs");
    }
    private static void RejectOptions()
    {
        foreach (string[] args in new[]
        {
            new[] { "clear-history", "--source", @"C:\ProgramData" },
            new[] { "clear-history", "--client-sid", "S-1-5-21-1-2-3-1001" },
            new[] { "clear-history", "--database", @"C:\other.db" },
            new[] { "clear-history", "--delete-directory" }, new[] { "Clear-History" },
            new[] { "clear-history", "extra" }
        }) Check.Throws<ArgumentException>(() => InstallerInvocation.Parse(args), "only fixed zero-option invocation");
    }
    private static async Task Running()
    {
        var platform = new Fake { State = HistoryServiceState.Running };
        var result = await new HistoryClearEngine(platform).ClearAsync();
        Check.True(result.StorageExists && result.ServiceRunning && result.ClearedRows == 7, "confirmed live clear");
        Check.Sequence(new[] { "elevation", "environment", "service", "owned", "storage", "management" }, platform.Calls, "running path has no offline access or service actions");
    }
    private static async Task RunningFailure()
    {
        var platform = new Fake { State = HistoryServiceState.Running, Fail = "management" };
        await Check.ThrowsAsync<InvalidOperationException>(() => new HistoryClearEngine(platform).ClearAsync(), "unsupported or unavailable management fails safely");
        Check.True(!platform.Calls.Contains("lease") && !platform.Calls.Contains("clear"), "no fallback mutation");
    }
    private static async Task MissingStorage()
    {
        var platform = new Fake { Storage = false };
        var result = await new HistoryClearEngine(platform).ClearAsync();
        Check.True(!result.StorageExists && result.ClearedRows == 0, "no fabricated success");
        Check.True(!platform.Calls.Contains("lease") && !platform.Calls.Contains("clear"), "no new storage");
    }
    private static async Task Stopped()
    {
        var platform = new Fake();
        var result = await new HistoryClearEngine(platform).ClearAsync();
        Check.True(result.StorageExists && !result.ServiceRunning, "stopped registration retained");
        Check.Sequence(new[] { "elevation", "environment", "service", "owned", "storage", "coordination", "lease", "service", "owned", "coordination", "no-collector", "storage", "clear", "release" }, platform.Calls, "transaction contained in exclusive ownership");
    }
    private static async Task Absent()
    {
        var platform = new Fake { State = HistoryServiceState.Missing };
        await new HistoryClearEngine(platform).ClearAsync();
        Check.True(!platform.Calls.Contains("owned") && platform.Calls.Contains("clear"), "retained database can clear without registration");
    }
    private static async Task LegacyStopped()
    {
        var platform = new Fake { Coordination = false };
        await Check.ThrowsAsync<AppUsageHistoryManagementUnavailableException>(() => new HistoryClearEngine(platform).ClearAsync(), "older stopped host must first be updated");
        Check.True(!platform.Calls.Contains("lease") && !platform.Calls.Contains("clear"), "no file or SQL mutation for incompatible installed host");
    }
    private static async Task Pending()
    {
        var platform = new Fake { State = HistoryServiceState.Pending };
        await Check.ThrowsAsync<InvalidOperationException>(() => new HistoryClearEngine(platform).ClearAsync(), "pending is not stopped");
        Check.True(!platform.Calls.Contains("lease"), "pending service cannot offline clear");
    }
    private static async Task StartedDuringLease()
    {
        var platform = new Fake { StateAfterLease = HistoryServiceState.Running };
        await Check.ThrowsAsync<InvalidOperationException>(() => new HistoryClearEngine(platform).ClearAsync(), "SCM state is checked under lease");
        Check.True(!platform.Calls.Contains("clear") && platform.Calls.Last() == "release", "state race leaves history unchanged and releases lock");
    }
    private static async Task RemainingProcess()
    {
        var platform = new Fake { ProcessId = 42 };
        await Check.ThrowsAsync<InvalidOperationException>(() => new HistoryClearEngine(platform).ClearAsync(), "stopped PID cannot access offline");
        Check.True(!platform.Calls.Contains("clear"), "no database mutation");
    }
    private static async Task ForeignRegistration()
    {
        var platform = new Fake { State = HistoryServiceState.Running, Fail = "owned" };
        await Check.ThrowsAsync<InvalidOperationException>(() => new HistoryClearEngine(platform).ClearAsync(), "ownership proof precedes clear");
        Check.True(!platform.Calls.Contains("management"), "foreign registration cannot trigger service clear");
    }
    private static async Task NonElevated()
    {
        var platform = new Fake { Elevated = false };
        await Check.ThrowsAsync<UnauthorizedAccessException>(() => new HistoryClearEngine(platform).ClearAsync(), "normal caller needs explicit UAC helper");
        Check.Sequence(new[] { "elevation" }, platform.Calls, "elevation checked first");
    }
    private static async Task InvalidEnvironment()
    {
        var platform = new Fake { Fail = "environment" };
        await Check.ThrowsAsync<InvalidOperationException>(() => new HistoryClearEngine(platform).ClearAsync(), "unsafe root rejected");
        Check.Sequence(new[] { "elevation", "environment" }, platform.Calls, "no history inspected");
    }
    private static async Task LeaseFailure()
    {
        var platform = new Fake { Fail = "lease" };
        await Check.ThrowsAsync<InvalidOperationException>(() => new HistoryClearEngine(platform).ClearAsync(), "busy store rejected");
        Check.True(!platform.Calls.Contains("clear"), "no clear without lock");
    }
    private static async Task OfflineCollector()
    {
        var platform = new Fake { Fail = "no-collector" };
        await Check.ThrowsAsync<InvalidOperationException>(() => new HistoryClearEngine(platform).ClearAsync(), "owned ETW presence rejects offline mode without stopping it");
        Check.True(!platform.Calls.Contains("clear") && platform.Calls.Last() == "release", "preserved history and released lock");
    }
    private static async Task DisappearingStorage()
    {
        var platform = new Fake { StorageAfterLease = false };
        var result = await new HistoryClearEngine(platform).ClearAsync();
        Check.True(!result.StorageExists && !platform.Calls.Contains("clear"), "never recreate vanished database");
    }
    private static async Task OfflineFailure()
    {
        var platform = new Fake { Fail = "clear" };
        await Check.ThrowsAsync<InvalidOperationException>(() => new HistoryClearEngine(platform).ClearAsync(), "database failure propagates");
        Check.Equal("release", platform.Calls.Last(), "lease released after failure");
        platform.Fail = null;
        Check.Equal(7, (await new HistoryClearEngine(platform).ClearAsync()).ClearedRows, "retry is possible");
    }
    private static async Task PreCancelled()
    {
        var platform = new Fake();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => new HistoryClearEngine(platform).ClearAsync(cancellation.Token), "cancel before any action");
        Check.Equal(0, platform.Calls.Count, "no operation before explicit launch");
    }
    private static async Task NonElevatedCli()
    {
        int exit = await NetTrace.AppUsageInstaller.Program.Main(["clear-history"]);
        Check.Equal(2, exit, "ordinary client remains unelevated and clear fails before accessing history");
    }
    private static async Task ExclusiveLease()
    {
        using var temporary = new TemporaryRoot();
        using var first = await AppUsageHistoryLease.AcquireAsync(temporary.Path, TimeSpan.Zero);
        await Check.ThrowsAsync<InvalidOperationException>(() => AppUsageHistoryLease.AcquireAsync(temporary.Path, TimeSpan.Zero), "live service and offline clear cannot share storage ownership");
    }
    private static async Task ReacquireLease()
    {
        using var temporary = new TemporaryRoot();
        using (await AppUsageHistoryLease.AcquireAsync(temporary.Path, TimeSpan.Zero)) { }
        using (await AppUsageHistoryLease.AcquireAsync(temporary.Path, TimeSpan.Zero)) { }
        Check.True(File.Exists(AppUsageHistoryLease.GetPath(temporary.Path)), "ownership file retained; no deletion gap");
    }
    private static async Task LeasePath()
    {
        using var temporary = new TemporaryRoot();
        Directory.CreateDirectory(System.IO.Path.Combine(temporary.Path, "NetTrace"));
        string sentinel = System.IO.Path.Combine(temporary.Path, "NetTrace", "connection-history.json");
        File.WriteAllText(sentinel, "physical sessions unchanged");
        using (await AppUsageHistoryLease.AcquireAsync(temporary.Path, TimeSpan.Zero)) { }
        Check.Equal("physical sessions unchanged", File.ReadAllText(sentinel), "only fixed app-usage lease created");
        Check.Equal(System.IO.Path.Combine(temporary.Path, "NetTrace", "app-usage.history.lock"), AppUsageHistoryLease.GetPath(temporary.Path), "fixed file name");
    }
    private static async Task InvalidLease()
    {
        Check.Throws<ArgumentException>(() => AppUsageHistoryLease.GetPath("relative"), "absolute data root required");
        using var temporary = new TemporaryRoot();
        await Check.ThrowsAsync<ArgumentOutOfRangeException>(() => AppUsageHistoryLease.AcquireAsync(temporary.Path, TimeSpan.FromSeconds(16)), "bounded acquisition window");
    }
    private static async Task CancelledLease()
    {
        using var temporary = new TemporaryRoot();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => AppUsageHistoryLease.AcquireAsync(temporary.Path, TimeSpan.Zero, cancelled.Token), "pre-cancelled ownership");
        Check.True(!Directory.Exists(System.IO.Path.Combine(temporary.Path, "NetTrace")), "no data directory created");
    }
    private static async Task IndependentLeases()
    {
        using var firstRoot = new TemporaryRoot(); using var secondRoot = new TemporaryRoot();
        using var first = await AppUsageHistoryLease.AcquireAsync(firstRoot.Path, TimeSpan.Zero);
        using var second = await AppUsageHistoryLease.AcquireAsync(secondRoot.Path, TimeSpan.Zero);
    }

    private sealed class Fake : IAppUsageHistoryClearSystem
    {
        public List<string> Calls { get; } = [];
        public bool Elevated = true, Storage = true, Coordination = true;
        public bool? StorageAfterLease;
        public HistoryServiceState State = HistoryServiceState.Stopped;
        public HistoryServiceState? StateAfterLease;
        public uint ProcessId;
        public string? Fail;
        private bool leased;
        private void Call(string name) { Calls.Add(name); if (Fail == name) throw new InvalidOperationException("injected " + name); }
        public void RequireElevation() { Call("elevation"); if (!Elevated) throw new UnauthorizedAccessException("Elevation required"); }
        public void ValidateClearEnvironment() => Call("environment");
        public HistoryServiceSnapshot ReadClearService()
        {
            Call("service"); var state = leased ? StateAfterLease ?? State : State;
            return new(state, state == HistoryServiceState.Missing ? null
                : new(InstallationConfiguration.ProductionServiceName, "fixed-image", "LocalSystem", InstallationConfiguration.OwnershipDescription,
                    state == HistoryServiceState.Running, state == HistoryServiceState.Running ? 99 : ProcessId));
        }
        public void ValidateClearRegistration(ServiceRegistration registration) => Call("owned");
        public bool SupportsOfflineCoordination(ServiceRegistration registration) { Call("coordination"); return Coordination; }
        public bool HistoryStorageExists() { Call("storage"); return leased ? StorageAfterLease ?? Storage : Storage; }
        public Task<int> ClearRunningAsync(CancellationToken token) { Call("management"); return Task.FromResult(7); }
        public Task<IDisposable> AcquireOfflineHistoryLeaseAsync(CancellationToken token)
        { Call("lease"); leased = true; return Task.FromResult<IDisposable>(new Lease(this)); }
        public void VerifyNoOfflineCollector() => Call("no-collector");
        public int ClearExistingHistory() { Call("clear"); return 7; }
        private sealed class Lease(Fake owner) : IDisposable { public void Dispose() { owner.Call("release"); owner.leased = false; } }
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NetTrace0054HistoryTests", Guid.NewGuid().ToString("N"));
        public TemporaryRoot() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            string allowed = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NetTrace0054HistoryTests")) + System.IO.Path.DirectorySeparatorChar;
            if (!System.IO.Path.GetFullPath(Path).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe temporary cleanup target.");
            Directory.Delete(Path, recursive: true);
        }
    }
}
