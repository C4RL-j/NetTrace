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
        MaxDepth = 32
    };

    public static async Task<PipeRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var bytes = await ReadFrameAsync(stream, Protocol.MaximumRequestBytes, cancellationToken).ConfigureAwait(false);
        return Deserialize<PipeRequest>(bytes);
    }

    public static async Task<PipeResponse> ReadResponseAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var bytes = await ReadFrameAsync(stream, Protocol.MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
        return Deserialize<PipeResponse>(bytes);
    }

    public static Task WriteRequestAsync(Stream stream, PipeRequest request, CancellationToken cancellationToken = default) =>
        WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions), Protocol.MaximumRequestBytes, cancellationToken);
    public static Task WriteResponseAsync(Stream stream, PipeResponse response, CancellationToken cancellationToken = default) =>
        WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions), Protocol.MaximumResponseBytes, cancellationToken);

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
        Func<CollectorCapabilities> capabilities, Func<CollectorSnapshot> snapshot, bool shutdownEnabled)
    {
        if (request.ProtocolVersion != Protocol.Version)
            return new(Protocol.Version, "IncompatibleProtocol", Error: "The protocol version is incompatible.");
        if (request.Operation is not ("Hello" or "GetCapabilities" or "GetSnapshot" or "ShutdownForTest"))
            return new(Protocol.Version, "UnknownOperation", Error: "The operation is unsupported.");
        if (!handshaken && request.Operation != "Hello")
            return new(Protocol.Version, "HandshakeRequired", Error: "Hello must complete before other requests.");
        return request.Operation switch
        {
            "Hello" or "GetCapabilities" => new(Protocol.Version, "Ok", Capabilities: capabilities()),
            "GetSnapshot" => new(Protocol.Version, "Ok", Snapshot: snapshot()),
            "ShutdownForTest" when shutdownEnabled => new(Protocol.Version, "Ok"),
            _ => new(Protocol.Version, "ShutdownDisabled", Error: "Test shutdown is disabled.")
        };
    }
}
