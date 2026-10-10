using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using NetTrace.Models;
using NetTrace.Presentation;
using NetTrace.Services;

internal static class ConnectionDetailsTests
{
    public static (string Name, Action Run)[] Cases =>
    [
        ("Connected monitored adapter renders its mapped identity and live configuration", Connected),
        ("Missing adapter IPv6 leaves all other connection details valid", MissingIpv6),
        ("Missing adapter gateway is consistently unavailable", MissingGateway),
        ("Missing configured DNS is consistently unavailable", MissingDns),
        ("Disconnected adapter clears stale addresses, mapping and link rate", Disconnected),
        ("Non-connected adapter operational states use readable disconnected status", DisconnectedStatuses),
        ("Unknown and unexpected adapter statuses do not claim connected data", UnknownStatuses),
        ("Missing or wrong-adapter details cannot expose stale selected data", MissingOrMismatchedDetails),
        ("Equivalent adapter IDs preserve existing case-insensitive identity semantics", EquivalentAdapterIds),
        ("No selected adapter has consistent unavailable fields", NoAdapter),
        ("Multiple usable IPv4 addresses are selected deterministically", Ipv4Selection),
        ("Unicast selection rejects unspecified, loopback, multicast and broadcast", InvalidUnicast),
        ("Usable IPv6 is deterministic and preferred over link-local addresses", Ipv6Selection),
        ("Unique-local IPv6 is usable while link-local-only IPv6 is unavailable", LocalIpv6),
        ("IPv6 display removes unnecessary scope IDs", ScopedIpv6),
        ("Gateway selection follows the usable address family and falls back compactly", GatewaySelection),
        ("Gateway presentation rejects invalid configuration addresses", InvalidGateways),
        ("Many configured DNS addresses have compact deterministic ordering", CompactDns),
        ("Configured local DNS resolvers remain visible", LocalDns),
        ("DNS removes invalid values and duplicate scope-free addresses", DnsFiltering),
        ("An unmapped connected network remains unassigned without creating profiles", UnassignedProfile),
        ("Another adapter's network and profile cannot appear on the selected adapter", WrongNetworkAdapter),
        ("Detecting, disconnected and invalid identity states cannot expose stale mappings", InvalidMappingStates),
        ("An empty network display name is unavailable", EmptyNetworkName),
        ("Switching adapter presentations refreshes every field without carrying stale values", PresentationSwitch),
        ("Local link rates retain Mbps and readable Gbps formatting", LinkRates),
        ("Absent or invalid local link rates use unavailable", InvalidLinkRates),
        ("Local link-rate presentation remains readable across display cultures", RateCulture),
        ("Formatting connection details does not mutate or retain changing input state", ImmutablePresentation),
        ("Older network-monitor fakes remain compatible with unavailable details", DefaultMonitorDetails),
        ("Monitoring snapshots read cached details without repeating network discovery", CachedSnapshot),
        ("Same-identity network observations refresh IP and DNS without resetting sampling", SameIdentityRefresh),
        ("Adapter selection refreshes its details while retaining unrelated connection totals", ServiceAdapterSwitch),
        ("A failed detail refresh clears the cache and later observations recover", FailedDetailRefresh),
    ];

    private static readonly AdapterInfo Wifi =
        new("wifi-a", "Wi-Fi", "Fixture wireless adapter", "Wireless80211", 86_000_000, "stale IPv4", true);
    private static readonly NetworkIdentity Network = new("WiFi", "OPPO A3x", "OPPO A3x", Wifi.Id);
    private static readonly DateTimeOffset Origin = new(2026, 10, 11, 5, 0, 0, TimeSpan.Zero);
    private static readonly ConnectionProfile Profile = new(1, "DITO", "OPPO A3x", Origin, Origin);
    private static CurrentConnectionState Mapped => new(ConnectionStatus.ConnectedRecognized, Network, Profile, null);

    private static NetworkAdapterDetails Details() => new(Wifi.Id, OperationalStatus.Up,
        Addresses("10.225.249.66", "2405:1234:5678::2"), Addresses("10.225.249.1"),
        Addresses("8.8.8.8", "1.1.1.1"), 86_000_000);

    private static AdapterDetailsPresentation Present(NetworkAdapterDetails? details = null) =>
        AdapterDetailsPresentation.Create(Wifi, details ?? Details(), Mapped);

    private static IPAddress[] Addresses(params string[] values) => values.Select(IPAddress.Parse).ToArray();

