using System.Net;
using AppUsageEtwProbe;

internal static class ProbeNormalizationTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Installed IPv4 output type decodes address bytes in the correct order", Check.Sync(Ipv4Address)),
        ("Signed TDH IPv4 values preserve all unsigned address bits", Check.Sync(SignedIpv4Address)),
        ("Installed IPv6 output type accepts a complete sixteen-byte address", Check.Sync(Ipv6Address)),
        ("IPv6 values already decoded by TDH retain their address identity", Check.Sync(DecodedIpv6Address)),
        ("Malformed or truncated IPv6 fields fail safely", Check.Sync(MalformedIpv6)),
        ("Malformed IPv4 payload types fail safely", Check.Sync(MalformedIpv4)),
        ("Signed TDH values for unsigned manifest integers preserve their bits", Check.Sync(UnsignedManifestIntegers)),
    ];

    private static readonly PayloadField V4 = new("saddr", "win:UInt32") { OutputType = "win:IPv4" };
    private static readonly PayloadField V6 = new("saddr", "win:Binary") { OutputType = "win:IPv6", Length = 16 };

    private static void Ipv4Address() =>
        Check.Equal(IPAddress.Parse("127.0.0.1"), EtwCapture.NormalizePayloadValue(V4, 0x0100007FU),
            "IPv4 event field byte order");

    private static void SignedIpv4Address() =>
        Check.Equal(IPAddress.Parse("127.0.0.254"),
            EtwCapture.NormalizePayloadValue(V4, unchecked((int)0xFE00007F)), "signed IPv4 address bits");

    private static void Ipv6Address()
    {
        var expected = IPAddress.Parse("2001:db8::1234");
        Check.Equal(expected, EtwCapture.NormalizePayloadValue(V6, expected.GetAddressBytes()), "IPv6 field");
    }

    private static void DecodedIpv6Address()
    {
        var expected = IPAddress.IPv6Loopback;
        Check.Equal(expected, EtwCapture.NormalizePayloadValue(V6, expected), "decoded IPv6 loopback");
    }

    private static void MalformedIpv6()
    {
        foreach (object? value in new object?[] { null, new byte[0], new byte[4], new byte[15], new byte[17], "::1", IPAddress.Loopback })
            Check.Throws<FormatException>(() => EtwCapture.NormalizePayloadValue(V6, value), "invalid IPv6 payload");
    }

    private static void MalformedIpv4()
    {
        foreach (object? value in new object?[] { null, new byte[4], "127.0.0.1", 1L, 1.0 })
            Check.Throws<FormatException>(() => EtwCapture.NormalizePayloadValue(V4, value), "invalid IPv4 payload");
    }

    private static void UnsignedManifestIntegers()
    {
        Check.Equal(uint.MaxValue, EtwCapture.NormalizePayloadValue(new("size", "win:UInt32"), -1),
            "unsigned 32-bit interpretation");
        Check.Equal(ushort.MaxValue, EtwCapture.NormalizePayloadValue(new("sport", "win:UInt16"), (short)-1),
            "unsigned 16-bit interpretation");
        Check.Equal(1024U, EtwCapture.NormalizePayloadValue(new("size", "win:UInt32"), 1024U),
            "already unsigned byte count");
    }
}
