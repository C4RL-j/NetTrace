using System.Runtime.InteropServices;
using NetTrace.Models;
using NetTrace.Utils;

namespace NetTrace.Services;

// Queries only the requested interface's current association. This source has
// no scan API, callbacks, sockets, timers, or persistent radio-data storage.
public sealed class WindowsWifiRadioSource : IWifiRadioSource
{
    private const uint CurrentConnectionOpcode = 7;
    private const uint ChannelNumberOpcode = 8;
    private const uint RssiOpcode = 0x10000102;
    private const long MaximumCachedAgeTicks = 30 * TimeSpan.TicksPerSecond;

    public WifiRadioSnapshot Read(string adapterId)
    {
        var unavailable = new WifiRadioSnapshot(adapterId, false);
        if (!Guid.TryParse(adapterId, out var interfaceGuid) || interfaceGuid == Guid.Empty)
            return unavailable with { Diagnostic = "The Wi-Fi interface identifier is unavailable." };

        IntPtr handle = IntPtr.Zero;
        try
        {
            if (WindowsNetworkNative.WlanOpenHandle(2, IntPtr.Zero, out _, out handle) != 0 ||
                handle == IntPtr.Zero)
                return unavailable with { Diagnostic = "Windows Wi-Fi information is unavailable." };

            if (!TryReadConnection(handle, ref interfaceGuid, out var connection))
                return unavailable;

            var snapshot = FromConnection(adapterId, connection);
            if (!snapshot.IsConnected)
                return snapshot;

            var rssi = QueryScalar(handle, ref interfaceGuid, RssiOpcode, DecodeRssi);
            var channel = QueryScalar(handle, ref interfaceGuid, ChannelNumberOpcode, DecodeChannel);
            var frequency = ReadCachedFrequency(handle, ref interfaceGuid, connection);

            // Native reads are separate operations. Reject an observation if a
            // disconnect/roam happened between them, rather than mixing radios.
            if (!TryReadConnection(handle, ref interfaceGuid, out var latest) ||
                !SameAssociation(connection, latest))
                return unavailable;

            var latestChannel = QueryScalar(handle, ref interfaceGuid, ChannelNumberOpcode, DecodeChannel);
            if (channel is not null && latestChannel is not null && channel != latestChannel)
                return unavailable;
            var observedChannel = latestChannel ?? channel;

            return FromConnection(adapterId, latest) with
            {
                RssiDbm = rssi,
                Channel = observedChannel,
                FrequencyMHz = ConfirmCurrentFrequency(frequency, observedChannel)
            };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or
            BadImageFormatException or ExternalException or ArgumentException or OverflowException)
        {
            return unavailable with { Diagnostic = "Windows Wi-Fi information is unavailable." };
        }
        finally
        {
            if (handle != IntPtr.Zero)
                WindowsNetworkNative.WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    private static bool TryReadConnection(IntPtr handle, ref Guid interfaceGuid,
        out WlanConnectionAttributes connection)
    {
        connection = default;
        IntPtr data = IntPtr.Zero;
        try
        {
            if (WindowsNetworkNative.WlanQueryInterface(handle, ref interfaceGuid, CurrentConnectionOpcode,
                IntPtr.Zero, out var size, out data, IntPtr.Zero) != 0 ||
                data == IntPtr.Zero || size < Marshal.SizeOf<WlanConnectionAttributes>())
                return false;

            connection = Marshal.PtrToStructure<WlanConnectionAttributes>(data);
            return true;
        }
        finally
        {
            if (data != IntPtr.Zero)
                WindowsNetworkNative.WlanFreeMemory(data);
        }
    }

    internal static WifiRadioSnapshot DecodeCurrentConnection(string adapterId, IntPtr data, uint dataSize) =>
        data == IntPtr.Zero || dataSize < Marshal.SizeOf<WlanConnectionAttributes>()
            ? new(adapterId, false)
            : FromConnection(adapterId, Marshal.PtrToStructure<WlanConnectionAttributes>(data));

    private static WifiRadioSnapshot FromConnection(string adapterId, WlanConnectionAttributes connection)
    {
        if (connection.InterfaceState != 1)
            return new(adapterId, false);

        var association = connection.Association;
        var bssid = association.Bssid;
        return new(adapterId, true)
        {
            SignalQualityPercent = (int)Math.Min(association.SignalQuality, 100u),
            Bssid = IsValidBssid(bssid)
                ? string.Join(":", bssid.Select(value => value.ToString("X2",
                    System.Globalization.CultureInfo.InvariantCulture)))
                : null,
            PhyType = association.PhyType == 0 ? null : association.PhyType,
            // Microsoft SDK wlanapi.h defines these rates in kbit/s, not bit/s.
            ReceiveLinkBitsPerSecond = association.ReceiveRateKbps == 0
                ? null : (long)association.ReceiveRateKbps * 1000,
            TransmitLinkBitsPerSecond = association.TransmitRateKbps == 0
                ? null : (long)association.TransmitRateKbps * 1000
        };
    }

    private static int? QueryScalar(IntPtr handle, ref Guid interfaceGuid, uint opcode,
        Func<IntPtr, uint, int?> decode)
    {
        IntPtr data = IntPtr.Zero;
        try
        {
            return WindowsNetworkNative.WlanQueryInterface(handle, ref interfaceGuid, opcode,
                IntPtr.Zero, out var size, out data, IntPtr.Zero) == 0 ? decode(data, size) : null;
        }
        finally
        {
            if (data != IntPtr.Zero)
                WindowsNetworkNative.WlanFreeMemory(data);
        }
    }

    internal static int? DecodeRssi(IntPtr data, uint dataSize)
    {
        if (data == IntPtr.Zero || dataSize < sizeof(int))
            return null;
        var rssi = Marshal.ReadInt32(data);
        // Read the driver's actual LONG value; never infer RSSI from quality.
        return rssi is >= -200 and <= 0 ? rssi : null;
    }

    internal static int? DecodeChannel(IntPtr data, uint dataSize)
    {
        if (data == IntPtr.Zero || dataSize < sizeof(uint))
            return null;
        var channel = unchecked((uint)Marshal.ReadInt32(data));
        return channel is > 0 and <= 233 ? (int)channel : null;
    }

    private static int? ReadCachedFrequency(IntPtr handle, ref Guid interfaceGuid,
        WlanConnectionAttributes connection)
    {
        var association = connection.Association;
        if (association.Ssid.Length is 0 or > 32 || !IsValidBssid(association.Bssid) ||
            association.BssType is not (1 or 2))
            return null;

        IntPtr list = IntPtr.Zero;
        try
        {
            // This reads Windows' already-cached metadata for the CURRENT SSID.
            // It does not initiate a WLAN scan. Only the exact current BSSID is
            // consumed; no nearby AP identifiers or information elements escape.
            var ssid = association.Ssid;
            if (WlanGetNetworkBssList(handle, ref interfaceGuid, ref ssid, association.BssType,
                connection.Security.SecurityEnabled != 0, IntPtr.Zero, out list) != 0)
                return null;

            return ReadCurrentFrequency(list,
                ssid.Bytes.AsSpan(0, (int)ssid.Length).ToArray(), association.Bssid,
                association.BssType, DateTime.UtcNow.ToFileTimeUtc());
        }
        finally
        {
            if (list != IntPtr.Zero)
                WindowsNetworkNative.WlanFreeMemory(list);
        }
    }

    internal static int? ReadCurrentFrequency(IntPtr bssList, byte[] currentSsid, byte[] currentBssid,
        uint currentBssType, long nowFileTime)
    {
        if (bssList == IntPtr.Zero || currentSsid.Length is 0 or > 32 || !IsValidBssid(currentBssid) ||
            currentBssType is not (1 or 2) || nowFileTime <= 0)
            return null;

        var totalSize = unchecked((uint)Marshal.ReadInt32(bssList));
        var count = unchecked((uint)Marshal.ReadInt32(bssList, sizeof(uint)));
        var entrySize = Marshal.SizeOf<WlanBssEntry>();
        const int headerSize = 8; // Two DWORDs align the eight-byte-aligned entries.
        if (totalSize < headerSize || totalSize > 1024 * 1024 || count == 0 ||
            (ulong)headerSize + (ulong)count * (uint)entrySize > totalSize)
            return null;

        int? frequency = null;
        for (var index = 0u; index < count; index++)
        {
            var entry = Marshal.PtrToStructure<WlanBssEntry>(
                IntPtr.Add(bssList, checked(headerSize + (int)index * entrySize)));
            if (entry.Ssid.Length != currentSsid.Length || entry.BssType != currentBssType ||
                !entry.Ssid.Bytes.AsSpan(0, currentSsid.Length).SequenceEqual(currentSsid) ||
                !entry.Bssid.AsSpan().SequenceEqual(currentBssid))
                continue;

            // FILETIME is a count of 100ns intervals since 1601. Old/future cache
            // observations cannot establish the current association's frequency.
            if (entry.PhyType == 1 || entry.HostTimestamp > (ulong)nowFileTime ||
                (ulong)nowFileTime - entry.HostTimestamp > (ulong)MaximumCachedAgeTicks)
                continue;

            var candidate = ConvertFrequencyKhz(entry.CenterFrequencyKhz);
            if (candidate is null)
                continue;
            if (frequency is not null && frequency != candidate)
                return null;
            frequency = candidate;
        }
        return frequency;
    }

    internal static int? ConvertFrequencyKhz(uint frequencyKhz) =>
        frequencyKhz > 0 && frequencyKhz % 1000 == 0
            ? (int)(frequencyKhz / 1000) : null;

    internal static int? ConfirmCurrentFrequency(int? frequencyMHz, int? currentChannel)
    {
        if (frequencyMHz is not > 0)
            return null;
        var mappedChannel = WifiRadioFrequency.Classify(frequencyMHz.Value).Channel;
        // Some drivers cannot query the channel scalar. The already validated
        // exact, fresh current-AP frequency can still supply its standard channel.
        // A reported native channel must agree; a contradiction remains unknown.
        return mappedChannel is not null && (currentChannel is null || currentChannel == mappedChannel)
            ? frequencyMHz : null;
    }

    private static bool IsValidBssid(byte[]? bssid) =>
        bssid is { Length: 6 } && (bssid[0] & 1) == 0 && bssid.Any(value => value != 0);

    private static bool SameAssociation(WlanConnectionAttributes first, WlanConnectionAttributes second)
    {
        var a = first.Association;
        var b = second.Association;
        return first.InterfaceState == 1 && second.InterfaceState == 1 && a.BssType == b.BssType &&
            a.PhyType == b.PhyType &&
            first.Security.SecurityEnabled == second.Security.SecurityEnabled &&
            a.Ssid.Length <= 32 && a.Ssid.Length == b.Ssid.Length &&
            a.Ssid.Bytes.AsSpan(0, (int)a.Ssid.Length).SequenceEqual(b.Ssid.Bytes.AsSpan(0, (int)b.Ssid.Length)) &&
            a.Bssid.AsSpan().SequenceEqual(b.Bssid);
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanGetNetworkBssList(IntPtr clientHandle, ref Guid interfaceGuid,
        ref WindowsNetworkNative.Dot11Ssid ssid, uint bssType,
        [MarshalAs(UnmanagedType.Bool)] bool securityEnabled, IntPtr reserved, out IntPtr bssList);

    // Native WLAN SDK layout is identical on 32/64-bit Windows: no pointers.
    // https://learn.microsoft.com/windows/win32/api/wlanapi/ns-wlanapi-wlan_connection_attributes
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WlanConnectionAttributes
    {
        internal int InterfaceState;
        internal int ConnectionMode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        internal string ProfileName;
        internal WlanAssociationAttributes Association;
        internal WlanSecurityAttributes Security;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WlanAssociationAttributes
    {
        internal WindowsNetworkNative.Dot11Ssid Ssid;
        internal uint BssType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        internal byte[] Bssid;
        internal uint PhyType;
        internal uint PhyIndex;
        internal uint SignalQuality;
        internal uint ReceiveRateKbps;
        internal uint TransmitRateKbps;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WlanSecurityAttributes
    {
        internal int SecurityEnabled;
        internal int OneXEnabled;
        internal uint AuthenticationAlgorithm;
        internal uint CipherAlgorithm;
    }

    // Only frequency from an exact, fresh cache match is used. The trailing rate
    // set and IE offsets retain SDK padding/stride; information elements are not read.
    // https://learn.microsoft.com/windows/win32/api/wlanapi/ns-wlanapi-wlan_bss_entry
    [StructLayout(LayoutKind.Explicit, Size = 360)]
    internal struct WlanBssEntry
    {
        [FieldOffset(0)] internal WindowsNetworkNative.Dot11Ssid Ssid;
        [FieldOffset(40), MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        internal byte[] Bssid;
        [FieldOffset(48)] internal uint BssType;
        [FieldOffset(52)] internal uint PhyType;
        [FieldOffset(80)] internal ulong HostTimestamp;
        [FieldOffset(92)] internal uint CenterFrequencyKhz;
    }
}