    private static void Connected()
    {
        var value = Present();
        Equal("Wi-Fi", value.Adapter, "adapter");
        Equal("Connected", value.Status, "status");
        Equal("OPPO A3x", value.Network, "network identity");
        Equal(Profile.DisplayName, value.Profile, "existing mapped profile");
        Equal("Wi-Fi", value.Type, "friendly type");
        Equal("10.225.249.66", value.IPv4, "selected IPv4");
        Equal("2405:1234:5678::2", value.IPv6, "usable IPv6");
        Equal("10.225.249.1", value.DefaultGateway, "gateway");
        Equal("1.1.1.1, 8.8.8.8", value.Dns, "actual configured DNS");
        Equal("Wi-Fi link rate", value.LinkRateLabel, "wireless link label");
        Equal("86 Mbps", value.LinkRate, "existing local NIC rate");
        Check(value.LinkRateToolTip.Contains("local Wi-Fi link", StringComparison.Ordinal), "Link rate explains local Wi-Fi semantics.");
    }

    private static void MissingIpv6()
    {
        var value = Present(Details() with { UnicastAddresses = Addresses("10.225.249.66") });
        Equal("Unavailable", value.IPv6, "missing IPv6");
        Equal(Present() with { IPv6 = "Unavailable" }, value, "missing IPv6 does not erase unrelated values");
    }

    private static void MissingGateway() => Equal("Unavailable",
        Present(Details() with { GatewayAddresses = [] }).DefaultGateway, "missing gateway");

    private static void MissingDns() => Equal("Unavailable",
        Present(Details() with { DnsAddresses = [] }).Dns, "missing DNS");

    private static void Disconnected()
    {
        var value = Present(Details() with { Status = OperationalStatus.Down });
        Equal("Disconnected", value.Status, "down adapter");
        AssertUnavailableConfiguration(value);
    }

    private static void DisconnectedStatuses()
    {
        foreach (var status in new[] { OperationalStatus.Testing, OperationalStatus.Dormant,
            OperationalStatus.NotPresent, OperationalStatus.LowerLayerDown })
        {
            var value = Present(Details() with { Status = status });
            Equal("Disconnected", value.Status, "safe user-facing status for " + status);
            AssertUnavailableConfiguration(value);
        }
    }

    private static void UnknownStatuses()
    {
        foreach (var status in new[] { OperationalStatus.Unknown, (OperationalStatus)999 })
        {
            var value = Present(Details() with { Status = status });
            Equal("Unknown", value.Status, "unsupported adapter status");
            AssertUnavailableConfiguration(value);
        }
    }

    private static void MissingOrMismatchedDetails()
    {
        foreach (var details in new NetworkAdapterDetails?[] { null, Details() with { AdapterId = "another-adapter" } })
        {
            var value = AdapterDetailsPresentation.Create(Wifi, details, Mapped);
            Equal("Unknown", value.Status, "no fresh matching details");
            Equal("Wi-Fi", value.Adapter, "selected adapter metadata is retained");
            AssertUnavailableConfiguration(value);
        }
    }

    private static void NoAdapter()
    {
        var value = AdapterDetailsPresentation.Create(null, Details(), Mapped);
        Equal("Unavailable", value.Adapter, "missing adapter");
        Equal("Unavailable", value.Type, "missing type");
        Equal("Unknown", value.Status, "missing status");
        AssertUnavailableConfiguration(value);
    }

    private static void EquivalentAdapterIds() => Equal(Present(),
        AdapterDetailsPresentation.Create(Wifi, Details() with { AdapterId = Wifi.Id.ToUpperInvariant() },
            Mapped with { Network = Network with { AdapterId = Wifi.Id.ToUpperInvariant() } }),
        "adapter identifier capitalization does not create mismatched stale data");

    private static void Ipv4Selection()
    {
        var details = Details() with { UnicastAddresses = Addresses("192.168.1.20", "10.1.2.30", "10.1.2.3") };
        Equal("10.1.2.3", Present(details).IPv4, "numeric-byte ordering rather than textual ordering");
        Equal(Present(details), Present(details with { UnicastAddresses = details.UnicastAddresses.Reverse().ToArray() }), "address order independent of enumeration");
    }

    private static void InvalidUnicast()
    {
        var value = Present(Details() with { UnicastAddresses = Addresses("0.0.0.0", "127.0.0.1", "224.0.0.1",
            "255.255.255.255", "::", "::1", "ff02::1", "::ffff:192.0.2.1") });
        Equal("Unavailable", value.IPv4, "invalid IPv4 unicast");
        Equal("Unavailable", value.IPv6, "invalid IPv6 unicast");
    }

