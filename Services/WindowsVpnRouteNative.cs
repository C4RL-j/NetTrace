using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using NetTrace.Models;

namespace NetTrace.Services;

// A read-only route inventory. Interface metrics and eligibility are queried
// for the requested IP family; no route or interface configuration is changed.
public sealed class WindowsVpnRouteNative : IVpnRouteSource
{
    public VpnRouteInventory Read(AddressFamily family)
    {
        if (family is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            throw new ArgumentOutOfRangeException(nameof(family), "Only IPv4 and IPv6 routes are supported.");

        var routes = new List<VpnRouteEvidence>();
        var diagnostics = new List<string>();
        var interfaces = new Dictionary<(ulong Luid, uint Index), RouteInterfaceState>();
        IntPtr table = IntPtr.Zero;
        try
        {
            var result = GetIpForwardTable2((ushort)family, out table);
            if (result != 0)
            {
                diagnostics.Add(DescribeError($"Windows {FamilyName(family)} route inventory", result));
                return new(family, routes, false, string.Join(" ", diagnostics));
            }
            if (table == IntPtr.Zero)
                throw new InvalidOperationException("Windows returned an empty route-table pointer.");

            var count = Marshal.ReadInt32(table);
            if (count is < 0 or > 1000000)
                throw new InvalidOperationException("Windows returned an invalid route count.");

            const int tableOffset = 8;
            var rowSize = Marshal.SizeOf<MibIpForwardRow2>();
            for (var i = 0; i < count; i++)
            {
                var pointer = IntPtr.Add(table, checked(tableOffset + i * rowSize));
                var row = Marshal.PtrToStructure<MibIpForwardRow2>(pointer);
                var destination = ReadAddress(IntPtr.Add(pointer, 12), family);
                var nextHop = ReadAddress(IntPtr.Add(pointer, 44), family);
                if (row.PrefixLength > (family == AddressFamily.InterNetwork ? 32 : 128))
                    throw new InvalidOperationException("Windows returned an invalid route prefix length.");

                var key = (row.InterfaceLuid, row.InterfaceIndex);
                if (!interfaces.TryGetValue(key, out var state))
                {
                    state = ReadInterface(family, row.InterfaceLuid, row.InterfaceIndex, diagnostics);
                    interfaces.Add(key, state);
                }
                routes.Add(new(row.InterfaceIndex, state.Guid, destination, row.PrefixLength,
                    nextHop, row.Metric, state.Metric, state.IsUsable, row.ValidLifetime,
                    row.Loopback != 0, state.DefaultRoutesEnabled));
            }
        }
        catch (Exception exception) when (IsInventoryException(exception))
        {
            diagnostics.Add($"Windows {FamilyName(family)} route inventory is incomplete: {exception.Message}");
        }
        finally
        {
            if (table != IntPtr.Zero)
                FreeMibTable(table);
        }
        return new(family, routes.AsReadOnly(), diagnostics.Count == 0,
            diagnostics.Count == 0 ? null : string.Join(" ", diagnostics));
    }

    private static RouteInterfaceState ReadInterface(AddressFamily family, ulong luid, uint index,
        List<string> diagnostics)
    {
        var link = new MibIfRow2 { InterfaceLuid = luid, InterfaceIndex = index };
        var ip = new MibIpInterfaceRow { Family = (ushort)family, InterfaceLuid = luid, InterfaceIndex = index };
        var linkResult = GetIfEntry2(ref link);
        var ipResult = GetIpInterfaceEntry(ref ip);
        if (linkResult != 0)
            diagnostics.Add(DescribeError("Windows route-interface state", linkResult));
        if (ipResult != 0)
            diagnostics.Add(DescribeError($"Windows {FamilyName(family)} interface metric", ipResult));
        if (linkResult != 0 || ipResult != 0)
            return new(linkResult == 0 && link.InterfaceGuid != Guid.Empty ? link.InterfaceGuid : null,
                ipResult == 0 ? ip.Metric : null, null,
                ipResult == 0 ? ip.DisableDefaultRoutes == 0 : null);

        if (link.InterfaceIndex != index || ip.InterfaceIndex != index ||
            link.InterfaceLuid != ip.InterfaceLuid ||
            (luid != 0 && (link.InterfaceLuid != luid || ip.InterfaceLuid != luid)) ||
            ip.Family != (ushort)family || link.InterfaceGuid == Guid.Empty)
        {
            diagnostics.Add("Windows route-interface identity changed during detection.");
            return new(null, null, null, null);
        }

        const byte excludedFlags = 2 | 8 | 16 | 32 | 64 | 128;
        var usable = link.OperStatus == 1 && link.MediaConnectState != 2 &&
            (link.StatusFlags & excludedFlags) == 0 && link.InterfaceType != 24 && ip.Connected != 0;
        return new(link.InterfaceGuid, ip.Metric, usable, ip.DisableDefaultRoutes == 0);
    }

    private static string FamilyName(AddressFamily family) =>
        family == AddressFamily.InterNetwork ? "IPv4" : "IPv6";

    private sealed record RouteInterfaceState(Guid? Guid, uint? Metric, bool? IsUsable, bool? DefaultRoutesEnabled);

    private static IPAddress ReadAddress(IntPtr sockaddr, AddressFamily expectedFamily)
    {
        var actualFamily = (AddressFamily)(ushort)Marshal.ReadInt16(sockaddr);
        if (actualFamily != expectedFamily)
            throw new InvalidOperationException("Windows returned a route with an unexpected address family.");

        var bytes = new byte[expectedFamily == AddressFamily.InterNetwork ? 4 : 16];
        Marshal.Copy(IntPtr.Add(sockaddr, expectedFamily == AddressFamily.InterNetwork ? 4 : 8),
            bytes, 0, bytes.Length);
        return expectedFamily == AddressFamily.InterNetwork
            ? new IPAddress(bytes)
            : new IPAddress(bytes, unchecked((uint)Marshal.ReadInt32(sockaddr, 24)));
    }

    private static string DescribeError(string operation, uint error) =>
        $"{operation} failed: {new Win32Exception((int)error).Message} (Windows error {error}).";

    private static bool IsInventoryException(Exception exception) =>
        exception is Win32Exception or IOException or InvalidOperationException or ArgumentException or
            OverflowException or ExternalException or DllNotFoundException or EntryPointNotFoundException or
            PlatformNotSupportedException;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIpForwardTable2(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIpInterfaceEntry(ref MibIpInterfaceRow row);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIfEntry2(ref MibIfRow2 row);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(IntPtr table);

    // Windows SDK layouts, with space for every field written by Windows.
    // SOCKADDR_INET is a 28-byte union and IP_ADDRESS_PREFIX is 32 bytes.
    // https://learn.microsoft.com/windows/win32/api/netioapi/ns-netioapi-mib_ipforward_row2
    [StructLayout(LayoutKind.Explicit, Size = 104)]
    private struct MibIpForwardRow2
    {
        [FieldOffset(0)] internal ulong InterfaceLuid;
        [FieldOffset(8)] internal uint InterfaceIndex;
        [FieldOffset(40)] internal byte PrefixLength;
        [FieldOffset(76)] internal uint ValidLifetime;
        [FieldOffset(84)] internal uint Metric;
        [FieldOffset(92)] internal byte Loopback;
    }

    // ADDRESS_FAMILY is a two-byte value. ZoneIndices has 16 ULONG entries;
    // the two NL_INTERFACE_OFFLOAD_ROD flag sets occupy one byte each.
    // https://learn.microsoft.com/windows/win32/api/netioapi/ns-netioapi-mib_ipinterface_row
    [StructLayout(LayoutKind.Explicit, Size = 168)]
    private struct MibIpInterfaceRow
    {
        [FieldOffset(0)] internal ushort Family;
        [FieldOffset(8)] internal ulong InterfaceLuid;
        [FieldOffset(16)] internal uint InterfaceIndex;
        [FieldOffset(148)] internal uint Metric;
        [FieldOffset(156)] internal byte Connected;
        [FieldOffset(166)] internal byte DisableDefaultRoutes;
    }

    [StructLayout(LayoutKind.Explicit, Size = 1352)]
    private struct MibIfRow2
    {
        [FieldOffset(0)] internal ulong InterfaceLuid;
        [FieldOffset(8)] internal uint InterfaceIndex;
        [FieldOffset(12)] internal Guid InterfaceGuid;
        [FieldOffset(1128)] internal uint InterfaceType;
        [FieldOffset(1152)] internal byte StatusFlags;
        [FieldOffset(1156)] internal uint OperStatus;
        [FieldOffset(1164)] internal uint MediaConnectState;
    }
}
