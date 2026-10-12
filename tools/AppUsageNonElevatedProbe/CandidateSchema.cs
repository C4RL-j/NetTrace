using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;
using AppUsageEtwProbe;

namespace AppUsageNonElevatedProbe;

public sealed record ProviderDefinition(string Name, Guid Guid)
{
    public static IReadOnlyList<ProviderDefinition> Candidates { get; } =
    [
        new("Microsoft-Windows-Network-DataUsage", new("5C1C9AB3-8689-4E41-90FA-85858306D7B7")),
        new("Microsoft-Windows-TCPIP", new("2F07E2EE-15DB-40F1-90EF-9D7BA282188A"))
    ];
}

public sealed record CandidateKeywordDescriptor(string Name, ulong Value);

public sealed record CandidateEventDescriptor(int EventId, int Version, string TaskName,
    string OpcodeName, IReadOnlyList<PayloadField> Fields, string? PidField, string? ByteField,
    TrafficDirection? Direction, bool IsTransferAccounting, bool IsConnectionActivity,
    string? LocalEndpointField, string? RemoteEndpointField)
{
    public string[] Notes { get; init; } = [];
    public string? InterfaceField { get; init; }
}

// Installed manifests establish possible telemetry, never permission to consume
// it or proof that live accounting events are emitted on this installation.
public sealed class CandidateProviderSchema
{
    public CandidateProviderSchema(ProviderDefinition definition, bool providerAvailable,
        int totalEventSchemas, IReadOnlyList<CandidateEventDescriptor> events,
        IReadOnlyList<CandidateKeywordDescriptor> keywords, ulong keywordMask, string? error = null)
    {
        Definition = definition;
        ProviderAvailable = providerAvailable;
        TotalEventSchemas = totalEventSchemas;
        Events = events;
        Keywords = keywords;
        KeywordMask = keywordMask;
        Error = error;
        SafeFields = events.GroupBy(descriptor => (descriptor.EventId, descriptor.Version))
            .ToDictionary(group => group.Key, group => SelectSafeFields(group.First()));
    }

    public static IReadOnlyList<ProviderDefinition> Candidates => ProviderDefinition.Candidates;
    public ProviderDefinition Definition { get; }
    public bool ProviderAvailable { get; }
    public string? Error { get; }
    public int TotalEventSchemas { get; }
    public IReadOnlyList<CandidateEventDescriptor> Events { get; }
    public IReadOnlyList<CandidateKeywordDescriptor> Keywords { get; }
    public ulong KeywordMask { get; }
    public IReadOnlyDictionary<(int, int), IReadOnlyList<PayloadField>> SafeFields { get; }

    public static CandidateProviderSchema Discover(ProviderDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        bool available = false;
        try
        {
            using var provider = new ProviderMetadata(definition.Name);
            if (provider.Id != definition.Guid)
                return new(definition, false, 0, [], [], 0,
                    "The installed provider GUID does not match the requested provider.");
            available = true;
            var keywords = provider.Keywords.Where(keyword => !string.IsNullOrWhiteSpace(keyword.Name))
                .Select(keyword => new CandidateKeywordDescriptor(keyword.Name!, unchecked((ulong)keyword.Value)))
                .ToArray();
            var keywordNames = definition.Name switch
            {
                "Microsoft-Windows-Network-DataUsage" => new[] { "win:ResponseTime" },
                "Microsoft-Windows-TCPIP" => new[]
                {
                    "ut:SendPath", "ut:ReceivePath", "ut:ConnectPath", "ut:ClosePath"
                },
                _ => Array.Empty<string>()
            };
            var mask = keywords.Where(keyword => keywordNames.Contains(keyword.Name, StringComparer.Ordinal))
                .Aggregate(0UL, (current, keyword) => current | keyword.Value);
            var schemas = new List<CandidateEventDescriptor>();
            int total = 0;
            foreach (var metadata in provider.Events)
            {
                total++;
                var fields = ReadFields(metadata.Template);
                var task = metadata.Task.Name ?? "";
                var opcode = metadata.Opcode.Name ?? "";
                var eventKeywords = metadata.Keywords.Where(keyword => keyword.Name is not null)
                    .Select(keyword => keyword.Name!).ToHashSet(StringComparer.Ordinal);
                // The full metadata catalog remains reportable, including the
                // DataUsage provider's empty helper start/stop templates.
                schemas.Add(Describe(checked((int)metadata.Id), metadata.Version, task, opcode,
                    fields, eventKeywords));
            }
            return new(definition, true, total, schemas.AsReadOnly(), keywords, mask,
                mask == 0 ? "No installed keyword for the selected telemetry scope was available." : null);
        }
        catch (Exception exception) when (exception is EventLogException or UnauthorizedAccessException
            or System.Security.SecurityException or System.Xml.XmlException or ArgumentException
            or OverflowException or InvalidOperationException)
        {
            return new(definition, available, 0, [], [], 0,
                $"Installed provider metadata could not be read: {exception.GetType().Name}.");
        }
    }

