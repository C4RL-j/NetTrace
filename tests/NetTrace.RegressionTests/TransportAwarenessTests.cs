using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using NetTrace.Models;
using NetTrace.Services;

internal static class TransportAwarenessTests
{
    public static (string Name, Action Run)[] Cases =>
    [
        ("No active VPN reports inactive outer transport", Inactive),
        ("Unobserved VPN transport remains unknown", InitialUnknown),
        ("Confirmed correlated RAS remote IPv4 proves IPv4 outer transport", ProvenIPv4),
        ("Confirmed correlated RAS remote IPv6 proves IPv6 outer transport", ProvenIPv6),
        ("Two independently correlated connected RAS peers prove mixed outer transport", ProvenMixed),
        ("Independent connected IPv4 and IPv6 RAS peers prove global mixed VPN transport with opaque routes", GlobalMixed),
        ("An active tunnel without a connected remote peer retains unknown transport", MissingPeer),
        ("A AAAA and dual-stack VPN hostname clues cannot prove transport", DnsClues),
        ("A single connected RAS peer overrides dual-stack hostname clues without guessing mixed", ConnectedEndpointOnly),
        ("Direct IPv6 with VPN IPv4 does not prove IPv6 outer transport", DirectIpv6NotProof),
        ("VPN interface and driver names cannot prove outer transport family", AdapterNameNotProof),
        ("Inactive and internal Hyper-V VMware VirtualBox Docker adapters cannot prove outer transport", InternalAdapters),
        ("Uncorrelated browser SSH arbitrary IPv6 and proxy endpoints cannot prove opaque VPN transport", UncorrelatedEndpoints),
        ("Known RAS peer cannot be borrowed by a different opaque tunnel participant", OpaqueParticipant),
        ("Modem and direct RAS remote endpoints cannot prove VPN transport", NonVpnRas),
        ("Disconnected RAS remote endpoint cannot prove current transport", DisconnectedRas),
        ("RAS connection errors cannot prove current transport", FailedRas),
        ("Dormant reconnecting and unknown RAS substates cannot prove current transport", DormantRas),
        ("Missing endpoint metadata preserves active routing and unknown transport", MissingMetadata),
        ("Incomplete interface inventory cannot prove outer transport", IncompleteInventory),
        ("Ambiguous RAS to PPP aliases cannot pick an arbitrary transport peer", AmbiguousCorrelation),
        ("Unmatched sibling RAS session cannot contribute a transport family", UnmatchedSibling),
        ("Mixed transport needs a valid remote endpoint for every correlated active session", EveryPeerRequired),
        ("Loopback unspecified multicast and mapped peers cannot prove outer transport", InvalidPeer),
        ("A single session's actual IPv4 or IPv6 endpoint is never classified mixed", SingleSession),
        ("Outer transport classification is deterministic and retains immutable values", Deterministic),
        ("IPv6 transport is independent of VPN IPv4 and direct IPv6 routing", IPv6MixedRoutes),
        ("IPv6 transport is independent of VPN routing in both payload families", IPv6BothVpnRoutes),
        ("IPv4 transport is independent of VPN IPv4 and unavailable IPv6 routing", IPv4UnavailableRoutes),
        ("Unknown outer transport preserves VPN IPv4 and direct IPv6 routing", UnknownMixedRoutes),
        ("A physical IPv6 route to a proven remote peer preserves active IPv4 VPN", UnderlayException),
        ("RAS endpoint and connection-status marshalling follows native Windows layout", NativeLayout),
        ("Native RAS IPv4 endpoint decoder preserves network byte order", NativeIPv4),
        ("Native RAS IPv6 endpoint decoder preserves all remote address bytes", NativeIPv6),
        ("Native RAS endpoint decoder rejects invalid type length and local addresses", NativeInvalid),
        ("Native RAS endpoint decoder admits only confirmed connected VPN sessions", NativeConnectedOnly),
        ("Native RAS transport uses remote endpoint rather than local endpoint or hostname", NativeRemoteOnly),
        ("Headless transport transitions preserve worker adapter session and byte totals", BackgroundTransitions),
        ("Cached transport reads do not perform native discovery", CachedReads),
        ("Transport refresh bursts reuse the existing serialized VPN worker", SerializedWorker),
        ("Suspend drops blocked old transport and resume uses fresh endpoint proof", SuspendResume),
        ("Disposal rejects blocked old transport publication", Disposal),
        ("Transport metadata access failure remains unknown and recovers on fresh observation", ReadFailure),
    ];

