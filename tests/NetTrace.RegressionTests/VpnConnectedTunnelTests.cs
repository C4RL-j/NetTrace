using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using NetTrace.Models;
using NetTrace.Services;

internal static class VpnConnectedTunnelTests
{
    private static readonly Guid TunnelId = Guid.Parse("c512d48f-a20d-4cc2-bb75-64c7b3973221");
    private static readonly Guid OtherId = Guid.Parse("c512d48f-a20d-4cc2-bb75-64c7b3973222");

    public static (string Name, Action Run)[] Cases =>
    [
        ("A confirmed connected RAS VPN is reported without default routes or internet reachability", RasConnected),
        ("Inactive erroneous dormant or non-VPN RAS entries cannot create a connected key", RasInactive),
        ("An identified tunnel with an assigned IP and a private split route is connected", PrivateSplit),
        ("A connected tunnel does not need to own or win the physical default route", PhysicalDefault),
        ("A known installed adapter needs up state an assigned IP and an active associated route", AdapterRequirements),
        ("An Always Connected TAP needs forwarding evidence beyond its own local address route", AlwaysConnectedTap),
        ("An IPv6-only assigned tunnel can be connected through its IPv6 split route", Ipv6Split),
        ("Expired unusable loopback or mismatched route evidence cannot create a VPN connection", InvalidRoutes),
        ("Ordinary Ethernet Wi-Fi and unrelated virtualization adapters cannot create a VPN key", ExcludedAdapters),
        ("All confirmed VPN names are retained with stable duplicate-free output", MultipleNames),
        ("Unknown native evidence hides the key while independent connected RAS proof remains usable", IncompleteEvidence),
        ("Connected split-tunnel names remain independent from the existing general-route classification", GeneralRouteIndependence),
        ("The existing observation attaches connected names without repeating native inventory reads", SharedObservation),
        ("Route-only native notifications hide stale connected names without waiting for adapter down state", RouteOnlyDisconnect),
        ("A pre-suspend blocked route observation cannot restore stale connected names after resume", StaleRouteNotification),
        ("The assigned-address native row matches the Windows 80-byte unicast address layout", NativeAddressLayout)
    ];

    private static void RasConnected()
    {
        var inventory = new VpnInterfaceInventory([], [Ras("Owned RAS VPN")]);
        Names(["Owned RAS VPN"], Classify(inventory), "A connected RAS VPN does not require route ownership or probes.");
    }

    private static void RasInactive()
    {
        foreach (var connection in new[]
        {
            Ras("Disconnected") with { ConnectionState = 0 },
            Ras("Error") with { ErrorCode = 691 },
            Ras("Dormant") with { ConnectionSubstate = 1 },
            Ras("Dial-up") with { DeviceType = "modem" }
        }) Names([], Classify(new([], [connection])), "Unsupported RAS evidence must remain hidden.");
        Names(["Reconnected"], Classify(new([], [Ras("Reconnected") with { ConnectionSubstate = 0x2000 }])),
            "A confirmed RAS reconnected substate remains connected.");
    }

    private static void PrivateSplit()
    {
        foreach (var driver in new[] { "wintun", "wireguard", "tap0901", "ovpn-dco" })
        {
            var tunnel = Tunnel() with { DriverService = driver };
            Names([tunnel.InterfaceName], Classify(new([tunnel], []), V4(Route(tunnel))),
                "A known tunnel's private split route counts for " + driver);
        }
    }

    private static void PhysicalDefault()
    {
        var tunnel = Tunnel();
        var physical = tunnel with { InterfaceIndex = 4, InterfaceGuid = OtherId, InterfaceName = "Owned Wi-Fi",
            InterfaceType = 71, TunnelType = 0, DriverService = null, StatusFlags = 1,
            AssignedAddresses = [IPAddress.Parse("192.0.2.20")] };
        var physicalDefault = Route(physical) with { PrefixAddress = IPAddress.Any, PrefixLength = 0,
            NextHop = IPAddress.Parse("192.0.2.1"), RouteMetric = 1 };
        Names([tunnel.InterfaceName], Classify(new([physical, tunnel], []),
            V4(physicalDefault, Route(tunnel) with { RouteMetric = 999 })),
            "A cheaper physical default cannot hide a connected private split tunnel.");
    }

