using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using AppUsageEtwProbe;
using AppUsageIpc;
using Microsoft.Win32.SafeHandles;

internal static class PipeTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("An authenticated local pipe handshake transfers detached aggregate snapshots", RealHandshake),
        ("The default client rejects a same-user server without an elevated token", RejectUnelevatedServer),
        ("Server peer rejection disconnects a client before any supplier is invoked", ServerPeerRejection),
        ("Client peer rejection never trusts a pipe merely because its name matches", ClientPeerRejection),
        ("An absent collector reports unavailability within the bounded connect interval", MissingCollector),
        ("Actual pipe access denial is reported without a hang or handshake", AccessDenied),
        ("A bad protocol version is rejected while a subsequent client still succeeds", BadVersion),
        ("An unknown pipe operation cannot invoke aggregate suppliers", UnknownOperation),
        ("A pipe client cannot request a snapshot before its Hello handshake", HandshakeRequired),
        ("Malformed request bytes disconnect only the offending pipe client", MalformedRequest),
        ("An oversized request header is rejected without waiting for its body", OversizedRequest),
        ("Two independent pipe clients observe consistent cumulative state", IndependentClients),
        ("Disposing the IPC server releases every instance for a fresh collector", DisposeReleasesHandles),
        ("The actual native pipe DACL admits only the current user's SID", ActualPipeDacl),
        ("Remote and UNC-shaped pipe names are rejected before a pipe is created", RejectRemoteNames),
        ("An incompatible collector response is classified explicitly", IncompatibleCollector),
        ("Malformed collector JSON produces a bounded malformed-response state", MalformedCollector),
        ("A collector cannot deliver an oversized response body past the framing bound", OversizedCollectorResponse),
        ("Null application or process entries cannot escape malformed-response handling", NullSummaryEntries),
        ("Negative or inconsistent aggregate counters are rejected after actual pipe deserialization", InvalidSummaryCounters),
        ("Cumulative snapshots that move backwards are rejected on the same connection", BackwardsSnapshot),
        ("A collector that closes halfway through a response is classified disconnected", MidResponseDisconnect),
        ("An unresponsive collector times out without retrying or invoking another request", RequestTimeout),
        ("Cancellation during a missing collector connection is explicit and bounded", CancelConnect),
        ("Cancellation while a connected collector stalls ends the pending request", CancelRequest),
        ("A diagnostic shutdown acknowledgement reaches the client before server disposal", TestShutdown),
        ("The same client can reconnect after a collector stops and restarts", ReconnectAfterStop),
    ];

    private static string Name() => "NetTrace.AppUsage002.Tests." + Guid.NewGuid().ToString("N");
    private static CollectorCapabilities Capabilities => ProtocolTests.Capabilities with { CollectorElevated = true };
    private static CollectorSnapshot Snapshot() => AggregationTests.Create().GetSnapshot() with { Capabilities = Capabilities };
    private static IpcClient Client(string name) => new(name, serverVerifier: _ => true);
    private static async Task<T> Bound<T>(Task<T> task) => await task.WaitAsync(TimeSpan.FromSeconds(8));

    private static async Task RealHandshake()
    {
        string name = Name();
        var aggregate = AggregationTests.Create();
        aggregate.Add(AggregationTests.Raw(bytes: 123));
        await using var server = new IpcServer(name, () => Capabilities,
            () => aggregate.GetSnapshot() with { Capabilities = Capabilities });
        _ = server.RunAsync();
        await using var client = Client(name);
        Check.Equal(IpcState.Connected, (await Bound(client.ConnectAsync())).State, "authenticated Hello");
        var result = await Bound(client.GetSnapshotAsync());
        Check.Equal(IpcState.Connected, result.State, "snapshot request");
        Check.Equal(123L, result.Value!.Applications.Single().Counters.TxBytes, "TX arrives through actual pipe");
        Check.Equal(101, result.Value.Applications.Single().Processes.Single().ProcessId, "PID survives actual pipe");
        Check.Equal(1L, server.AcceptedConnections, "one authenticated connection");
    }

    private static async Task RejectUnelevatedServer()
    {
        Check.True(!EtwCapture.IsElevated, "security test is executed from a real unelevated token");
        string name = Name();
        await using var server = new IpcServer(name, () => Capabilities, Snapshot);
        _ = server.RunAsync();
        await using var client = new IpcClient(name);
        Check.Equal(IpcState.PeerRejected, (await Bound(client.ConnectAsync())).State,
            "same SID and pipe name do not substitute for an elevated server token");
    }

    private static async Task ServerPeerRejection()
    {
        string name = Name(); int reads = 0;
        await using var server = new IpcServer(name, () => { reads++; return Capabilities; }, Snapshot,
            clientVerifier: _ => false);
        _ = server.RunAsync();
        await using var client = Client(name);
        var result = await Bound(client.ConnectAsync());
        Check.Equal(IpcState.Disconnected, result.State, "denied peer is disconnected");
        Check.Equal(0, reads, "unauthenticated client cannot read capabilities");
        Check.Equal(0L, server.AcceptedConnections, "denied peer never accepted");
        Check.Equal(1L, server.RejectedClients, "denial is counted");
    }

    private static async Task ClientPeerRejection()
    {
        string name = Name();
        await using var server = new IpcServer(name, () => Capabilities, Snapshot);
        _ = server.RunAsync();
        await using var client = new IpcClient(name, serverVerifier: _ => false);
        Check.Equal(IpcState.PeerRejected, (await Bound(client.ConnectAsync())).State, "untrusted server rejected");
    }

    private static async Task MissingCollector()
    {
        var watch = Stopwatch.StartNew();
        await using var client = Client(Name());
        Check.Equal(IpcState.CollectorUnavailable, (await Bound(client.ConnectAsync())).State, "absent collector state");
        Check.True(watch.Elapsed < TimeSpan.FromSeconds(4), "connection has a bounded wait");
    }

    private static async Task AccessDenied()
    {
        string name = Name();
        using var pipe = new NamedPipeServerStream(PipeDirection.InOut, true, false, CreateMutableTestPipe(name));
        var descriptor = new RawSecurityDescriptor($"D:P(D;;0x2;;;{NativePipeSecurity.CurrentUserSid()})(A;;GA;;;{NativePipeSecurity.CurrentUserSid()})");
        var acl = descriptor.DiscretionaryAcl!;
        byte[] bytes = new byte[acl.BinaryLength];
        acl.GetBinaryForm(bytes, 0);
        IntPtr buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            uint error = SetSecurityInfo(pipe.SafePipeHandle, 6, 0x80000004,
                IntPtr.Zero, IntPtr.Zero, buffer, IntPtr.Zero);
            if (error != 0) throw new Win32Exception((int)error);
            var watch = Stopwatch.StartNew();
            await using var client = Client(name);
            Check.Equal(IpcState.AccessDenied, (await Bound(client.ConnectAsync())).State, "denied read/write connection is explicit");
            Check.True(watch.Elapsed < TimeSpan.FromSeconds(4), "access denial is bounded");
            Check.True(!pipe.IsConnected, "an access-denied client never reaches the handshake");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static SafePipeHandle CreateMutableTestPipe(string name)
    {
        // Request WRITE_DAC only on this owned test pipe so the access-denied
        // condition can be installed after creation. Production does not request it.
        var descriptor = new RawSecurityDescriptor(NativePipeSecurity.SecurityDescriptorSddl(NativePipeSecurity.CurrentUserSid()));
        byte[] bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        IntPtr buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            var attributes = new TestSecurityAttributes { Length = Marshal.SizeOf<TestSecurityAttributes>(), Descriptor = buffer };
            var handle = CreateNamedPipe(@"\\.\pipe\" + name, 3 | 0x40000000 | 0x40000 | NativePipeSecurity.FirstInstanceFlag,
                NativePipeSecurity.RejectRemoteClientsFlag, 1, 4096, 4096, 0, ref attributes);
            if (!handle.IsInvalid) return handle;
            int error = Marshal.GetLastWin32Error(); handle.Dispose();
            throw new Win32Exception(error);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static async Task BadVersion() => await RejectedRequest(new(Protocol.Version + 1, "Hello"),
        "IncompatibleProtocol", handshaken: false);
    private static async Task UnknownOperation() => await RejectedRequest(new(Protocol.Version, "ExecuteCommand"), "UnknownOperation");
    private static async Task HandshakeRequired() => await RejectedRequest(new(Protocol.Version, "GetSnapshot"),
        "HandshakeRequired", handshaken: false);

    private static async Task RejectedRequest(PipeRequest request, string status, bool handshaken = true)
    {
        string name = Name(); int snapshots = 0;
        await using var server = new IpcServer(name, () => Capabilities, () => { snapshots++; return Snapshot(); });
        _ = server.RunAsync();
        using (var raw = NativePipeSecurity.CreateClient(name))
        {
            await raw.ConnectAsync(2000);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (handshaken)
            {
                await IpcProtocol.WriteRequestAsync(raw, new(Protocol.Version, "Hello"), timeout.Token);
                Check.Equal("Ok", (await IpcProtocol.ReadResponseAsync(raw, timeout.Token)).Status, "setup Hello");
            }
            await IpcProtocol.WriteRequestAsync(raw, request, timeout.Token);
            Check.Equal(status, (await IpcProtocol.ReadResponseAsync(raw, timeout.Token)).Status, "invalid request response");
        }
        Check.Equal(0, snapshots, "rejected operation never fetches a snapshot");
        await HealthyAfterRejection(name);
    }

    private static async Task MalformedRequest() => await InvalidBytes(ProtocolTests.Frame("{\"protocolVersion\":1,\"operation\":\"Hello\",\"command\":\"private-sentinel\"}"));
    private static async Task OversizedRequest() => await InvalidBytes(ProtocolTests.Header(Protocol.MaximumRequestBytes + 1));
    private static async Task InvalidBytes(byte[] bytes)
    {
        string name = Name();
        await using var server = new IpcServer(name, () => Capabilities, Snapshot);
        _ = server.RunAsync();
        using (var raw = NativePipeSecurity.CreateClient(name))
        {
            await raw.ConnectAsync(2000);
            await raw.WriteAsync(bytes);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            byte[] one = new byte[1];
            int length;
            try { length = await raw.ReadAsync(one, timeout.Token); }
            catch (IOException) { length = 0; }
            Check.Equal(0, length, "invalid client is disconnected without an unbounded body read");
        }
        await HealthyAfterRejection(name);
        Check.True(server.RejectedRequests >= 1, "invalid request is counted");
    }

    private static async Task HealthyAfterRejection(string name)
    {
        await using var healthy = Client(name);
        Check.Equal(IpcState.Connected, (await Bound(healthy.ConnectAsync())).State, "subsequent client can handshake");
        Check.Equal(IpcState.Connected, (await Bound(healthy.GetSnapshotAsync())).State, "subsequent client can read snapshot");
    }

    private static async Task IndependentClients()
    {
        string name = Name(); var aggregate = AggregationTests.Create();
        aggregate.Add(AggregationTests.Raw(bytes: 400));
        await using var server = new IpcServer(name, () => Capabilities,
            () => aggregate.GetSnapshot() with { Capabilities = Capabilities });
        _ = server.RunAsync();
        await using var first = Client(name); await using var second = Client(name);
        var hellos = await Bound(Task.WhenAll(first.ConnectAsync(), second.ConnectAsync()));
        Check.True(hellos.All(x => x.State == IpcState.Connected), "both clients authenticate independently");
        var results = await Bound(Task.WhenAll(first.GetSnapshotAsync(), second.GetSnapshotAsync()));
        Check.True(results.All(x => x.State == IpcState.Connected), "concurrent requests succeed");
        Check.True(results.All(x => x.Value!.Applications.Single().Counters.TxBytes == 400), "snapshot reads cannot consume cumulative state");
        Check.Equal(results[0].Value!.CollectorInstanceId, results[1].Value!.CollectorInstanceId, "clients share collector instance identity");
        Check.Equal(2L, aggregate.GetSnapshot(countRequest: false).Metrics.SnapshotsServed, "one request from each client counted once");
    }

    private static async Task DisposeReleasesHandles()
    {
        string name = Name();
        var server = new IpcServer(name, () => Capabilities, Snapshot);
        var running = server.RunAsync();
        await using var client = Client(name);
        Check.Equal(IpcState.Connected, (await Bound(client.ConnectAsync())).State, "connection before disposal");
        await server.DisposeAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(2));
        Check.Equal(IpcState.Disconnected, (await Bound(client.GetSnapshotAsync())).State, "existing client sees disposal");
        await server.DisposeAsync();
        await using var replacement = new IpcServer(name, () => Capabilities, Snapshot);
        _ = replacement.RunAsync();
        await HealthyAfterRejection(name);
    }

    private static Task ActualPipeDacl()
    {
        using var pipe = NativePipeSecurity.CreateServer(Name(), true);
        uint result = GetSecurityInfo(pipe.SafePipeHandle, 6, 4, out _, out _, out _, out _, out var descriptor);
        if (result != 0) throw new Win32Exception((int)result);
        try
        {
            int length = checked((int)GetSecurityDescriptorLength(descriptor));
            byte[] bytes = new byte[length]; Marshal.Copy(descriptor, bytes, 0, length);
            var security = new RawSecurityDescriptor(bytes, 0);
            Check.True((security.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0, "pipe DACL does not inherit broad principals");
            var allowed = security.DiscretionaryAcl!.Cast<GenericAce>().OfType<CommonAce>()
                .Where(ace => ace.AceQualifier == AceQualifier.AccessAllowed).ToArray();
            Check.Equal(1, allowed.Length, "only one allow ACE exists on the real pipe");
            Check.Equal(NativePipeSecurity.CurrentUserSid(), allowed[0].SecurityIdentifier.Value, "only current user receives pipe access");
            Check.True((allowed[0].AccessMask & 0x10000000) != 0 || (allowed[0].AccessMask & 0x0012019f) == 0x0012019f,
                "the same user can read and write the pipe");
        }
        finally { LocalFree(descriptor); }
        return Task.CompletedTask;
    }

    private static Task RejectRemoteNames()
    {
        foreach (string name in new[] { @"\\server\pipe\nettrace", @"\\.\pipe\nettrace", "server/pipe/nettrace", "..\\nettrace" })
        {
            bool rejected = false;
            try { using var pipe = NativePipeSecurity.CreateClient(name); }
            catch (ArgumentException) { rejected = true; }
            Check.True(rejected, "a client cannot choose a remote transport through its pipe name");
        }
        Check.Equal(0x8U, NativePipeSecurity.RejectRemoteClientsFlag, "native PIPE_REJECT_REMOTE_CLIENTS is selected");
        return Task.CompletedTask;
    }

    private static async Task IncompatibleCollector() => await FakeCollector(async pipe =>
    {
        await IpcProtocol.ReadRequestAsync(pipe);
        await IpcProtocol.WriteResponseAsync(pipe, new(Protocol.Version + 1, "Ok", Capabilities));
    }, async client => Check.Equal(IpcState.IncompatibleProtocol, (await Bound(client.ConnectAsync())).State, "bad collector version"));

    private static async Task MalformedCollector() => await FakeCollector(async pipe =>
    {
        await IpcProtocol.ReadRequestAsync(pipe);
        await pipe.WriteAsync(ProtocolTests.Frame("{\"protocolVersion\":1,\"status\":\"Ok\",\"payload\":\"private-sentinel\"}"));
    }, async client => Check.Equal(IpcState.MalformedResponse, (await Bound(client.ConnectAsync())).State, "malformed response is explicit"));

    private static async Task OversizedCollectorResponse() => await FakeCollector(async pipe =>
    {
        await IpcProtocol.ReadRequestAsync(pipe);
        await pipe.WriteAsync(ProtocolTests.Header(Protocol.MaximumResponseBytes + 1));
    }, async client => Check.Equal(IpcState.MalformedResponse, (await Bound(client.ConnectAsync())).State,
        "oversized header rejected before a response body can be allocated"));

    private static async Task NullSummaryEntries()
    {
        var aggregate = AggregationTests.Create(); aggregate.Add(AggregationTests.Raw());
        var good = aggregate.GetSnapshot() with { Capabilities = Capabilities };
        await InvalidSnapshot(good with { Applications = [null!] });
        await InvalidSnapshot(good with { Applications = [good.Applications.Single() with { Processes = [null!] }] });
    }

    private static async Task InvalidSummaryCounters()
    {
        var aggregate = AggregationTests.Create(); aggregate.Add(AggregationTests.Raw(bytes: 100));
        var good = aggregate.GetSnapshot() with { Capabilities = Capabilities };
        var application = good.Applications.Single();
        await InvalidSnapshot(good with { EventCount = -1 });
        await InvalidSnapshot(good with
        {
            Applications = [application with { Counters = application.Counters with { TxBytes = -1 } }]
        });
        await InvalidSnapshot(good with
        {
            Applications = [application with
            {
                // Each local/family total remains internally coherent, but no longer
                // matches the per-process totals. An application sum must be checked too.
                Counters = application.Counters with { TxBytes = 101, Ipv4TxBytes = 101, NonLoopbackTxBytes = 101 }
            }]
        });
    }

    private static async Task InvalidSnapshot(CollectorSnapshot snapshot) => await FakeCollector(async pipe =>
    {
        await IpcProtocol.ReadRequestAsync(pipe);
        await IpcProtocol.WriteResponseAsync(pipe, new(Protocol.Version, "Ok", Capabilities));
        await IpcProtocol.ReadRequestAsync(pipe);
        await IpcProtocol.WriteResponseAsync(pipe, new(Protocol.Version, "Ok", Snapshot: snapshot));
    }, async client =>
    {
        Check.Equal(IpcState.Connected, (await Bound(client.ConnectAsync())).State, "valid Hello before invalid snapshot");
        var result = await Bound(client.GetSnapshotAsync());
        Check.Equal(IpcState.MalformedResponse, result.State, "untrusted summary cannot violate counter or collection semantics");
        Check.True(result.Value is null, "invalid summaries are never delivered to the client caller");
    });

    private static async Task BackwardsSnapshot()
    {
        var aggregate = AggregationTests.Create();
        var low = aggregate.GetSnapshot(countRequest: false) with { Capabilities = Capabilities };
        aggregate.Add(AggregationTests.Raw(bytes: 100));
        var high = aggregate.GetSnapshot(countRequest: false) with { Capabilities = Capabilities };
        await FakeCollector(async pipe =>
        {
            await IpcProtocol.ReadRequestAsync(pipe);
            await IpcProtocol.WriteResponseAsync(pipe, new(Protocol.Version, "Ok", Capabilities));
            await IpcProtocol.ReadRequestAsync(pipe);
            await IpcProtocol.WriteResponseAsync(pipe, new(Protocol.Version, "Ok", Snapshot: high));
            await IpcProtocol.ReadRequestAsync(pipe);
            await IpcProtocol.WriteResponseAsync(pipe, new(Protocol.Version, "Ok", Snapshot: low));
        }, async client =>
        {
            Check.Equal(IpcState.Connected, (await Bound(client.ConnectAsync())).State, "setup handshake");
            Check.Equal(IpcState.Connected, (await Bound(client.GetSnapshotAsync())).State, "first cumulative snapshot");
            Check.Equal(IpcState.MalformedResponse, (await Bound(client.GetSnapshotAsync())).State,
                "a connected collector cannot silently reset its cumulative totals");
        });
    }

    private static async Task MidResponseDisconnect() => await FakeCollector(async pipe =>
    {
        await IpcProtocol.ReadRequestAsync(pipe);
        await pipe.WriteAsync(ProtocolTests.Header(100));
        await pipe.WriteAsync(new byte[] { (byte)'{' });
    }, async client => Check.Equal(IpcState.Disconnected, (await Bound(client.ConnectAsync())).State, "partial response is disconnected"));

    private static async Task RequestTimeout()
    {
        int requests = 0; var watch = Stopwatch.StartNew();
        await FakeCollector(async pipe =>
        {
            await IpcProtocol.ReadRequestAsync(pipe); requests++;
            await Task.Delay(TimeSpan.FromSeconds(5));
        }, async client => Check.Equal(IpcState.TimedOut, (await Bound(client.ConnectAsync())).State, "silent collector request timeout"));
        Check.Equal(1, requests, "timeout does not retry the request");
        Check.True(watch.Elapsed < TimeSpan.FromSeconds(7), "silent peer fixture terminates within a finite interval");
    }

    private static async Task CancelConnect()
    {
        await using var client = Client(Name());
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        Check.Equal(IpcState.Cancelled, (await Bound(client.ConnectAsync(cancel.Token))).State, "connect cancellation");
    }

    private static async Task CancelRequest() => await FakeCollector(async pipe =>
    {
        await IpcProtocol.ReadRequestAsync(pipe);
        await IpcProtocol.WriteResponseAsync(pipe, new(Protocol.Version, "Ok", Capabilities));
        await IpcProtocol.ReadRequestAsync(pipe);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
    }, async client =>
    {
        Check.Equal(IpcState.Connected, (await Bound(client.ConnectAsync())).State, "setup connection");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        Check.Equal(IpcState.Cancelled, (await Bound(client.GetSnapshotAsync(cancel.Token))).State, "request cancellation");
    });

    private static async Task FakeCollector(Func<NamedPipeServerStream, Task> serve, Func<IpcClient, Task> check)
    {
        string name = Name();
        using var pipe = NativePipeSecurity.CreateServer(name, true, 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var serving = Task.Run(async () =>
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            try { await serve(pipe); }
            finally { pipe.Dispose(); }
        });
        try
        {
            await using var client = Client(name);
            await check(client);
            await serving.WaitAsync(timeout.Token);
        }
        finally { timeout.Cancel(); pipe.Dispose(); }
    }

    private static async Task TestShutdown()
    {
        string name = Name(); using var stopping = new CancellationTokenSource();
        await using var server = new IpcServer(name, () => Capabilities with { ShutdownForTestEnabled = true }, Snapshot,
            shutdownForTest: stopping.Cancel);
        var running = server.RunAsync(stopping.Token);
        await using var client = Client(name);
        Check.Equal(IpcState.Connected, (await Bound(client.ConnectAsync())).State, "setup diagnostic handshake");
        var result = await Bound(client.ShutdownForTestAsync());
        Check.Equal(IpcState.Connected, result.State, "shutdown acknowledgement arrives");
        Check.True(result.Value, "shutdown acknowledged as successful");
        await running.WaitAsync(TimeSpan.FromSeconds(3));
        Check.True(stopping.IsCancellationRequested, "only enabled diagnostic shutdown cancels server");
    }

    private static async Task ReconnectAfterStop()
    {
        string name = Name(); var first = AggregationTests.Create(); first.Add(AggregationTests.Raw(bytes: 500));
        var server = new IpcServer(name, () => Capabilities, () => first.GetSnapshot() with { Capabilities = Capabilities });
        _ = server.RunAsync();
        await using var client = Client(name);
        Check.Equal(IpcState.Connected, (await Bound(client.ConnectAsync())).State, "first collector handshake");
        var old = (await Bound(client.GetSnapshotAsync())).Value!;
        await server.DisposeAsync();
        Check.Equal(IpcState.Disconnected, (await Bound(client.GetSnapshotAsync())).State, "stopped collector state");
        var fresh = AggregationTests.Create();
        await using var restarted = new IpcServer(name, () => Capabilities, () => fresh.GetSnapshot() with { Capabilities = Capabilities });
        _ = restarted.RunAsync();
        Check.Equal(IpcState.Connected, (await Bound(client.ConnectAsync())).State, "same client reconnects");
        var current = (await Bound(client.GetSnapshotAsync())).Value!;
        Check.True(old.CollectorInstanceId != current.CollectorInstanceId, "restart observed through a new instance identity");
        Check.Equal(0L, current.EventCount, "restart cumulative counters begin at zero");
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(SafePipeHandle handle, int objectType, uint information,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint SetSecurityInfo(SafePipeHandle handle, int objectType, uint information,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [StructLayout(LayoutKind.Sequential)]
    private struct TestSecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateNamedPipeW")]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode,
        uint maximumInstances, uint outputBuffer, uint inputBuffer, uint timeout, ref TestSecurityAttributes attributes);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr value);
}