    private static void Ipv6Selection()
    {
        var details = Details() with { UnicastAddresses = Addresses("fe80::1%8", "2001:db8::20", "2001:db8::3") };
        Equal("2001:db8::3", Present(details).IPv6, "usable global IPv6 precedes link-local and sorts by bytes");
        Equal(Present(details), Present(details with { UnicastAddresses = details.UnicastAddresses.Reverse().ToArray() }), "IPv6 selection is deterministic");
    }

    private static void LocalIpv6()
    {
        Equal("fd12:3456::2", Present(Details() with { UnicastAddresses = Addresses("fe80::1%4", "fd12:3456::2") }).IPv6, "unique-local unicast is usable");
        Equal("Unavailable", Present(Details() with { UnicastAddresses = Addresses("fe80::1%4") }).IPv6, "documented link-local-only policy");
    }

    private static void ScopedIpv6() => Equal("2001:db8::2",
        Present(Details() with { UnicastAddresses = Addresses("2001:db8::2%13") }).IPv6, "scope-free IPv6 display");

    private static void GatewaySelection()
    {
        var details = Details() with { GatewayAddresses = Addresses("fe80::1%12", "10.225.249.1") };
        Equal("10.225.249.1", Present(details).DefaultGateway, "IPv4 gateway matches selected IPv4");
        Equal("fe80::1", Present(details with { UnicastAddresses = Addresses("2001:db8::2") }).DefaultGateway, "IPv6-only gateway strips its scope");
        Equal("fe80::1", Present(details with { GatewayAddresses = Addresses("fe80::1%12") }).DefaultGateway, "usable opposite-family fallback");
    }

    private static void InvalidGateways() => Equal("Unavailable",
        Present(Details() with { GatewayAddresses = Addresses("0.0.0.0", "127.0.0.1", "255.255.255.255", "224.0.0.1", "::", "::1", "ff02::1") }).DefaultGateway,
        "invalid gateways cannot look valid");

    private static void CompactDns()
    {
        var details = Details() with { DnsAddresses = Addresses("2001:4860:4860::8888", "9.9.9.9", "8.8.8.8", "1.1.1.1", "8.8.8.8") };
        Equal("1.1.1.1, 8.8.8.8 (+2 more)", Present(details).Dns, "two configured servers with distinct remaining count");
        Equal(Present(details).Dns, Present(details with { DnsAddresses = details.DnsAddresses.Reverse().ToArray() }).Dns, "DNS ordering is stable");
    }

    private static void LocalDns() => Equal("127.0.0.1, ::1",
        Present(Details() with { DnsAddresses = Addresses("::1", "127.0.0.1") }).Dns, "actual configured local DNS resolvers");

    private static void DnsFiltering() => Equal("1.1.1.1, fe80::53",
        Present(Details() with { DnsAddresses = Addresses("0.0.0.0", "::", "224.0.0.1", "255.255.255.255", "ff02::1", "fe80::53%7", "fe80::53%3", "1.1.1.1") }).Dns,
        "invalid DNS removed and equivalent scope-free displays deduplicated");

    private static void UnassignedProfile()
    {
        var value = AdapterDetailsPresentation.Create(Wifi, Details(), Mapped with { Status = ConnectionStatus.ConnectedUnassigned, Profile = null });
        Equal("Unassigned", value.Profile, "unmapped profile terminology");
        Equal("OPPO A3x", value.Network, "valid unmapped identity remains visible");
    }

    private static void WrongNetworkAdapter()
    {
        var value = AdapterDetailsPresentation.Create(Wifi, Details(), Mapped with { Network = Network with { AdapterId = "ethernet-b" } });
        Equal("Unavailable", value.Network, "another adapter's physical identity");
        Equal("Unassigned", value.Profile, "another adapter's associated profile");
        Equal("10.225.249.66", value.IPv4, "selected adapter's actual configuration is independent");
    }

    private static void InvalidMappingStates()
    {
        foreach (var status in new[] { ConnectionStatus.Detecting, ConnectionStatus.Disconnected,
            ConnectionStatus.ConnectedIdentityUnavailable, (ConnectionStatus)999 })
        {
            var value = AdapterDetailsPresentation.Create(Wifi, Details(), Mapped with { Status = status });
            Equal("Unavailable", value.Network, "state cannot establish valid network identity");
            Equal("Unassigned", value.Profile, "state cannot establish profile mapping");
        }
        var invalidIdentity = AdapterDetailsPresentation.Create(Wifi, Details(), Mapped with { Network = Network with { NetworkIdentifier = "" } });
        Equal("Unavailable", invalidIdentity.Network, "invalid identity key");
        Equal("Unassigned", invalidIdentity.Profile, "invalid identity cannot expose mapped profile");
    }

