using System.Diagnostics;
using System.IO.Pipes;
using AppUsageCollectorCore;
using AppUsageEtwProbe;
using AppUsageIpc;

internal static class PipeTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("An authenticated actual unelevated pipe client receives Today 7D and 30D fixture history", UnelevatedRanges),
        ("A query requires the existing authenticated Hello handshake", HandshakeRequired),
        ("Peer rejection prevents the new storage query supplier from running", PeerRejection),
        ("Remote and UNC pipe names remain rejected before connection", Check.Sync(LocalOnly)),
        ("Unknown operations cannot reach the persisted history supplier", UnknownOperation),
        ("Missing null or mismatched typed query data is rejected without database access", InvalidOperationPayload),
        ("Malformed and oversized raw pipe requests leave valid history clients usable", RawMalformed),
        ("Invalid typed client ranges do not disturb an already connected pipe", InvalidClientRange),
        ("Incompatible protocol cannot invoke the service history supplier", IncompatibleProtocol),
        ("A protocol 1 collector cannot expose the new history handler", Check.Sync(LegacyCollectorGuard)),
        ("An older protocol 2 service without history support fails the new query safely", OldServiceUnsupported),
        ("All legacy capabilities and snapshot operations remain usable with history enabled", LegacyOperations),
        ("Unavailable history keeps the pipe connected for capabilities and future queries", UnavailableKeepsConnection),
        ("Degraded persisted history keeps the pipe connected and transfers its durable rows", DegradedKeepsConnection),
        ("An oversized history result returns bounded unavailable history on the same pipe", OversizedResultFallback),
        ("Negative inconsistent duplicated and misdated query responses are rejected", InvalidResponses),
        ("A history response for the wrong requested range is rejected", WrongRange),
        ("Caller cancellation reaches a bounded pending pipe query", CancelQuery),
        ("A server deadline cancels a stalled history supplier and permits a new client", ServerDeadline),
        ("A stopped and restarted history service supports the same client reconnect", Reconnect),
        ("An absent service returns bounded unavailability and querying stays safe", AbsentService),
        ("Modest repeated real SQLite pipe queries measure latency without resource growth", RepeatedQueries)
    ];

    private static async Task UnelevatedRanges()
    {
        Check.True(!EtwCapture.IsElevated, "test client process has a real unelevated token");
        using var scope = new StorageScope(); scope.Seed(); var query = Fixtures.Database(scope);
        await WithServer(query.QueryAsync, async (client, server) =>
        {
            foreach (var (range, expected) in new[] { (AppUsageRange.Today, 2200L), (AppUsageRange.SevenDays, 4500L), (AppUsageRange.ThirtyDays, 4500L) })
            {
                var result = await client.GetAppUsageRangeAsync(range);
                Check.Equal(IpcState.Connected, result.State, "history delivered through authenticated local pipe");
                Check.Equal(expected, result.Value!.LogicalTotalBytes, "exact pipe fixture total");
            }
            Check.Equal(1L, server.AcceptedConnections, "one authenticated normal-token connection");
        });
    }
    private static async Task HandshakeRequired()
    {
        int queries = 0; string name = Fixtures.PipeName();
        await using var server = Fixtures.Server(name, (range, token) => { queries++; return Fixtures.Service().QueryAsync(range, token); }); _ = server.RunAsync();
        using var raw = NativePipeSecurity.CreateClient(name); await raw.ConnectAsync(2000);
        await IpcProtocol.WriteRequestAsync(raw, new(Protocol.ServiceVersion, "GetAppUsageRange", new(AppUsageRange.Today)));
        Check.Equal("HandshakeRequired", (await IpcProtocol.ReadResponseAsync(raw, expectedVersion: Protocol.ServiceVersion)).Status, "query cannot skip Hello");
        Check.Equal(0, queries, "no history supplier before handshake");
    }
    private static async Task PeerRejection()
    {
        int queries = 0; string name = Fixtures.PipeName();
        await using var server = Fixtures.Server(name, (range, token) => { queries++; return Fixtures.Service().QueryAsync(range, token); }, _ => false); _ = server.RunAsync();
        await using var client = Fixtures.Client(name);
        Check.Equal(IpcState.Disconnected, (await client.ConnectAsync()).State, "server rejects unauthenticated peer");
        Check.Equal(0, queries, "authentication precedes history read"); Check.Equal(1L, server.RejectedClients, "denied client counted");
    }
    private static void LocalOnly()
    {
        foreach (string name in new[] { @"\\remote\pipe\NetTrace", @"\\.\pipe\NetTrace", "remote/NetTrace", "remote\\NetTrace" })
            Check.Throws<ArgumentException>(() => new IpcClient(name), "only bare local pipe names accepted");
    }
    private static async Task UnknownOperation()
    {
        int queries = 0; string name = Fixtures.PipeName();
        await using var server = Fixtures.Server(name, (range, token) => { queries++; return Fixtures.Service().QueryAsync(range, token); }); _ = server.RunAsync();
        using var raw = await HandshakenRaw(name);
        await IpcProtocol.WriteRequestAsync(raw, new(Protocol.ServiceVersion, "ExecuteSql"));
        Check.Equal("UnknownOperation", (await IpcProtocol.ReadResponseAsync(raw, expectedVersion: Protocol.ServiceVersion)).Status, "unknown operation rejected");
        Check.Equal(0, queries, "unsupported action cannot read history");
    }
    private static async Task InvalidOperationPayload()
    {
        int queries = 0; string name = Fixtures.PipeName();
        await using var server = Fixtures.Server(name, (range, token) => { queries++; return Fixtures.Service().QueryAsync(range, token); }); _ = server.RunAsync();
        foreach (var request in new[] { new PipeRequest(2, "GetAppUsageRange"),
            new PipeRequest(2, "GetCapabilities", new(AppUsageRange.Today)), new PipeRequest(2, "Hello", new(AppUsageRange.Today)) })
        {
            using var raw = await HandshakenRaw(name); await IpcProtocol.WriteRequestAsync(raw, request);
            Check.True((await IpcProtocol.ReadResponseAsync(raw, expectedVersion: 2)).Status != "Ok", "query envelope only accepted for approved operation");
        }
        Check.Equal(0, queries, "invalid operation payloads do not touch storage");
    }
    private static async Task RawMalformed()
    {
        var query = Fixtures.Service(); string name = Fixtures.PipeName();
        await using var server = Fixtures.Server(name, query.QueryAsync); _ = server.RunAsync();
        foreach (byte[] bytes in new[]
        {
            ProtocolTests.Frame("{\"protocolVersion\":2,\"operation\":\"GetAppUsageRange\",\"appUsageQuery\":{\"range\":\"Today\",\"sql\":\"SELECT private\"}}"),
            ProtocolTests.Frame("{\"protocolVersion\":2,\"operation\":\"GetAppUsageRange\",\"appUsageQuery\":{\"range\":7}}"),
            ProtocolTests.Header(Protocol.MaximumRequestBytes + 1)
        })
        {
            using var raw = await HandshakenRaw(name); await raw.WriteAsync(bytes);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); int length;
            try { length = await raw.ReadAsync(new byte[1], timeout.Token); } catch (IOException) { length = 0; }
            Check.Equal(0, length, "malformed request closes offending connection");
        }
        await using var client = Fixtures.Client(name); Check.Equal(IpcState.Connected, (await client.ConnectAsync()).State, "valid client still connects");
        Check.Equal(2200L, (await client.GetAppUsageRangeAsync(AppUsageRange.Today)).Value!.LogicalTotalBytes, "healthy history survives invalid peers");
    }
    private static async Task InvalidClientRange()
    {
        await WithServer(Fixtures.Service().QueryAsync, async (client, _) =>
        {
            await Check.ThrowsAsync<ArgumentOutOfRangeException>(async () => { await client.GetAppUsageRangeAsync((AppUsageRange)2); }, "typed client rejects unsupported enum");
            Check.Equal(IpcState.Connected, (await client.GetAppUsageRangeAsync(AppUsageRange.Today)).State, "rejected caller input leaves connection usable");
        });
    }
    private static async Task IncompatibleProtocol()
    {
        int queries = 0; string name = Fixtures.PipeName();
        await using var server = Fixtures.Server(name, (range, token) => { queries++; return Fixtures.Service().QueryAsync(range, token); }); _ = server.RunAsync();
        await using var client = Fixtures.Client(name, Protocol.Version);
        Check.Equal(IpcState.IncompatibleProtocol, (await client.ConnectAsync()).State, "legacy protocol explicit incompatibility");
        Check.Equal(0, queries, "version mismatch before new storage supplier");
    }
    private static void LegacyCollectorGuard() => Check.Throws<ArgumentException>(() => new IpcServer(
        Fixtures.PipeName(), () => Fixtures.Capabilities, Fixtures.Snapshot,
        appUsageQuery: Fixtures.Service().QueryAsync), "collector protocol 1 cannot gain privileged history reads");
    private static async Task OldServiceUnsupported()
    {
        string name = Fixtures.PipeName();
        await using var server = new IpcServer(name, () => Fixtures.Capabilities, Fixtures.Snapshot, protocolVersion: Protocol.ServiceVersion); _ = server.RunAsync();
        await using var client = Fixtures.Client(name); Check.Equal(IpcState.Connected, (await client.ConnectAsync()).State, "old v2 service handshake still valid");
        var result = await client.GetAppUsageRangeAsync(AppUsageRange.Today);
        Check.True(result.State != IpcState.Connected && result.Value is null, "missing query support fails explicitly and safely");
    }
    private static async Task LegacyOperations()
    {
        await WithServer(Fixtures.Service().QueryAsync, async (client, _) =>
        {
            Check.Equal(IpcState.Connected, (await client.GetCapabilitiesAsync()).State, "existing capabilities operation");
            Check.Equal(IpcState.Connected, (await client.GetSnapshotAsync()).State, "existing cumulative snapshot operation");
            Check.Equal(IpcState.Connected, (await client.GetAppUsageRangeAsync(AppUsageRange.Today)).State, "history independently queryable");
        });
    }
    private static async Task UnavailableKeepsConnection()
    {
        int reads = 0;
        var query = new AppUsageQueryService((start, end) => Interlocked.Increment(ref reads) == 1
            ? throw new IOException("privileged-file-secret") : Fixtures.Rows.Where(row => row.LocalDate >= start && row.LocalDate <= end).ToArray(),
            timeProvider: new FixedClock(Fixtures.Now), localTimeZone: Fixtures.Zone);
        await WithServer(query.QueryAsync, async (client, server) =>
        {
            var first = await client.GetAppUsageRangeAsync(AppUsageRange.Today);
            Check.Equal(IpcState.Connected, first.State, "storage failure is typed history state");
            Check.Equal(AppUsageAvailability.Unavailable, first.Value!.Availability, "unavailable typed result");
            Check.Equal(IpcState.Connected, (await client.GetCapabilitiesAsync()).State, "same pipe still usable");
            Check.Equal(2200L, (await client.GetAppUsageRangeAsync(AppUsageRange.Today)).Value!.LogicalTotalBytes, "history recovers without reconnect");
            Check.Equal(1L, server.AcceptedConnections, "no reconnect needed for storage failure");
        });
    }
    private static async Task DegradedKeepsConnection()
    {
        await WithServer(Fixtures.Service(health: Fixtures.Healthy with { PersistenceDegraded = true }).QueryAsync, async (client, _) =>
        {
            var result = await client.GetAppUsageRangeAsync(AppUsageRange.SevenDays);
            Check.Equal(IpcState.Connected, result.State, "degraded response delivered");
            Check.Equal(AppUsageAvailability.Degraded, result.Value!.Availability, "typed degraded history");
            Check.Equal(4500L, result.Value.LogicalTotalBytes, "durable totals retained");
            Check.Equal(IpcState.Connected, (await client.GetCapabilitiesAsync()).State, "same pipe remains connected");
        });
    }
    private static async Task OversizedResultFallback()
    {
        var good = await Fixtures.Service().QueryAsync(AppUsageRange.Today);
        foreach (string metadata in new[] { new string('x', Protocol.MaximumResponseBytes),
            new string('<', Protocol.MaximumResponseBytes / 6 + 1) })
        {
            var large = good with { Applications = [good.Applications[0] with { DisplayName = metadata }, good.Applications[1]] };
            int calls = 0;
            await WithServer((_, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1 ? large : good), async (client, server) =>
            {
                var first = await client.GetAppUsageRangeAsync(AppUsageRange.Today);
                Check.Equal(IpcState.Connected, first.State, "plain or escape-expanded oversize does not emit partial frame or close connection");
                Check.Equal(AppUsageAvailability.Unavailable, first.Value!.Availability, "bounded typed result instead of oversized body");
                Check.Equal(0, first.Value.Applications.Count, "no partial oversized rows");
                Check.Equal(IpcState.Connected, (await client.GetCapabilitiesAsync()).State, "same pipe remains usable after frame rejection");
                Check.Equal(2200L, (await client.GetAppUsageRangeAsync(AppUsageRange.Today)).Value!.LogicalTotalBytes, "later small history succeeds");
                Check.Equal(1L, server.AcceptedConnections, "bounded fallback needs no reconnect");
            });
        }
    }
    private static async Task InvalidResponses()
    {
        var good = await Fixtures.Service().QueryAsync(AppUsageRange.Today); var first = good.Applications[0];
        foreach (var bad in new[]
        {
            good with { TotalDownloadBytes = -1 }, good with { LogicalTotalBytes = good.LogicalTotalBytes + 1 },
            good with { TrackedApplicationCount = 3 }, good with { StartLocalDate = good.StartLocalDate.AddDays(-1) },
            good with { Applications = [first, first] }, good with { Applications = [first with { DownloadBytes = -1 }, good.Applications[1]] },
            good with { Applications = [first with { IsSystem = true }, good.Applications[1]] },
            good with { Applications = [null!] }, good with { Diagnostic = "privileged-stack-secret" },
            good with { Applications = good.Applications.Reverse().ToArray() }
        }) await RejectResponse(bad, AppUsageRange.Today);
    }
    private static async Task WrongRange() => await RejectResponse(await Fixtures.Service().QueryAsync(AppUsageRange.SevenDays), AppUsageRange.Today);
    private static async Task RejectResponse(AppUsageQueryResult bad, AppUsageRange requested)
    {
        await FakeServer(async raw =>
        {
            await IpcProtocol.ReadRequestAsync(raw); await IpcProtocol.WriteResponseAsync(raw, new(2, "Ok", Fixtures.Capabilities));
            await IpcProtocol.ReadRequestAsync(raw); await IpcProtocol.WriteResponseAsync(raw, new(2, "Ok", AppUsage: bad));
        }, async client =>
        {
            Check.Equal(IpcState.Connected, (await client.ConnectAsync()).State, "valid Hello before malicious history");
            var result = await client.GetAppUsageRangeAsync(requested);
            Check.Equal(IpcState.MalformedResponse, result.State, "inconsistent untrusted history rejected");
            Check.True(result.Value is null, "bad history never reaches callers");
        });
    }
    private static async Task CancelQuery()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await FakeServer(async raw =>
        {
            await IpcProtocol.ReadRequestAsync(raw); await IpcProtocol.WriteResponseAsync(raw, new(2, "Ok", Fixtures.Capabilities));
            await IpcProtocol.ReadRequestAsync(raw); entered.SetResult(); await Task.Delay(150);
        }, async client =>
        {
            Check.Equal(IpcState.Connected, (await client.ConnectAsync()).State, "connected before cancellation");
            using var cancellation = new CancellationTokenSource(); var pending = client.GetAppUsageRangeAsync(AppUsageRange.Today, cancellation.Token);
            await entered.Task; cancellation.Cancel();
            Check.Equal(IpcState.Cancelled, (await pending).State, "caller cancellation ends stalled query");
        });
    }
    private static async Task ServerDeadline()
    {
        string name = Fixtures.PipeName(); var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int calls = 0;
        async Task<AppUsageQueryResult> Query(AppUsageRange range, CancellationToken token)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            }
            return await Fixtures.Service().QueryAsync(range, token);
        }
        await using var server = Fixtures.Server(name, Query); _ = server.RunAsync(); await using var client = Fixtures.Client(name);
        Check.Equal(IpcState.Connected, (await client.ConnectAsync()).State, "connected before stalled handler"); var watch = Stopwatch.StartNew();
        var result = await client.GetAppUsageRangeAsync(AppUsageRange.Today);
        Check.True(result.State is IpcState.TimedOut or IpcState.Disconnected, "stalled handler has bounded client result");
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2)); Check.True(watch.Elapsed < TimeSpan.FromSeconds(5), "server cancellation bounded");
        await using var next = Fixtures.Client(name); Check.Equal(IpcState.Connected, (await next.ConnectAsync()).State, "server remains alive after handler deadline");
        Check.Equal(2200L, (await next.GetAppUsageRangeAsync(AppUsageRange.Today)).Value!.LogicalTotalBytes, "later healthy request succeeds");
    }
    private static async Task Reconnect()
    {
        string name = Fixtures.PipeName(); var first = Fixtures.Server(name, Fixtures.Service().QueryAsync); _ = first.RunAsync();
        await using var client = Fixtures.Client(name); Check.Equal(IpcState.Connected, (await client.ConnectAsync()).State, "initial connection");
        Check.Equal(2200L, (await client.GetAppUsageRangeAsync(AppUsageRange.Today)).Value!.LogicalTotalBytes, "initial history"); await first.DisposeAsync();
        Check.Equal(IpcState.Disconnected, (await client.GetAppUsageRangeAsync(AppUsageRange.Today)).State, "service stop safe");
        await using var next = Fixtures.Server(name, Fixtures.Service().QueryAsync); _ = next.RunAsync();
        Check.Equal(IpcState.Connected, (await client.ConnectAsync()).State, "same diagnostic client reconnects");
        Check.Equal(4500L, (await client.GetAppUsageRangeAsync(AppUsageRange.SevenDays)).Value!.LogicalTotalBytes, "restart retains persisted model");
    }
    private static async Task AbsentService()
    {
        await using var client = Fixtures.Client(Fixtures.PipeName()); var watch = Stopwatch.StartNew();
        Check.Equal(IpcState.CollectorUnavailable, (await client.ConnectAsync()).State, "service absent safe");
        Check.Equal(IpcState.Disconnected, (await client.GetAppUsageRangeAsync(AppUsageRange.Today)).State, "no pipe safe");
        Check.True(watch.Elapsed < TimeSpan.FromSeconds(4), "missing service connect bounded");
    }
    private static async Task RepeatedQueries()
    {
        using var scope = new StorageScope(); scope.Seed(); var query = Fixtures.Database(scope);
        await WithServer(query.QueryAsync, async (client, _) =>
        {
            var ranges = new[] { AppUsageRange.Today, AppUsageRange.SevenDays, AppUsageRange.ThirtyDays };
            var samples = ranges.ToDictionary(range => range, _ => new List<double>());
            foreach (var range in ranges)
            {
                var cold = Stopwatch.StartNew(); Check.Equal(IpcState.Connected, (await client.GetAppUsageRangeAsync(range)).State, "initial range latency sample");
                Console.WriteLine($"LATENCY cold {range}={cold.Elapsed.TotalMilliseconds:F3} ms");
            }
            for (int i = 0; i < 20; i++) await client.GetAppUsageRangeAsync(ranges[i % 3]);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); using var process = Process.GetCurrentProcess(); process.Refresh();
            int handlesBefore = process.HandleCount; long managedBefore = GC.GetTotalMemory(true), workingBefore = process.WorkingSet64;
            for (int i = 0; i < 210; i++)
            {
                var range = ranges[i % 3]; var watch = Stopwatch.StartNew(); var result = await client.GetAppUsageRangeAsync(range);
                samples[range].Add(watch.Elapsed.TotalMilliseconds);
                Check.Equal(IpcState.Connected, result.State, "repeated query connected");
                Check.Equal(range == AppUsageRange.Today ? 2200L : 4500L, result.Value!.LogicalTotalBytes, "repeated result exact");
            }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); process.Refresh();
            int handlesAfter = process.HandleCount; long managedAfter = GC.GetTotalMemory(true), workingAfter = process.WorkingSet64;
            Check.True(handlesAfter - handlesBefore <= 16, "no sustained per-query handle accumulation");
            Check.True(managedAfter - managedBefore < 8 * 1024 * 1024, "no significant retained managed memory growth");
            Check.True(workingAfter - workingBefore < 32 * 1024 * 1024, "no significant working set growth in modest repeated queries");
            using (var exclusive = new FileStream(scope.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.None))
                Check.True(exclusive.Length > 0, "every query released its SQLite file handle");
            foreach (var range in ranges)
            {
                var values = samples[range].Order().ToArray();
                Console.WriteLine($"LATENCY warm {range}: n={values.Length}; mean={values.Average():F3} ms; median={values[values.Length / 2]:F3} ms; max={values[^1]:F3} ms");
            }
            Console.WriteLine($"RESOURCES queries=210; handles={handlesBefore}->{handlesAfter}; managedBytes={managedBefore}->{managedAfter}; workingSetBytes={workingBefore}->{workingAfter}");
        });
    }
    private static async Task WithServer(Func<AppUsageRange, CancellationToken, Task<AppUsageQueryResult>> query, Func<IpcClient, IpcServer, Task> check)
    {
        string name = Fixtures.PipeName(); await using var server = Fixtures.Server(name, query); _ = server.RunAsync();
        await using var client = Fixtures.Client(name); Check.Equal(IpcState.Connected, (await client.ConnectAsync()).State, "authenticated service-protocol Hello");
        await check(client, server);
    }
    private static async Task<NamedPipeClientStream> HandshakenRaw(string name)
    {
        var pipe = NativePipeSecurity.CreateClient(name); await pipe.ConnectAsync(2000);
        await IpcProtocol.WriteRequestAsync(pipe, new(2, "Hello"));
        Check.Equal("Ok", (await IpcProtocol.ReadResponseAsync(pipe, expectedVersion: 2)).Status, "raw test client Hello"); return pipe;
    }
    private static async Task FakeServer(Func<NamedPipeServerStream, Task> serve, Func<IpcClient, Task> check)
    {
        string name = Fixtures.PipeName(); using var pipe = NativePipeSecurity.CreateServer(name, true, 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var serving = Task.Run(async () =>
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            try { await serve(pipe); } finally { pipe.Dispose(); }
        });
        try { await using var client = Fixtures.Client(name); await check(client); await serving.WaitAsync(timeout.Token); }
        finally { timeout.Cancel(); pipe.Dispose(); }
    }
}
