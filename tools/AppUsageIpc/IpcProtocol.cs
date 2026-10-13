using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppUsageIpc;

public sealed class IpcProtocolException(string message) : IOException(message);

public static class IpcProtocol
{
    // Protocol types are fixed. Reflection and nullable metadata are inspected
    // once per type, rather than for every process in every polled snapshot.
    private sealed record PropertyShape(string JsonName, Type ValueType, bool Nullable);
    private sealed record ObjectShape(IReadOnlyDictionary<string, PropertyShape> Properties,
        IReadOnlyList<string> RequiredProperties);
    private static readonly ConcurrentDictionary<Type, ObjectShape> Shapes = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = IpcJsonContext.Default,
        MaxDepth = 32
    };

    public static async Task<PipeRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var bytes = await ReadFrameAsync(stream, Protocol.MaximumRequestBytes, cancellationToken).ConfigureAwait(false);
        return Deserialize<PipeRequest>(bytes);
    }

    public static async Task<PipeResponse> ReadResponseAsync(Stream stream, CancellationToken cancellationToken = default,
        int expectedVersion = Protocol.Version)
    {
        var bytes = await ReadFrameAsync(stream, Protocol.MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
        // Inspect the version before validating this version's exact record shape.
        // Newer health fields must yield an explicit compatibility error to old clients.
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("protocolVersion", out var version)
                && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out int number))
            {
                if (json.RootElement.EnumerateObject().Count(property => property.Name == "protocolVersion") != 1)
                    throw new JsonException();
                if (number != expectedVersion)
                    return new(number, "IncompatibleProtocol", Error: "The protocol version is incompatible.");
            }
        }
        catch (JsonException) { throw new IpcProtocolException("Malformed IPC JSON."); }
        return Deserialize<PipeResponse>(bytes);
    }

    public static Task WriteRequestAsync(Stream stream, PipeRequest request, CancellationToken cancellationToken = default) =>
        WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions), Protocol.MaximumRequestBytes, cancellationToken);
    public static async Task WriteResponseAsync(Stream stream, PipeResponse response, CancellationToken cancellationToken = default)
    {
        if (response.AppUsage is null)
        {
            await WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions),
                Protocol.MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
            return;
        }
        ValidateQueryMetadataSize(response.AppUsage, cancellationToken);
        // Query history can contain many rows. Cap allocation while serializing,
        // before writing any frame header, and honor cancellation between chunks.
        using var buffer = new BoundedResponseBuffer(Protocol.MaximumResponseBytes, cancellationToken);
        using (var writer = new Utf8JsonWriter(buffer))
            JsonSerializer.Serialize(writer, response, JsonOptions);
        await WriteFrameAsync(stream, buffer.ToArray(), Protocol.MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateQueryMetadataSize(AppUsageQueryResult usage, CancellationToken cancellationToken)
    {
        // Escaping a single JSON string can allocate before the writer requests
        // output space. Bound each stored field too; Windows paths fit this cap.
        const int maximumCharacters = 65536;
        static bool TooLong(string? value) => value?.Length > maximumCharacters;
        if (TooLong(usage.Diagnostic)) throw new IpcProtocolException("Invalid or oversized IPC metadata.");
        foreach (var application in usage.Applications)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (application is not null && (TooLong(application.ApplicationIdentity) || TooLong(application.DisplayName)
                || TooLong(application.ExecutableName) || TooLong(application.ExecutablePath)))
                throw new IpcProtocolException("Invalid or oversized IPC metadata.");
        }
    }

    private sealed class BoundedResponseBuffer(int maximumBytes, CancellationToken cancellationToken)
        : IBufferWriter<byte>, IDisposable
    {
        private byte[] bytes = ArrayPool<byte>.Shared.Rent(256);
        private int written;
        private void EnsureSpace(int sizeHint)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sizeHint = Math.Max(1, sizeHint);
            if (sizeHint > maximumBytes - written)
                throw new IpcProtocolException("Invalid or oversized IPC frame.");
            if (sizeHint <= bytes.Length - written) return;
            int capacity = Math.Max(written + sizeHint, Math.Min(maximumBytes, bytes.Length * 2));
            var replacement = ArrayPool<byte>.Shared.Rent(capacity);
            bytes.AsSpan(0, written).CopyTo(replacement);
            ArrayPool<byte>.Shared.Return(bytes);
            bytes = replacement;
        }
        public void Advance(int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (count < 0 || count > bytes.Length - written || count > maximumBytes - written)
                throw new IpcProtocolException("Invalid or oversized IPC frame.");
            written += count;
        }
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureSpace(sizeHint);
            return bytes.AsMemory(written, Math.Min(bytes.Length - written, maximumBytes - written));
        }
        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureSpace(sizeHint);
            return bytes.AsSpan(written, Math.Min(bytes.Length - written, maximumBytes - written));
        }
        public byte[] ToArray() => bytes.AsSpan(0, written).ToArray();
        public void Dispose() => ArrayPool<byte>.Shared.Return(bytes);
    }

    public static async Task<byte[]> ReadFrameAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > maximumBytes) throw new IpcProtocolException("Invalid or oversized IPC frame.");
        byte[] bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    public static async Task WriteFrameAsync(Stream stream, byte[] bytes, int maximumBytes, CancellationToken cancellationToken = default)
    {
        if (bytes.Length == 0 || bytes.Length > maximumBytes) throw new IpcProtocolException("Invalid or oversized IPC frame.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static T Deserialize<T>(byte[] bytes)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            ValidateShape(json.RootElement, typeof(T));
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new IpcProtocolException("Empty IPC object.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            // Never reflect an untrusted request/response value into diagnostics.
            throw new IpcProtocolException("Malformed IPC JSON or unsupported fields.");
        }
    }

    private static void ValidateShape(JsonElement value, Type type)
    {
        if (value.ValueKind == JsonValueKind.Null) return;
        if (Nullable.GetUnderlyingType(type) is Type underlying) { ValidateShape(value, underlying); return; }
        if (type.IsEnum)
        {
            if (value.ValueKind != JsonValueKind.String
                || !Enum.GetNames(type).Contains(value.GetString(), StringComparer.Ordinal))
                throw new JsonException();
            return;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            if (value.ValueKind != JsonValueKind.Array) throw new JsonException();
            foreach (var element in value.EnumerateArray())
            {
                // These protocol collections contain non-null summary records.
                if (element.ValueKind == JsonValueKind.Null) throw new JsonException();
                ValidateShape(element, type.GenericTypeArguments[0]);
            }
            return;
        }
        if (type.Namespace != typeof(PipeRequest).Namespace) return;
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException();
        var shape = Shapes.GetOrAdd(type, CreateShape);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name) || !shape.Properties.TryGetValue(property.Name, out var descriptor)) throw new JsonException();
            if (property.Value.ValueKind == JsonValueKind.Null && !descriptor.Nullable) throw new JsonException();
            ValidateShape(property.Value, descriptor.ValueType);
        }
        foreach (var property in shape.RequiredProperties)
            if (!seen.Contains(property)) throw new JsonException();
    }

    private static ObjectShape CreateShape(Type type)
    {
        var nullability = new NullabilityInfoContext();
        var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(property =>
            new PropertyShape(JsonNamingPolicy.CamelCase.ConvertName(property.Name), property.PropertyType,
                Nullable.GetUnderlyingType(property.PropertyType) is not null || !property.PropertyType.IsValueType
                    && nullability.Create(property).ReadState == NullabilityState.Nullable)).ToArray();
        return new(properties.ToDictionary(property => property.JsonName, StringComparer.Ordinal),
            Array.AsReadOnly(properties.Where(property => !property.Nullable).Select(property => property.JsonName).ToArray()));
    }

    public static PipeResponse Dispatch(PipeRequest request, bool handshaken,
        Func<CollectorCapabilities> capabilities, Func<CollectorSnapshot> snapshot, bool shutdownEnabled,
        int protocolVersion = Protocol.Version)
    {
        if (request.ProtocolVersion != protocolVersion)
            return new(protocolVersion, "IncompatibleProtocol", Error: "The protocol version is incompatible.");
        if (request.AppUsageQuery is not null)
            return new(protocolVersion, "InvalidRequest", Error: "The request parameters are unsupported for this operation.");
        if (request.Operation is not ("Hello" or "GetCapabilities" or "GetSnapshot" or "ShutdownForTest"))
            return new(protocolVersion, "UnknownOperation", Error: "The operation is unsupported.");
        if (!handshaken && request.Operation != "Hello")
            return new(protocolVersion, "HandshakeRequired", Error: "Hello must complete before other requests.");
        return request.Operation switch
        {
            "Hello" or "GetCapabilities" => new(protocolVersion, "Ok", Capabilities: capabilities()),
            "GetSnapshot" => new(protocolVersion, "Ok", Snapshot: snapshot()),
            "ShutdownForTest" when shutdownEnabled => new(protocolVersion, "Ok"),
            _ => new(protocolVersion, "ShutdownDisabled", Error: "Test shutdown is disabled.")
        };
    }

    public static async Task<PipeResponse> DispatchAsync(PipeRequest request, bool handshaken,
        Func<CollectorCapabilities> capabilities, Func<CollectorSnapshot> snapshot, bool shutdownEnabled,
        int protocolVersion = Protocol.Version,
        Func<AppUsageRange, CancellationToken, Task<AppUsageQueryResult>>? appUsageQuery = null,
        CancellationToken cancellationToken = default)
    {
        if (request.Operation != "GetAppUsageRange")
            return Dispatch(request, handshaken, capabilities, snapshot, shutdownEnabled, protocolVersion);
        if (request.ProtocolVersion != protocolVersion)
            return new(protocolVersion, "IncompatibleProtocol", Error: "The protocol version is incompatible.");
        if (protocolVersion != Protocol.ServiceVersion || appUsageQuery is null)
            return new(protocolVersion, "UnknownOperation", Error: "The operation is unsupported.");
        if (!handshaken)
            return new(protocolVersion, "HandshakeRequired", Error: "Hello must complete before other requests.");
        if (request.AppUsageQuery is null || !Enum.IsDefined(request.AppUsageQuery.Range))
            return new(protocolVersion, "InvalidRequest", Error: "Only Today, 7D and 30D are supported.");
        try
        {
            var result = await appUsageQuery(request.AppUsageQuery.Range, cancellationToken)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            return new(protocolVersion, "Ok", AppUsage: result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // The authenticated connection remains usable after a history failure.
            return new(protocolVersion, "Ok", AppUsage: AppUsageRangeRules.Unavailable(request.AppUsageQuery.Range));
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PipeRequest))]
[JsonSerializable(typeof(PipeResponse))]
internal partial class IpcJsonContext : JsonSerializerContext;
