using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using AppUsageIpc;

internal static class ProtocolTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Hello establishes the supported version before snapshots are accessible", Check.Sync(Handshake)),
        ("An incompatible request version cannot invoke collector suppliers", Check.Sync(IncompatibleVersion)),
        ("Unsupported privileged operations cannot reach collector suppliers", Check.Sync(NarrowOperations)),
        ("Test shutdown is available only when explicitly enabled", Check.Sync(TestShutdownGuard)),
        ("Malformed, incomplete, duplicate, or extra request fields are rejected safely", MalformedRequests),
        ("Invalid and oversized request frames are rejected before their bodies are read", OversizedRequests),
        ("Malformed response objects and incomplete nested capability objects are rejected", MalformedResponses),
        ("Oversized response frames cannot allocate an unbounded payload", OversizedResponses),
        ("A snapshot round trip preserves aggregate metadata while excluding raw event endpoints", SnapshotRoundTrip),
    ];

    internal static CollectorCapabilities Capabilities => AggregationTests.Create().GetCapabilities();
    private static PipeResponse Dispatch(string operation, bool handshaken = true, int version = Protocol.Version, bool shutdown = false) =>
        IpcProtocol.Dispatch(new(version, operation), handshaken, () => Capabilities,
            () => AggregationTests.Create().GetSnapshot(), shutdown);

    private static void Handshake()
    {
        var hello = Dispatch("Hello", false);
        Check.Equal("Ok", hello.Status, "valid Hello");
        Check.Equal(Protocol.Version, hello.ProtocolVersion, "advertised protocol version");
        Check.True(hello.Capabilities is not null, "Hello supplies capabilities");
        Check.Equal("HandshakeRequired", Dispatch("GetSnapshot", false).Status, "snapshot requires Hello first");
        Check.True(Dispatch("GetSnapshot").Snapshot is not null, "handshaken snapshot is available");
    }

    private static void IncompatibleVersion()
    {
        var invoked = 0;
        var response = IpcProtocol.Dispatch(new(Protocol.Version + 1, "Hello"), false,
            () => { invoked++; return Capabilities; },
            () => { invoked++; return AggregationTests.Create().GetSnapshot(); }, false);
        Check.Equal("IncompatibleProtocol", response.Status, "version mismatch is explicit");
        Check.Equal(0, invoked, "version mismatch cannot access collector data");
    }

    private static void NarrowOperations()
    {
        int invoked = 0;
        foreach (string operation in new[] { "RunProcess", "ExecuteCommand", "ReadFile", "WriteRegistry", "SelectEtwProvider", "hello" })
        {
            var response = IpcProtocol.Dispatch(new(Protocol.Version, operation), true,
                () => { invoked++; return Capabilities; },
                () => { invoked++; return AggregationTests.Create().GetSnapshot(); }, true);
            Check.Equal("UnknownOperation", response.Status, "unrecognized command is rejected");
        }
        Check.Equal(0, invoked, "unsupported operations never access collector suppliers");
    }

    private static void TestShutdownGuard()
    {
        Check.Equal("ShutdownDisabled", Dispatch("ShutdownForTest").Status, "default control surface excludes shutdown");
        Check.Equal("Ok", Dispatch("ShutdownForTest", shutdown: true).Status, "explicit diagnostic shutdown is recognized");
        Check.Equal("HandshakeRequired", Dispatch("ShutdownForTest", handshaken: false, shutdown: true).Status,
            "even diagnostic shutdown requires a handshake");
    }

    private static async Task MalformedRequests()
    {
        foreach (string json in new[]
        {
            "{", "null", "[]", "{}", "{\"protocolVersion\":1}",
            "{\"protocolVersion\":1,\"operation\":null}",
            "{\"protocolVersion\":\"1\",\"operation\":\"Hello\"}",
            "{\"protocolVersion\":1,\"operation\":\"Hello\",\"operation\":\"RunProcess\"}",
            "{\"protocolVersion\":1,\"operation\":\"Hello\",\"command\":\"private-sentinel\"}"
        })
        {
            using var stream = new MemoryStream(Frame(json));
            await Check.ThrowsAsync<IpcProtocolException>(async () => { await IpcProtocol.ReadRequestAsync(stream); }, "invalid request shape");
        }
    }

    private static async Task OversizedRequests()
    {
        foreach (int length in new[] { 0, -1, Protocol.MaximumRequestBytes + 1 })
        {
            using var stream = new MemoryStream(Header(length));
            await Check.ThrowsAsync<IpcProtocolException>(async () => { await IpcProtocol.ReadRequestAsync(stream); }, "bad request length rejected from header");
        }
        using var output = new MemoryStream();
        await Check.ThrowsAsync<IpcProtocolException>(() => IpcProtocol.WriteRequestAsync(output,
            new(Protocol.Version, new string('x', Protocol.MaximumRequestBytes))), "oversized outbound request rejected");
        Check.Equal(0L, output.Length, "oversized writes do not emit a partial frame");
    }

    private static async Task MalformedResponses()
    {
        foreach (string json in new[]
        {
            "{", "null", "{}", "{\"protocolVersion\":1}",
            "{\"protocolVersion\":1,\"status\":\"Ok\",\"payload\":\"private-sentinel\"}",
            "{\"protocolVersion\":1,\"status\":\"Ok\",\"capabilities\":{\"collectorElevated\":true}}"
        })
        {
            using var stream = new MemoryStream(Frame(json));
            await Check.ThrowsAsync<IpcProtocolException>(async () => { await IpcProtocol.ReadResponseAsync(stream); }, "bad response shape rejected");
        }
    }

    private static async Task OversizedResponses()
    {
        using var stream = new MemoryStream(Header(Protocol.MaximumResponseBytes + 1));
        await Check.ThrowsAsync<IpcProtocolException>(async () => { await IpcProtocol.ReadResponseAsync(stream); }, "oversized response header rejected");
    }

    private static async Task SnapshotRoundTrip()
    {
        var aggregate = AggregationTests.Create();
        aggregate.Add(AggregationTests.Raw(bytes: 125));
        aggregate.Add(AggregationTests.Raw(801, bytes: 250, kind: "loopback"));
        var snapshot = aggregate.GetSnapshot();
        using var stream = new MemoryStream();
        await IpcProtocol.WriteResponseAsync(stream, new(Protocol.Version, "Ok", Snapshot: snapshot));
        var encoded = stream.ToArray();
        stream.Position = 0;
        var received = (await IpcProtocol.ReadResponseAsync(stream)).Snapshot!;
        Check.Equal(snapshot.CollectorInstanceId, received.CollectorInstanceId, "instance ID survives IPC");
        Check.Equal(snapshot.CapturedAtUtc, received.CapturedAtUtc, "capture timestamp survives IPC");
        Check.Equal(snapshot.Applications.Single().Counters, received.Applications.Single().Counters, "directional counters survive IPC");
        Check.Equal(snapshot.Applications.Single().Processes.Single().LifetimeIdentity,
            received.Applications.Single().Processes.Single().LifetimeIdentity, "process lifetime survives IPC");
        Check.Equal(snapshot.Applications.Single().ExecutablePath, received.Applications.Single().ExecutablePath, "safe executable identity survives IPC");
        using var json = JsonDocument.Parse(encoded.AsMemory(4));
        AssertMetadataOnly(json.RootElement);
        Check.True(!Encoding.UTF8.GetString(encoded, 4, encoded.Length - 4).Contains("192.0.2.", StringComparison.Ordinal),
            "raw network endpoint values are not serialized");
    }

    private static void AssertMetadataOnly(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                Check.True(!new[] { "payload", "sourceAddress", "destinationAddress", "localAddress", "remoteAddress", "sourcePort", "destinationPort", "url", "dns", "credentials", "rawEvents" }
                    .Contains(property.Name, StringComparer.OrdinalIgnoreCase), "unapproved raw metadata field absent from IPC");
                AssertMetadataOnly(property.Value);
            }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) AssertMetadataOnly(item);
    }

    internal static byte[] Header(int length)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
        return bytes;
    }
    internal static byte[] Frame(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return Header(bytes.Length).Concat(bytes).ToArray();
    }
}
