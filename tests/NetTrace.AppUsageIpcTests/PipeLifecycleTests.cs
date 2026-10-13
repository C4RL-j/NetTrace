using AppUsageIpc;

internal static class PipeLifecycleTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Native service pipe instances remain reusable after repeated query clients close", RepeatedServiceQueries),
        ("Native pipe EOF marks a server disconnected before explicit instance cleanup", EofRequiresDisconnect),
        ("Abrupt and malformed clients cannot exhaust reusable IPC listener instances", AbruptClients)
    ];

    private static string Name() => "NetTrace.AppUsage0056.Tests." + Guid.NewGuid().ToString("N");
    private static CollectorCapabilities Capabilities => ProtocolTests.Capabilities with
        { CollectorElevated = true, ServiceIdentity = "LocalSystem" };
    private static CollectorSnapshot Snapshot() => AggregationTests.Create().GetSnapshot() with
        { Capabilities = Capabilities };
    private static AppUsageQueryResult Range(AppUsageRange range)
    {
        var date = DateOnly.FromDateTime(DateTime.Now);
        return new(range, AppUsageRangeRules.GetStartLocalDate(range, date), date, 0, 0, 0, 0, []);
    }
    private static IpcClient Client(string name) => new(name, serverVerifier: _ => true,
        serviceName: ServicePipeSecurity.ProductionServiceName);

    private static async Task RepeatedServiceQueries()
    {
        string name = Name();
        await using var server = new IpcServer(name, () => Capabilities, Snapshot, clientVerifier: _ => true,
            protocolVersion: Protocol.ServiceVersion, appUsageQuery: (range, _) => Task.FromResult(Range(range)));
        var running = server.RunAsync();
        for (var cycle = 0; cycle < NativePipeSecurity.MaximumClients * 10; cycle++)
        {
            await using (var client = Client(name))
            {
                Check.Equal(IpcState.Connected, (await client.ConnectAsync()).State, "fresh service-mask handshake cycle " + cycle);
                var range = (cycle % 3) switch { 0 => AppUsageRange.Today, 1 => AppUsageRange.SevenDays, _ => AppUsageRange.ThirtyDays };
                var result = await client.GetAppUsageRangeAsync(range);
                Check.Equal(IpcState.Connected, result.State, "real selected-range query cycle " + cycle);
                Check.Equal(range, result.Value!.Range, "current request range survives cycle " + cycle);
            }
            // Let the server observe EOF, as it does between four-second UI
            // requests, rather than racing each client against the old reader.
            await Task.Delay(30);
        }
        Check.Equal((long)NativePipeSecurity.MaximumClients * 10, server.AcceptedConnections,
            "more than one full listener-capacity cycle succeeds without replacing guarded handles");
        Check.True(!running.IsCompleted, "client disconnects do not fault or stop the collector listener");
    }

    private static async Task EofRequiresDisconnect()
    {
        string name = Name();
        using var server = NativePipeSecurity.CreateServer(name, firstInstance: true, maxInstances: 1);
        var accepted = server.WaitForConnectionAsync();
        using (var client = NativePipeSecurity.CreateClient(name))
        {
            await client.ConnectAsync(2000);
            await accepted;
        }
        Check.Equal(0, await server.ReadAsync(new byte[1]), "closed client returns EOF");
        Check.True(!server.IsConnected, "managed stream is Broken after EOF and no longer reports IsConnected");
        server.Disconnect();
        var next = server.WaitForConnectionAsync();
        using var replacement = NativePipeSecurity.CreateClient(name);
        await replacement.ConnectAsync(2000);
        await next.WaitAsync(TimeSpan.FromSeconds(2));
        Check.True(server.IsConnected, "Disconnect is still required and valid after EOF to release the native server instance");
    }

    private static async Task AbruptClients()
    {
        string name = Name();
        await using var server = new IpcServer(name, () => Capabilities, Snapshot, clientVerifier: _ => true,
            protocolVersion: Protocol.ServiceVersion, appUsageQuery: (range, _) => Task.FromResult(Range(range)));
        var running = server.RunAsync();
        for (var cycle = 0; cycle < NativePipeSecurity.MaximumClients * 3; cycle++)
        {
            using (var raw = NativePipeSecurity.CreateClient(name))
            {
                await raw.ConnectAsync(2000);
                if (cycle % 2 == 0) await raw.WriteAsync(new byte[] { 0, 0, 0, 0 });
                // Both an empty EOF and a malformed zero-size frame abandon the
                // actual native instance. Neither may poison future clients.
            }
            await Task.Delay(30);
            await using var healthy = Client(name);
            Check.Equal(IpcState.Connected, (await healthy.ConnectAsync()).State, "healthy handshake after abandoned client " + cycle);
            Check.Equal(IpcState.Connected, (await healthy.GetAppUsageRangeAsync(AppUsageRange.Today)).State,
                "healthy usage query after abandoned client " + cycle);
        }
        Check.True(!running.IsCompleted, "malformed and abandoned clients never stop healthy sibling listeners");
    }
}
