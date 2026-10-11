using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using NetTrace.Models;
using NetTrace.Presentation;
using NetTrace.Services;
using NetTrace.Utils;

internal static class WifiRadioTests
{
    public static (string Name, Action Run)[] Cases =>
    [
        ("Connected selected Wi-Fi renders every available radio measurement", CompleteRadio),
        ("Wi-Fi signal quality preserves a reported zero percent", SignalZero),
        ("Wi-Fi signal quality preserves a reported hundred percent", SignalHundred),
        ("Wi-Fi signal quality preserves a typical reported percentage", SignalTypical),
        ("Wi-Fi signal quality clamps reported values safely at both boundaries", SignalClamp),
        ("Missing Wi-Fi signal quality remains unavailable", SignalMissing),
        ("Wi-Fi RSSI presents the actual native dBm measurement", ActualRssi),
        ("Missing Wi-Fi RSSI is never synthesized from signal percentage", MissingRssi),
        ("Invalid Wi-Fi RSSI does not produce a fabricated measurement", InvalidRssi),
        ("Wi-Fi 2.4 GHz frequency has deterministic compact formatting", Band24),
        ("Wi-Fi 5 GHz frequency has deterministic compact formatting", Band5),
        ("Wi-Fi 6 GHz frequency has deterministic compact formatting", Band6),
        ("An unrecognized Wi-Fi frequency is displayed without an invented band or channel", UnknownFrequency),
        ("Missing Wi-Fi channel and frequency stay unavailable", MissingChannel),
        ("An explicit native Wi-Fi channel takes precedence over derived frequency", NativeChannel),
        ("Connected access-point BSSID normalizes to uppercase colon-separated octets", BssidFormatting),
        ("Missing malformed zero or multicast BSSID stays unavailable", MissingBssid),
        ("Current Wi-Fi PHY names render readable n ac and ax technologies", ModernPhy),
        ("Older Wi-Fi PHY names stay readable while unsupported PHY is unavailable", OlderPhy),
        ("Different negotiated Wi-Fi receive and transmit rates remain independent", DifferentRates),
        ("Missing one or both negotiated Wi-Fi rates never manufactures a rate", MissingRates),
        ("Wi-Fi negotiated rates use Mbps Gbps and invariant decimal formatting", RateFormatting),
        ("Disconnected Wi-Fi clears the radio subsection and its previous values", DisconnectedRadio),
        ("Ethernet and other non-Wi-Fi adapters do not show a radio subsection", NonWifiRadio),
        ("Radio presentation rejects details or evidence from a different adapter", WrongAdapterRadio),
        ("Switching monitored Wi-Fi adapters cannot retain another radio's BSSID", AdapterSwitchRadio),
        ("Case-insensitive matching adapter identities retain the correct radio observation", EquivalentAdapterIdentity),
        ("Absent radio observations do not create an empty Wi-Fi subsection", MissingRadio),
        ("Formatting radio observations is immutable and leaves general link rate available", ImmutableRadio),
        ("Central Wi-Fi conversion maps standard 2.4 GHz centers and channel 14", Frequencies24),
        ("Central Wi-Fi conversion maps representative 5 GHz channels", Frequencies5),
        ("Central Wi-Fi conversion maps valid 6 GHz primary channels", Frequencies6),
        ("Central Wi-Fi conversion rejects unsupported centers and incomplete frequencies", UnsupportedFrequencies),
        ("The presentation-owned Wi-Fi service reads only a requested connected selected adapter", ServiceSelectedAdapter),
        ("Presentation reads reuse a Wi-Fi cache until the lightweight refresh interval expires", ServiceCache),
        ("Concurrent Wi-Fi refresh requests share one in-flight native read", ServiceSingleFlight),
        ("A disconnected Wi-Fi selection invalidates cached radio data without a query", ServiceDisconnect),
        ("Non-Wi-Fi missing or mismatched adapter selections never perform WLAN discovery", ServiceIneligible),
        ("Explicit UI hide or sleep invalidation discards a blocked old Wi-Fi observation", ServiceInvalidate),
        ("Switching Wi-Fi selection while native discovery is blocked rejects the old interface", ServiceSwitch),
        ("Wi-Fi discovery failure clears stale radio data and later refresh recovers", ServiceFailure),
        ("Wi-Fi service disposal rejects late publication and future reads", ServiceDispose),
        ("Wi-Fi radio service has no timer persistence or retained monitoring history", ServiceNoTimerOrHistory),
        ("Native WLAN connection association and cached-entry layouts match Windows SDK", NativeLayout),
        ("Native current association decoder preserves signal access-point PHY and separate kbit rates", NativeConnection),
        ("Current association PHY overrides adapter capability descriptions and remains independent of band and PHY index", NativeAssociationPhy),
        ("Native disconnected short or absent WLAN observations expose no stale radio data", NativeUnavailableConnection),
        ("Native WLAN RSSI decoder reads actual signed dBm and channel decoder rejects invalid values", NativeScalars),
        ("Native cached frequency conversion preserves exact MHz without rounding incomplete kHz", NativeFrequencyUnits),
        ("Fresh current-AP frequency supplies a channel when its scalar is absent and rejects contradictions", NativeFrequencyChannelAgreement),
        ("Only an exact fresh connected SSID BSSID and BSS type can supply cached frequency", NativeMatchingFrequency),
        ("Nearby mismatched old future and frequency-hopping cached APs cannot supply current frequency", NativeRejectedFrequency),
        ("Malformed cached WLAN lists and conflicting exact frequencies preserve uncertainty", NativeMalformedFrequency),
        ("Native association recheck rejects disconnects roaming and changed SSID", NativeAssociationRecheck),
        ("Invalid WLAN adapter identifiers return safely before native discovery", NativeInvalidAdapter),
        ("Every unfamiliar Connections term has a short central help definition", RequiredHelp),
        ("BSSID help identifies an access point radio and distinguishes the SSID", BssidHelp),
        ("Receive and transmit help distinguish wireless link rate from internet speed", LinkRateHelp),
        ("VPN transport help stays separate from application route families", TransportHelp),
        ("Central help definitions are immutable and unknown terms have no invented help", ImmutableHelp),
    ];

