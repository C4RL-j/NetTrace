using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using AppUsageIpc;

internal static class HistoryManagementTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("History management uses a distinct deterministic production pipe", Check.Sync(DistinctPipe)),
        ("History management ACL permits only SYSTEM and administrators at high integrity", Check.Sync(AdminAcl)),
        ("History management accepts only the fixed bounded clear request", Check.Sync(FixedRequest)),
        ("Ordinary client identity cannot authorize privileged clear", Check.Sync(NormalIdentity)),
        ("Authenticated clear acknowledges committed row count", SuccessfulClear),
        ("Management checks client identity only after the complete bounded read", AuthenticateAfterRead),
        ("Unauthorized management request cannot clear any rows", Unauthorized),
        ("Malformed operation is rejected before privileged callback", Malformed),
        ("Oversized management frame is rejected before allocating its body", Oversized),
        ("Unknown management version cannot clear rows", UnknownVersion),
        ("Storage failure produces a failure acknowledgement and permits retry", ClearFailure),
        ("Service cancellation waits for an admitted clear to finish determinately", AdmittedClear),
        ("Management connection loss cannot cancel an admitted clear", DisconnectedClear),
        ("Management rejects a second listener start", DuplicateRun),
    ];

    private static void DistinctPipe()
    {
        NativePipeSecurity.ValidatePipeName(AppUsageHistoryManagement.PipeName);
        Check.True(AppUsageHistoryManagement.PipeName != ServicePipeSecurity.DefaultPipeName, "normal v2 query pipe remains separate");
        Check.True(AppUsageHistoryManagement.PipeName.Contains("Management", StringComparison.Ordinal), "fixed management identity");
    }
    private static void AdminAcl()
    {
        string acl = AppUsageHistoryManagement.SecurityDescriptorSddl;
        Check.Equal("O:SYD:P(A;;GA;;;SY)(A;;GA;;;BA)S:(ML;;NW;;;HI)", acl, "protected SYSTEM owner, administrators and high integrity only");
        Check.True(!acl.Contains(";;;BU)", StringComparison.Ordinal) && !acl.Contains(";;;WD)", StringComparison.Ordinal), "no users or everyone");
    }
    private static void FixedRequest()
    {
        Check.True(AppUsageHistoryManagement.IsClearRequest([0x4e, 0x54, 0x48, 1, 1]), "fixed operation");
        foreach (byte[] value in new byte[][] { [], [0x4e], [0x4e, 0x54, 0x48, 1, 2], [0x4e, 0x54, 0x48, 2, 1], [0x4e, 0x54, 0x48, 1, 1, 0] })
            Check.True(!AppUsageHistoryManagement.IsClearRequest(value), "reject unsupported and arbitrary bodies");
    }
    private static void NormalIdentity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        Check.True(!AppUsageHistoryManagement.IsAdministratorIdentity(identity, elevated: false), "ordinary NetTrace token cannot authorize history clear");
        Check.True(!AppUsageHistoryManagement.IsAdministratorIdentity(identity, elevated: true), "an elevation assertion cannot bypass membership in the actual token");
    }
    private static async Task SuccessfulClear()
    {
        using var fixture = new Fixture(_ => Task.FromResult(11));
        fixture.Start();
        using var client = await fixture.Connect();
        await Send(client);
        var response = await Read(client);
        Check.Equal((byte)0, response[4], "success acknowledgement");
        Check.Equal(11, BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(5)), "committed row count");
        client.Dispose();
        await fixture.Stop();
    }
    private static async Task AuthenticateAfterRead()
    {
        int verified = 0, cleared = 0;
        using var fixture = new Fixture(_ => { cleared++; return Task.FromResult(1); }, _ => { verified++; return true; });
        fixture.Start(); using var client = await fixture.Connect();
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, 5);
        await client.WriteAsync(header);
        await Task.Delay(30);
        Check.Equal(0, verified, "no authorization before body is read");
        await client.WriteAsync(new byte[] { 0x4e, 0x54, 0x48, 1, 1 });
        await Read(client);
        Check.Equal(1, verified, "one authenticated fixed request");
        Check.Equal(1, cleared, "clear follows authorization");
        client.Dispose(); await fixture.Stop();
    }
    private static async Task Unauthorized()
    {
        int cleared = 0;
        using var fixture = new Fixture(_ => { cleared++; return Task.FromResult(1); }, _ => false);
        fixture.Start(); using var client = await fixture.Connect();
        await Send(client); await ExpectDisconnected(client);
        Check.Equal(0, cleared, "denied token never reaches mutation");
        await fixture.Stop();
    }
    private static Task Malformed() => Rejected([0x4e, 0x54, 0x48, 1, 9]);
    private static Task UnknownVersion() => Rejected([0x4e, 0x54, 0x48, 2, 1]);
    private static async Task Rejected(byte[] body)
    {
        int verified = 0, cleared = 0;
        using var fixture = new Fixture(_ => { cleared++; return Task.FromResult(1); }, _ => { verified++; return true; });
        fixture.Start(); using var client = await fixture.Connect();
        await Send(client, body); await ExpectDisconnected(client);
        Check.Equal(0, cleared, "invalid body cannot clear");
        Check.Equal(0, verified, "malformed frame cannot invoke identity hook");
        await fixture.Stop();
    }
    private static async Task Oversized()
    {
        int cleared = 0;
        using var fixture = new Fixture(_ => { cleared++; return Task.FromResult(1); });
        fixture.Start(); using var client = await fixture.Connect();
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, int.MaxValue);
        await client.WriteAsync(header); await ExpectDisconnected(client);
        Check.Equal(0, cleared, "unbounded body never read or cleared");
        await fixture.Stop();
    }
    private static async Task ClearFailure()
    {
        int calls = 0;
        using var fixture = new Fixture(_ => ++calls == 1 ? Task.FromException<int>(new IOException("busy database")) : Task.FromResult(3));
        fixture.Start();
        using (var first = await fixture.Connect())
        { await Send(first); Check.Equal((byte)1, (await Read(first))[4], "failure acknowledgement"); }
        using (var retry = await fixture.Connect())
        { await Send(retry); Check.Equal((byte)0, (await Read(retry))[4], "same listener allows retry"); }
        Check.Equal(2, calls, "no automatic mutation retry"); await fixture.Stop();
    }
    private static async Task AdmittedClear()
    {
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool canCancel = true;
        using var fixture = new Fixture(token => { canCancel = token.CanBeCanceled; admitted.SetResult(); return completion.Task; });
        fixture.Start(); using var client = await fixture.Connect(); await Send(client); await admitted.Task;
        fixture.Cancel();
        Check.True(!fixture.Running.IsCompleted && !canCancel, "admitted transaction is not cancelled by stop");
        completion.SetResult(4);
        Check.Equal((byte)0, (await Read(client))[4], "success means operation committed before stop");
        client.Dispose(); await fixture.Stop();
    }
    private static async Task DisconnectedClear()
    {
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool committed = false;
        using var fixture = new Fixture(async token => { admitted.SetResult(); int rows = await completion.Task; committed = true; return rows; });
        fixture.Start(); using var client = await fixture.Connect(); await Send(client); await admitted.Task;
        client.Dispose(); fixture.Cancel(); completion.SetResult(5); await fixture.Stop();
        Check.True(committed, "client disconnect cannot abandon SQL commit");
    }
    private static async Task DuplicateRun()
    {
        using var fixture = new Fixture(_ => Task.FromResult(0)); fixture.Start();
        await Check.ThrowsAsync<InvalidOperationException>(() => fixture.Server.RunAsync(), "one listener lifetime");
        await fixture.Stop();
    }
    private static async Task Send(Stream pipe, byte[]? body = null)
    {
        body ??= [0x4e, 0x54, 0x48, 1, 1];
        byte[] frame = new byte[4 + body.Length]; BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length); body.CopyTo(frame, 4);
        await pipe.WriteAsync(frame);
    }
    private static async Task<byte[]> Read(Stream pipe)
    {
        byte[] response = new byte[9]; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await pipe.ReadExactlyAsync(response, timeout.Token);
        await pipe.WriteAsync(new byte[1], timeout.Token);
        Check.Equal(0, await pipe.ReadAsync(new byte[1], timeout.Token), "response consumption acknowledged before listener reuse");
        return response;
    }
    private static async Task ExpectDisconnected(Stream pipe)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { Check.Equal(0, await pipe.ReadAsync(new byte[1], timeout.Token), "rejected client disconnected"); }
        catch (IOException) { }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string name = "NetTrace0054Test" + Guid.NewGuid().ToString("N");
        private readonly CancellationTokenSource cancellation = new();
        public AppUsageHistoryManagementServer Server { get; }
        public Task Running { get; private set; } = Task.CompletedTask;
        public Fixture(Func<CancellationToken, Task<int>> clear, Func<NamedPipeServerStream, bool>? verify = null)
        {
            var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            Server = new(clear, pipe, verify ?? (_ => true));
        }
        public void Start() => Running = Server.RunAsync(cancellation.Token);
        public void Cancel() => cancellation.Cancel();
        public async Task<NamedPipeClientStream> Connect()
        {
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(timeout.Token); return client;
        }
        public async Task Stop() { Cancel(); await Server.DisposeAsync(); }
        public void Dispose() { cancellation.Cancel(); Server.DisposeAsync().AsTask().GetAwaiter().GetResult(); cancellation.Dispose(); }
    }
}