    private static void EmptyNetworkName() => Equal("Unavailable",
        AdapterDetailsPresentation.Create(Wifi, Details(), Mapped with { Network = Network with { FriendlyName = " " } }).Network,
        "blank friendly network name");

    private static void PresentationSwitch()
    {
        var other = new AdapterInfo("ethernet-b", "Ethernet", "Second fixture adapter", "Ethernet", 1_000_000_000, "stale", true);
        var details = new NetworkAdapterDetails(other.Id, OperationalStatus.Up, Addresses("192.0.2.2"), [], [], 1_000_000_000);
        var first = Present();
        var second = AdapterDetailsPresentation.Create(other, details, Mapped);
        Equal("Ethernet", second.Adapter, "new selected name");
        Equal("Ethernet", second.Type, "new selected type");
        Equal("Connected", second.Status, "new selected status");
        Equal("192.0.2.2", second.IPv4, "new selected IPv4");
        Equal("Unavailable", second.IPv6, "old IPv6 is cleared");
        Equal("Unavailable", second.DefaultGateway, "old gateway is cleared");
        Equal("Unavailable", second.Dns, "old DNS is cleared");
        Equal("Unavailable", second.Network, "old network is cleared");
        Equal("Unassigned", second.Profile, "old profile is cleared");
        Equal("Ethernet link rate", second.LinkRateLabel, "non-Wi-Fi label");
        Equal("1 Gbps", second.LinkRate, "new selected rate");
        Equal(first, Present(), "returning to original adapter restores current details");
    }

    private static void LinkRates()
    {
        foreach (var (rate, expected) in new[] { (86_000_000L, "86 Mbps"), (100_000_000L, "100 Mbps"),
            (1_000_000_000L, "1 Gbps"), (2_500_000_000L, "2.5 Gbps") })
            Equal(expected, Present(Details() with { SpeedBitsPerSecond = rate }).LinkRate, "readable local link rate");
        var ethernet = AdapterDetailsPresentation.Create(Wifi with { Type = "Ethernet" }, Details(), Mapped);
        Equal("Ethernet link rate", ethernet.LinkRateLabel, "wired label does not falsely claim Wi-Fi");
    }

    private static void InvalidLinkRates()
    {
        foreach (var rate in new[] { 0L, -1L })
            Equal("Unavailable", Present(Details() with { SpeedBitsPerSecond = rate }).LinkRate, "invalid rate");
        Equal("<0.01 Mbps", Present(Details() with { SpeedBitsPerSecond = 1 }).LinkRate, "a tiny positive rate does not round to valid zero");
    }

    private static void RateCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Equal("2.5 Gbps", Present(Details() with { SpeedBitsPerSecond = 2_500_000_000 }).LinkRate, "stable decimal display");
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    private static void ImmutablePresentation()
    {
        var addresses = Addresses("192.0.2.2", "2001:db8::2");
        var details = Details() with { UnicastAddresses = addresses };
        var before = addresses.ToArray();
        var value = Present(details);
        Check(addresses.SequenceEqual(before), "Formatting leaves input address order unchanged.");
        addresses[0] = IPAddress.Parse("192.0.2.3");
        Equal("192.0.2.2", value.IPv4, "presentation stores display values rather than mutable raw addresses");
        Equal("192.0.2.3", Present(details).IPv4, "later observations produce fresh display values");
    }

    private static void DefaultMonitorDetails()
    {
        INetworkMonitor monitor = new FakeBandwidthMonitor();
        Check(monitor.GetAdapterDetails(Wifi.Id) is null, "Existing bandwidth-only fakes have safe unavailable details.");
    }

    private static void CachedSnapshot()
    {
        using var test = new DetailsMonitoringContext(start: false);
        var reads = test.Monitor.Reads;
        var original = test.Service.GetSnapshot().SelectedAdapterDetails;
        for (var index = 0; index < 100; index++)
            Equal(original, test.Service.GetSnapshot().SelectedAdapterDetails, "latest cache is immediately available to page navigation");
        Equal(reads, test.Monitor.Reads, "snapshot reads cannot trigger extra network discovery");
    }