    private static readonly DateTimeOffset Origin = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid PhysicalGuid = new("c4c590b9-494f-477a-a78f-c7137ac14a18");
    private static readonly Guid PppGuid = new("825018c6-06a8-4721-9b04-9aedfbf70d23");
    private static readonly IPAddress Peer4 = IPAddress.Parse("203.0.113.64");
    private static readonly IPAddress Peer6 = IPAddress.Parse("2001:db8::64");
    private static VpnInterfaceEvidence Physical => new()
    {
        InterfaceIndex = 7, InterfaceGuid = PhysicalGuid, InterfaceName = "Physical Wi-Fi",
        Description = "Owned physical fixture", InterfaceType = 71, StatusFlags = 1,
        OperationalStatus = OperationalStatus.Up, MediaConnectState = 1
    };
    private static VpnInterfaceEvidence Ppp => new()
    {
        InterfaceIndex = 42, InterfaceGuid = PppGuid, InterfaceName = "Work VPN",
        Description = "WAN Miniport (IKEv2)", InterfaceType = 23,
        OperationalStatus = OperationalStatus.Up, MediaConnectState = 1
    };
    private static VpnInterfaceEvidence Opaque => Ppp with
    {
        InterfaceType = 131, TunnelType = 1, InterfaceName = "Custom tunnel", Description = "Owned tunnel fixture"
    };
    private static VpnInterfaceEvidence SecondPpp => Ppp with
    {
        InterfaceIndex = 43, InterfaceGuid = new Guid("cf9f97ef-61ac-48f0-80db-b5f5e8289eb9"),
        InterfaceName = "Second VPN", Description = "Second native VPN device"
    };
    private static VpnRasConnectionEvidence Ras(IPAddress? peer) =>
        new("Work VPN", "WAN Miniport (IKEv2)", "vpn", 0x2000) { RemoteEndpoint = peer };
    private static VpnRasConnectionEvidence SecondRas(IPAddress? peer) =>
        Ras(peer) with { ConnectionName = SecondPpp.InterfaceName, DeviceName = SecondPpp.Description };
    private static VpnInterfaceInventory Inventory(params VpnRasConnectionEvidence[] connections) =>
        new([Physical, Ppp], connections);
    private static VpnInterfaceInventory TwoPeers(IPAddress? first, IPAddress? second) =>
        new([Physical, Ppp, SecondPpp], [Ras(first), SecondRas(second)]);
    private static VpnRouteEvidence Route(VpnInterfaceEvidence owner, string prefix = "0.0.0.0", byte length = 0) =>
        new(owner.InterfaceIndex, owner.InterfaceGuid, IPAddress.Parse(prefix), length,
            IPAddress.Parse(prefix.Contains(':') ? "::" : "0.0.0.0"), 5, 0, true);
    private static VpnRouteInventory V4(params VpnRouteEvidence[] routes) => new(AddressFamily.InterNetwork, routes);
    private static VpnRouteInventory Direct6 => new(AddressFamily.InterNetworkV6, [Route(Physical, "::")]);
    private static VpnRouteInventory No6 => new(AddressFamily.InterNetworkV6, [], Availability: IpFamilyAvailability.Unavailable);
    private static VpnRouteSnapshot Snapshot(IPAddress? peer, VpnRouteInventory? ipv6 = null) =>
        VpnRouteClassifier.Classify(Inventory(Ras(peer)), V4(Route(Ppp)), ipv6 ?? Direct6, Origin);
    private static void Transport(VpnTransportFamily expected, VpnInterfaceInventory inventory,
        VpnTunnelStatus status = VpnTunnelStatus.Active, params VpnInterfaceEvidence[] participants)
    {
        var result = VpnTransportClassifier.Classify(inventory, status, participants.Length == 0 ? [Ppp] : participants);
        Equal(expected, result.Family, "outer transport family");
        Check(!string.IsNullOrWhiteSpace(result.Reason), "Transport interpretation explains the accepted or missing evidence.");
    }
    private static void Routes(VpnRouteSnapshot snapshot, VpnTransportFamily transport,
        InternetRouteKind ipv4, InternetRouteKind ipv6)
    {
        Equal(VpnTunnelStatus.Active, snapshot.Status, "proven VPN participation remains active");
        Equal(transport, snapshot.TransportFamily, "independent outer transport");
        Equal(ipv4, snapshot.IPv4RouteKind, "independent IPv4 payload route");
        Equal(ipv6, snapshot.IPv6RouteKind, "independent IPv6 payload route");
        Check(!string.IsNullOrWhiteSpace(snapshot.TransportReason), "Cached transport has diagnostic provenance.");
        Equal<DateTimeOffset?>(Origin, snapshot.CheckedAtUtc, "shared observation timestamp");
    }