    private static readonly Guid FirstGuid = new("52f80a4d-1b30-484f-851f-445ed13fbfa4");
    private static readonly Guid SecondGuid = new("b23016f2-20d1-41bf-baa4-aa6584a3d48e");
    private static readonly AdapterInfo Wifi = new(FirstGuid.ToString(), "Selected Wi-Fi", "Owned fixture", "Wireless80211", 433_000_000, "192.0.2.2", true);
    private static readonly AdapterInfo OtherWifi = Wifi with { Id = SecondGuid.ToString(), Name = "Second Wi-Fi" };
    private static NetworkAdapterDetails Details(AdapterInfo? adapter = null, OperationalStatus status = OperationalStatus.Up) =>
        new((adapter ?? Wifi).Id, status, [], [], [], (adapter ?? Wifi).SpeedBitsPerSecond);
    private static WifiRadioSnapshot Radio(AdapterInfo? adapter = null) => new((adapter ?? Wifi).Id, true)
    {
        SignalQualityPercent = 82, RssiDbm = -58, Channel = 44, FrequencyMHz = 5220,
        Bssid = "AA:BB:CC:DD:EE:FF", PhyType = 8,
        ReceiveLinkBitsPerSecond = 433_000_000, TransmitLinkBitsPerSecond = 390_000_000
    };
    private static WifiRadioPresentation Present(WifiRadioSnapshot? radio = null) =>
        WifiRadioPresentation.Create(Wifi, Details(), radio ?? Radio());