    private static CandidateEventDescriptor Describe(int id, int version, string task, string opcode,
        IReadOnlyList<PayloadField> fields, IReadOnlySet<string> keywords)
    {
        var notes = new List<string>();
        string? pid = FindNumeric(fields, "Pid", "ProcessId");
        string? localEndpoint = FindSocketAddress(fields, "LocalSockAddr", "LocalAddress");
        string? remoteEndpoint = FindSocketAddress(fields, "RemoteSockAddr", "RemoteAddress");
        string? interfaceField = FindNumeric(fields, "InterfaceIndex", "IfIndex", "AdapterIndex", "AdapterLuid");
        TrafficDirection? direction = null;
        string? bytes = null;
        bool transfer = false;

        // Stable installed task names and their actual named direction keywords
        // validate semantics. Event numbers, versions, indexes and keyword values
        // are discovered, including both legacy and current TCP templates.
        if (task is "UdpEndpointSendMessages" or "UdpEndpointReceiveMessages")
        {
            bool sending = task == "UdpEndpointSendMessages";
            bytes = FindNumeric(fields, "NumBytes");
            if (keywords.Contains(sending ? "ut:SendPath" : "ut:ReceivePath") && bytes is not null)
            {
                direction = sending ? TrafficDirection.Send : TrafficDirection.Receive;
                transfer = true;
            }
            notes.Add("UDP message-transfer bytes; socket-address family and locality require decoding the observed endpoints.");
        }
        else if (task is "TcpDataTransferSend" or "TcpCtcpDataTransferSend" or "TcpDataTransferReceive")
        {
            bool sending = task != "TcpDataTransferReceive";
            bytes = sending ? FindNumeric(fields, "BytesSent", "NumBytes") : FindNumeric(fields, "NumBytes");
            var tcb = fields.FirstOrDefault(field => field.Name == "Tcb" && field.Type == "win:Pointer");
            if (keywords.Contains(sending ? "ut:SendPath" : "ut:ReceivePath") && bytes is not null && tcb is not null)
            {
                direction = sending ? TrafficDirection.Send : TrafficDirection.Receive;
                transfer = true;
            }
            if (pid is null)
                notes.Add("TCP transfer bytes have no direct payload PID; Tcb-to-connection/lifetime correlation is required and is not implemented in this phase.");
            notes.Add("TCP transfer telemetry is separate from cumulative summaries, application requests, offload and retransmission diagnostics.");
        }
        else if (task == "TcpConnectionSummary")
            notes.Add("Connection summary byte counters are cumulative; excluded from transfer-byte aggregation.");
        else if (fields.Any(field => field.Name.Contains("Bytes", StringComparison.OrdinalIgnoreCase)))
            notes.Add("A byte-named diagnostic field alone does not establish additive transferred bytes; excluded from accounting.");

        bool connection = !transfer && pid is not null && (localEndpoint is not null || remoteEndpoint is not null)
            && (task.StartsWith("Tcp", StringComparison.Ordinal) || task.StartsWith("Udp", StringComparison.Ordinal))
            && (task.Contains("Connect", StringComparison.Ordinal) || task.Contains("Accept", StringComparison.Ordinal)
                || task.Contains("Close", StringComparison.Ordinal) || task.Contains("Disconnect", StringComparison.Ordinal)
                || task.Contains("Abort", StringComparison.Ordinal) || task.Contains("Endpoint", StringComparison.Ordinal));
        if (connection)
            notes.Add("Connection/endpoint activity supplies attribution metadata, not transferred byte accounting.");
        if (fields.Count == 0)
            notes.Add("No installed payload template; an emitted event cannot provide payload PID, byte count or direction.");
        if (transfer && pid is null && !notes.Any(note => note.Contains("no direct payload PID", StringComparison.Ordinal)))
            notes.Add("The transfer schema lacks a direct payload PID.");
        if (transfer && interfaceField is null)
            notes.Add("No selected-interface index or LUID is available in this transfer template.");
        return new(id, version, task, opcode, fields, pid, bytes, direction, transfer, connection,
            localEndpoint, remoteEndpoint) { Notes = notes.ToArray(), InterfaceField = interfaceField };
    }