    private static void SameIdentityRefresh()
    {
        using var test = new DetailsMonitoringContext();
        var initial = test.Service.GetSnapshot();
        test.Sample(10_100, 1010);
        test.Monitor.SetDetails(Details() with { UnicastAddresses = Addresses("10.225.249.77"), DnsAddresses = Addresses("9.9.9.9") });
        test.Identity.RaiseNetworkChanged();
        WaitUntil(() => test.Service.GetSnapshot().SelectedAdapterDetails?.DnsAddresses is { Count: 1 } dns &&
            dns[0].ToString() == "9.9.9.9", "unchanged identity refresh updates cached configuration");
        var current = test.Service.GetSnapshot();
        Equal(initial.Session!.Id, current.Session!.Id, "configuration change does not create a new physical interval");
        Equal("10.225.249.77", AdapterDetailsPresentation.Create(current.SelectedAdapter, current.SelectedAdapterDetails, current.Connection).IPv4, "latest IP rendered");
        test.Sample(10_300, 1030);
        Equal(300L, test.Service.GetSnapshot().SessionDownloaded, "unchanged identity preserves the existing sampler baseline");
        Equal(1, test.Service.History.GetRecentSessions().Count, "no duplicated session");
    }

    private static void ServiceAdapterSwitch()
    {
        using var test = new DetailsMonitoringContext();
        test.Sample(10_100, 1010);
        var before = test.Service.GetSnapshot();
        test.Service.SelectAdapter(test.Other.Id);
        var after = test.Service.GetSnapshot();
        Equal(test.Other.Id, after.SelectedAdapterDetails!.AdapterId, "selection reads the newly selected adapter");
        Equal(before.Session!.Id, after.Session!.Id, "selection leaves the physical profile/session intact");
        Equal(before.SessionDownloaded, after.SessionDownloaded, "selection retains unrelated application download totals");
        var display = AdapterDetailsPresentation.Create(after.SelectedAdapter, after.SelectedAdapterDetails, after.Connection);
        Equal("192.0.2.2", display.IPv4, "new adapter configuration is immediately available");
        Equal("Unavailable", display.IPv6, "old selected IPv6 cleared");
        Equal("Unavailable", display.Dns, "old selected DNS cleared");
        Equal("Unavailable", display.Network, "old adapter mapping cleared");
        Equal("Unassigned", display.Profile, "old adapter profile cleared");
    }

    private static void FailedDetailRefresh()
    {
        using var test = new DetailsMonitoringContext();
        test.Monitor.Fail = true;
        test.Identity.RaiseNetworkChanged();
        WaitUntil(() => test.Service.GetSnapshot().SelectedAdapterDetails is null, "failed detail query clears the previous cache");
        var snapshot = test.Service.GetSnapshot();
        AssertUnavailableConfiguration(AdapterDetailsPresentation.Create(snapshot.SelectedAdapter, snapshot.SelectedAdapterDetails, snapshot.Connection));
        test.Monitor.Fail = false;
        test.Identity.RaiseNetworkChanged();
        WaitUntil(() => test.Service.GetSnapshot().SelectedAdapterDetails is not null, "subsequent ordinary observation recovers details");
    }