    private static void Inactive()
    {
        var snapshot = VpnRouteClassifier.Classify(new([Physical], []), V4(Route(Physical)), Direct6, Origin);
        Equal(VpnTunnelStatus.Inactive, snapshot.Status, "no VPN");
        Equal(VpnTransportFamily.Inactive, snapshot.TransportFamily, "no outer connection");
        Transport(VpnTransportFamily.Inactive, Inventory(Ras(Peer6)), VpnTunnelStatus.Inactive);
    }
    private static void InitialUnknown()
    {
        Equal(VpnTransportFamily.Unknown, VpnRouteSnapshot.Unknown.TransportFamily, "initial transport is unknown");
        Transport(VpnTransportFamily.Unknown, Inventory(Ras(Peer6)), VpnTunnelStatus.Unknown);
    }
    private static void ProvenIPv4() => Transport(VpnTransportFamily.IPv4, Inventory(Ras(Peer4)));
    private static void ProvenIPv6()
    {
        Transport(VpnTransportFamily.IPv6, Inventory(Ras(Peer6)));
        Transport(VpnTransportFamily.IPv6, Inventory(Ras(Peer6) with { ConnectionSubstate = 0x2000 }));
        Transport(VpnTransportFamily.IPv6, Inventory(Ras(Peer6) with { DeviceType = "VPN", ConnectionName = "WORK VPN" }));
        var renamed = Ppp with { InterfaceName = "Renamed interface" };
        Transport(VpnTransportFamily.IPv6, new([Physical, renamed], [Ras(Peer6)]), participants: [renamed]);
    }
    private static void ProvenMixed() =>
        Transport(VpnTransportFamily.Mixed, TwoPeers(Peer4, Peer6), participants: [Ppp, SecondPpp]);
    private static void GlobalMixed()
    {
        var inventory = new VpnInterfaceInventory([Physical, Opaque], [Ras(Peer4), SecondRas(Peer6)]);
        var snapshot = VpnRouteClassifier.Classify(inventory, V4(Route(Opaque)), Direct6, Origin);
        Routes(snapshot, VpnTransportFamily.Mixed, InternetRouteKind.Vpn, InternetRouteKind.Direct);
        Transport(VpnTransportFamily.Unknown, inventory with { RasConnections = [Ras(Peer4), SecondRas(null)] }, participants: [Opaque]);
    }
    private static void MissingPeer() => Transport(VpnTransportFamily.Unknown, Inventory());
    private static void DnsClues()
    {
        // Endpoint evidence deliberately excludes DNS answer inventories. A/AAAA-style
        // strings in available session metadata cannot substitute for an actual peer.
        foreach (var description in new[] { "vpn.example.test A 203.0.113.64", "vpn.example.test AAAA 2001:db8::64",
            "vpn.example.test A 203.0.113.64 AAAA 2001:db8::64" })
        {
            var item = Ppp with { Description = description };
            Transport(VpnTransportFamily.Unknown, new([item], [Ras(null)]), participants: [item]);
        }
    }
    private static void ConnectedEndpointOnly()
    {
        foreach (var (peer, family) in new[] { (Peer4, VpnTransportFamily.IPv4), (Peer6, VpnTransportFamily.IPv6) })
        {
            var item = Ppp with { Description = "vpn.example.test A 203.0.113.64 AAAA 2001:db8::64" };
            Transport(family, new([item], [Ras(peer)]), participants: [item]);
        }
    }
    private static void DirectIpv6NotProof() => Routes(Snapshot(null), VpnTransportFamily.Unknown, InternetRouteKind.Vpn, InternetRouteKind.Direct);
    private static void AdapterNameNotProof()
    {
        foreach (var driver in new[] { "wintun", "wireguard", "tap0901", "ovpn-dco" })
        {
            var item = Opaque with { InterfaceName = "VPN IPv6 dual stack", DriverComponentId = driver };
            var snapshot = VpnRouteClassifier.Classify(new([Physical, item], []), V4(Route(item)), Direct6, Origin);
            Equal(VpnTransportFamily.Unknown, snapshot.TransportFamily, "driver and IPv6-looking name do not expose outer peer");
        }
    }
    private static void InternalAdapters()
    {
        foreach (var (name, driver) in new[] { ("Hyper-V VPN IPv6", "vms_mp"), ("VMware VPN IPv6", "vmnetadapter"),
            ("VirtualBox VPN IPv6", "vboxnetadp"), ("Docker VPN IPv6", "") })
        {
            var item = Opaque with { InterfaceType = 6, TunnelType = 0, InterfaceName = name, DriverComponentId = driver };
            var snapshot = VpnRouteClassifier.Classify(new([Physical, item], []), V4(Route(Physical), Route(item, "172.16.0.0", 16)), Direct6, Origin);
            Equal(VpnTransportFamily.Inactive, snapshot.TransportFamily, "internal installed adapter does not establish transport");
        }
        var disconnected = Opaque with { OperationalStatus = OperationalStatus.Down, MediaConnectState = 2 };
        var inactive = VpnRouteClassifier.Classify(new([Physical, disconnected], []), V4(Route(Physical)), Direct6, Origin);
        Equal(VpnTransportFamily.Inactive, inactive.TransportFamily, "inactive installed tunnel does not establish transport");
    }
    private static void UncorrelatedEndpoints()
    {
        // An arbitrary connected endpoint remains insufficient without the actual VPN
        // session/interface association. No ambient socket inventory is accepted by the API.
        foreach (var (name, peer) in new[] { ("Browser TCP", Peer6), ("Unrelated SSH", Peer4),
            ("Arbitrary IPv6 established socket", Peer6), ("Local SOCKS listener", IPAddress.IPv6Loopback),
            ("Local HTTP proxy", IPAddress.Loopback), ("DNS forwarder", IPAddress.Loopback) })
            Transport(VpnTransportFamily.Unknown, new([Opaque], [Ras(peer) with { ConnectionName = name, DeviceName = name }]), participants: [Opaque]);
    }
    private static void OpaqueParticipant() =>
        Transport(VpnTransportFamily.Unknown, new([Physical, Ppp, Opaque], [Ras(Peer6)]), participants: [Opaque]);
    private static void NonVpnRas()
    {
        foreach (var type in new[] { "modem", "direct", "pppoe", "" })
            Transport(VpnTransportFamily.Unknown, Inventory(Ras(Peer6) with { DeviceType = type }));
    }
    private static void DisconnectedRas()
    {
        foreach (uint state in new uint[] { 0, 1, 0x2001 })
            Transport(VpnTransportFamily.Unknown, Inventory(Ras(Peer6) with { ConnectionState = state }));
    }
    private static void FailedRas() => Transport(VpnTransportFamily.Unknown, Inventory(Ras(Peer6) with { ErrorCode = 5 }));
    private static void DormantRas()
    {
        foreach (uint state in new uint[] { 1, 0x2001, 0x2002, uint.MaxValue })
            Transport(VpnTransportFamily.Unknown, Inventory(Ras(Peer6) with { ConnectionSubstate = state }));
    }
    private static void MissingMetadata() => Routes(Snapshot(null), VpnTransportFamily.Unknown, InternetRouteKind.Vpn, InternetRouteKind.Direct);
    private static void IncompleteInventory() =>
        Transport(VpnTransportFamily.Unknown, Inventory(Ras(Peer6)) with { IsComplete = false, Diagnostic = "Owned access-denied fixture" });
    private static void AmbiguousCorrelation()
    {
        Transport(VpnTransportFamily.Unknown, Inventory(Ras(Peer4), Ras(Peer6)));
        var sibling = SecondPpp with { InterfaceName = Ppp.InterfaceName, Description = Ppp.Description };
        Transport(VpnTransportFamily.Unknown, new([Ppp, sibling], [Ras(Peer6)]), participants: [Ppp, sibling]);
    }
    private static void UnmatchedSibling() =>
        Transport(VpnTransportFamily.Unknown, Inventory(Ras(Peer4), SecondRas(Peer4)));
    private static void EveryPeerRequired()
    {
        Transport(VpnTransportFamily.Unknown, TwoPeers(Peer4, null), participants: [Ppp, SecondPpp]);
        Transport(VpnTransportFamily.Unknown, TwoPeers(null, Peer6), participants: [Ppp, SecondPpp]);
        Transport(VpnTransportFamily.Unknown, TwoPeers(Peer4, Peer6) with
        {
            RasConnections = [Ras(Peer4), SecondRas(Peer6) with { ErrorCode = 5 }]
        }, participants: [Ppp, SecondPpp]);
    }
    private static void InvalidPeer()
    {
        foreach (var address in new[] { "0.0.0.0", "0.1.2.3", "127.0.0.1", "169.254.1.2", "224.0.0.1", "255.255.255.255",
            "::", "::1", "fe80::1", "ff02::1", "::ffff:203.0.113.64" })
            Transport(VpnTransportFamily.Unknown, Inventory(Ras(IPAddress.Parse(address))));
    }
    private static void SingleSession()
    {
        Transport(VpnTransportFamily.IPv4, Inventory(Ras(Peer4)));
        Transport(VpnTransportFamily.IPv6, Inventory(Ras(Peer6)));
    }
    private static void Deterministic()
    {
        var inventory = TwoPeers(Peer4, Peer6);
        var first = VpnTransportClassifier.Classify(inventory, VpnTunnelStatus.Active, [Ppp, SecondPpp]);
        var reversed = VpnTransportClassifier.Classify(inventory with
        {
            Interfaces = inventory.Interfaces.Reverse().ToArray(), RasConnections = inventory.RasConnections.Reverse().ToArray()
        }, VpnTunnelStatus.Active, [SecondPpp, Ppp]);
        Equal(first, reversed, "reordered proof yields stable family and diagnostic");
        var snapshot = Snapshot(Peer6);
        var next = Snapshot(Peer4);
        Equal(VpnTransportFamily.IPv6, snapshot.TransportFamily, "prior observation retains immutable transport");
        Equal(VpnTransportFamily.IPv4, next.TransportFamily, "endpoint replacement creates a new value");
    }
    private static void IPv6MixedRoutes() => Routes(Snapshot(Peer6), VpnTransportFamily.IPv6, InternetRouteKind.Vpn, InternetRouteKind.Direct);
    private static void IPv6BothVpnRoutes() => Routes(Snapshot(Peer6, new(AddressFamily.InterNetworkV6, [Route(Ppp, "::")])),
        VpnTransportFamily.IPv6, InternetRouteKind.Vpn, InternetRouteKind.Vpn);
    private static void IPv4UnavailableRoutes() => Routes(Snapshot(Peer4, No6), VpnTransportFamily.IPv4, InternetRouteKind.Vpn, InternetRouteKind.Unavailable);
    private static void UnknownMixedRoutes() => Routes(Snapshot(null), VpnTransportFamily.Unknown, InternetRouteKind.Vpn, InternetRouteKind.Direct);
    private static void UnderlayException() => Routes(Snapshot(Peer6, Direct6 with
    {
        Routes = [Route(Physical, "::"), Route(Physical, Peer6.ToString(), 128)]
    }), VpnTransportFamily.IPv6, InternetRouteKind.Vpn, InternetRouteKind.Direct);