    private static IReadOnlyList<PayloadField> ReadFields(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return [];
        return XDocument.Parse(template).Descendants().Where(element => element.Name.LocalName == "data")
            .Select(element => new PayloadField((string?)element.Attribute("name") ?? "",
                (string?)element.Attribute("inType") ?? "")
            {
                OutputType = (string?)element.Attribute("outType"),
                Length = int.TryParse((string?)element.Attribute("length"), out var length) ? length : null,
                Count = element.Attribute("count") is null ? 1
                    : int.TryParse((string?)element.Attribute("count"), out var count) ? count : -1
            }).ToArray();
    }

    private static string? FindNumeric(IReadOnlyList<PayloadField> fields, params string[] names) => names
        .Select(name => fields.FirstOrDefault(field => field.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            && IsUnsignedScalar(field)))
        .FirstOrDefault(field => field is not null)?.Name;

    private static bool IsUnsignedScalar(PayloadField field) => field.Count == 1
        && field.Type is "win:UInt16" or "win:UInt32" or "win:UInt64";

    private static string? FindSocketAddress(IReadOnlyList<PayloadField> fields, params string[] names) => names
        .Select(name => fields.FirstOrDefault(field => field.Name == name && field.Type == "win:Binary"
            && field.OutputType == "win:SocketAddress" && field.Count == 1))
        .FirstOrDefault(field => field is not null)?.Name;

    private static IReadOnlyList<PayloadField> SelectSafeFields(CandidateEventDescriptor descriptor)
    {
        var numericNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "ProcessStartKey", "AddressFamily", "NumMessages"
        };
        foreach (var selected in new[] { descriptor.PidField, descriptor.ByteField, descriptor.InterfaceField })
            if (selected is not null) numericNames.Add(selected);
        // Endpoint lengths/statuses are numeric metadata. Raw endpoint values
        // remain in memory, and pointers, DNS names, payloads and other strings
        // are deliberately never requested from the dynamic event consumer.
        foreach (var selected in new[] { descriptor.LocalEndpointField, descriptor.RemoteEndpointField })
            if (selected is not null) numericNames.Add(selected + "Length");
        if (descriptor.IsConnectionActivity)
        {
            numericNames.Add("Status");
            numericNames.Add("State");
        }
        return descriptor.Fields.Where(field => numericNames.Contains(field.Name) && IsUnsignedScalar(field)
            || (field.Name == descriptor.LocalEndpointField || field.Name == descriptor.RemoteEndpointField)
                && field.Type == "win:Binary" && field.OutputType == "win:SocketAddress" && field.Count == 1)
            .DistinctBy(field => field.Name, StringComparer.Ordinal).ToArray();
    }
}