    private static void AdapterRequirements()
    {
        var tunnel = Tunnel();
        foreach (var item in new[]
        {
            tunnel with { OperationalStatus = OperationalStatus.Down },
            tunnel with { OperationalStatus = OperationalStatus.Dormant },
            tunnel with { MediaConnectState = 2 },
            tunnel with { AssignedAddresses = [] },
            tunnel with { AssignedAddresses = [IPAddress.Any] },
            tunnel with { AssignedAddresses = [IPAddress.Loopback] },
            tunnel with { AssignedAddresses = [IPAddress.Parse("169.254.2.3")] }
        }) Names([], Classify(new([item], []), V4(Route(item))), "An installed adapter alone cannot imply a connection.");
        Names([], Classify(new([tunnel], []), V4()), "An operational assigned adapter still requires an associated route.");
        Names([], Classify(new([tunnel], []), V4(Route(tunnel) with { InterfaceIndex = 99 })),
            "A different interface's route cannot activate the installed tunnel.");
    }

    private static void Ipv6Split()
    {
        var tunnel = Tunnel() with { AssignedAddresses = [IPAddress.Parse("fd42::2")] };
        var route = Route(tunnel) with { PrefixAddress = IPAddress.Parse("fd43::"), PrefixLength = 64,
            NextHop = IPAddress.Parse("fd42::1") };
        Names([tunnel.InterfaceName], Classify(new([tunnel], []), V4(),
            new(AddressFamily.InterNetworkV6, [route], Availability: IpFamilyAvailability.Usable)),
            "IPv6 split routing does not require IPv4 addresses or routes.");
        Names([tunnel.InterfaceName], Classify(new([tunnel], []), V4(),
            new(AddressFamily.InterNetworkV6, [route with { NextHop = IPAddress.Parse("fe80::1") }], Availability: IpFamilyAvailability.Usable)),
            "An IPv6 route's legitimate link-local next hop is separate from the tunnel's usable assigned address.");
        Names([], Classify(new([tunnel], []), V4(Route(tunnel))),
            "An assigned IPv6 address cannot confirm a same-interface IPv4 route.");
    }

    private static void AlwaysConnectedTap()
    {
        var tunnel = Tunnel() with { DriverService = "tap0901", TapMediaAlwaysConnected = true };
        var ownSubnet = Route(tunnel) with { PrefixAddress = IPAddress.Parse("10.40.0.0"), PrefixLength = 24, NextHop = IPAddress.Any };
        Names([], Classify(new([tunnel], []), V4(ownSubnet)),
            "A permanently-up TAP with only its own on-link subnet is still an installed adapter.");
        Names([tunnel.InterfaceName], Classify(new([tunnel], []), V4(ownSubnet, Route(tunnel))),
            "An Always Connected TAP with a separate active split route has actual forwarding evidence.");
        Names([tunnel.InterfaceName], Classify(new([tunnel], []), V4(ownSubnet,
            ownSubnet with { PrefixAddress = IPAddress.Any, PrefixLength = 0 })),
            "An Always Connected TAP with an enabled default route is connected.");
    }

    private static void InvalidRoutes()
    {
        var tunnel = Tunnel();
        foreach (var route in new[]
        {
            Route(tunnel) with { ValidLifetime = 0 },
            Route(tunnel) with { InterfaceUsable = false },
            Route(tunnel) with { InterfaceUsable = null },
            Route(tunnel) with { IsLoopback = true },
            Route(tunnel) with { InterfaceGuid = OtherId },
            Route(tunnel) with { InterfaceGuid = null },
            Route(tunnel) with { PrefixAddress = IPAddress.Loopback, PrefixLength = 32 },
            Route(tunnel) with { PrefixAddress = IPAddress.Parse("224.0.0.0"), PrefixLength = 4 }
        }) Names([], Classify(new([tunnel], []), V4(route)), "Invalid or stale associated routes cannot confirm VPN connectivity.");
    }