    private static Type NativeType => typeof(VpnDetectionService).Assembly.GetType("NetTrace.Services.WindowsVpnNative", throwOnError: true)!;
    private static Type NativeStructure(string name) => NativeType.GetNestedType(name, BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing native fixture type " + name);
    private static void NativeLayout()
    {
        var endpoint = NativeStructure("RasTunnelEndpoint");
        var status = NativeStructure("RasConnStatus");
        Equal(20, Marshal.SizeOf(endpoint), "native endpoint union size");
        Equal(608, Marshal.SizeOf(status), "native Unicode RAS status size");
        Equal(584L, Marshal.OffsetOf(status, "RemoteEndpoint").ToInt64(), "native remote endpoint offset");
        Equal(4L, Marshal.OffsetOf(endpoint, "Address0").ToInt64(), "native address union beginning");
        Equal(19L, Marshal.OffsetOf(endpoint, "Address15").ToInt64(), "native address union end");
    }
    private static IPAddress? Decode(uint type, byte[] bytes) =>
        (IPAddress?)NativeType.GetMethod("ReadRasRemoteEndpoint", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [type, bytes]);
    private static byte[] EndpointBytes(IPAddress address)
    {
        var bytes = new byte[16];
        address.GetAddressBytes().CopyTo(bytes, 0);
        return bytes;
    }
    private static void NativeIPv4()
    {
        var bytes = EndpointBytes(Peer4);
        Array.Fill(bytes, (byte)0xAA, 4, 12); // ignored tail of the union for IPv4
        Equal(Peer4, Decode(1, bytes), "native IPv4 endpoint stays in network order");
    }
    private static void NativeIPv6() => Equal(Peer6, Decode(2, EndpointBytes(Peer6)), "native IPv6 endpoint has all sixteen bytes");
    private static void NativeInvalid()
    {
        foreach (uint type in new uint[] { 0, 3, uint.MaxValue }) Equal<IPAddress?>(null, Decode(type, EndpointBytes(Peer6)), "unsupported endpoint type");
        foreach (var bytes in new[] { Array.Empty<byte>(), new byte[4], new byte[15], new byte[17] })
            Equal<IPAddress?>(null, Decode(2, bytes), "invalid endpoint union length");
        foreach (var text in new[] { "0.0.0.0", "0.1.2.3", "127.0.0.1", "224.0.0.1", "255.255.255.255",
            "::", "::1", "ff02::1", "::ffff:203.0.113.64" })
        {
            var address = IPAddress.Parse(text);
            Equal<IPAddress?>(null, Decode(address.AddressFamily == AddressFamily.InterNetwork ? 1u : 2u, EndpointBytes(address)), "invalid native outer peer");
        }
    }
    private static void SetNative(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static object NativeEndpoint(IPAddress peer)
    {
        var endpoint = Activator.CreateInstance(NativeStructure("RasTunnelEndpoint"))!;
        SetNative(endpoint, "Type", peer.AddressFamily == AddressFamily.InterNetwork ? 1u : 2u);
        var bytes = EndpointBytes(peer);
        for (var index = 0; index < bytes.Length; index++) SetNative(endpoint, "Address" + index, bytes[index]);
        return endpoint;
    }
    private static object NativeStatus(IPAddress peer, uint state = 0x2000, uint error = 0, uint substate = 0, string device = "vpn")
    {
        var status = Activator.CreateInstance(NativeStructure("RasConnStatus"))!;
        SetNative(status, "ConnectionState", state);
        SetNative(status, "ErrorCode", error);
        SetNative(status, "Substate", substate);
        SetNative(status, "DeviceType", device);
        SetNative(status, "RemoteEndpoint", NativeEndpoint(peer));
        return status;
    }
    private static IPAddress? DecodeConnected(object status) =>
        (IPAddress?)NativeType.GetMethod("ReadConnectedRasRemoteEndpoint", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [status]);
    private static void NativeConnectedOnly()
    {
        Equal(Peer6, DecodeConnected(NativeStatus(Peer6)), "confirmed RAS connected remote peer");
        Equal(Peer4, DecodeConnected(NativeStatus(Peer4, substate: 0x2000, device: "VPN")), "reconnected confirmed VPN peer");
        foreach (var status in new[] { NativeStatus(Peer6, state: 0x2001), NativeStatus(Peer6, error: 5),
            NativeStatus(Peer6, substate: 0x2001), NativeStatus(Peer6, substate: 0x2002), NativeStatus(Peer6, device: "modem") })
            Equal<IPAddress?>(null, DecodeConnected(status), "unconfirmed session cannot reuse a remote peer");
    }
    private static void NativeRemoteOnly()
    {
        var status = NativeStatus(Peer6);
        SetNative(status, "LocalEndpoint", NativeEndpoint(Peer4));
        SetNative(status, "PhoneNumber", "vpn.example.test A 203.0.113.64 AAAA 2001:db8::64");
        Equal(Peer6, DecodeConnected(status), "only the connected remote endpoint proves the family");
        SetNative(status, "RemoteEndpoint", Activator.CreateInstance(NativeStructure("RasTunnelEndpoint"))!);
        Equal<IPAddress?>(null, DecodeConnected(status), "local endpoint and configured hostname do not replace missing remote proof");
    }

    private static void BackgroundTransitions()
    {
        using var test = new TransportContext();
        var initial = test.Service.GetSnapshot();
        var observer = PrivateField<VpnSessionMonitor>(test.Service, "_vpnSessions");
        var timer = PrivateField<Timer>(observer, "_timer");
        var worker = PrivateField<object>(observer, "_worker");
        foreach (var (peer, expected, connected) in new[]
        {
            (Peer6, VpnTransportFamily.IPv6, true), // Inactive -> IPv6
            (Peer6, VpnTransportFamily.Inactive, false), // IPv6 -> Inactive
            (Peer4, VpnTransportFamily.IPv4, true),
            (Peer6, VpnTransportFamily.IPv6, true), // IPv4 -> IPv6
            ((IPAddress?)null, VpnTransportFamily.Unknown, true), // IPv6 -> Unknown
            (Peer6, VpnTransportFamily.IPv6, true), // Unknown -> IPv6
        })
        {
            test.Sample(10_000 + (test.Samples + 1) * 100, 1000 + (test.Samples + 1) * 10);
            var before = test.Service.GetSnapshot();
            test.SetConnection(peer, connected);
            WaitUntil(() => test.Service.GetSnapshot().VpnRoute.TransportFamily == expected, "transport transition " + expected);
            var after = test.Service.GetSnapshot();
            Equal(initial.SelectedAdapter!.Id, after.SelectedAdapter!.Id, "outer transport preserves selected physical adapter");
            Equal(initial.Session!.Id, after.Session!.Id, "outer transport preserves physical session");
            Equal(before.SessionDownloaded, after.SessionDownloaded, "outer transport does not reset downloaded bytes");
            Equal(before.SessionUploaded, after.SessionUploaded, "outer transport does not reset uploaded bytes");
            Equal(connected ? InternetRouteKind.Vpn : InternetRouteKind.Direct, after.VpnRoute.IPv4RouteKind, "independent IPv4 route");
            Equal(InternetRouteKind.Direct, after.VpnRoute.IPv6RouteKind, "direct IPv6 is independent of every outer transition");
            Check(ReferenceEquals(observer, PrivateField<VpnSessionMonitor>(test.Service, "_vpnSessions")), "Existing observer instance is reused.");
            Check(ReferenceEquals(timer, PrivateField<Timer>(observer, "_timer")), "Existing observer timer is reused.");
            Check(ReferenceEquals(worker, PrivateField<object>(observer, "_worker")), "Existing serialization worker is reused.");
        }
        Equal(600L, test.Service.GetSnapshot().SessionDownloaded, "all transport changes preserve the byte baseline");
        Equal(60L, test.Service.GetSnapshot().SessionUploaded, "all transport changes preserve upload baseline");
        Check(test.Changes > 0, "Headless route observation publishes existing Changed notifications.");
    }
    private static void CachedReads()
    {
        using var test = new TransportContext();
        test.SetConnection(Peer6);
        WaitUntil(() => test.Service.GetSnapshot().VpnRoute.TransportFamily == VpnTransportFamily.IPv6, "IPv6 cache is observed");
        var native = test.Interfaces.Reads;
        var route = test.Routes.Reads;
        for (var index = 0; index < 100; index++) Equal(VpnTransportFamily.IPv6, test.Service.GetSnapshot().VpnRoute.TransportFamily, "cached transport read");
        Equal(native, test.Interfaces.Reads, "cached transport cannot enumerate native interfaces");
        Equal(route, test.Routes.Reads, "cached transport cannot enumerate native routes");
    }
    private static void SerializedWorker()
    {
        using var test = new TransportContext();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        test.Routes.Entered = entered;
        test.Routes.Release = release;
        test.SetConnection(Peer6);
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "Existing VPN observer reaches controlled native query.");
        try
        {
            for (var index = 0; index < 25; index++) test.Refresh();
            Equal(1, test.Routes.MaxConcurrent, "refresh bursts cannot create a parallel native route worker");
            Equal(VpnTransportFamily.Inactive, test.Service.GetSnapshot().VpnRoute.TransportFamily, "reads remain nonblocking while refresh is held");
        }
        finally { release.Set(); }
        WaitUntil(() => test.Service.GetSnapshot().VpnRoute.TransportFamily == VpnTransportFamily.IPv6, "fresh generation follows blocked observation");
        Equal(1, test.Routes.MaxConcurrent, "all fresh observations remain serialized");
    }
    private static void SuspendResume()
    {
        using var test = new TransportContext();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        test.Routes.Entered = entered;
        test.Routes.Release = release;
        test.SetConnection(Peer4);
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "Pre-suspend outer IPv4 proof is blocked.");
        try
        {
            test.Service.Suspend();
            Equal(VpnTransportFamily.Unknown, test.Service.GetSnapshot().VpnRoute.TransportFamily, "suspend clears stale transport cache");
            test.Interfaces.Inventory = Inventory(Ras(Peer6));
            test.Service.Resume();
            PrivateField<Timer>(test.Service, "_timer").Change(Timeout.Infinite, Timeout.Infinite);
        }
        finally { release.Set(); }
        WaitUntil(() => test.Service.GetSnapshot().VpnRoute.TransportFamily == VpnTransportFamily.IPv6, "resume refresh uses new remote endpoint");
        Equal(InternetRouteKind.Vpn, test.Service.GetSnapshot().VpnRoute.IPv4RouteKind, "resume preserves independent VPN route");
        Equal(InternetRouteKind.Direct, test.Service.GetSnapshot().VpnRoute.IPv6RouteKind, "resume preserves independent direct route");
    }
    private static void Disposal()
    {
        using var test = new TransportContext();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        test.Routes.Entered = entered;
        test.Routes.Release = release;
        test.SetConnection(Peer6);
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "Old transport query starts before disposal.");
        var disposal = Task.Run(test.Service.Dispose);
        try { WaitUntil(() => !test.Service.GetSnapshot().IsRunning, "service shuts down before native query joins"); }
        finally { release.Set(); }
        Check(disposal.Wait(TimeSpan.FromSeconds(5)), "Shutdown joins its owned existing worker.");
        Equal(VpnTransportFamily.Unknown, test.Service.GetSnapshot().VpnRoute.TransportFamily, "late endpoint cannot publish after shutdown");
    }
    private static void ReadFailure()
    {
        using var test = new TransportContext();
        test.Interfaces.Failure = new UnauthorizedAccessException("Owned metadata fixture");
        test.Refresh();
        WaitUntil(() => test.Service.GetSnapshot().VpnRoute.Status == VpnTunnelStatus.Unknown, "metadata access failure publishes uncertainty");
        Equal(VpnTransportFamily.Unknown, test.Service.GetSnapshot().VpnRoute.TransportFamily, "permission failure has no family proof");
        Check(test.Service.GetSnapshot().IsRunning, "Missing metadata cannot stop monitoring.");
        test.Interfaces.Failure = null;
        test.SetConnection(Peer6);
        WaitUntil(() => test.Service.GetSnapshot().VpnRoute.TransportFamily == VpnTransportFamily.IPv6, "fresh metadata recovers transport without restart");
    }

    private sealed class TransportContext : IDisposable
    {
        private readonly TestWorkspace _workspace = new();
        private int _changes;
        public int Changes => Volatile.Read(ref _changes);
        public int Samples { get; private set; }
        public HeadlessTimeProvider Clock { get; } = new();
        public InterfaceSource Interfaces { get; } = new() { Inventory = new([Physical], []) };
        public RouteSource Routes { get; } = new() { IPv4 = V4(Route(Physical)), IPv6 = Direct6 };
        public FakeBandwidthMonitor Bandwidth { get; } = new();
        public MonitoringService Service { get; }
        public TransportContext()
        {
            var identity = new FakeNetworkIdentityService
            {
                Network = new NetworkIdentity("WiFi", "Fixture physical", "Fixture physical", PhysicalGuid.ToString())
            };
            Bandwidth.Adapters = [new AdapterInfo(PhysicalGuid.ToString(), "Physical Wi-Fi", "Owned fixture", "Wireless80211", 86_000_000, "192.0.2.2", true)];
            Bandwidth.SetSnapshot(PhysicalGuid.ToString(), new NetworkSnapshot(10_000, 1000, Clock.GetUtcNow()));
            try
            {
                Service = MonitoringService.CreateForDataRoot(_workspace.Root, identity,
                    new VpnRouteVerifier(new VpnDetectionService(Interfaces), Routes), Bandwidth, Clock,
                    new IcmpMonitor(new NoInternetPingSource(), timeProvider: Clock),
                    new InternetReachabilityMonitor(new NoInternetReachabilitySource(), timeProvider: Clock));
                Service.Changed += (_, _) => Interlocked.Increment(ref _changes);
                Service.Start();
                PrivateField<Timer>(Service, "_timer").Change(Timeout.Infinite, Timeout.Infinite);
                WaitUntil(() => Service.GetSnapshot().Connection.Status != ConnectionStatus.Detecting, "owned physical identity is recognized");
                Service.SampleNow();
                WaitUntil(() => Service.GetSnapshot().VpnRoute.TransportFamily == VpnTransportFamily.Inactive, "initial owned inactive cache completes");
            }
            catch { Service?.Dispose(); _workspace.Dispose(); throw; }
        }
        public void SetConnection(IPAddress? peer, bool connected = true)
        {
            Interfaces.Inventory = connected ? Inventory(Ras(peer)) : new([Physical], []);
            Routes.IPv4 = V4(Route(connected ? Ppp : Physical));
            Refresh();
        }
        public void Refresh() => PrivateField<VpnSessionMonitor>(Service, "_vpnSessions").Refresh();
        public void Sample(long received, long sent)
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            Bandwidth.SetSnapshot(PhysicalGuid.ToString(), new NetworkSnapshot(received, sent, Clock.GetUtcNow()));
            Service.SampleNow();
            Samples++;
        }
        public void Dispose() { try { Service.Dispose(); } finally { _workspace.Dispose(); } }
    }
    private sealed class InterfaceSource : IVpnInterfaceSource
    {
        private VpnInterfaceInventory _inventory = new([], []);
        private int _reads;
        public VpnInterfaceInventory Inventory { get => Volatile.Read(ref _inventory); set => Volatile.Write(ref _inventory, value); }
        public Exception? Failure { get; set; }
        public int Reads => Volatile.Read(ref _reads);
        public VpnInterfaceInventory Read()
        {
            Interlocked.Increment(ref _reads);
            if (Failure is { } failure) throw failure;
            return Inventory;
        }
    }
    private sealed class RouteSource : IVpnRouteSource
    {
        private VpnRouteInventory _ipv4 = V4();
        private VpnRouteInventory _ipv6 = No6;
        private int _reads;
        private int _concurrent;
        private int _maxConcurrent;
        public int Reads => Volatile.Read(ref _reads);
        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);
        public VpnRouteInventory IPv4 { get => Volatile.Read(ref _ipv4); set => Volatile.Write(ref _ipv4, value); }
        public VpnRouteInventory IPv6 { get => Volatile.Read(ref _ipv6); set => Volatile.Write(ref _ipv6, value); }
        public ManualResetEventSlim? Entered { get; set; }
        public ManualResetEventSlim? Release { get; set; }
        public VpnRouteInventory Read(AddressFamily family)
        {
            Interlocked.Increment(ref _reads);
            var concurrent = Interlocked.Increment(ref _concurrent);
            if (concurrent > Volatile.Read(ref _maxConcurrent)) Volatile.Write(ref _maxConcurrent, concurrent);
            try
            {
                var inventory = family == AddressFamily.InterNetwork ? IPv4 : IPv6;
                if (family == AddressFamily.InterNetwork && Release is { } release)
                {
                    Entered?.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Owned route source release timeout.");
                }
                return inventory;
            }
            finally { Interlocked.Decrement(ref _concurrent); }
        }
    }
    private static T PrivateField<T>(object instance, string name) =>
        (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
            ?? throw new InvalidOperationException("Missing fixture field " + name));
    private static void WaitUntil(Func<bool> condition, string description)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(description);
            Thread.Sleep(20);
        }
    }
    private static void Equal<T>(T expected, T actual, string description)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{description}: expected {expected}, got {actual}.");
    }
    private static void Check(bool condition, string description) { if (!condition) throw new InvalidOperationException(description); }
}
