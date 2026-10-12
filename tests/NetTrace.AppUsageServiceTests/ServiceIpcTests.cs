using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Text;
using AppUsageIpc;

internal static class ServiceIpcTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Service protocol 2 carries collector health, loss, lifecycle, and aggregate counters", HealthRoundTrip),
        ("LocalService capability does not require an administrator role assertion", LocalServiceCapabilities),
        ("A protocol 1 diagnostic client explicitly rejects a protocol 2 service", LegacyClientRejectsService),
        ("A protocol 2 service client explicitly rejects a protocol 1 collector", ServiceClientRejectsLegacy),
        ("Older and newer request versions cannot access service suppliers", InvalidRequestVersions),
        ("Future response fields produce compatibility failure before strict deserialization", FutureResponseShape),
        ("Malformed response protocol-version fields produce protocol errors without crashing", MalformedVersionFields),
        ("Missing or inconsistent service health never reaches client callers as a healthy snapshot", InvalidHealth),
        ("Service pipes disallow diagnostic shutdown and arbitrary privileged operations", NarrowServiceOperations),
        ("The service pipe ACL grants only bounded client rights and retains a medium integrity label", ServiceAcl),
        ("Broad client principals and unsupported service identities are rejected", InvalidAclPrincipals),
        ("The real pipe client token must match the configured intended SID", UnauthorizedClient),
        ("Malformed and oversized service requests disconnect only the offending client", MalformedRequests),
        ("A disconnected service client reconnects to a new lifetime with fresh counters", ServiceReconnect),
        ("Default service peer verification rejects a pipe unrelated to the registered SCM service", RejectUnregisteredPeer),
        ("Reconnect delays back off to a finite maximum and reset after successful connection", Check.Sync(Backoff)),
        ("Cancellation interrupts a scheduled reconnect delay without a tight retry loop", CancelBackoff),
    ];

    private static IpcClient Client(string name, int version = Protocol.ServiceVersion) =>
        new(name, serverVerifier: _ => true, protocolVersion: version);
    private static CollectorSnapshot ServiceSnapshot()
    {
        var snapshot = Fixtures.Snapshot();
        return snapshot with { Health = new(snapshot.CapturedAtUtc, true,
            "NetTrace-AppUsage003-Collector-938F90CEB2A34EF7A67BA5A43521EE77", null,
            snapshot.Applications.Count, "LocalSystem", true) };
    }

    private static async Task HealthRoundTrip()
    {
        string name = Fixtures.PipeName(); var snapshot = ServiceSnapshot();
        snapshot = snapshot with { LostEventCount = 3,
            Capabilities = snapshot.Capabilities with { LostEventCountAvailable = true } };
        await using var server = new IpcServer(name, () => snapshot.Capabilities, () => snapshot,
            protocolVersion: Protocol.ServiceVersion);
        _ = server.RunAsync(); await using var client = Client(name);
        Check.Equal(IpcState.Connected, (await client.ConnectAsync()).State, "v2 Hello");
        var response = await client.GetSnapshotAsync();
        Check.Equal(IpcState.Connected, response.State, "v2 summary");
        Check.Equal(snapshot.Health, response.Value!.Health, "health survives actual local named pipe");
        Check.Equal(3, response.Value.LostEventCount, "loss is visible to the service client");
        Check.Equal(snapshot.CollectorInstanceId, response.Value.CollectorInstanceId, "lifetime remains explicit");
        Check.Equal(snapshot.Capabilities.ServiceIdentity, response.Value.Capabilities.ServiceIdentity, "safe service identity crosses IPC");
    }

    private static async Task LocalServiceCapabilities()
    {
        var snapshot = ServiceSnapshot();
        snapshot = snapshot with { Capabilities = snapshot.Capabilities with
            { CollectorElevated = false, ServiceIdentity = "LocalService" },
            Health = snapshot.Health! with { ServiceIdentity = "LocalService" } };
        await WithServer(snapshot, async client =>
        {
            Check.Equal(IpcState.Connected, (await client.ConnectAsync()).State, "built-in service identity is distinct from administrator role");
            Check.Equal(IpcState.Connected, (await client.GetSnapshotAsync()).State, "actual capability and health accepted");
        });
    }

    private static async Task LegacyClientRejectsService()
    {
        string name = Fixtures.PipeName();
        await using var server = new IpcServer(name, () => Fixtures.Capabilities, ServiceSnapshot,
            protocolVersion: Protocol.ServiceVersion);
        _ = server.RunAsync(); await using var old = Client(name, Protocol.Version);
        Check.Equal(IpcState.IncompatibleProtocol, (await old.ConnectAsync()).State, "v1 against v2 is explicit incompatibility");
    }
    private static async Task ServiceClientRejectsLegacy()
    {
        string name = Fixtures.PipeName();
        await using var server = new IpcServer(name, () => Fixtures.Capabilities, Fixtures.Snapshot);
        _ = server.RunAsync(); await using var modern = Client(name);
        Check.Equal(IpcState.IncompatibleProtocol, (await modern.ConnectAsync()).State, "v2 against v1 is explicit incompatibility");
    }
    private static async Task InvalidRequestVersions()
    {
        foreach (int version in new[] { 0, 1, 3, int.MaxValue })
        {
            int invoked = 0; string name = Fixtures.PipeName();
            await using var server = new IpcServer(name, () => { invoked++; return Fixtures.Capabilities; }, ServiceSnapshot,
                protocolVersion: Protocol.ServiceVersion);
            _ = server.RunAsync();
            using var pipe = NativePipeSecurity.CreateClient(name); await pipe.ConnectAsync(2000);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await IpcProtocol.WriteRequestAsync(pipe, new(version, "Hello"), timeout.Token);
            var response = await IpcProtocol.ReadResponseAsync(pipe, timeout.Token, Protocol.ServiceVersion);
            Check.Equal("IncompatibleProtocol", response.Status, "unsupported version rejected");
            Check.Equal(Protocol.ServiceVersion, response.ProtocolVersion, "server advertises service version");
            Check.Equal(0, invoked, "version checks precede privileged suppliers");
        }
    }
    private static async Task FutureResponseShape()
    {
        using var stream = new MemoryStream(Frame("{\"protocolVersion\":3,\"status\":\"Ok\",\"futureHealth\":{\"unknown\":true}}"));
        var response = await IpcProtocol.ReadResponseAsync(stream, expectedVersion: Protocol.ServiceVersion);
        Check.Equal("IncompatibleProtocol", response.Status, "future shape is classified by version first");
    }
    private static async Task MalformedVersionFields()
    {
        foreach (string value in new[] { "\"2\"", "null", "{}", "[]", "true" })
        {
            using var stream = new MemoryStream(Frame($"{{\"protocolVersion\":{value},\"status\":\"Ok\"}}"));
            await Check.ThrowsAsync<IpcProtocolException>(async () =>
                { await IpcProtocol.ReadResponseAsync(stream, expectedVersion: Protocol.ServiceVersion); }, "invalid version type");
        }
        using var duplicate = new MemoryStream(Frame("{\"protocolVersion\":2,\"protocolVersion\":3,\"status\":\"Ok\"}"));
        await Check.ThrowsAsync<IpcProtocolException>(async () =>
            { await IpcProtocol.ReadResponseAsync(duplicate, expectedVersion: Protocol.ServiceVersion); }, "duplicate version cannot bypass shape checks");
    }
    private static async Task InvalidHealth()
    {
        var good = ServiceSnapshot();
        foreach (var snapshot in new[]
        {
            good with { Health = null },
            good with { Health = good.Health! with { CaptureRunning = false } },
            good with { Health = good.Health! with { CollectorPrivileged = false } },
            good with { Health = good.Health! with { ApplicationCount = 100 } },
            good with { Health = good.Health! with { ServiceIdentity = "LocalService" } },
            good with { Health = good.Health! with { StartedAtUtc = good.CapturedAtUtc.AddSeconds(1) } }
        })
            await WithServer(snapshot, async client =>
            {
                Check.Equal(IpcState.Connected, (await client.ConnectAsync()).State, "valid service Hello");
                var result = await client.GetSnapshotAsync();
                Check.Equal(IpcState.MalformedResponse, result.State, "inconsistent health rejected");
                Check.True(result.Value is null, "unhealthy data not exposed as successful accounting");
            });
    }
    private static async Task WithServer(CollectorSnapshot snapshot, Func<IpcClient, Task> test)
    {
        string name = Fixtures.PipeName();
        await using var server = new IpcServer(name, () => snapshot.Capabilities, () => snapshot,
            protocolVersion: Protocol.ServiceVersion);
        _ = server.RunAsync(); await using var client = Client(name); await test(client);
    }
    private static Task NarrowServiceOperations()
    {
        int invoked = 0;
        foreach (string operation in new[] { "RunProcess", "ReadFile", "WriteRegistry", "SelectEtwProvider", "ExecuteCommand" })
        {
            var response = IpcProtocol.Dispatch(new(Protocol.ServiceVersion, operation), true,
                () => { invoked++; return Fixtures.Capabilities; },
                () => { invoked++; return ServiceSnapshot(); }, false, Protocol.ServiceVersion);
            Check.Equal("UnknownOperation", response.Status, "arbitrary privileged action unavailable");
        }
        Check.Equal(0, invoked, "unsupported commands cannot read collector state");
        Check.Equal("ShutdownDisabled", IpcProtocol.Dispatch(new(Protocol.ServiceVersion, "ShutdownForTest"), true,
            () => Fixtures.Capabilities, ServiceSnapshot, false, Protocol.ServiceVersion).Status, "SCM owns service shutdown");
        bool rejected = false;
        try
        {
            _ = new IpcServer(Fixtures.PipeName(), () => Fixtures.Capabilities, ServiceSnapshot,
                shutdownForTest: () => { }, serviceSecurity: new(NativePipeSecurity.CurrentUserSid()),
                protocolVersion: Protocol.ServiceVersion);
        }
        catch (ArgumentException) { rejected = true; }
        Check.True(rejected, "service pipe construction cannot expose diagnostic shutdown");
        return Task.CompletedTask;
    }
    private static Task ServiceAcl()
    {
        string user = NativePipeSecurity.CurrentUserSid();
        string serviceSid = ServicePipeSecurity.ServiceSid(ServicePipeSecurity.DefaultServiceName);
        var descriptor = new RawSecurityDescriptor(NativePipeSecurity.ServiceSecurityDescriptorSddl(user, "S-1-5-18", serviceSid));
        Check.True((descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0, "protected service DACL");
        var allowed = descriptor.DiscretionaryAcl!.Cast<GenericAce>().OfType<CommonAce>().ToArray();
        Check.Equal(3, allowed.Length, "System, own service, intended user only");
        Check.True(allowed.All(ace => ace.AceQualifier == AceQualifier.AccessAllowed), "no ambiguous allow/deny ordering");
        Check.True(allowed.Select(ace => ace.SecurityIdentifier.Value).ToHashSet()
            .SetEquals(new[] { "S-1-5-18", serviceSid, user }), "no anonymous, Everyone, or broad user principal");
        var client = allowed.Single(ace => ace.SecurityIdentifier.Value == user);
        Check.Equal(0, client.AccessMask & (0x4 | 0x10000 | 0x40000 | 0x80000 | 0x10000000),
            "client lacks create-instance, delete, ACL-write, owner-write, and generic-full-control rights");
        // .NET represents a mandatory-label ACE as CustomAce, so inspect its
        // native ACE header/access mask/SID rather than treating it as an allow ACE.
        var label = descriptor.SystemAcl!.Cast<GenericAce>().Single();
        byte[] labelBytes = new byte[label.BinaryLength]; label.GetBinaryForm(labelBytes, 0);
        Check.Equal(17, (int)label.AceType, "SYSTEM_MANDATORY_LABEL_ACE_TYPE");
        Check.Equal("S-1-16-8192", new System.Security.Principal.SecurityIdentifier(labelBytes, 8).Value,
            "medium integrity SID permits normal client writes");
        Check.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(labelBytes.AsSpan(4, 4)), "no-write-up mandatory label policy");
        return Task.CompletedTask;
    }
    private static Task InvalidAclPrincipals()
    {
        string service = ServicePipeSecurity.ServiceSid(ServicePipeSecurity.DefaultServiceName);
        foreach (string user in new[] { "S-1-1-0", "S-1-5-7", "S-1-5-32-545", "S-1-5-18" })
        {
            bool rejected = false;
            try { NativePipeSecurity.ServiceSecurityDescriptorSddl(user, "S-1-5-18", service); }
            catch (ArgumentException) { rejected = true; }
            Check.True(rejected, "broad principal cannot become intended service client");
        }
        foreach (string identity in new[] { "S-1-5-20", NativePipeSecurity.CurrentUserSid() })
        {
            bool rejected = false;
            try { NativePipeSecurity.ServiceSecurityDescriptorSddl(NativePipeSecurity.CurrentUserSid(), identity, service); }
            catch (ArgumentException) { rejected = true; }
            Check.True(rejected, "only tested built-in identities accepted");
        }
        return Task.CompletedTask;
    }
    private static async Task UnauthorizedClient()
    {
        string name = Fixtures.PipeName(); int invoked = 0;
        string wrongUser = NativePipeSecurity.CurrentUserSid()[..NativePipeSecurity.CurrentUserSid().LastIndexOf('-')] + "-987654321";
        await using var server = new IpcServer(name, () => { invoked++; return Fixtures.Capabilities; }, ServiceSnapshot,
            clientVerifier: pipe => NativePipeSecurity.VerifyClient(pipe.SafePipeHandle, wrongUser), protocolVersion: Protocol.ServiceVersion);
        _ = server.RunAsync(); await using var client = Client(name);
        Check.Equal(IpcState.Disconnected, (await client.ConnectAsync()).State, "real token does not match intended user");
        Check.Equal(0, invoked, "unauthorized client cannot invoke health or summary suppliers");
        Check.Equal(1L, server.RejectedClients, "denied token counted");
    }
    private static async Task MalformedRequests()
    {
        string name = Fixtures.PipeName();
        await using var server = new IpcServer(name, () => Fixtures.Capabilities, ServiceSnapshot,
            protocolVersion: Protocol.ServiceVersion);
        _ = server.RunAsync();
        foreach (byte[] request in new[]
        {
            Frame("{\"protocolVersion\":2,\"operation\":\"Hello\",\"shell\":\"private-sentinel\"}"),
            Header(Protocol.MaximumRequestBytes + 1)
        })
        {
            using var raw = NativePipeSecurity.CreateClient(name); await raw.ConnectAsync(2000);
            await raw.WriteAsync(request); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            int length;
            try { length = await raw.ReadAsync(new byte[1], timeout.Token); } catch (IOException) { length = 0; }
            Check.Equal(0, length, "only malformed client disconnected");
        }
        await using var valid = Client(name);
        Check.Equal(IpcState.Connected, (await valid.ConnectAsync()).State, "valid v2 client succeeds after rejected requests");
    }
    private static async Task ServiceReconnect()
    {
        string name = Fixtures.PipeName(); var before = ServiceSnapshot();
        var server = new IpcServer(name, () => before.Capabilities, () => before, protocolVersion: Protocol.ServiceVersion);
        _ = server.RunAsync(); await using var client = Client(name);
        Check.Equal(IpcState.Connected, (await client.ConnectAsync()).State, "first lifetime");
        var first = (await client.GetSnapshotAsync()).Value!;
        await server.DisposeAsync();
        Check.Equal(IpcState.Disconnected, (await client.GetSnapshotAsync()).State, "service stop disconnects client");
        var after = ServiceSnapshot();
        await using var next = new IpcServer(name, () => after.Capabilities, () => after, protocolVersion: Protocol.ServiceVersion);
        _ = next.RunAsync();
        Check.Equal(IpcState.Connected, (await client.ConnectAsync()).State, "same client reconnects after service restart");
        var second = (await client.GetSnapshotAsync()).Value!;
        Check.True(first.CollectorInstanceId != second.CollectorInstanceId, "new lifetime identity across reconnect");
        Check.Equal(0L, second.EventCount, "restart counters fresh");
    }
    private static async Task RejectUnregisteredPeer()
    {
        string name = Fixtures.PipeName();
        await using var server = new IpcServer(name, () => Fixtures.Capabilities, ServiceSnapshot,
            protocolVersion: Protocol.ServiceVersion);
        _ = server.RunAsync();
        await using var client = new IpcClient(name, serviceName: ServicePipeSecurity.DefaultServiceName);
        Check.Equal(IpcState.PeerRejected, (await client.ConnectAsync()).State,
            "an ordinary same-user pipe cannot impersonate the protected SCM service");
    }
    private static void Backoff()
    {
        var backoff = new ReconnectBackoff();
        foreach (int seconds in new[] { 2, 4, 8, 8, 8 })
            Check.Equal(TimeSpan.FromSeconds(seconds), backoff.NextDelay(), "bounded retry cadence");
        backoff.Reset(); Check.Equal(TimeSpan.FromSeconds(2), backoff.NextDelay(), "successful Hello resets retry delay");
    }
    private static async Task CancelBackoff()
    {
        var backoff = new ReconnectBackoff(); using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Check.ThrowsAsync<OperationCanceledException>(() => Task.Delay(backoff.NextDelay(), cancel.Token),
            "stop cancels backoff instead of waiting for the next retry");
    }
    private static byte[] Header(int length) { byte[] bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, length); return bytes; }
    private static byte[] Frame(string json) { byte[] bytes = Encoding.UTF8.GetBytes(json); return Header(bytes.Length).Concat(bytes).ToArray(); }
}