    private static void ExcludedAdapters()
    {
        var tunnel = Tunnel();
        foreach (var (name, driver) in new[]
        {
            ("Owned Hyper-V VPN", "vms_mp"), ("Owned VMware VPN", "vmnetadapter"),
            ("Owned VirtualBox VPN", "vboxnetadp"), ("Owned Docker VPN", "vms_mp"), ("Owned WSL VPN", "ms_vms_mp"),
            ("Owned Ethernet VPN", "e1dexpress"), ("Owned Wi-Fi VPN", "netwtw14")
        })
        {
            var item = tunnel with { InterfaceName = name, Description = name, InterfaceType = name.Contains("Wi-Fi", StringComparison.Ordinal) ? 71u : 6u,
                TunnelType = 0, DriverService = driver, StatusFlags = name.Contains("Ethernet", StringComparison.Ordinal) ||
                    name.Contains("Wi-Fi", StringComparison.Ordinal) ? (byte)1 : (byte)0 };
            Names([], Classify(new([item], []), V4(Route(item))), "Display names must not promote unrelated virtual or physical adapters.");
        }
    }

    private static void MultipleNames()
    {
        var first = Tunnel();
        var second = Tunnel() with { InterfaceIndex = 42, InterfaceGuid = OtherId, InterfaceName = "Owned second VPN" };
        var inventory = new VpnInterfaceInventory([second, first, first], [Ras("Owned RAS VPN"), Ras("Owned RAS VPN")]);
        var names = Classify(inventory, V4(Route(first), Route(second)));
        Require(names.Count == 3 && names.Contains(first.InterfaceName) && names.Contains(second.InterfaceName) &&
            names.Contains("Owned RAS VPN"), "The connected list lost a confirmed interface name or retained duplicates.");
        var reordered = Classify(new([first, second], [Ras("Owned RAS VPN")]), V4(Route(second), Route(first)));
        Require(names.SequenceEqual(reordered), "Reordering identical native evidence changed the connected tooltip order.");
    }

    private static void IncompleteEvidence()
    {
        var tunnel = Tunnel();
        Names([], Classify(new([tunnel], [], false, "Owned native inventory unavailable."), V4() with { IsComplete = false }),
            "Incomplete evidence without active proof must keep the key hidden.");
        Names(["Owned confirmed RAS"], Classify(new([], [Ras("Owned confirmed RAS")], false),
            V4() with { IsComplete = false }), "Confirmed RAS proof is independent of unrelated missing adapter or route data.");
    }

    private static void GeneralRouteIndependence()
    {
        var tunnel = Tunnel();
        var physical = tunnel with { InterfaceIndex = 4, InterfaceGuid = OtherId, InterfaceName = "Owned Wi-Fi",
            InterfaceType = 71, TunnelType = 0, DriverService = null, StatusFlags = 1 };
        var inventory = new VpnInterfaceInventory([physical, tunnel], []);
        var routes = V4(Route(physical) with { PrefixAddress = IPAddress.Any, PrefixLength = 0 }, Route(tunnel));
        var snapshot = VpnRouteClassifier.Classify(inventory, routes, EmptyV6, DateTimeOffset.UtcNow);
        var connected = Classify(inventory, routes);
        Require(snapshot.Status == VpnTunnelStatus.Inactive && snapshot.RouteKind == InternetRouteKind.Direct &&
            connected.Contains(tunnel.InterfaceName),
            "A connected private split tunnel changed the established general-route semantics or lost the key evidence.");
    }

    private static void SharedObservation()
    {
        var interfaces = new InterfaceSource(new([], [Ras("Owned observed VPN")]));
        var routes = new RouteSource();
        var observation = new VpnRouteVerifier(new VpnDetectionService(interfaces), routes).GetCurrentObservation(DateTimeOffset.UtcNow);
        Require(interfaces.Reads == 1 && routes.Reads == 2 && observation.Snapshot.HasActiveTunnel &&
            observation.Snapshot.ConnectedTunnelNames.SequenceEqual(new[] { "Owned observed VPN" }),
            "The connected key repeated native queries instead of sharing the existing route observation.");
    }

