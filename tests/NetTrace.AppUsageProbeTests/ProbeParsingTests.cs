using System.Net;
using AppUsageEtwProbe;

internal static class ProbeParsingTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlySet<IPAddress> Local = new HashSet<IPAddress>
    {
        IPAddress.Parse("192.0.2.10"), IPAddress.Parse("192.0.2.11"), IPAddress.Parse("2001:db8::10")
    };
    private static readonly ProviderSchema Schema = new(true, 3,
    [
        Descriptor(700, TrafficDirection.Send, IpFamily.Ipv4),
        Descriptor(701, TrafficDirection.Receive, IpFamily.Ipv4),
        Descriptor(800, TrafficDirection.Send, IpFamily.Ipv6),
        Descriptor(801, TrafficDirection.Receive, IpFamily.Ipv6),
        Descriptor(900, TrafficDirection.Send, IpFamily.Ipv4) with { InterfaceIndexField = "ifIndex" },
    ]);

    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Payload PID owns attribution even when the event header names another process", Check.Sync(PayloadPid)),
        ("Missing payload PID cannot be replaced by an unrelated header PID", Check.Sync(MissingPid)),
        ("Schema send and receive direction is independent of endpoint port numbers", Check.Sync(SchemaDirection)),
        ("Validated IPv4 and IPv6 schemas identify both address families", Check.Sync(IpFamilies)),
        ("The installed schema mapping determines payload field names", Check.Sync(FieldMapping)),
        ("Zero and large unsigned byte counts parse without becoming negative", Check.Sync(UnsignedByteCounts)),
        ("Invalid PID and byte counts are ignored without an exception or partial activity", Check.Sync(MalformedCounts)),
        ("Unknown event IDs and unvalidated versions never enter byte accounting", Check.Sync(UnsupportedEvents)),
        ("Addresses from a different family cannot be accepted under the schema keyword", Check.Sync(FamilyMismatch)),
        ("The entire IPv4 127/8 range is classified as loopback", Check.Sync(Ipv4Loopback)),
        ("IPv6 ::1 traffic is classified as loopback", Check.Sync(Ipv6Loopback)),
        ("IPv4-mapped IPv6 loopback addresses remain loopback", Check.Sync(MappedLoopback)),
        ("Both known local addresses identify local proxy traffic separately", Check.Sync(LocalToLocal)),
        ("Private address ranges alone do not prove both endpoints are this machine", Check.Sync(PrivateAddresses)),
        ("Incomplete or contradictory loopback endpoints stay out of non-loopback totals", Check.Sync(UnknownEndpoints)),
        ("Interface index is retained only when exposed by the installed schema", Check.Sync(InterfaceIndex)),
    ];

    private static void PayloadPid()
    {
        var activity = Parse(Event(headerPid: 4, pid: 4321U));
        Check.Equal(4321, activity.Pid, "payload PID attribution");
        Check.Equal(1024L, activity.Bytes, "event byte count");
        Check.Equal(Epoch, activity.TimestampUtc, "event timestamp");
    }

    private static void MissingPid()
    {
        var raw = Event(headerPid: 1234);
        var payload = new Dictionary<string, object?>(raw.Payload);
        payload.Remove("PID");
        Reject(raw with { Payload = payload });
    }

    private static void SchemaDirection()
    {
        var send = Event(700);
        var receive = send with { EventId = 701 };
        Check.Equal(TrafficDirection.Send, Parse(send).Direction, "send opcode");
        Check.Equal(TrafficDirection.Receive, Parse(receive).Direction, "receive opcode with identical ports");
    }

    private static void IpFamilies()
    {
        Check.Equal(IpFamily.Ipv4, Parse(Event()).Family, "IPv4 schema");
        var raw = Event(800, source: IPAddress.Parse("2001:db8::10"), destination: IPAddress.Parse("2001:db8::20"));
        Check.Equal(IpFamily.Ipv6, Parse(raw).Family, "IPv6 schema");
        Check.Equal(TrafficDirection.Receive, Parse(raw with { EventId = 801 }).Direction, "IPv6 receive schema");
    }

    private static void FieldMapping()
    {
        var descriptor = Descriptor(950, TrafficDirection.Receive, IpFamily.Ipv4) with
        {
            PidField = "OwnerPid", SizeField = "TransferBytes",
            SourceAddressField = "Source", DestinationAddressField = "Destination"
        };
        var custom = new ProviderSchema(true, 1, [descriptor]);
        var raw = new RawNetworkEvent(950, 2, 4, Epoch, new Dictionary<string, object?>
        {
            ["OwnerPid"] = 1234U, ["TransferBytes"] = 2048U,
            ["Source"] = IPAddress.Loopback, ["Destination"] = IPAddress.Parse("127.1.2.3")
        });
        Check.True(ActivityParser.TryParse(raw, custom, Local, out var activity), "custom field mapping parses");
        Check.Equal(1234, activity!.Pid, "mapped PID");
        Check.Equal(2048L, activity.Bytes, "mapped byte count");
        Check.Equal(TrafficClass.Loopback, activity.TrafficClass, "mapped endpoint classification");
    }

    private static void UnsignedByteCounts()
    {
        Check.Equal(0L, Parse(Event(bytes: 0U)).Bytes, "zero-byte event");
        Check.Equal((long)uint.MaxValue, Parse(Event(bytes: uint.MaxValue)).Bytes, "manifest UInt32 maximum");
    }

    private static void MalformedCounts()
    {
        foreach (object? bytes in new object?[] { null, -1, long.MinValue, ulong.MaxValue, 1.5, "1024", new byte[] { 1 } })
            Reject(PayloadValue(Event(), "size", bytes));
        foreach (object? pid in new object?[] { null, -1, (uint)int.MaxValue + 1U, ulong.MaxValue, 1.5, "1234" })
            Reject(PayloadValue(Event(), "PID", pid));
    }

    private static void UnsupportedEvents()
    {
        Reject(Event(999));
        Reject(Event() with { Version = 3 });
        Check.True(!ActivityParser.TryParse(Event(), new ProviderSchema(false, 0, []), Local, out var activity),
            "absent provider with no validated schema is ignored");
        Check.True(activity is null, "unsupported event produces no partial activity");
    }

    private static void FamilyMismatch()
    {
        Reject(Event(source: IPAddress.IPv6Loopback, destination: IPAddress.IPv6Loopback));
        Reject(Event(800, source: IPAddress.Loopback, destination: IPAddress.Loopback));
    }

    private static void Ipv4Loopback()
    {
        var activity = Parse(Event(source: IPAddress.Parse("127.9.3.4"), destination: IPAddress.Parse("127.200.1.1")));
        Check.Equal(TrafficClass.Loopback, activity.TrafficClass, "all 127/8 addresses are loopback");
    }

    private static void Ipv6Loopback() =>
        Check.Equal(TrafficClass.Loopback,
            Parse(Event(800, source: IPAddress.IPv6Loopback, destination: IPAddress.IPv6Loopback)).TrafficClass,
            "IPv6 loopback event");

    private static void MappedLoopback()
    {
        var activity = Parse(Event(800, source: IPAddress.Parse("::ffff:127.1.2.3"), destination: IPAddress.Parse("::ffff:127.0.0.1")));
        Check.Equal(IpFamily.Ipv6, activity.Family, "mapped address retains observed IPv6 schema");
        Check.Equal(TrafficClass.Loopback, activity.TrafficClass, "mapped IPv4 loopback classification");
    }

    private static void LocalToLocal()
    {
        Check.Equal(TrafficClass.LocalToLocal,
            Parse(Event(source: IPAddress.Parse("192.0.2.10"), destination: IPAddress.Parse("192.0.2.11"))).TrafficClass,
            "both endpoints confirmed local");
        Check.Equal(TrafficClass.LocalToLocal,
            ActivityParser.Classify(IPAddress.Parse("::ffff:192.0.2.10"), IPAddress.Parse("::ffff:192.0.2.11"), Local),
            "mapped known local addresses");
        Check.Equal(TrafficClass.NonLoopback,
            Parse(Event(source: IPAddress.Parse("192.0.2.10"), destination: IPAddress.Parse("192.0.2.20"))).TrafficClass,
            "one local endpoint does not imply both endpoints are local");
    }

    private static void PrivateAddresses() =>
        Check.Equal(TrafficClass.NonLoopback,
            ActivityParser.Classify(IPAddress.Parse("10.1.1.1"), IPAddress.Parse("10.1.1.2"), Local),
            "RFC1918 addresses are not automatically local to this machine");

    private static void UnknownEndpoints()
    {
        Check.Equal(TrafficClass.Unknown, ActivityParser.Classify(null, IPAddress.Loopback, Local), "missing endpoint");
        Check.Equal(TrafficClass.Unknown,
            ActivityParser.Classify(IPAddress.Loopback, IPAddress.Parse("192.0.2.20"), Local),
            "one loopback and one external endpoint is contradictory");
        var raw = Event();
        var payload = new Dictionary<string, object?>(raw.Payload) { ["saddr"] = "invalid-address" };
        Check.Equal(TrafficClass.Unknown, Parse(raw with { Payload = payload }).TrafficClass,
            "unavailable endpoint metadata is recorded without inventing physical traffic");
    }

    private static void InterfaceIndex()
    {
        var raw = Event(900);
        var payload = new Dictionary<string, object?>(raw.Payload) { ["ifIndex"] = 7U };
        Check.Equal<int?>(7, Parse(raw with { Payload = payload }).InterfaceIndex, "exposed interface index");
        Check.Equal<int?>(null, Parse(raw with { EventId = 700, Payload = payload }).InterfaceIndex,
            "field not exposed by schema cannot be inferred");
        payload["ifIndex"] = -1;
        Check.Equal<int?>(null, Parse(raw with { Payload = payload }).InterfaceIndex, "invalid optional index unavailable");
    }

    internal static RawNetworkEvent Event(int eventId = 700, int headerPid = 4,
        object? pid = null, object? bytes = null, IPAddress? source = null, IPAddress? destination = null) =>
        new(eventId, 2, headerPid, Epoch, new Dictionary<string, object?>
        {
            ["PID"] = pid ?? 1234U, ["size"] = bytes ?? 1024U,
            ["saddr"] = source ?? IPAddress.Parse("192.0.2.10"),
            ["daddr"] = destination ?? IPAddress.Parse("192.0.2.20"),
            ["sport"] = (ushort)443, ["dport"] = (ushort)443
        });

    internal static NetworkActivity Parse(RawNetworkEvent raw)
    {
        Check.True(ActivityParser.TryParse(raw, Schema, Local, out var activity), "supported event parses");
        return activity!;
    }

    private static void Reject(RawNetworkEvent raw)
    {
        Check.True(!ActivityParser.TryParse(raw, Schema, Local, out var activity), "unsupported/malformed event ignored");
        Check.True(activity is null, "rejected event has no partial activity");
    }

    private static RawNetworkEvent PayloadValue(RawNetworkEvent raw, string field, object? value) =>
        raw with { Payload = new Dictionary<string, object?>(raw.Payload) { [field] = value } };

    private static NetworkEventSchema Descriptor(int id, TrafficDirection direction, IpFamily family) =>
        new(id, 2, direction, family,
            [new PayloadField("PID", "win:UInt32"), new PayloadField("size", "win:UInt32")]);
}
