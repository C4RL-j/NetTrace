using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using NetTrace.Models;
using NetTrace.Services;

internal static class RouteAwarenessTests
{
    public static (string Name, Action Run)[] Cases =>
    [
        ("Physical general routes prove inactive VPN and direct routing", Direct),
        ("A known operational tunnel winning the default route proves active VPN", ActiveDefault),
        ("Installed disconnected or non-effective tunnel adapters remain inactive", InactiveTunnels),
        ("Internal Hyper-V VMware VirtualBox and Docker adapters cannot become VPN by name", InternalVirtual),
        ("Incomplete interfaces or routing cannot prove direct or VPN ownership", IncompleteEvidence),
        ("Paired VPN /1 routes outrank a cheaper physical default", PairedHalves),
        ("One VPN half or mixed physical and VPN halves remain unknown", IncompleteHalves),
        ("Tied effective defaults remain unknown rather than picking a VPN arbitrarily", TiedDefaults),
        ("Combined route and interface metric determines effective ownership", CombinedMetrics),
        ("Large combined route metrics cannot wrap into a false VPN winner", LargeMetrics),
        ("Unknown route metric state or default eligibility remains unknown", UnknownMetrics),
        ("IPv4 VPN and IPv6 direct remain active with explicit mixed-family context", MixedFamilies),
        ("A proven VPN family remains active when its other family is unknown", VpnWithUnknownFamily),
        ("Both VPN families retain independently proven route values", BothFamiliesVpn),
        ("Both direct families retain independently proven route values", BothFamiliesDirect),
        ("IPv4 direct and IPv6 VPN preserve their distinct route values", ReverseMixedFamilies),
        ("Unknown routing in either family preserves the other family's independent proof", IndependentUnknownFamilies),
        ("Unavailable families remain distinct from unknown and preserve the peer route", IndependentUnavailableFamilies),
        ("Expired or disabled routes preserve family usability while truly unusable families remain unavailable", FamilyUsability),
        ("Private internal tunnel routes do not count as an active internet VPN", PrivateTunnelPrefix),
        ("Effective public split routes prevent an overbroad direct-route claim", PublicTunnelPrefix),
        ("Broad prefixes beginning in local space cannot hide public VPN coverage", LocalBasePublicCoverage),
        ("Broad prefixes spanning private and public space cannot hide VPN coverage", PrivateBasePublicCoverage),
        ("Fully shadowed VPN default routes do not count as active internet routing", ShadowedDefault),
        ("Fully shadowed paired VPN /1 routes do not count as active internet routing", ShadowedHalves),
        ("Effective public tunnel participants replace a fully shadowed broad tunnel name", ShadowedByOtherTunnel),
        ("Multiple installed tunnels select the actual effective default participant", MultipleCandidates),
        ("Independent VPN winners across families avoid naming an arbitrary installed tunnel", MultipleFamilyParticipants),
        ("Confirmed correlated Windows RAS PPP default routing supports VPN awareness", CorrelatedPpp),
        ("Uncorrelated PPP and ordinary dial-up defaults remain unknown", UnsupportedPpp),
        ("Competing RAS or PPP candidates cannot manufacture a VPN association", AmbiguousPpp),
        ("Virtual Ethernet fallback requires operational effective routing and tunnel evidence", VirtualFallback),
        ("An opaque generic virtual default interface remains unknown", OpaqueVirtual),
        ("Always Connected TAP metadata cannot be promoted by a configured route", AlwaysConnectedTap),
        ("Indeterminate opaque tunnel metadata cannot be promoted to active VPN", OpaqueTunnel),
        ("Changed route-interface identity cannot reuse stale VPN evidence", IdentityMismatch),
        ("Unavailable IPv6 can be excluded only with complete consistent evidence", FamilyAvailability),
        ("Route snapshots are deterministic and retain immutable presentation values", DeterministicSnapshot),
        ("Current route observation reads native inventories once while preserving history classification", SharedObservation),
        ("Background route transitions preserve adapter selection physical session and byte baseline", BackgroundTransitions),
        ("Route awareness remains available when physical identity permission is unavailable", IdentityUnavailable),
        ("Reading cached route snapshots does not trigger native discovery", CachedRouteReads),
        ("Suspend drops blocked old route publication and resume promptly refreshes", StaleSuspend),
        ("Disposal prevents a blocked route query from publishing late state", StaleDisposal),
    ];