    // Explicit read-only opt-in. Ordinary Cases never inspect the actual machine network.
    public static int RunLiveValidation()
    {
        try
        {
            var monitor = new NetworkMonitor();
            var adapter = monitor.GetActiveAdapters().FirstOrDefault();
            if (adapter is null)
            {
                Console.WriteLine("No active selected adapter; connection details are unavailable.");
                return 0;
            }
            var details = monitor.GetAdapterDetails(adapter.Id);
            if (details is null || !string.Equals(details.AdapterId, adapter.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Live details must belong to the selected adapter.");
            using var identity = new NetworkIdentityService();
            var detected = identity.GetCurrentNetwork();
            var state = detected.Availability == NetworkIdentityAvailability.Available && detected.Network is { HasValidIdentifier: true }
                ? new CurrentConnectionState(ConnectionStatus.ConnectedUnassigned, detected.Network, null, detected.Message)
                : new CurrentConnectionState(ConnectionStatus.ConnectedIdentityUnavailable, null, null, detected.Message);
            var display = AdapterDetailsPresentation.Create(adapter, details, state);
            Equal(details.Status == OperationalStatus.Up ? "Connected" : details.Status == OperationalStatus.Unknown ? "Unknown" : "Disconnected",
                display.Status, "live operational status agrees with raw selected data");
            Console.WriteLine($"Adapter={display.Adapter}; Status={display.Status}; Network={display.Network}; Profile={display.Profile}; Type={display.Type}");
            Console.WriteLine($"IPv4={display.IPv4}; IPv6={display.IPv6}; Gateway={display.DefaultGateway}; DNS={display.Dns}; {display.LinkRateLabel}={display.LinkRate}");
            Console.WriteLine("Read-only selected adapter validation passed; no network probes or configuration changes.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Live connection details validation failed: " + exception.Message);
            return 1;
        }
    }

    private static void AssertUnavailableConfiguration(AdapterDetailsPresentation value)
    {
        foreach (var field in new[] { value.IPv4, value.IPv6, value.DefaultGateway, value.Dns, value.LinkRate, value.Network })
            Equal("Unavailable", field, "unavailable connection detail");
        Equal("Unassigned", value.Profile, "unavailable profile association");
    }

    private static void WaitUntil(Func<bool> predicate, string description)
    {
        var elapsed = Stopwatch.StartNew();
        while (!predicate())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(description);
            Thread.Sleep(20);
        }
    }

    private static void Equal<T>(T expected, T actual, string description)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{description}: expected {expected}, got {actual}.");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }

    private sealed class DetailsMonitoringContext : IDisposable
    {
        private readonly TestWorkspace _workspace = new();
        public HeadlessTimeProvider Clock { get; } = new();
        public FakeNetworkIdentityService Identity { get; } = new() { Network = Network };
        public DetailsMonitor Monitor { get; } = new();
        public AdapterInfo Other { get; } = new("ethernet-b", "Ethernet", "Second fixture adapter", "Ethernet", 1_000_000_000, "stale", true);
        public MonitoringService Service { get; }

        public DetailsMonitoringContext(bool start = true)
        {
            Monitor.Adapters = [Wifi, Other];
            Monitor.SetDetails(Details());
            Monitor.SetDetails(new NetworkAdapterDetails(Other.Id, OperationalStatus.Up, Addresses("192.0.2.2"), [], [], 1_000_000_000));
            Monitor.Bandwidth.SetSnapshot(Wifi.Id, new NetworkSnapshot(10_000, 1000, Clock.GetUtcNow()));
            var verifier = new VpnRouteVerifier(new VpnDetectionService(new FakeVpnInterfaceSource()), new FakeVpnRouteSource());
            try
            {
                Service = MonitoringService.CreateForDataRoot(_workspace.Root, Identity, verifier, Monitor, Clock,
                    new IcmpMonitor(new NoInternetPingSource(), timeProvider: Clock),
                    new InternetReachabilityMonitor(new NoInternetReachabilitySource(), timeProvider: Clock));
                if (start)
                {
                    Service.Start();
                    WaitUntil(() => Service.GetSnapshot().Session is not null, "fixture establishes an existing physical connection");
                    Service.SampleNow();
                }
            }
            catch
            {
                Service?.Dispose();
                _workspace.Dispose();
                throw;
            }
        }

        public void Sample(long received, long sent)
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            Monitor.Bandwidth.SetSnapshot(Wifi.Id, new NetworkSnapshot(received, sent, Clock.GetUtcNow()));
            Service.SampleNow();
        }

        public void Dispose()
        {
            try { Service.Dispose(); }
            finally { _workspace.Dispose(); }
        }
    }

    private sealed class DetailsMonitor : INetworkMonitor
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, NetworkAdapterDetails> _details = new(StringComparer.Ordinal);
        private int _reads;
        private int _fail;
        public FakeBandwidthMonitor Bandwidth { get; } = new();
        public IReadOnlyList<AdapterInfo> Adapters { get; set; } = [];
        public int Reads => Volatile.Read(ref _reads);
        public bool Fail { get => Volatile.Read(ref _fail) != 0; set => Volatile.Write(ref _fail, value ? 1 : 0); }
        public IReadOnlyList<AdapterInfo> GetActiveAdapters() => Adapters;
        public NetworkSnapshot? Capture(string adapterId) => Bandwidth.Capture(adapterId);

        public NetworkAdapterDetails? GetAdapterDetails(string adapterId)
        {
            Interlocked.Increment(ref _reads);
            if (Fail) throw new NetworkInformationException();
            lock (_gate) return _details.GetValueOrDefault(adapterId);
        }

        public void SetDetails(NetworkAdapterDetails details)
        {
            lock (_gate) _details[details.AdapterId] = details;
        }
    }
}