    private static void CompleteRadio()
    {
        var value = Present();
        Check(value.Show, "Connected selected Wi-Fi has a compact radio subsection.");
        Equal("82%", value.SignalQuality, "signal quality");
        Equal("-58 dBm", value.Rssi, "actual RSSI");
        Equal("44", value.Channel, "current channel");
        Equal("5 GHz • 5220 MHz", value.BandFrequency, "band and frequency");
        Equal("AA:BB:CC:DD:EE:FF", value.Bssid, "access point BSSID");
        Equal("802.11ac", value.Phy, "radio technology");
        Equal("433 Mbps", value.ReceiveLinkRate, "receive link");
        Equal("390 Mbps", value.TransmitLinkRate, "transmit link");
        Check(value.HasSeparateLinkRates, "Separate negotiated rates are usable.");
    }
    private static void SignalZero() => Equal("0%", Present(Radio() with { SignalQualityPercent = 0 }).SignalQuality, "zero is actual data");
    private static void SignalHundred() => Equal("100%", Present(Radio() with { SignalQualityPercent = 100 }).SignalQuality, "full signal");
    private static void SignalTypical() => Equal("57%", Present(Radio() with { SignalQualityPercent = 57 }).SignalQuality, "typical signal");
    private static void SignalClamp()
    {
        Equal("0%", Present(Radio() with { SignalQualityPercent = int.MinValue }).SignalQuality, "lower clamp");
        Equal("100%", Present(Radio() with { SignalQualityPercent = int.MaxValue }).SignalQuality, "upper clamp");
    }
    private static void SignalMissing() => Equal("Unavailable", Present(Radio() with { SignalQualityPercent = null }).SignalQuality, "missing signal");
    private static void ActualRssi()
    {
        foreach (var rssi in new[] { -200, -128, -80, -50, 0 })
            Equal(rssi.ToString(CultureInfo.InvariantCulture) + " dBm", Present(Radio() with { RssiDbm = rssi }).Rssi, "actual dBm value");
    }
    private static void MissingRssi()
    {
        foreach (var quality in new[] { 0, 50, 100 })
            Equal("Unavailable", Present(Radio() with { SignalQualityPercent = quality, RssiDbm = null }).Rssi, "percentage cannot invent actual RSSI");
    }
    private static void InvalidRssi()
    {
        foreach (var rssi in new[] { int.MinValue, -201, 1, int.MaxValue })
            Equal("Unavailable", Present(Radio() with { RssiDbm = rssi }).Rssi, "invalid native dBm");
    }
    private static void Band24() => Equal("2.4 GHz • 2412 MHz", Present(Radio() with { FrequencyMHz = 2412, Channel = null }).BandFrequency, "2.4 GHz");
    private static void Band5() => Equal("5 GHz • 5180 MHz", Present(Radio() with { FrequencyMHz = 5180, Channel = null }).BandFrequency, "5 GHz");
    private static void Band6() => Equal("6 GHz • 5955 MHz", Present(Radio() with { FrequencyMHz = 5955, Channel = null }).BandFrequency, "6 GHz");
    private static void UnknownFrequency()
    {
        var value = Present(Radio() with { FrequencyMHz = 1234, Channel = null });
        Equal("1234 MHz", value.BandFrequency, "known frequency without known Wi-Fi band");
        Equal("Unavailable", value.Channel, "unrecognized frequency cannot fabricate channel");
    }
    private static void MissingChannel()
    {
        foreach (var frequency in new int?[] { null, 0, -1 })
        {
            var value = Present(Radio() with { FrequencyMHz = frequency, Channel = null });
            Equal("Unavailable", value.Channel, "missing channel");
            Equal("Unavailable", value.BandFrequency, "missing band and frequency");
        }
    }
    private static void NativeChannel()
    {
        Equal("44", Present(Radio() with { Channel = 44, FrequencyMHz = 5180 }).Channel, "native current channel takes precedence");
        Equal("233", Present(Radio() with { Channel = 233, FrequencyMHz = null }).Channel, "supported explicit channel");
        foreach (var channel in new[] { 0, -1, 234, int.MaxValue })
            Equal("Unavailable", Present(Radio() with { Channel = channel, FrequencyMHz = null }).Channel, "unsupported explicit channel");
    }
    private static void BssidFormatting()
    {
        foreach (var bssid in new[] { "aa:bb:cc:dd:ee:ff", "AA-BB-CC-DD-EE-FF", "aabbccddeeff" })
            Equal("AA:BB:CC:DD:EE:FF", Present(Radio() with { Bssid = bssid }).Bssid, "normalized access point BSSID");
    }
    private static void MissingBssid()
    {
        foreach (var bssid in new string?[] { null, "", "AA:BB", "ZZ:BB:CC:DD:EE:FF", "00:00:00:00:00:00", "FF:FF:FF:FF:FF:FF", "01:11:22:33:44:55" })
            Equal("Unavailable", Present(Radio() with { Bssid = bssid }).Bssid, "unavailable BSSID");
    }
    private static void ModernPhy()
    {
        foreach (var (phy, name) in new[] { (7u, "802.11n"), (8u, "802.11ac"), (10u, "802.11ax") })
            Equal(name, Present(Radio() with { PhyType = phy }).Phy, "current negotiated PHY");
    }
    private static void OlderPhy()
    {
        Equal("802.11g", Present(Radio() with { PhyType = 6 }).Phy, "older PHY");
        foreach (var phy in new uint?[] { null, 0, uint.MaxValue })
            Equal("Unavailable", Present(Radio() with { PhyType = phy }).Phy, "unknown PHY is not a raw enum");
    }
    private static void DifferentRates()
    {
        var value = Present(Radio() with { ReceiveLinkBitsPerSecond = 866_700_000, TransmitLinkBitsPerSecond = 144_400_000 });
        Equal("866.7 Mbps", value.ReceiveLinkRate, "RX independent from TX");
        Equal("144.4 Mbps", value.TransmitLinkRate, "TX independent from RX");
    }
    private static void MissingRates()
    {
        foreach (var missing in new long?[] { null, 0, -1 })
        {
            var receiveMissing = Present(Radio() with { ReceiveLinkBitsPerSecond = missing });
            Equal("Unavailable", receiveMissing.ReceiveLinkRate, "missing RX");
            Equal("390 Mbps", receiveMissing.TransmitLinkRate, "available TX remains visible");
            var transmitMissing = Present(Radio() with { TransmitLinkBitsPerSecond = missing });
            Equal("433 Mbps", transmitMissing.ReceiveLinkRate, "available RX remains visible");
            Equal("Unavailable", transmitMissing.TransmitLinkRate, "missing TX");
        }
        var both = Present(Radio() with { ReceiveLinkBitsPerSecond = null, TransmitLinkBitsPerSecond = null });
        Check(!both.HasSeparateLinkRates, "Missing directional rates permit the existing general link-rate fallback.");
    }
    private static void RateFormatting()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var value = Present(Radio() with { ReceiveLinkBitsPerSecond = 1_200_000_000, TransmitLinkBitsPerSecond = 86_650_000 });
            Equal("1.2 Gbps", value.ReceiveLinkRate, "gigabit formatting");
            Equal("86.65 Mbps", value.TransmitLinkRate, "decimal formatting is culture-independent");
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }
    private static void DisconnectedRadio()
    {
        Hidden(WifiRadioPresentation.Create(Wifi, Details(status: OperationalStatus.Down), Radio()), "disconnected adapter");
        Hidden(Present(Radio() with { IsConnected = false }), "disconnected WLAN association");
    }
    private static void NonWifiRadio()
    {
        foreach (var type in new[] { "Ethernet", "Tunnel", "Loopback", "Unknown" })
            Hidden(WifiRadioPresentation.Create(Wifi with { Type = type }, Details(), Radio()), type);
    }
    private static void WrongAdapterRadio()
    {
        Hidden(WifiRadioPresentation.Create(Wifi, Details(OtherWifi), Radio()), "different adapter details");
        Hidden(Present(Radio(OtherWifi)), "different WLAN interface");
        Hidden(WifiRadioPresentation.Create(Wifi, null, Radio()), "unconfirmed adapter status");
        Hidden(WifiRadioPresentation.Create(null, Details(), Radio()), "no monitored adapter");
    }
    private static void AdapterSwitchRadio()
    {
        var first = Present();
        var second = WifiRadioPresentation.Create(OtherWifi, Details(OtherWifi), Radio(OtherWifi) with { Bssid = "B2:11:22:33:44:55", Channel = 1, FrequencyMHz = 2412 });
        Equal("AA:BB:CC:DD:EE:FF", first.Bssid, "first radio unchanged");
        Equal("B2:11:22:33:44:55", second.Bssid, "second selected radio");
        Equal("1", second.Channel, "second channel");
        Hidden(WifiRadioPresentation.Create(OtherWifi, Details(OtherWifi), Radio()), "first radio cannot follow selection");
    }
    private static void EquivalentAdapterIdentity() => Equal(Present(),
        WifiRadioPresentation.Create(Wifi, Details() with { AdapterId = Wifi.Id.ToUpperInvariant() }, Radio() with { AdapterId = Wifi.Id.ToUpperInvariant() }),
        "equivalent monitored adapter identity");
    private static void MissingRadio()
    {
        Hidden(WifiRadioPresentation.Create(Wifi, Details(), null), "no current observation");
        Hidden(Present(new(Wifi.Id, true)), "no useful radio measurements");
    }
    private static void ImmutableRadio()
    {
        var radio = Radio();
        var before = radio with { };
        Equal(Present(radio), Present(radio), "repeat formatting is deterministic");
        Equal(before, radio, "input snapshot remains immutable");
        var general = AdapterDetailsPresentation.Create(Wifi, Details(), new(ConnectionStatus.Disconnected, null, null, null));
        Equal("433 Mbps", general.LinkRate, "existing general local link-rate field is retained");
    }
    private static void Hidden(WifiRadioPresentation value, string description)
    {
        Check(!value.Show, description + " hides radio subsection.");
        foreach (var text in new[] { value.SignalQuality, value.Rssi, value.Channel, value.BandFrequency, value.Bssid, value.Phy, value.ReceiveLinkRate, value.TransmitLinkRate })
            Equal("Unavailable", text, description + " exposes no stale field");
    }

    private static void Frequencies24()
    {
        foreach (var (frequency, channel) in new[] { (2412, 1), (2437, 6), (2462, 11), (2472, 13), (2484, 14) })
            Frequency(frequency, "2.4 GHz", channel);
    }
    private static void Frequencies5()
    {
        foreach (var (frequency, channel) in new[] { (5180, 36), (5220, 44), (5500, 100), (5745, 149), (5805, 161) })
            Frequency(frequency, "5 GHz", channel);
    }
    private static void Frequencies6()
    {
        foreach (var (frequency, channel) in new[] { (5935, 2), (5955, 1), (5975, 5), (6115, 33), (7115, 233) })
            Frequency(frequency, "6 GHz", channel);
    }
    private static void UnsupportedFrequencies()
    {
        foreach (var frequency in new[] { -1, 0, 1234, 2413, 2490, 5181, 5900, 5925, 5956, 7125, int.MaxValue })
        {
            var result = WifiRadioFrequency.Classify(frequency);
            Equal<int?>(null, result.Channel, "unsupported frequency cannot invent a channel: " + frequency);
        }
    }
    private static void Frequency(int mhz, string band, int channel)
    {
        var result = WifiRadioFrequency.Classify(mhz);
        Equal(band, result.Band, "frequency band " + mhz);
        Equal<int?>(channel, result.Channel, "frequency channel " + mhz);
        Equal(channel.ToString(CultureInfo.InvariantCulture), Present(Radio() with { FrequencyMHz = mhz, Channel = null }).Channel, "shared conversion used by presentation");
    }

    private static void ServiceSelectedAdapter()
    {
        var source = new Source();
        using var service = new WifiRadioService(source);
        service.RequestSnapshot(OtherWifi, Details(OtherWifi));
        WaitUntil(() => service.Current?.AdapterId == OtherWifi.Id, "selected second Wi-Fi is read");
        Equal(OtherWifi.Id, source.AdapterIds.Single(), "source receives the stable selected interface identity");
        Equal(1, source.Reads, "no enumeration of other adapters");
    }
    private static void ServiceCache()
    {
        var source = new Source();
        var clock = new HeadlessTimeProvider();
        using var service = new WifiRadioService(source, clock, TimeSpan.FromSeconds(5));
        service.RequestSnapshot(Wifi, Details());
        WaitUntil(() => service.Current is not null, "initial cache");
        var first = service.Current;
        for (var i = 0; i < 100; i++)
            Equal(first, service.RequestSnapshot(Wifi, Details()), "cached reads are immediate");
        Equal(1, source.Reads, "UI burst does not query native WLAN repeatedly");
        clock.Advance(TimeSpan.FromSeconds(4));
        service.RequestSnapshot(Wifi, Details());
        Equal(1, source.Reads, "refresh interval has not elapsed");
        source.Handler = id => Radio() with { AdapterId = id, SignalQualityPercent = 53 };
        clock.Advance(TimeSpan.FromSeconds(1));
        service.RequestSnapshot(Wifi, Details());
        WaitUntil(() => service.Current?.SignalQualityPercent == 53, "new signal after lightweight refresh interval");
        Equal(2, source.Reads, "exactly one new scheduled read");
    }
    private static void ServiceSingleFlight()
    {
        using var block = new BlockedSource();
        using var service = new WifiRadioService(block.Source);
        service.RequestSnapshot(Wifi, Details());
        block.WaitEntered();
        for (var i = 0; i < 200; i++) service.RequestSnapshot(Wifi, Details());
        Equal(1, block.Source.Reads, "one in-flight native query despite bursts");
        block.Release.Set();
        Complete(service);
        Equal(1, block.Source.MaxConcurrent, "source access remains serialized");
        Check(service.Current is not null, "successful single-flight observation publishes.");
    }
    private static void ServiceDisconnect()
    {
        var source = new Source();
        using var service = new WifiRadioService(source);
        service.RequestSnapshot(Wifi, Details());
        WaitUntil(() => service.Current is not null, "connected cached radio");
        Equal<WifiRadioSnapshot?>(null, service.RequestSnapshot(Wifi, Details(status: OperationalStatus.Down)), "disconnect returns no stale values");
        Equal<WifiRadioSnapshot?>(null, service.Current, "disconnect clears current BSSID and channel");
        Equal(1, source.Reads, "disconnect performs no WLAN query");
    }
    private static void ServiceIneligible()
    {
        var source = new Source();
        using var service = new WifiRadioService(source);
        foreach (var type in new[] { "Ethernet", "Tunnel", "Loopback", "Unknown" })
            Equal<WifiRadioSnapshot?>(null, service.RequestSnapshot(Wifi with { Type = type }, Details()), "non-Wi-Fi " + type);
        service.RequestSnapshot(null, Details());
        service.RequestSnapshot(Wifi, null);
        service.RequestSnapshot(Wifi, Details(OtherWifi));
        service.RequestSnapshot(Wifi, Details(status: OperationalStatus.Unknown));
        Equal(0, source.Reads, "ineligible adapter states never perform native discovery");
        Equal<WifiRadioSnapshot?>(null, service.Current, "ineligible adapters expose no radio cache");
    }
    private static void ServiceInvalidate()
    {
        using var block = new BlockedSource();
        using var service = new WifiRadioService(block.Source);
        service.RequestSnapshot(Wifi, Details());
        block.WaitEntered();
        service.Invalidate();
        block.Release.Set();
        Complete(service);
        Equal<WifiRadioSnapshot?>(null, service.Current, "old hidden or sleeping observation cannot publish");
        service.RequestSnapshot(Wifi, Details());
        WaitUntil(() => service.Current is not null, "fresh visible or resumed observation");
        Equal(2, block.Source.Reads, "fresh request recovers without a timer");
    }
    private static void ServiceSwitch()
    {
        using var block = new BlockedSource();
        using var service = new WifiRadioService(block.Source);
        service.RequestSnapshot(Wifi, Details());
        block.WaitEntered();
        Equal<WifiRadioSnapshot?>(null, service.RequestSnapshot(OtherWifi, Details(OtherWifi)), "selection switch has no borrowed radio");
        block.Release.Set();
        Complete(service);
        Equal<WifiRadioSnapshot?>(null, service.Current, "old first interface observation is rejected");
        service.RequestSnapshot(OtherWifi, Details(OtherWifi));
        WaitUntil(() => service.Current?.AdapterId == OtherWifi.Id, "second interface query completes");
        Equal(OtherWifi.Id, block.Source.AdapterIds.Last(), "new query targets second monitored adapter");
        Equal(1, block.Source.MaxConcurrent, "switch does not overlap native reads");
    }
    private static void ServiceFailure()
    {
        var source = new Source();
        var clock = new HeadlessTimeProvider();
        using var service = new WifiRadioService(source, clock, TimeSpan.FromSeconds(5));
        service.RequestSnapshot(Wifi, Details());
        WaitUntil(() => service.Current is not null, "initial good cache");
        source.Handler = _ => throw new UnauthorizedAccessException("Owned radio permission fixture");
        clock.Advance(TimeSpan.FromSeconds(5));
        service.RequestSnapshot(Wifi, Details());
        Complete(service);
        Check(service.Current is null || !service.Current.IsConnected, "Failure does not retain previous connected values.");
        source.Handler = id => Radio() with { AdapterId = id, Bssid = "B2:11:22:33:44:55" };
        clock.Advance(TimeSpan.FromSeconds(5));
        service.RequestSnapshot(Wifi, Details());
        WaitUntil(() => service.Current?.Bssid == "B2:11:22:33:44:55", "fresh permissions recover radio cache");
    }
    private static void ServiceDispose()
    {
        using var block = new BlockedSource();
        var service = new WifiRadioService(block.Source);
        try
        {
            service.RequestSnapshot(Wifi, Details());
            block.WaitEntered();
            service.Dispose();
            block.Release.Set();
            Complete(service);
            Equal<WifiRadioSnapshot?>(null, service.Current, "late query rejected after disposal");
            Equal<WifiRadioSnapshot?>(null, service.RequestSnapshot(Wifi, Details()), "disposed future requests");
            Equal(1, block.Source.Reads, "disposal cannot create a new native read");
        }
        finally { block.Release.Set(); service.Dispose(); }
    }
    private static void ServiceNoTimerOrHistory()
    {
        var fields = typeof(WifiRadioService).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Check(!fields.Any(field => typeof(ITimer).IsAssignableFrom(field.FieldType) || typeof(Timer).IsAssignableFrom(field.FieldType)),
            "Radio service has no periodic timer.");
        Check(!fields.Any(field => field.FieldType == typeof(UsageStore) || field.FieldType == typeof(ConnectionHistoryStore) || field.FieldType == typeof(MonitoringService)),
            "Presentation radio cache neither persists nor changes accounting.");
        Check(!fields.Any(field => typeof(Thread).IsAssignableFrom(field.FieldType)), "No dedicated persistent radio worker.");
    }

    private static readonly byte[] CurrentSsid = [79, 80, 80, 79, 32, 65, 51, 120];
    private static readonly byte[] CurrentBssid = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF];
    private static readonly long FileTimeNow = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
    private static Type NativeType(string name) => typeof(WindowsWifiRadioSource).GetNestedType(name, BindingFlags.NonPublic)!;
    private static T? NativeCall<T>(string name, params object?[] args) =>
        (T?)typeof(WindowsWifiRadioSource).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
    private static void NativeLayout()
    {
        var connection = NativeType("WlanConnectionAttributes");
        var association = NativeType("WlanAssociationAttributes");
        var bss = NativeType("WlanBssEntry");
        Equal(604, Marshal.SizeOf(connection), "WLAN_CONNECTION_ATTRIBUTES native size");
        Equal(68, Marshal.SizeOf(association), "WLAN_ASSOCIATION_ATTRIBUTES native size");
        Equal(360, Marshal.SizeOf(bss), "WLAN_BSS_ENTRY native stride");
        Equal(new IntPtr(520), Marshal.OffsetOf(connection, "Association"), "association offset after native profile string");
        Equal(new IntPtr(588), Marshal.OffsetOf(connection, "Security"), "security offset");
        foreach (var (name, offset) in new[] { ("Bssid", 40), ("PhyType", 48), ("PhyIndex", 52), ("SignalQuality", 56), ("ReceiveRateKbps", 60), ("TransmitRateKbps", 64) })
            Equal(new IntPtr(offset), Marshal.OffsetOf(association, name), "native association field " + name);
        Equal(new IntPtr(80), Marshal.OffsetOf(bss, "HostTimestamp"), "cached host FILETIME offset");
        Equal(new IntPtr(92), Marshal.OffsetOf(bss, "CenterFrequencyKhz"), "cached center frequency offset");
    }
    private static byte[] ConnectionBytes()
    {
        var bytes = new byte[604];
        Put(bytes, 0, 1);
        Put(bytes, 520, CurrentSsid.Length);
        CurrentSsid.CopyTo(bytes, 524);
        Put(bytes, 556, 1);
        CurrentBssid.CopyTo(bytes, 560);
        Put(bytes, 568, 8);
        Put(bytes, 572, 1);
        Put(bytes, 576, 82);
        Put(bytes, 580, 433_000);
        Put(bytes, 584, 390_000);
        return bytes;
    }
    private static void NativeConnection()
    {
        using var memory = new NativeBuffer(ConnectionBytes());
        var value = NativeCall<WifiRadioSnapshot>("DecodeCurrentConnection", Wifi.Id, memory.Pointer, 604u)!;
        Equal(Wifi.Id, value.AdapterId, "requested interface identity remains correlated");
        Check(value.IsConnected, "Native association is connected.");
        Equal<int?>(82, value.SignalQualityPercent, "native quality");
        Equal("AA:BB:CC:DD:EE:FF", value.Bssid, "connected access point address");
        Equal<uint?>(8, value.PhyType, "actual negotiated PHY");
        Equal<long?>(433_000_000, value.ReceiveLinkBitsPerSecond, "SDK receive kbit/s converted exactly once");
        Equal<long?>(390_000_000, value.TransmitLinkBitsPerSecond, "SDK transmit kbit/s converted exactly once");
        Equal<int?>(null, value.RssiDbm, "quality cannot manufacture RSSI");
        Marshal.WriteInt32(memory.Pointer, 576, -1);
        Marshal.WriteInt32(memory.Pointer, 580, -1);
        Marshal.WriteInt32(memory.Pointer, 584, 0);
        value = NativeCall<WifiRadioSnapshot>("DecodeCurrentConnection", Wifi.Id, memory.Pointer, 604u)!;
        Equal<int?>(100, value.SignalQualityPercent, "unsigned quality is safely clamped");
        Equal<long?>(4_294_967_295_000, value.ReceiveLinkBitsPerSecond, "unsigned rate conversion cannot overflow 32-bit");
        Equal<long?>(null, value.TransmitLinkBitsPerSecond, "zero negotiated rate is unavailable");
    }
    private static void NativeAssociationPhy()
    {
        var capableAdapter = Wifi with { Description = "Hardware supports 802.11ac" };
        foreach (var (phy, text) in new[]
        {
            (7u, "802.11n"), (8u, "802.11ac"), (10u, "802.11ax"),
            (0u, "Unavailable"), (uint.MaxValue, "Unavailable")
        })
        {
            using var memory = new NativeBuffer(ConnectionBytes());
            Marshal.WriteInt32(memory.Pointer, 568, unchecked((int)phy));
            // A PHY-list index is a separate field, even when its number also
            // names a valid PHY enum. Neither it nor hardware text supplies PHY.
            Marshal.WriteInt32(memory.Pointer, 572, phy == 8 ? 7 : 8);
            var radio = NativeCall<WifiRadioSnapshot>("DecodeCurrentConnection", capableAdapter.Id, memory.Pointer, 604u)!;
            Equal<uint?>(phy == 0 ? null : phy, radio.PhyType, "native current-association PHY is decoded separately from its list index");
            var value = WifiRadioPresentation.Create(capableAdapter, Details(capableAdapter),
                radio with { Channel = 1, FrequencyMHz = 2412 });
            Equal(text, value.Phy, "current association determines PHY without capability fallback or frequency guessing");
            Equal("2.4 GHz • 2412 MHz", value.BandFrequency, "measured frequency remains independent of Windows-reported PHY");
        }
        var absent = NativeCall<WifiRadioSnapshot>("DecodeCurrentConnection", capableAdapter.Id, IntPtr.Zero, 604u)!;
        Equal("Unavailable", WifiRadioPresentation.Create(capableAdapter, Details(capableAdapter), absent).Phy,
            "missing current association cannot borrow an adapter capability description");
    }
    private static void NativeUnavailableConnection()
    {
        using var memory = new NativeBuffer(ConnectionBytes());
        foreach (var state in new[] { 0, 2, 3, 4, 5, 6, 7, int.MaxValue })
        {
            Marshal.WriteInt32(memory.Pointer, state);
            var value = NativeCall<WifiRadioSnapshot>("DecodeCurrentConnection", Wifi.Id, memory.Pointer, 604u)!;
            Check(!value.IsConnected, "Only the actual connected WLAN state may publish radio values.");
            Equal<string?>(null, value.Bssid, "not connected cannot carry a previous BSSID");
            Equal<int?>(null, value.SignalQualityPercent, "not connected cannot carry a previous signal");
        }
        foreach (var size in new[] { 0u, 4u, 603u })
            Check(!NativeCall<WifiRadioSnapshot>("DecodeCurrentConnection", Wifi.Id, memory.Pointer, size)!.IsConnected,
                "Truncated native connection buffer stays unavailable.");
        Check(!NativeCall<WifiRadioSnapshot>("DecodeCurrentConnection", Wifi.Id, IntPtr.Zero, 604u)!.IsConnected, "Missing native connection buffer stays unavailable.");
    }
    private static void NativeScalars()
    {
        using var memory = new NativeBuffer(new byte[4]);
        foreach (var rssi in new[] { -80, -58, -50, 0 })
        {
            Marshal.WriteInt32(memory.Pointer, rssi);
            Equal<int?>(rssi, NativeCall<int?>("DecodeRssi", memory.Pointer, 4u), "actual signed LONG dBm");
        }
        foreach (var invalid in new[] { int.MinValue, 1, int.MaxValue })
        {
            Marshal.WriteInt32(memory.Pointer, invalid);
            Equal<int?>(null, NativeCall<int?>("DecodeRssi", memory.Pointer, 4u), "invalid actual RSSI");
        }
        foreach (var channel in new[] { 1, 44, 149, 233 })
        {
            Marshal.WriteInt32(memory.Pointer, channel);
            Equal<int?>(channel, NativeCall<int?>("DecodeChannel", memory.Pointer, 4u), "native current channel");
        }
        foreach (var channel in new[] { 0, -1, 234, int.MaxValue })
        {
            Marshal.WriteInt32(memory.Pointer, channel);
            Equal<int?>(null, NativeCall<int?>("DecodeChannel", memory.Pointer, 4u), "invalid current channel");
        }
        foreach (var helper in new[] { "DecodeRssi", "DecodeChannel" })
        {
            Equal<int?>(null, NativeCall<int?>(helper, memory.Pointer, 3u), "truncated scalar cannot provide data");
            Equal<int?>(null, NativeCall<int?>(helper, IntPtr.Zero, 4u), "missing scalar cannot provide data");
        }
    }
    private static void NativeFrequencyUnits()
    {
        foreach (var mhz in new[] { 2412, 5180, 5955 })
            Equal<int?>(mhz, NativeCall<int?>("ConvertFrequencyKhz", (uint)mhz * 1000), "native kHz converts to exact MHz");
        foreach (var khz in new[] { 0u, 1u, 2_412_001u, uint.MaxValue })
            Equal<int?>(null, NativeCall<int?>("ConvertFrequencyKhz", khz), "partial or missing frequency is not rounded to a channel");
    }
    private static void NativeFrequencyChannelAgreement()
    {
        foreach (var (frequency, channel) in new[] { (2412, 1), (5180, 36), (5220, 44), (5955, 1) })
            Equal<int?>(frequency, NativeCall<int?>("ConfirmCurrentFrequency", (int?)frequency, (int?)channel), "actual channel confirms cached frequency");
        foreach (var (frequency, channel, band) in new[] { (2412, 1, "2.4 GHz"), (5220, 44, "5 GHz"), (5955, 1, "6 GHz") })
        {
            using var memory = new NativeBuffer(BssList(BssEntry(frequencyKhz: (uint)frequency * 1000)));
            var cachedFrequency = CurrentFrequency(memory);
            var confirmedFrequency = NativeCall<int?>("ConfirmCurrentFrequency", cachedFrequency, null);
            Equal<int?>(frequency, confirmedFrequency, "fresh exact current-AP frequency remains usable without a channel scalar");
            var value = Present(Radio() with { FrequencyMHz = confirmedFrequency, Channel = null });
            Equal(channel.ToString(CultureInfo.InvariantCulture), value.Channel, "channel derives centrally from the measured current-AP frequency");
            Equal(band + " • " + frequency.ToString(CultureInfo.InvariantCulture) + " MHz", value.BandFrequency,
                "measured current-AP frequency supplies its actual band without inferring from PHY or channel alone");
        }
        foreach (var (frequency, channel) in new (int?, int?)[] { (2412, 6), (5220, 36), (1234, 1), (1234, null), (null, 44), (null, null) })
            Equal<int?>(null, NativeCall<int?>("ConfirmCurrentFrequency", frequency, channel), "missing unsupported or contradictory cache cannot establish current band");
    }
    private static byte[] BssEntry(uint frequencyKhz = 5_220_000, long? timestamp = null, byte[]? ssid = null,
        byte[]? bssid = null, uint bssType = 1, uint phy = 8)
    {
        var bytes = new byte[360];
        var network = ssid ?? CurrentSsid;
        Put(bytes, 0, network.Length);
        network.CopyTo(bytes, 4);
        (bssid ?? CurrentBssid).CopyTo(bytes, 40);
        Put(bytes, 48, bssType);
        Put(bytes, 52, phy);
        BitConverter.GetBytes(timestamp ?? FileTimeNow).CopyTo(bytes, 80);
        Put(bytes, 92, frequencyKhz);
        return bytes;
    }
    private static byte[] BssList(params byte[][] entries)
    {
        var bytes = new byte[8 + entries.Length * 360];
        Put(bytes, 0, bytes.Length);
        Put(bytes, 4, entries.Length);
        for (var index = 0; index < entries.Length; index++) entries[index].CopyTo(bytes, 8 + index * 360);
        return bytes;
    }
    private static int? CurrentFrequency(NativeBuffer memory, byte[]? ssid = null, byte[]? bssid = null, uint bssType = 1) =>
        NativeCall<int?>("ReadCurrentFrequency", memory.Pointer, ssid ?? CurrentSsid, bssid ?? CurrentBssid, bssType, FileTimeNow);
    private static void NativeMatchingFrequency()
    {
        using var memory = new NativeBuffer(BssList(BssEntry(bssid: [0xB2, 1, 2, 3, 4, 5]), BssEntry()));
        Equal<int?>(5220, CurrentFrequency(memory), "only exact current BSSID selected from cached entries");
        using var repeated = new NativeBuffer(BssList(BssEntry(), BssEntry()));
        Equal<int?>(5220, CurrentFrequency(repeated), "consistent exact duplicate metadata remains deterministic");
        using var boundary = new NativeBuffer(BssList(BssEntry(timestamp: FileTimeNow - TimeSpan.FromSeconds(30).Ticks)));
        Equal<int?>(5220, CurrentFrequency(boundary), "freshness boundary is explicit");
    }
    private static void NativeRejectedFrequency()
    {
        foreach (var entry in new[]
        {
            BssEntry(bssid: [0xB2, 1, 2, 3, 4, 5]), BssEntry(ssid: [1, 2, 3]), BssEntry(bssType: 2),
            BssEntry(timestamp: FileTimeNow - TimeSpan.FromSeconds(30).Ticks - 1), BssEntry(timestamp: FileTimeNow + 1),
            BssEntry(phy: 1), BssEntry(frequencyKhz: 5_220_001)
        })
        {
            using var memory = new NativeBuffer(BssList(entry));
            Equal<int?>(null, CurrentFrequency(memory), "unrelated stale future or incomplete cache cannot establish current frequency");
        }
        using var valid = new NativeBuffer(BssList(BssEntry()));
        Equal<int?>(null, CurrentFrequency(valid, bssid: new byte[6]), "missing current BSSID cannot borrow a cache match");
        Equal<int?>(null, CurrentFrequency(valid, ssid: []), "missing current SSID cannot borrow a cache match");
        Equal<int?>(null, CurrentFrequency(valid, bssType: 99), "unrecognized current BSS type cannot borrow a cache match");
    }
    private static void NativeMalformedFrequency()
    {
        using var conflicting = new NativeBuffer(BssList(BssEntry(), BssEntry(frequencyKhz: 5_180_000)));
        Equal<int?>(null, CurrentFrequency(conflicting), "conflicting exact observations stay unknown");
        foreach (var (size, count) in new[] { (0u, 1u), (7u, 1u), (368u, 2u), (2_000_000u, 1u), (368u, uint.MaxValue), (368u, 0u) })
        {
            using var memory = new NativeBuffer(BssList(BssEntry()));
            Marshal.WriteInt32(memory.Pointer, unchecked((int)size));
            Marshal.WriteInt32(memory.Pointer, 4, unchecked((int)count));
            Equal<int?>(null, CurrentFrequency(memory), "invalid list header is rejected before entry access");
        }
        Equal<int?>(null, NativeCall<int?>("ReadCurrentFrequency", IntPtr.Zero, CurrentSsid, CurrentBssid, 1u, FileTimeNow), "missing cached buffer");
    }
    private static void NativeAssociationRecheck()
    {
        var native = NativeType("WlanConnectionAttributes");
        using var first = new NativeBuffer(ConnectionBytes());
        using var same = new NativeBuffer(ConnectionBytes());
        var a = Marshal.PtrToStructure(first.Pointer, native)!;
        Check(NativeCall<bool>("SameAssociation", a, Marshal.PtrToStructure(same.Pointer, native)!), "Same association can retain scalar observations.");
        foreach (var offset in new[] { 0, 524, 560, 568, 588 })
        {
            using var changed = new NativeBuffer(ConnectionBytes());
            Marshal.WriteByte(changed.Pointer, offset, 2);
            Check(!NativeCall<bool>("SameAssociation", a, Marshal.PtrToStructure(changed.Pointer, native)!),
                "A disconnected different-SSID or roamed-BSSID observation is rejected.");
        }
    }
    private static void NativeInvalidAdapter()
    {
        var source = new WindowsWifiRadioSource();
        foreach (var id in new[] { "", "not-a-guid", Guid.Empty.ToString() })
        {
            var result = source.Read(id);
            Check(!result.IsConnected, "Invalid stable interface ID cannot perform or claim WLAN discovery.");
            Equal(id, result.AdapterId, "unavailable result keeps requested correlation");
        }
    }
    private static void Put(byte[] bytes, int offset, int value) => BitConverter.GetBytes(value).CopyTo(bytes, offset);
    private static void Put(byte[] bytes, int offset, uint value) => BitConverter.GetBytes(value).CopyTo(bytes, offset);
    private sealed class NativeBuffer : IDisposable
    {
        public IntPtr Pointer { get; }
        public NativeBuffer(byte[] bytes)
        {
            Pointer = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, Pointer, bytes.Length);
        }
        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }

    private sealed class Source : IWifiRadioSource
    {
        private int _reads;
        private int _active;
        private int _maxConcurrent;
        public int Reads => Volatile.Read(ref _reads);
        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);
        public ConcurrentQueue<string> AdapterIds { get; } = new();
        public Func<string, WifiRadioSnapshot> Handler { get; set; } = id => Radio() with { AdapterId = id };
        public WifiRadioSnapshot Read(string adapterId)
        {
            AdapterIds.Enqueue(adapterId);
            Interlocked.Increment(ref _reads);
            var active = Interlocked.Increment(ref _active);
            int maximum;
            do { maximum = Volatile.Read(ref _maxConcurrent); }
            while (active > maximum && Interlocked.CompareExchange(ref _maxConcurrent, active, maximum) != maximum);
            try { return Handler(adapterId); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
    private sealed class BlockedSource : IDisposable
    {
        public Source Source { get; } = new();
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public BlockedSource()
        {
            Source.Handler = id =>
            {
                Entered.Set();
                Check(Release.Wait(TimeSpan.FromSeconds(5)), "Blocked owned native read must be released.");
                return Radio() with { AdapterId = id };
            };
        }
        public void WaitEntered() => Check(Entered.Wait(TimeSpan.FromSeconds(5)), "Owned native read starts.");
        public void Dispose() { Release.Set(); Entered.Dispose(); Release.Dispose(); }
    }
    private static void Complete(WifiRadioService service)
    {
        var gate = typeof(WifiRadioService).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        var inFlight = typeof(WifiRadioService).GetField("_inFlight", BindingFlags.Instance | BindingFlags.NonPublic)!;
        WaitUntil(() => { lock (gate) return !(bool)inFlight.GetValue(service)!; }, "Owned radio query completes before lifecycle assertions");
    }
    private static void WaitUntil(Func<bool> condition, string description)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(description);
            Thread.Sleep(10);
        }
    }

    private static readonly string[] RequiredTerms =
    [
        "Signal quality", "RSSI", "Channel", "Band / Frequency", "BSSID", "PHY / radio type",
        "Receive link rate", "Transmit link rate", "IPv4", "IPv6", "Default gateway", "DNS", "VPN transport"
    ];

    private static void RequiredHelp()
    {
        foreach (var term in RequiredTerms)
        {
            Check(NetworkHelpText.Definitions.TryGetValue(term, out var explanation), term + " is centrally associated with help.");
            Check(!string.IsNullOrWhiteSpace(explanation), term + " has nonempty help.");
            Check(explanation!.Length <= 330, term + " has a short plain-language definition.");
            Equal(explanation, NetworkHelpText.Get(term), term + " lookup returns its own explanation");
        }
        Check(NetworkHelpText.Get("Signal quality").Contains("percentages", StringComparison.Ordinal), "Signal help explains the percentage.");
        Check(NetworkHelpText.Get("RSSI").Contains("dBm", StringComparison.Ordinal), "RSSI help explains its unit.");
        Check(NetworkHelpText.Get("Channel").Contains("radio channel", StringComparison.Ordinal), "Channel has the radio explanation.");
        Check(NetworkHelpText.Get("Band / Frequency").Contains("2.4 GHz", StringComparison.Ordinal), "Band help introduces actual wireless bands.");
        Check(NetworkHelpText.Get("PHY / radio type").Contains("802.11", StringComparison.Ordinal), "PHY help names recognizable technologies.");
        Check(NetworkHelpText.Get("IPv4").Contains("192.168.1.10", StringComparison.Ordinal), "IPv4 gets dotted-address help.");
        Check(NetworkHelpText.Get("IPv6").Contains("colons", StringComparison.Ordinal), "IPv6 gets colon-address help.");
        Check(NetworkHelpText.Get("Default gateway").Contains("outside the local network", StringComparison.Ordinal), "Gateway help explains outgoing destinations.");
        Check(NetworkHelpText.Get("DNS").Contains("translates names", StringComparison.Ordinal), "DNS gets name-resolution help.");
    }

    private static void BssidHelp()
    {
        var text = NetworkHelpText.Get("BSSID");
        Check(text.Contains("access point", StringComparison.Ordinal), "BSSID identifies the connected access point.");
        Check(text.Contains("different", StringComparison.Ordinal) && text.Contains("SSID", StringComparison.Ordinal),
            "BSSID is explicitly different from SSID.");
    }

    private static void LinkRateHelp()
    {
        var receive = NetworkHelpText.Get("Receive link rate");
        var transmit = NetworkHelpText.Get("Transmit link rate");
        Check(receive.Contains("access point to this PC", StringComparison.Ordinal), "Receive explains its direction.");
        Check(transmit.Contains("PC to the access point", StringComparison.Ordinal), "Transmit explains its direction.");
        foreach (var text in new[] { receive, transmit })
            Check(text.Contains("not the same", StringComparison.Ordinal) && text.Contains("internet", StringComparison.Ordinal),
                "Wireless negotiation is explicitly different from internet speed.");
        Check(NetworkHelpText.Get("Link rate").Contains("not the same", StringComparison.Ordinal), "Retained general link rate has the same caveat.");
    }

    private static void TransportHelp()
    {
        var text = NetworkHelpText.Get("VPN transport");
        Check(text.Contains("VPN itself", StringComparison.Ordinal) && text.Contains("server", StringComparison.Ordinal), "VPN transport explains the VPN server path.");
        Check(text.Contains("separate", StringComparison.Ordinal) && text.Contains("application traffic", StringComparison.Ordinal), "Transport help preserves route-family independence.");
    }

    private static void ImmutableHelp()
    {
        Equal("", NetworkHelpText.Get("invented fixture term"), "unknown definition");
        var dictionary = (IDictionary<string, string>)NetworkHelpText.Definitions;
        Check(dictionary.IsReadOnly, "Shared help is exposed read-only.");
        try { dictionary.Add("fixture", "fixture"); }
        catch (NotSupportedException) { return; }
        throw new InvalidOperationException("Help definitions must reject mutation.");
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
}