    private static readonly DateTimeOffset Origin = new(2026, 10, 11, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid PhysicalGuid = new("c4c590b9-494f-477a-a78f-c7137ac14a18");
    private static readonly Guid TunnelGuid = new("825018c6-06a8-4721-9b04-9aedfbf70d23");
    private static VpnInterfaceEvidence Physical => new()
    {
        InterfaceIndex = 7, InterfaceGuid = PhysicalGuid, InterfaceName = "Physical Wi-Fi",
        Description = "Owned physical fixture", InterfaceType = 71, StatusFlags = 1,
        OperationalStatus = OperationalStatus.Up, MediaConnectState = 1
    };
    private static VpnInterfaceEvidence Tunnel => new()
    {
        InterfaceIndex = 42, InterfaceGuid = TunnelGuid, InterfaceName = "Fixture tunnel",
        Description = "Owned tunnel fixture", InterfaceType = 131, TunnelType = 1,
        OperationalStatus = OperationalStatus.Up, MediaConnectState = 1
    };
    private static VpnInterfaceInventory Inventory(params VpnInterfaceEvidence[] interfaces) => new(interfaces, []);
    private static VpnRouteInventory V4(params VpnRouteEvidence[] routes) => new(AddressFamily.InterNetwork, routes);
    private static VpnRouteInventory NoV6 => new(AddressFamily.InterNetworkV6, [], Availability: IpFamilyAvailability.Unavailable);
    private static VpnRouteEvidence Route(VpnInterfaceEvidence owner, string prefix = "0.0.0.0", byte length = 0, uint metric = 5,
        uint? interfaceMetric = 0) => new(owner.InterfaceIndex, owner.InterfaceGuid, IPAddress.Parse(prefix), length,
            IPAddress.Parse(prefix.Contains(':') ? "::" : "0.0.0.0"), metric, interfaceMetric, true);
    private static VpnRouteSnapshot Classify(VpnInterfaceInventory interfaces, VpnRouteInventory ipv4, VpnRouteInventory? ipv6 = null) =>
        VpnRouteClassifier.Classify(interfaces, ipv4, ipv6 ?? NoV6, Origin);
    private static void State(VpnRouteSnapshot snapshot, VpnTunnelStatus status, InternetRouteKind route)
    {
        Equal(status, snapshot.Status, "tunnel status");
        Equal(route, snapshot.RouteKind, "effective route");
        Equal<DateTimeOffset?>(Origin, snapshot.CheckedAtUtc, "UTC checked timestamp");
        Check(!string.IsNullOrWhiteSpace(snapshot.Reason), "Route interpretation explains its evidence.");
    }
    private static void Unknown(VpnRouteSnapshot snapshot) => State(snapshot, VpnTunnelStatus.Unknown, InternetRouteKind.Unknown);
    private static void FamilyRoutes(VpnRouteSnapshot snapshot, InternetRouteKind ipv4, InternetRouteKind ipv6)
    {
        Equal(ipv4, snapshot.IPv4RouteKind, "independent IPv4 route");
        Equal(ipv6, snapshot.IPv6RouteKind, "independent IPv6 route");
    }

    private static void Direct()
    {
        var snapshot = Classify(Inventory(Physical), V4(Route(Physical)));
        State(snapshot, VpnTunnelStatus.Inactive, InternetRouteKind.Direct);
        FamilyRoutes(snapshot, InternetRouteKind.Direct, InternetRouteKind.Unavailable);
    }
    private static void ActiveDefault()
    {
        var snapshot = Classify(Inventory(Physical, Tunnel), V4(Route(Physical, metric: 50), Route(Tunnel)));
        State(snapshot, VpnTunnelStatus.Active, InternetRouteKind.Vpn);
        FamilyRoutes(snapshot, InternetRouteKind.Vpn, InternetRouteKind.Unavailable);
    }

    private static void InactiveTunnels()
    {
        foreach (var tunnel in new[] { Tunnel, Tunnel with { OperationalStatus = OperationalStatus.Down, MediaConnectState = 2 } })
            State(Classify(Inventory(Physical, tunnel), V4(Route(Physical), Route(tunnel, metric: 100) with { InterfaceUsable = tunnel.OperationalStatus == OperationalStatus.Up })),
                VpnTunnelStatus.Inactive, InternetRouteKind.Direct);
    }

    private static void InternalVirtual()
    {
        foreach (var (name, driver) in new[] { ("Hyper-V VPN", "vms_mp"), ("VMware VPN", "vmnetadapter"),
            ("VirtualBox VPN", "vboxnetadp"), ("Docker VPN", "") })
        {
            var item = Tunnel with { InterfaceType = 6, TunnelType = 0, InterfaceName = name, DriverComponentId = driver };
            State(Classify(Inventory(Physical, item), V4(Route(Physical), Route(item, "172.16.0.0", 16))), VpnTunnelStatus.Inactive, InternetRouteKind.Direct);
            Unknown(Classify(Inventory(Physical, item), V4(Route(item))));
        }
    }

    private static void IncompleteEvidence()
    {
        Unknown(Classify(Inventory(Physical) with { IsComplete = false }, V4(Route(Physical))));
        Unknown(Classify(Inventory(Physical), V4(Route(Physical)) with { IsComplete = false }));
        Unknown(Classify(Inventory(Tunnel), V4(Route(Tunnel)) with { AddressFamily = AddressFamily.InterNetworkV6 }));
    }

    private static void PairedHalves() => State(Classify(Inventory(Physical, Tunnel),
        V4(Route(Physical, metric: 1), Route(Tunnel, length: 1, metric: 100), Route(Tunnel, "128.0.0.0", 1, 100))),
        VpnTunnelStatus.Active, InternetRouteKind.Vpn);

    private static void IncompleteHalves()
    {
        Unknown(Classify(Inventory(Physical, Tunnel), V4(Route(Tunnel, length: 1))));
        Unknown(Classify(Inventory(Physical, Tunnel), V4(Route(Tunnel, length: 1), Route(Physical, "128.0.0.0", 1))));
    }

    private static void TiedDefaults() => Unknown(Classify(Inventory(Physical, Tunnel), V4(Route(Physical), Route(Tunnel))));
    private static void CombinedMetrics()
    {
        State(Classify(Inventory(Physical, Tunnel), V4(Route(Physical, metric: 10, interfaceMetric: 2), Route(Tunnel, metric: 1, interfaceMetric: 30))), VpnTunnelStatus.Inactive, InternetRouteKind.Direct);
        State(Classify(Inventory(Physical, Tunnel), V4(Route(Physical, metric: 10, interfaceMetric: 30), Route(Tunnel, metric: 20, interfaceMetric: 1))), VpnTunnelStatus.Active, InternetRouteKind.Vpn);
    }
    private static void LargeMetrics() => State(Classify(Inventory(Physical, Tunnel),
        V4(Route(Physical, metric: 100), Route(Tunnel, metric: uint.MaxValue - 1, interfaceMetric: uint.MaxValue))),
        VpnTunnelStatus.Inactive, InternetRouteKind.Direct);

    private static void UnknownMetrics()
    {
        foreach (var route in new[] { Route(Tunnel) with { InterfaceMetric = null }, Route(Tunnel) with { RouteMetric = uint.MaxValue },
            Route(Tunnel) with { InterfaceUsable = null }, Route(Tunnel) with { DefaultRoutesEnabled = null } })
            Unknown(Classify(Inventory(Physical, Tunnel), V4(Route(Physical), route)));
        State(Classify(Inventory(Physical, Tunnel), V4(Route(Physical), Route(Tunnel) with { DefaultRoutesEnabled = false })),
            VpnTunnelStatus.Inactive, InternetRouteKind.Direct);
    }

    private static void MixedFamilies()
    {
        var snapshot = Classify(Inventory(Physical, Tunnel), V4(Route(Tunnel)), new(AddressFamily.InterNetworkV6, [Route(Physical, "::")]));
        State(snapshot, VpnTunnelStatus.Active, InternetRouteKind.Vpn);
        FamilyRoutes(snapshot, InternetRouteKind.Vpn, InternetRouteKind.Direct);
        Check(snapshot.Reason.Contains("IPv4: VPN", StringComparison.Ordinal) && snapshot.Reason.Contains("IPv6: Direct", StringComparison.Ordinal), "Mixed family routing is explicit rather than a full-tunnel claim.");
    }
    private static void VpnWithUnknownFamily()
    {
        var snapshot = Classify(Inventory(Tunnel), V4(Route(Tunnel)), NoV6 with { IsComplete = false });
        State(snapshot, VpnTunnelStatus.Active, InternetRouteKind.Vpn);
        FamilyRoutes(snapshot, InternetRouteKind.Vpn, InternetRouteKind.Unknown);
    }
    private static void BothFamiliesVpn()
    {
        var snapshot = Classify(Inventory(Tunnel), V4(Route(Tunnel)), new(AddressFamily.InterNetworkV6, [Route(Tunnel, "::")]));
        State(snapshot, VpnTunnelStatus.Active, InternetRouteKind.Vpn);
        FamilyRoutes(snapshot, InternetRouteKind.Vpn, InternetRouteKind.Vpn);
    }
    private static void BothFamiliesDirect()
    {
        var snapshot = Classify(Inventory(Physical), V4(Route(Physical)), new(AddressFamily.InterNetworkV6, [Route(Physical, "::")]));
        State(snapshot, VpnTunnelStatus.Inactive, InternetRouteKind.Direct);
        FamilyRoutes(snapshot, InternetRouteKind.Direct, InternetRouteKind.Direct);
    }
    private static void ReverseMixedFamilies()
    {
        var snapshot = Classify(Inventory(Physical, Tunnel), V4(Route(Physical)), new(AddressFamily.InterNetworkV6, [Route(Tunnel, "::")]));
        State(snapshot, VpnTunnelStatus.Active, InternetRouteKind.Vpn);
        FamilyRoutes(snapshot, InternetRouteKind.Direct, InternetRouteKind.Vpn);
    }
    private static void IndependentUnknownFamilies()
    {
        Equal(InternetRouteKind.Unknown, VpnRouteSnapshot.Unknown.IPv4RouteKind, "unobserved IPv4 remains unknown");
        Equal(InternetRouteKind.Unknown, VpnRouteSnapshot.Unknown.IPv6RouteKind, "unobserved IPv6 remains unknown");
        var interfaces = Inventory(Physical, Tunnel);
        var unknown4 = V4() with { IsComplete = false };
        var unknown6 = NoV6 with { IsComplete = false };
        var direct4 = V4(Route(Physical));
        var direct6 = new VpnRouteInventory(AddressFamily.InterNetworkV6, [Route(Physical, "::")]);
        var vpn4 = V4(Route(Tunnel));
        var vpn6 = new VpnRouteInventory(AddressFamily.InterNetworkV6, [Route(Tunnel, "::")]);
        foreach (var (ipv4, ipv6, expected4, expected6, status, aggregate) in new[]
        {
            (unknown4, direct6, InternetRouteKind.Unknown, InternetRouteKind.Direct, VpnTunnelStatus.Unknown, InternetRouteKind.Unknown),
            (direct4, unknown6, InternetRouteKind.Direct, InternetRouteKind.Unknown, VpnTunnelStatus.Unknown, InternetRouteKind.Unknown),
            (unknown4, vpn6, InternetRouteKind.Unknown, InternetRouteKind.Vpn, VpnTunnelStatus.Active, InternetRouteKind.Vpn),
            (vpn4, unknown6, InternetRouteKind.Vpn, InternetRouteKind.Unknown, VpnTunnelStatus.Active, InternetRouteKind.Vpn),
            (unknown4, unknown6, InternetRouteKind.Unknown, InternetRouteKind.Unknown, VpnTunnelStatus.Unknown, InternetRouteKind.Unknown)
        })
        {
            var snapshot = Classify(interfaces, ipv4, ipv6);
            FamilyRoutes(snapshot, expected4, expected6);
            State(snapshot, status, aggregate);
        }
    }
    private static void IndependentUnavailableFamilies()
    {
        var interfaces = Inventory(Physical, Tunnel);
        var unavailable4 = V4() with { Availability = IpFamilyAvailability.Unavailable };
        var unknown4 = unavailable4 with { IsComplete = false };
        var unknown6 = NoV6 with { IsComplete = false };
        foreach (var (ipv4, ipv6, expected4, expected6, status, aggregate) in new[]
        {
            (unavailable4, new VpnRouteInventory(AddressFamily.InterNetworkV6, [Route(Tunnel, "::")]), InternetRouteKind.Unavailable, InternetRouteKind.Vpn, VpnTunnelStatus.Active, InternetRouteKind.Vpn),
            (unavailable4, new VpnRouteInventory(AddressFamily.InterNetworkV6, [Route(Physical, "::")]), InternetRouteKind.Unavailable, InternetRouteKind.Direct, VpnTunnelStatus.Inactive, InternetRouteKind.Direct),
            (unavailable4, NoV6, InternetRouteKind.Unavailable, InternetRouteKind.Unavailable, VpnTunnelStatus.Unknown, InternetRouteKind.Unknown),
            (unknown4, NoV6, InternetRouteKind.Unknown, InternetRouteKind.Unavailable, VpnTunnelStatus.Unknown, InternetRouteKind.Unknown),
            (unavailable4, unknown6, InternetRouteKind.Unavailable, InternetRouteKind.Unknown, VpnTunnelStatus.Unknown, InternetRouteKind.Unknown)
        })
        {
            var snapshot = Classify(interfaces, ipv4, ipv6);
            FamilyRoutes(snapshot, expected4, expected6);
            State(snapshot, status, aggregate);
        }
    }
    private static void FamilyUsability()
    {
        var eligible = Route(Tunnel, "::");
        foreach (var ipv6 in new[]
        {
            new VpnRouteInventory(AddressFamily.InterNetworkV6, [eligible with { ValidLifetime = 0 }]),
            new VpnRouteInventory(AddressFamily.InterNetworkV6, [eligible with { DefaultRoutesEnabled = false }]),
            new VpnRouteInventory(AddressFamily.InterNetworkV6, [eligible with { ValidLifetime = 0 }], Availability: IpFamilyAvailability.Unavailable),
            new VpnRouteInventory(AddressFamily.InterNetworkV6, [eligible with { ValidLifetime = 0, InterfaceUsable = null }]),
            NoV6 with { Availability = IpFamilyAvailability.Usable }
        })
        {
            var snapshot = Classify(Inventory(Physical, Tunnel), V4(Route(Physical)), ipv6);
            Unknown(snapshot);
            FamilyRoutes(snapshot, InternetRouteKind.Direct, InternetRouteKind.Unknown);
        }
        foreach (var ipv6 in new[]
        {
            new VpnRouteInventory(AddressFamily.InterNetworkV6, [eligible with { InterfaceUsable = false }]),
            new VpnRouteInventory(AddressFamily.InterNetworkV6, [eligible with { IsLoopback = true }]),
            new VpnRouteInventory(AddressFamily.InterNetworkV6, [])
        })
        {
            var snapshot = Classify(Inventory(Physical, Tunnel), V4(Route(Physical)), ipv6);
            State(snapshot, VpnTunnelStatus.Inactive, InternetRouteKind.Direct);
            FamilyRoutes(snapshot, InternetRouteKind.Direct, InternetRouteKind.Unavailable);
        }
    }
    private static void PrivateTunnelPrefix() => State(Classify(Inventory(Physical, Tunnel), V4(Route(Physical), Route(Tunnel, "10.0.0.0", 8))), VpnTunnelStatus.Inactive, InternetRouteKind.Direct);
    private static void PublicTunnelPrefix() => Unknown(Classify(Inventory(Physical, Tunnel), V4(Route(Physical), Route(Tunnel, "8.8.8.8", 32))));
    private static void LocalBasePublicCoverage() => Unknown(Classify(Inventory(Physical, Tunnel), V4(Route(Physical), Route(Tunnel, length: 2))));
    private static void PrivateBasePublicCoverage() => Unknown(Classify(Inventory(Physical, Tunnel), V4(Route(Physical), Route(Tunnel, "10.0.0.0", 7))));
    private static VpnRouteEvidence[] Quarters(VpnInterfaceEvidence owner) =>
        [Route(owner, "0.0.0.0", 2), Route(owner, "64.0.0.0", 2), Route(owner, "128.0.0.0", 2), Route(owner, "192.0.0.0", 2)];
    private static void ShadowedDefault() => State(Classify(Inventory(Physical, Tunnel),
        V4([Route(Tunnel), .. Quarters(Physical)])), VpnTunnelStatus.Inactive, InternetRouteKind.Direct);
    private static void ShadowedHalves() => State(Classify(Inventory(Physical, Tunnel),
        V4([Route(Tunnel, length: 1), Route(Tunnel, "128.0.0.0", 1), .. Quarters(Physical)])), VpnTunnelStatus.Inactive, InternetRouteKind.Direct);
    private static void ShadowedByOtherTunnel()
    {
        var actual = Tunnel with { InterfaceIndex = 43, InterfaceGuid = Guid.NewGuid(), InterfaceName = "Actual public tunnel" };
        var snapshot = Classify(Inventory(Tunnel, actual), V4([Route(Tunnel), .. Quarters(actual)]));
        State(snapshot, VpnTunnelStatus.Active, InternetRouteKind.Vpn);
        Equal(actual.InterfaceName, snapshot.InterfaceName, "shadowed broad route owner is excluded from active metadata");
    }

    private static void MultipleCandidates()
    {
        var second = Tunnel with { InterfaceIndex = 43, InterfaceGuid = Guid.NewGuid(), InterfaceName = "Effective second tunnel" };
        var snapshot = Classify(Inventory(Physical, Tunnel, second), V4(Route(Physical, metric: 50), Route(Tunnel, metric: 100), Route(second)));
        State(snapshot, VpnTunnelStatus.Active, InternetRouteKind.Vpn);
        Equal(second.InterfaceName, snapshot.InterfaceName, "effective candidate is selected by routing evidence");
    }
    private static void MultipleFamilyParticipants()
    {
        var second = Tunnel with { InterfaceIndex = 43, InterfaceGuid = Guid.NewGuid(), InterfaceName = "IPv6 tunnel" };
        var snapshot = Classify(Inventory(Tunnel, second), V4(Route(Tunnel)), new(AddressFamily.InterNetworkV6, [Route(second, "::")]));
        State(snapshot, VpnTunnelStatus.Active, InternetRouteKind.Vpn);
        Equal("Multiple tunnel interfaces", snapshot.InterfaceName, "independent family participants are represented together");
    }

    private static VpnInterfaceEvidence Ppp => Tunnel with { InterfaceType = 23, TunnelType = 0, InterfaceName = "Work VPN", Description = "WAN Miniport (IKEv2)" };
    private static VpnRasConnectionEvidence Ras => new("Work VPN", "WAN Miniport (IKEv2)", "vpn", 0x2000);
    private static void CorrelatedPpp() => State(Classify(Inventory(Physical, Ppp) with { RasConnections = [Ras] }, V4(Route(Ppp))), VpnTunnelStatus.Active, InternetRouteKind.Vpn);
    private static void UnsupportedPpp()
    {
        Unknown(Classify(Inventory(Ppp), V4(Route(Ppp))));
        Unknown(Classify(Inventory(Ppp) with { RasConnections = [Ras with { ConnectionName = "Other", DeviceName = "Other" }] }, V4(Route(Ppp))));
        Unknown(Classify(Inventory(Ppp) with { RasConnections = [Ras with { DeviceType = "modem" }] }, V4(Route(Ppp))));
    }
    private static void AmbiguousPpp()
    {
        foreach (var inventory in new[]
        {
            Inventory(Ppp) with { RasConnections = [Ras, Ras with { ConnectionName = "Second" }] },
            Inventory(Ppp) with { RasConnections = [Ras, Ras with { DeviceType = "modem", ConnectionName = "Dial-up" }] },
            Inventory(Ppp, Ppp with { InterfaceIndex = 43, InterfaceGuid = Guid.NewGuid() }) with { RasConnections = [Ras] }
        }) Unknown(Classify(inventory, V4(Route(Ppp))));
    }
    private static void VirtualFallback()
    {
        var item = Tunnel with { InterfaceType = 6, TunnelType = 0, InterfaceName = "Generic VPN adapter" };
        State(Classify(Inventory(Physical, item), V4(Route(item))), VpnTunnelStatus.Active, InternetRouteKind.Vpn);
        State(Classify(Inventory(Physical, item), V4(Route(Physical), Route(item, metric: 100))), VpnTunnelStatus.Inactive, InternetRouteKind.Direct);
        Unknown(Classify(Inventory(item with { MediaConnectState = 0 }), V4(Route(item))));
    }
    private static void OpaqueVirtual() => Unknown(Classify(Inventory(Tunnel with
    {
        InterfaceType = 6, TunnelType = 0, InterfaceName = "Virtual adapter", Description = "Unidentified virtual interface"
    }), V4(Route(Tunnel))));
    private static void AlwaysConnectedTap()
    {
        var item = Tunnel with { InterfaceType = 6, TunnelType = 0, InterfaceName = "TAP VPN", DriverComponentId = "tap0901", TapMediaAlwaysConnected = true };
        Unknown(Classify(Inventory(item), V4(Route(item))));
        State(Classify(Inventory(Physical, item), V4(Route(Physical), Route(item, metric: 100))), VpnTunnelStatus.Inactive, InternetRouteKind.Direct);
    }
    private static void OpaqueTunnel() => Unknown(Classify(Inventory(Tunnel with { TunnelType = 99 }), V4(Route(Tunnel))));
    private static void IdentityMismatch() => Unknown(Classify(Inventory(Tunnel), V4(Route(Tunnel) with { InterfaceGuid = Guid.NewGuid() })));
    private static void FamilyAvailability()
    {
        Unknown(Classify(Inventory(Physical), V4(Route(Physical)), NoV6 with { IsComplete = false }));
        Unknown(Classify(Inventory(Physical), V4(Route(Physical)), NoV6 with { Availability = IpFamilyAvailability.Usable }));
        Unknown(Classify(Inventory(Physical), V4(Route(Physical)), NoV6 with { Availability = (IpFamilyAvailability)99 }));
        Unknown(Classify(Inventory(Physical), V4(Route(Physical)) with { Availability = IpFamilyAvailability.Unavailable }));
    }
    private static void DeterministicSnapshot()
    {
        var rows = new[] { Route(Physical, metric: 50), Route(Tunnel), Route(Tunnel) };
        var value = Classify(Inventory(Physical, Tunnel), V4(rows));
        Equal(value, Classify(Inventory(Tunnel, Physical), V4(rows.Reverse().ToArray())), "reordered and duplicate evidence does not change presentation");
        rows[1] = Route(Physical);
        Equal("Fixture tunnel", value.InterfaceName, "snapshot holds immutable display metadata");
    }
    private static void SharedObservation()
    {
        var interfaces = new FakeVpnInterfaceSource { Inventory = Inventory(Physical) };
        var routes = new RouteSource { IPv4 = V4(Route(Physical)) };
        var verifier = new VpnRouteVerifier(new VpnDetectionService(interfaces), routes);
        var observation = verifier.GetCurrentObservation(Origin);
        State(observation.Snapshot, VpnTunnelStatus.Inactive, InternetRouteKind.Direct);
        Equal(VpnStatus.NoVpn, observation.Routing.Detection.Status, "history retains the previous conservative detection model");
        Equal(0, observation.Routing.Tunnels.Count, "route awareness does not invent historical VPN sessions");
        Equal(1, interfaces.Reads, "one native interface read");
        Equal(2, routes.Reads, "one route read for each family");
        _ = verifier.GetCurrentState();
        Equal(2, routes.Reads, "legacy no-candidate API still avoids native route reads");
    }

    private static void BackgroundTransitions()
    {
        using var test = new RouteContext();
        WaitUntil(() => test.Service.GetSnapshot().VpnRoute.RouteKind == InternetRouteKind.Direct, "initial direct state is observed in the headless service");
        var initial = test.Service.GetSnapshot();
        test.Sample(10_100, 1010);
        test.Interfaces.Inventory = Inventory(Physical, Tunnel);
        test.Routes.IPv4 = V4(Route(Physical, metric: 50), Route(Tunnel));
        test.RefreshRoute();
        WaitUntil(() => test.Service.GetSnapshot().VpnRoute.RouteKind == InternetRouteKind.Vpn, "VPN transition publishes cached route context");
        test.Sample(10_300, 1030);
        var active = test.Service.GetSnapshot();
        Equal(initial.SelectedAdapter!.Id, active.SelectedAdapter!.Id, "VPN does not silently change the monitored adapter");
        Equal(initial.Session!.Id, active.Session!.Id, "VPN transition does not split the physical session");
        Equal(300L, active.SessionDownloaded, "VPN transition preserves accounting baseline and totals");
        test.Interfaces.Inventory = Inventory(Physical);
        test.Routes.IPv4 = V4(Route(Physical));
        test.RefreshRoute();
        WaitUntil(() => test.Service.GetSnapshot().VpnRoute.RouteKind == InternetRouteKind.Direct, "VPN disconnect returns to direct without restart");
        Equal(initial.Session.Id, test.Service.GetSnapshot().Session!.Id, "VPN disconnect retains the physical session");
        Check(test.Changes > 0, "Route publication reaches the existing service Changed event.");
    }
    private static void IdentityUnavailable()
    {
        using var test = new RouteContext(identityAvailable: false);
        test.Interfaces.Inventory = Inventory(Physical, Tunnel);
        test.Routes.IPv4 = V4(Route(Tunnel));
        test.RefreshRoute();
        WaitUntil(() => test.Service.GetSnapshot().VpnRoute.RouteKind == InternetRouteKind.Vpn, "route awareness does not depend on identity permission");
        var snapshot = test.Service.GetSnapshot();
        Equal(ConnectionStatus.ConnectedIdentityUnavailable, snapshot.Connection.Status, "physical identity remains independently unavailable");
        Check(snapshot.Session is null, "route awareness cannot manufacture a confirmed physical history session");
        Equal(0, new VpnSessionStore(test.Service.Usage).GetRecentSessions().Count, "awareness without parent cannot write VPN history");
    }
    private static void CachedRouteReads()
    {
        using var test = new RouteContext();
        WaitUntil(() => test.Service.GetSnapshot().VpnRoute.RouteKind == InternetRouteKind.Direct, "cache completes its first observation");
        var reads = test.Routes.Reads;
        var native = test.Interfaces.Reads;
        for (var index = 0; index < 100; index++) Equal(InternetRouteKind.Direct, test.Service.GetSnapshot().VpnRoute.RouteKind, "cached route read");
        Equal(reads, test.Routes.Reads, "reading UI snapshots does not reread route tables");
        Equal(native, test.Interfaces.Reads, "reading UI snapshots does not reread native interfaces");
    }
    private static void StaleSuspend()
    {
        using var test = new RouteContext();
        WaitUntil(() => test.Service.GetSnapshot().VpnRoute.RouteKind == InternetRouteKind.Direct, "initial direct route observed");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        test.Interfaces.Inventory = Inventory(Physical, Tunnel);
        test.Routes.IPv4 = V4(Route(Tunnel));
        test.Routes.Entered = entered;
        test.Routes.Release = release;
        test.RefreshRoute();
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "Pre-suspend native query is blocked deterministically.");
        try
        {
            test.Service.Suspend();
            Equal(InternetRouteKind.Unknown, test.Service.GetSnapshot().VpnRoute.RouteKind, "suspend clears stale active route presentation");
            test.Interfaces.Inventory = Inventory(Physical);
            test.Routes.IPv4 = V4(Route(Physical));
            test.Service.Resume();
        }
        finally { release.Set(); }
        WaitUntil(() => test.Service.GetSnapshot().VpnRoute.RouteKind == InternetRouteKind.Direct, "post-resume generation promptly replaces stale native evidence");
        Check(test.Service.GetSnapshot().VpnRoute.Status != VpnTunnelStatus.Active, "Blocked pre-suspend VPN evidence is discarded.");
    }
    private static void StaleDisposal()
    {
        using var test = new RouteContext();
        WaitUntil(() => test.Service.GetSnapshot().VpnRoute.RouteKind == InternetRouteKind.Direct, "initial direct route observed");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        test.Interfaces.Inventory = Inventory(Physical, Tunnel);
        test.Routes.IPv4 = V4(Route(Tunnel));
        test.Routes.Entered = entered;
        test.Routes.Release = release;
        test.RefreshRoute();
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "Native route query starts before shutdown.");
        var shutdown = Task.Run(test.Service.Dispose);
        try { WaitUntil(() => !test.Service.GetSnapshot().IsRunning, "shutdown marks the service stopped before joining native query"); }
        finally { release.Set(); }
        Check(shutdown.Wait(TimeSpan.FromSeconds(5)), "Shutdown joins its existing route observer.");
        Equal(InternetRouteKind.Unknown, test.Service.GetSnapshot().VpnRoute.RouteKind, "late native query cannot publish a VPN snapshot after disposal");
    }

    private sealed class RouteContext : IDisposable
    {
        private readonly TestWorkspace _workspace = new();
        private int _changes;
        public int Changes => Volatile.Read(ref _changes);
        public HeadlessTimeProvider Clock { get; } = new();
        public FakeVpnInterfaceSource Interfaces { get; } = new() { Inventory = Inventory(Physical) };
        public RouteSource Routes { get; } = new() { IPv4 = V4(Route(Physical)) };
        public FakeBandwidthMonitor Bandwidth { get; } = new();
        public MonitoringService Service { get; }
        public RouteContext(bool identityAvailable = true)
        {
            var identity = new FakeNetworkIdentityService { Network = new NetworkIdentity("WiFi", "Fixture physical", "Fixture physical", PhysicalGuid.ToString()) };
            if (!identityAvailable) identity.Availability = NetworkIdentityAvailability.Unavailable;
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
                WaitUntil(() => Service.GetSnapshot().Connection.Status != ConnectionStatus.Detecting, "owned physical identity is observed");
                Service.SampleNow();
            }
            catch { Service?.Dispose(); _workspace.Dispose(); throw; }
        }
        public void RefreshRoute() => PrivateField<VpnSessionMonitor>(Service, "_vpnSessions").Refresh();
        public void Sample(long received, long sent)
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            Bandwidth.SetSnapshot(PhysicalGuid.ToString(), new NetworkSnapshot(received, sent, Clock.GetUtcNow()));
            Service.SampleNow();
        }
        public void Dispose() { try { Service.Dispose(); } finally { _workspace.Dispose(); } }
    }
    private sealed class RouteSource : IVpnRouteSource
    {
        private VpnRouteInventory _ipv4 = V4();
        private VpnRouteInventory _ipv6 = NoV6;
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public VpnRouteInventory IPv4 { get => Volatile.Read(ref _ipv4); set => Volatile.Write(ref _ipv4, value); }
        public VpnRouteInventory IPv6 { get => Volatile.Read(ref _ipv6); set => Volatile.Write(ref _ipv6, value); }
        public ManualResetEventSlim? Entered { get; set; }
        public ManualResetEventSlim? Release { get; set; }
        public VpnRouteInventory Read(AddressFamily family)
        {
            Interlocked.Increment(ref _reads);
            var inventory = family == AddressFamily.InterNetwork ? IPv4 : IPv6;
            if (family == AddressFamily.InterNetwork && Release is { } release)
            {
                Entered?.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Owned route source release timeout.");
            }
            return inventory;
        }
    }
    private static T PrivateField<T>(object instance, string name) =>
        (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
            ?? throw new InvalidOperationException("Missing fixture field " + name));
    private static void WaitUntil(Func<bool> condition, string description)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition()) { if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(description); Thread.Sleep(20); }
    }
    private static void Equal<T>(T expected, T actual, string description)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{description}: expected {expected}, got {actual}.");
    }
    private static void Check(bool condition, string description) { if (!condition) throw new InvalidOperationException(description); }
}