    private static void RouteOnlyDisconnect()
    {
        using var fixture = new ObserverFixture();
        Wait(() => fixture.Observer.LatestRouteSnapshot.HasActiveTunnel, "initial connected split route");
        var timer = PrivateField<object>(fixture.Observer, "_timer");
        var checkedAt = fixture.Observer.LatestRouteSnapshot.CheckedAtUtc;
        var originalAddress = fixture.Interface.AssignedAddresses.Single();
        fixture.Routes.IPv4 = V4();
        fixture.Notifications.Raise();
        Wait(() => fixture.Observer.LatestRouteSnapshot.CheckedAtUtc > checkedAt &&
            !fixture.Observer.LatestRouteSnapshot.HasActiveTunnel, "route-only disconnect refresh");
        Require(fixture.Interface.OperationalStatus == OperationalStatus.Up &&
            fixture.Interface.AssignedAddresses.Single().Equals(originalAddress) &&
            fixture.Observer.LatestRouteSnapshot.ConnectedTunnelNames.Count == 0 &&
            ReferenceEquals(timer, PrivateField<object>(fixture.Observer, "_timer")) && fixture.Notifications.Starts == 1,
            "Removing only the active route did not clear the key through the existing observer/timer.");
        fixture.Observer.Dispose();
        var reads = fixture.Routes.Reads;
        fixture.Notifications.Raise();
        Require(fixture.Notifications.Disposed && fixture.Notifications.Subscribers == 0 && fixture.Routes.Reads == reads,
            "Disposal left route notifications subscribed or permitted stale refresh queries.");
    }

    private static void StaleRouteNotification()
    {
        using var fixture = new ObserverFixture();
        Wait(() => fixture.Observer.LatestRouteSnapshot.HasActiveTunnel, "initial connected route before suspend");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fixture.Routes.BlockNext(entered, release);
        fixture.Notifications.Raise();
        Require(entered.Wait(TimeSpan.FromSeconds(5)), "The owned route-only observation did not start.");
        try
        {
            fixture.Observer.Suspend();
            fixture.Routes.IPv4 = V4();
            fixture.Observer.Resume();
        }
        finally { release.Set(); }
        Wait(() => fixture.Observer.LatestRouteSnapshot.CheckedAtUtc is not null &&
            !fixture.Observer.LatestRouteSnapshot.HasActiveTunnel, "fresh post-resume route-only observation");
        Require(fixture.Observer.LatestRouteSnapshot.ConnectedTunnelNames.Count == 0,
            "Blocked pre-suspend connected evidence replaced the fresh disconnected result.");
    }

    private static void NativeAddressLayout()
    {
        var row = typeof(WindowsVpnNative).GetNestedType("MibUnicastIpAddressRow", BindingFlags.NonPublic)!;
        Require(row is not null && Marshal.SizeOf(row) == 80 &&
            Marshal.OffsetOf(row, "InterfaceLuid").ToInt32() == 32 &&
            Marshal.OffsetOf(row, "InterfaceIndex").ToInt32() == 40 &&
            Marshal.OffsetOf(row, "ValidLifetime").ToInt32() == 52 &&
            Marshal.OffsetOf(row, "DadState").ToInt32() == 64,
            "The native assigned-address reader no longer matches Windows MIB_UNICASTIPADDRESS_ROW layout.");
    }

