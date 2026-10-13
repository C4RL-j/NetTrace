using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using AppUsageIpc;

internal static class ProtocolTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Each typed range request survives strict serialization round trip", RequestRoundTrip),
        ("Application usage response fields survive serialization round trip", ResponseRoundTrip),
        ("Malformed extra duplicate missing or null query fields are rejected", MalformedQueries),
        ("Invalid numeric and unknown range values are rejected before dispatch", InvalidRanges),
        ("Arbitrary SQL paths and date expressions cannot enter the query request", NoArbitraryAccess),
        ("Oversized requests are rejected from framing header without reading a body", OversizedRequest),
        ("Protocol 1 and protocol 2 legacy Hello requests remain byte compatible", LegacyRequestBytes),
        ("Legacy response bytes omit all new optional fields", LegacyResponseBytes),
        ("Future protocol responses still report explicit incompatibility before shape validation", FutureProtocol),
        ("Query wire payload contains no PID physical totals or event endpoints", MetadataOnly),
        ("Missing or extra application fields cannot bypass strict response parsing", MalformedResponse),
        ("An oversized query response cannot allocate an unbounded payload", OversizedResponse),
        ("Oversized outbound history serialization emits neither header nor partial body", OversizedOutboundResponse)
    ];

    private static async Task RequestRoundTrip()
    {
        foreach (var range in new[] { AppUsageRange.Today, AppUsageRange.SevenDays, AppUsageRange.ThirtyDays })
        {
            using var stream = new MemoryStream();
            var request = new PipeRequest(Protocol.ServiceVersion, "GetAppUsageRange", new(range));
            await IpcProtocol.WriteRequestAsync(stream, request); stream.Position = 0;
            Check.Equal(request, await IpcProtocol.ReadRequestAsync(stream), "typed range preserved");
            Check.True(Encoding.UTF8.GetString(stream.ToArray(), 4, (int)stream.Length - 4).Contains($"\"range\":\"{range}\"", StringComparison.Ordinal), "approved wire enum names");
        }
    }
    private static async Task ResponseRoundTrip()
    {
        var expected = await Fixtures.Service().QueryAsync(AppUsageRange.SevenDays);
        using var stream = new MemoryStream(); await IpcProtocol.WriteResponseAsync(stream, new(Protocol.ServiceVersion, "Ok", AppUsage: expected));
        stream.Position = 0; var received = (await IpcProtocol.ReadResponseAsync(stream, expectedVersion: Protocol.ServiceVersion)).AppUsage!;
        Check.Equal(expected.Range, received.Range, "range"); Check.Equal(expected.StartLocalDate, received.StartLocalDate, "inclusive start");
        Check.Equal(expected.EndLocalDate, received.EndLocalDate, "inclusive end"); Check.Equal(expected.TrackedApplicationCount, received.TrackedApplicationCount, "count");
        Check.Equal(expected.TotalDownloadBytes, received.TotalDownloadBytes, "download"); Check.Equal(expected.TotalUploadBytes, received.TotalUploadBytes, "upload");
        Check.Equal(expected.LogicalTotalBytes, received.LogicalTotalBytes, "logical"); Check.Equal(expected.Availability, received.Availability, "availability");
        Check.True(expected.Applications.SequenceEqual(received.Applications), "all application metadata and counts");
    }
    private static async Task MalformedQueries()
    {
        foreach (string query in new[] { "{}", "[]", "{\"range\":null}",
            "{\"range\":\"Today\",\"range\":\"SevenDays\"}", "{\"range\":\"Today\",\"extra\":true}" })
        {
            await RejectRequest($"{{\"protocolVersion\":2,\"operation\":\"GetAppUsageRange\",\"appUsageQuery\":{query}}}");
        }
        await RejectRequest("{\"protocolVersion\":2,\"operation\":\"GetAppUsageRange\",\"appUsageQuery\":{\"range\":\"Today\"},\"appUsageQuery\":{\"range\":\"SevenDays\"}}");
    }
    private static async Task InvalidRanges()
    {
        foreach (string value in new[] { "0", "7", "30", "1", "-1", "\"7D\"", "\"30D\"", "\"today\"", "\"SevenDays, ThirtyDays\"", "\"AllTime\"", "true", "[]" })
            await RejectRequest($"{{\"protocolVersion\":2,\"operation\":\"GetAppUsageRange\",\"appUsageQuery\":{{\"range\":{value}}}}}");
    }
    private static async Task NoArbitraryAccess()
    {
        foreach (string injected in new[] { "\"sql\":\"DELETE FROM daily_app_usage\"", "\"databasePath\":\"C:\\\\private.db\"", "\"startLocalDate\":\"2026-01-01\"", "\"dateExpression\":\"now-90days\"" })
            await RejectRequest($"{{\"protocolVersion\":2,\"operation\":\"GetAppUsageRange\",\"appUsageQuery\":{{\"range\":\"Today\",{injected}}}}}");
    }
    private static async Task RejectRequest(string json)
    {
        using var stream = new MemoryStream(Frame(json));
        await Check.ThrowsAsync<IpcProtocolException>(async () => { await IpcProtocol.ReadRequestAsync(stream); }, "unapproved request shape");
    }
    private static async Task OversizedRequest()
    {
        using var stream = new MemoryStream(Header(Protocol.MaximumRequestBytes + 1));
        await Check.ThrowsAsync<IpcProtocolException>(async () => { await IpcProtocol.ReadRequestAsync(stream); }, "oversized header rejected before body");
        Check.Equal(4L, stream.Position, "only fixed header consumed");
    }
    private static async Task LegacyRequestBytes()
    {
        Check.Equal(1, Protocol.Version, "collector protocol unchanged"); Check.Equal(2, Protocol.ServiceVersion, "service protocol unchanged");
        foreach (int version in new[] { Protocol.Version, Protocol.ServiceVersion })
        {
            using var stream = new MemoryStream(); await IpcProtocol.WriteRequestAsync(stream, new(version, "Hello"));
            Check.True(stream.ToArray().SequenceEqual(Frame($"{{\"protocolVersion\":{version},\"operation\":\"Hello\"}}")), "legacy golden Hello bytes");
        }
    }
    private static async Task LegacyResponseBytes()
    {
        foreach (int version in new[] { Protocol.Version, Protocol.ServiceVersion })
        {
            using var stream = new MemoryStream(); await IpcProtocol.WriteResponseAsync(stream, new(version, "UnknownOperation", Error: "Unsupported IPC operation."));
            Check.True(stream.ToArray().SequenceEqual(Frame($"{{\"protocolVersion\":{version},\"status\":\"UnknownOperation\",\"error\":\"Unsupported IPC operation.\"}}")), "legacy golden rejection bytes");
        }
    }
    private static async Task FutureProtocol()
    {
        using var stream = new MemoryStream(Frame("{\"protocolVersion\":3,\"status\":\"Ok\",\"futureField\":true}"));
        Check.Equal("IncompatibleProtocol", (await IpcProtocol.ReadResponseAsync(stream, expectedVersion: Protocol.ServiceVersion)).Status, "future protocol explicit");
    }
    private static async Task MetadataOnly()
    {
        using var stream = new MemoryStream(); await IpcProtocol.WriteResponseAsync(stream,
            new(Protocol.ServiceVersion, "Ok", AppUsage: await Fixtures.Service().QueryAsync(AppUsageRange.Today)));
        using var json = JsonDocument.Parse(stream.ToArray().AsMemory(4)); AssertNoRawFields(json.RootElement);
    }
    private static void AssertNoRawFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                Check.True(!new[] { "processId", "pid", "physicalTotal", "dashboardToday", "sourceAddress", "destinationAddress", "sourcePort", "destinationPort", "rawEvents", "sql", "databasePath" }.Contains(property.Name, StringComparer.OrdinalIgnoreCase), "no arbitrary raw or physical fields");
                AssertNoRawFields(property.Value);
            }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var element in value.EnumerateArray()) AssertNoRawFields(element);
    }
    private static async Task MalformedResponse()
    {
        using var encoded = new MemoryStream(); await IpcProtocol.WriteResponseAsync(encoded,
            new(Protocol.ServiceVersion, "Ok", AppUsage: await Fixtures.Service().QueryAsync(AppUsageRange.Today)));
        string valid = Encoding.UTF8.GetString(encoded.ToArray(), 4, (int)encoded.Length - 4);
        foreach (string modified in new[] { valid.Replace("\"downloadBytes\":1000,", "", StringComparison.Ordinal),
            valid.Replace("\"displayName\":\"Brave\"", "\"displayName\":\"Brave\",\"pid\":123", StringComparison.Ordinal),
            valid.Replace("\"availability\":\"Available\"", "\"availability\":0", StringComparison.Ordinal) })
        {
            Check.True(valid != modified, "invalid shape mutation applied");
            using var stream = new MemoryStream(Frame(modified));
            await Check.ThrowsAsync<IpcProtocolException>(async () => { await IpcProtocol.ReadResponseAsync(stream, expectedVersion: Protocol.ServiceVersion); }, "bad response shape rejected");
        }
    }
    private static async Task OversizedResponse()
    {
        using var stream = new MemoryStream(Header(Protocol.MaximumResponseBytes + 1));
        await Check.ThrowsAsync<IpcProtocolException>(async () => { await IpcProtocol.ReadResponseAsync(stream, expectedVersion: Protocol.ServiceVersion); }, "bounded query response frame");
    }
    private static async Task OversizedOutboundResponse()
    {
        var good = await Fixtures.Service().QueryAsync(AppUsageRange.Today);
        foreach (string metadata in new[] { new string('x', Protocol.MaximumResponseBytes),
            new string('<', Protocol.MaximumResponseBytes / 6 + 1) })
        {
            // '<' expands to six UTF-8 JSON bytes per character. Bounding just
            // final wire writes would still permit a large temporary escape buffer.
            var oversized = good with { Applications = [good.Applications[0] with { DisplayName = metadata }, good.Applications[1]] };
            using var destination = new MemoryStream();
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            await Check.ThrowsAsync<IpcProtocolException>(() => IpcProtocol.WriteResponseAsync(destination,
                new(Protocol.ServiceVersion, "Ok", AppUsage: oversized)), "plain or escape-expanded history rejects the response bound");
            long rejectionAllocation = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Check.Equal(0L, destination.Length, "outbound oversize rejected before any wire header or body");
            if (metadata[0] == '<')
            {
                Check.True(rejectionAllocation >= 0 && rejectionAllocation < 1024 * 1024,
                    "escaped oversized metadata rejected before a large temporary escape allocation");
                Console.WriteLine($"SERIALIZATION escapedOversizeRejectedAllocationBytes={rejectionAllocation}");
            }
        }
    }
    internal static byte[] Header(int length) { byte[] value = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(value, length); return value; }
    internal static byte[] Frame(string json) { var bytes = Encoding.UTF8.GetBytes(json); return Header(bytes.Length).Concat(bytes).ToArray(); }
}
