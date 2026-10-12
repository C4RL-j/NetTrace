using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;

namespace AppUsageEtwProbe;

public sealed record PayloadField(string Name, string Type)
{
    public string? OutputType { get; init; }
    public int? Length { get; init; }
    public int Count { get; init; } = 1;
}

public sealed record NetworkEventSchema(int EventId, int Version, TrafficDirection Direction,
    IpFamily Family, IReadOnlyList<PayloadField> Fields, string PidField = "PID",
    string SizeField = "size", string SourceAddressField = "saddr",
    string DestinationAddressField = "daddr", string SourcePortField = "sport",
    string DestinationPortField = "dport", string? InterfaceIndexField = null)
{
    public string? OpcodeName { get; init; }
    public string? Protocol { get; init; }
    public int? MinimumPayloadBytes { get; init; }
}

/// <summary>
/// Discovers this machine's registered manifest. Event numbers, versions, direction,
/// family keywords and payload fields are accepted only after this validation.
/// </summary>
public sealed class ProviderSchema(bool providerAvailable, ulong keywordMask,
    IReadOnlyList<NetworkEventSchema> events, string? error = null)
{
    public const string ProviderName = "Microsoft-Windows-Kernel-Network";
    public static readonly Guid ProviderGuid = new("7DD42A49-5329-4832-8DFD-43D979153A88");

    public bool ProviderAvailable { get; } = providerAvailable;
    public ulong KeywordMask { get; } = keywordMask;
    public IReadOnlyList<NetworkEventSchema> Events { get; } = events;
    public string? Error { get; } = error;
    public bool SupportsIpv4 => Events.Any(e => e.Family == IpFamily.Ipv4);
    public bool SupportsIpv6 => Events.Any(e => e.Family == IpFamily.Ipv6);
    public IReadOnlyList<string> SchemaNotes { get; init; } = [];

    public static ProviderSchema Discover()
    {
        bool available = false;
        try
        {
            using var provider = new ProviderMetadata(ProviderName);
            if (provider.Id != ProviderGuid)
                return new(false, 0, [], "Registered provider GUID does not match the required Kernel-Network provider.");
            available = true;

            // Use manifest names and their installed values, never assumed IDs or bit positions.
            var familyKeywords = provider.Keywords.Where(k => k.Name is not null)
                .Where(k => k.Name!.EndsWith("_IPV4", StringComparison.OrdinalIgnoreCase)
                    || k.Name.EndsWith("_IPV6", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(k => k.Name!, k => unchecked((ulong)k.Value), StringComparer.OrdinalIgnoreCase);
            ulong keywordMask = familyKeywords.Values.Aggregate(0UL, (mask, value) => mask | value);
            var schemas = new List<NetworkEventSchema>();
            var notes = new List<string>();
            foreach (var metadata in provider.Events)
            {
                string opcodeName = metadata.Opcode.Name ?? "";
                if (!TryDirection(opcodeName, out var direction, out var protocol)) continue;
                var familyNames = metadata.Keywords.Select(k => k.Name ?? "")
                    .Where(familyKeywords.ContainsKey).ToArray();
                if (familyNames.Length != 1)
                {
                    notes.Add($"Event {metadata.Id}/{metadata.Version}: ambiguous or absent address-family keyword; ignored.");
                    continue;
                }
                var family = familyNames[0].EndsWith("_IPV4", StringComparison.OrdinalIgnoreCase)
                    ? IpFamily.Ipv4 : IpFamily.Ipv6;
                if (string.IsNullOrWhiteSpace(metadata.Template))
                {
                    notes.Add($"Event {metadata.Id}/{metadata.Version}: no installed payload template; ignored.");
                    continue;
                }
                var fields = ReadFields(metadata.Template);
                if (!ValidateFields(fields, family, out string reason))
                {
                    notes.Add($"Event {metadata.Id}/{metadata.Version}: {reason}; ignored.");
                    continue;
                }
                string? interfaceField = fields.FirstOrDefault(f => f.Name.Equals("InterfaceIndex", StringComparison.OrdinalIgnoreCase)
                    || f.Name.Equals("ifIndex", StringComparison.OrdinalIgnoreCase))?.Name;
                schemas.Add(new(checked((int)metadata.Id), metadata.Version, direction, family, fields,
                    InterfaceIndexField: interfaceField)
                {
                    OpcodeName = opcodeName, Protocol = protocol,
                    MinimumPayloadBytes = fields.Sum(FixedFieldSize)
                });
            }
            if (schemas.Count == 0)
                return new(true, keywordMask, [], "No installed send/receive schema has validated PID, byte count and endpoint fields.")
                    { SchemaNotes = notes };
            return new(true, keywordMask, schemas) { SchemaNotes = notes };
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or System.Security.SecurityException
            or System.Xml.XmlException or ArgumentException or OverflowException or InvalidOperationException)
        {
            return new(available, 0, [], $"Provider/schema discovery failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool TryDirection(string opcodeName, out TrafficDirection direction, out string protocol)
    {
        string operation = opcodeName.Split('_').Last().ToUpperInvariant();
        protocol = operation.EndsWith("UDP", StringComparison.Ordinal) ? "UDP" : "TCP";
        direction = operation is "RECV" or "RECVUDP" ? TrafficDirection.Receive : TrafficDirection.Send;
        // Retransmit, copied data and connection lifecycle events deliberately never enter byte totals.
        return operation is "SEND" or "RECV" or "SENDUDP" or "RECVUDP";
    }

    private static IReadOnlyList<PayloadField> ReadFields(string template)
    {
        var xml = XDocument.Parse(template);
        return xml.Descendants().Where(e => e.Name.LocalName == "data").Select(e => new PayloadField(
            (string?)e.Attribute("name") ?? "", (string?)e.Attribute("inType") ?? "")
        {
            OutputType = (string?)e.Attribute("outType"),
            Length = int.TryParse((string?)e.Attribute("length"), out int length) ? length : null,
            Count = e.Attribute("count") is null ? 1 : int.TryParse((string?)e.Attribute("count"), out int count) ? count : -1
        }).ToArray();
    }

    private static bool ValidateFields(IReadOnlyList<PayloadField> fields, IpFamily family, out string reason)
    {
        reason = "unsupported payload template";
        if (fields.Count == 0 || fields.Any(f => string.IsNullOrEmpty(f.Name))
            || fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != fields.Count) return false;
        if (fields.Any(f => FixedFieldSize(f) <= 0))
        {
            reason = "payload contains an unsupported variable/structured field";
            return false;
        }
        var map = fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        if (!Matches("PID", "win:UInt32") || !Matches("size", "win:UInt32"))
        {
            reason = "PID/size fields are absent or have unsupported types";
            return false;
        }
        if (!Matches("sport", "win:UInt16") || !Matches("dport", "win:UInt16"))
        {
            reason = "port fields are absent or have unsupported types";
            return false;
        }
        foreach (string name in new[] { "saddr", "daddr" })
        {
            if (!map.TryGetValue(name, out var field)
                || (family == IpFamily.Ipv4 && (field.Type != "win:UInt32" || field.OutputType != "win:IPv4"))
                || (family == IpFamily.Ipv6 && (field.Type != "win:Binary" || field.OutputType != "win:IPv6" || field.Length != 16)))
            {
                reason = "endpoint fields disagree with the address-family keyword";
                return false;
            }
        }
        reason = "";
        return true;

        bool Matches(string name, string type) => map.TryGetValue(name, out var field) && field.Type == type;
    }

    private static int FixedFieldSize(PayloadField field) => field.Count != 1 ? 0 : field.Type switch
    {
        "win:UInt8" or "win:Int8" => 1,
        "win:UInt16" or "win:Int16" => 2,
        "win:UInt32" or "win:Int32" => 4,
        "win:UInt64" or "win:Int64" => 8,
        "win:Binary" => field.Length ?? 0,
        _ => 0
    };
}