    private static VpnRasConnectionEvidence Ras(string name) => new(name, "Owned VPN device", "vpn", 0x2000);
    private static VpnInterfaceEvidence Tunnel() => new()
    {
        InterfaceIndex = 41, InterfaceGuid = TunnelId, InterfaceName = "Owned split VPN", Description = "Owned known tunnel",
        InterfaceType = 131, TunnelType = 1, OperationalStatus = OperationalStatus.Up, MediaConnectState = 1,
        DriverService = "wintun", AssignedAddresses = [IPAddress.Parse("10.40.0.2")]
    };
    private static VpnRouteEvidence Route(VpnInterfaceEvidence item) => new(item.InterfaceIndex, item.InterfaceGuid,
        IPAddress.Parse("10.80.0.0"), 16, IPAddress.Parse("10.40.0.1"), 1, 1, true);
    private static VpnRouteInventory V4(params VpnRouteEvidence[] routes) =>
        new(AddressFamily.InterNetwork, routes, Availability: IpFamilyAvailability.Usable);
    private static VpnRouteInventory EmptyV6 => new(AddressFamily.InterNetworkV6, [], Availability: IpFamilyAvailability.Unavailable);
    private static IReadOnlyList<string> Classify(VpnInterfaceInventory inventory, VpnRouteInventory? ipv4 = null,
        VpnRouteInventory? ipv6 = null) => VpnConnectedTunnelClassifier.Classify(inventory, ipv4 ?? V4(), ipv6 ?? EmptyV6);
    private static void Names(string[] expected, IReadOnlyList<string> actual, string description) =>
        Require(expected.SequenceEqual(actual), description + " Expected [" + string.Join(", ", expected) + "]; actual [" + string.Join(", ", actual) + "].");
    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }

    private static T PrivateField<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Wait(Func<bool> ready, string description)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(description);
            Thread.Sleep(10);
        }
    }
    private sealed class InterfaceSource(VpnInterfaceInventory inventory) : IVpnInterfaceSource
    {
        public int Reads { get; private set; }
        public VpnInterfaceInventory Read() { Reads++; return inventory; }
    }
    private sealed class RouteSource : IVpnRouteSource
    {
        public int Reads { get; private set; }
        public VpnRouteInventory Read(AddressFamily family)
        {
            Reads++;
            return new(family, [], Availability: IpFamilyAvailability.Unavailable);
        }
    }

    private sealed class ObserverFixture : IDisposable
    {
        private readonly TestWorkspace _workspace = new();
        public VpnInterfaceEvidence Interface { get; } = Tunnel();
        public MutableRoutes Routes { get; } = new();
        public RouteNotifications Notifications { get; } = new();
        public VpnSessionMonitor Observer { get; }
        public ObserverFixture()
        {
            var store = new UsageStore(_workspace.Root);
            var physical = new ConnectionSessionTracker(new ConnectionHistoryStore(store));
            Routes.IPv4 = V4(Route(Interface));
            Observer = new VpnSessionMonitor(physical, new VpnSessionStore(store),
                new VpnRouteVerifier(new VpnDetectionService(new InterfaceSource(new([Interface], []))), Routes),
                observeRoutesWithoutPhysicalSession: true, routeChanges: Notifications);
            Observer.Start();
        }
        public void Dispose() { try { Observer.Dispose(); } finally { _workspace.Dispose(); } }
    }
    private sealed class MutableRoutes : IVpnRouteSource
    {
        private VpnRouteInventory _ipv4 = V4();
        private int _reads;
        private int _blockNext;
        private ManualResetEventSlim? _entered;
        private ManualResetEventSlim? _release;
        public int Reads => Volatile.Read(ref _reads);
        public VpnRouteInventory IPv4 { get => Volatile.Read(ref _ipv4); set => Volatile.Write(ref _ipv4, value); }
        public void BlockNext(ManualResetEventSlim entered, ManualResetEventSlim release)
        {
            _entered = entered;
            _release = release;
            Interlocked.Exchange(ref _blockNext, 1);
        }
        public VpnRouteInventory Read(AddressFamily family)
        {
            Interlocked.Increment(ref _reads);
            var snapshot = family == AddressFamily.InterNetwork ? IPv4 : EmptyV6;
            if (family == AddressFamily.InterNetwork && Interlocked.Exchange(ref _blockNext, 0) == 1)
            {
                _entered!.Set();
                if (!_release!.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Owned blocked route source was not released.");
            }
            return snapshot;
        }
    }
    private sealed class RouteNotifications : IVpnRouteChangeSource
    {
        private EventHandler? _changed;
        public event EventHandler? Changed { add { _changed += value; } remove { _changed -= value; } }
        public int Subscribers => _changed?.GetInvocationList().Length ?? 0;
        public int Starts { get; private set; }
        public bool Disposed { get; private set; }
        public void Start() { Starts++; }
        public void Raise() => _changed?.Invoke(this, EventArgs.Empty);
        public void Dispose() { Disposed = true; }
    }
}
