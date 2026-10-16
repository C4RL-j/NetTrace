using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using NetTrace.Models;

namespace NetTrace.Services;

// Read-only inventory for VPN detection. It deliberately does not use the
// physical identity service, inspect routes, or open the usage database.
public sealed class WindowsVpnNative : IVpnInterfaceSource
{
    private const string NetworkDriverClass =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    public VpnInterfaceInventory Read()
    {
        var diagnostics = new List<string>();
        var interfaces = ReadInterfaces(diagnostics, out var identities);
        var drivers = ReadDriverIdentities(diagnostics);
        var assigned = ReadAssignedAddresses(identities);
        var enriched = interfaces.Select(item => drivers.TryGetValue(item.InterfaceGuid, out var driver)
            ? item with { DriverComponentId = driver.ComponentId, DriverService = driver.Service,
                TapMediaAlwaysConnected = driver.TapMediaAlwaysConnected }
            : item).Select(item => item with
            {
                AssignedAddresses = assigned.TryGetValue(item.InterfaceGuid, out var addresses)
                    ? addresses : Array.Empty<IPAddress>()
            }).ToArray();
        var rasConnections = ReadRasConnections(diagnostics);
        return new(enriched, rasConnections, diagnostics.Count == 0,
            diagnostics.Count == 0 ? null : string.Join(" ", diagnostics));
    }

    private static IReadOnlyList<VpnInterfaceEvidence> ReadInterfaces(List<string> diagnostics,
        out Dictionary<(ulong Luid, uint Index), Guid> identities)
    {
        identities = new();
        var entries = new List<VpnInterfaceEvidence>();
        IntPtr table = IntPtr.Zero;
        try
        {
            var result = GetIfTable2(out table);
            if (result != 0)
            {
                diagnostics.Add(DescribeError("Windows interface inventory", result));
                return entries;
            }
            if (table == IntPtr.Zero)
                throw new InvalidOperationException("Windows returned an empty interface-table pointer.");

            var count = Marshal.ReadInt32(table);
            if (count is < 0 or > 65536)
                throw new InvalidOperationException("Windows returned an invalid interface count.");

            // MIB_IF_TABLE2 aligns its MIB_IF_ROW2 array to an eight-byte boundary.
            // Both 32-bit and 64-bit Windows SDK layouts use 1,352-byte rows.
            const int tableOffset = 8;
            var rowSize = Marshal.SizeOf<MibIfRow2>();
            for (var i = 0; i < count; i++)
            {
                var rowPointer = IntPtr.Add(table, checked(tableOffset + i * rowSize));
                var row = Marshal.PtrToStructure<MibIfRow2>(rowPointer);
                identities[(row.InterfaceLuid, row.InterfaceIndex)] = row.InterfaceGuid;
                entries.Add(new VpnInterfaceEvidence
                {
                    InterfaceIndex = row.InterfaceIndex,
                    InterfaceGuid = row.InterfaceGuid,
                    InterfaceName = ReadFixedString(rowPointer, 28, 257),
                    Description = ReadFixedString(rowPointer, 542, 257),
                    InterfaceType = row.Type,
                    TunnelType = row.TunnelType,
                    OperationalStatus = (OperationalStatus)row.OperStatus,
                    MediaConnectState = row.MediaConnectState,
                    StatusFlags = row.StatusFlags
                });
            }
        }
        catch (Exception ex) when (IsInventoryException(ex))
        {
            diagnostics.Add($"Windows interface inventory is incomplete: {ex.Message}");
        }
        finally
        {
            if (table != IntPtr.Zero)
                FreeMibTable(table);
        }
        return entries;
    }

    private static Dictionary<Guid, IReadOnlyList<IPAddress>> ReadAssignedAddresses(
        IReadOnlyDictionary<(ulong Luid, uint Index), Guid> identities)
    {
        var addresses = new Dictionary<Guid, List<IPAddress>>();
        IntPtr table = IntPtr.Zero;
        try
        {
            // One read of both families, associated by the same LUID/index seen
            // in GetIfTable2. An index recycled during discovery cannot borrow an IP.
            var result = GetUnicastIpAddressTable(0, out table); // AF_UNSPEC
            if (result != 0)
                throw new Win32Exception((int)result);
            if (table == IntPtr.Zero)
                throw new InvalidOperationException("Windows returned an empty unicast-address table pointer.");
            var count = Marshal.ReadInt32(table);
            if (count is < 0 or > 1000000)
                throw new InvalidOperationException("Windows returned an invalid unicast-address count.");

            const int tableOffset = 8;
            var rowSize = Marshal.SizeOf<MibUnicastIpAddressRow>();
            for (var i = 0; i < count; i++)
            {
                var pointer = IntPtr.Add(table, checked(tableOffset + i * rowSize));
                var row = Marshal.PtrToStructure<MibUnicastIpAddressRow>(pointer);
                // Tentative, duplicate and invalid addresses do not establish a
                // connected IP interface. A deprecated address remains assigned.
                if (row.ValidLifetime == 0 || row.DadState is not (3 or 4) ||
                    !identities.TryGetValue((row.InterfaceLuid, row.InterfaceIndex), out var guid) || guid == Guid.Empty)
                    continue;
                var family = (AddressFamily)(ushort)Marshal.ReadInt16(pointer);
                if (family is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)) continue;
                var bytes = new byte[family == AddressFamily.InterNetwork ? 4 : 16];
                Marshal.Copy(IntPtr.Add(pointer, family == AddressFamily.InterNetwork ? 4 : 8), bytes, 0, bytes.Length);
                var address = family == AddressFamily.InterNetwork ? new IPAddress(bytes)
                    : new IPAddress(bytes, unchecked((uint)Marshal.ReadInt32(pointer, 24)));
                if (!addresses.TryGetValue(guid, out var values)) addresses.Add(guid, values = new());
                values.Add(address);
            }
        }
        catch (Exception exception) when (IsInventoryException(exception))
        {
            // IP evidence is used only by the connected header indicator. A failed
            // address read must not change the established history/route semantics.
            Trace.TraceWarning($"Windows assigned VPN addresses could not be fully read: {exception.Message}");
        }
        finally
        {
            if (table != IntPtr.Zero) FreeMibTable(table);
        }
        return addresses.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<IPAddress>)Array.AsReadOnly(pair.Value.Distinct().ToArray()));
    }

    private static Dictionary<Guid, DriverIdentity> ReadDriverIdentities(List<string> diagnostics)
    {
        var identities = new Dictionary<Guid, DriverIdentity>();
        try
        {
            // Driver ComponentId/Service are installation metadata, not the
            // adapter's user-editable alias or its descriptive marketing name.
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var driverClass = machine.OpenSubKey(NetworkDriverClass, writable: false);
            if (driverClass is null)
            {
                diagnostics.Add("Windows network-driver identities are unavailable.");
                return identities;
            }

            foreach (var childName in driverClass.GetSubKeyNames())
            {
                if (childName.Length != 4 || !childName.All(char.IsAsciiDigit))
                    continue;
                try
                {
                    using var instance = driverClass.OpenSubKey(childName, writable: false);
                    if (instance is null || !Guid.TryParse(instance.GetValue("NetCfgInstanceId") as string, out var guid))
                        continue;
                    var component = instance.GetValue("ComponentId") as string;
                    using var ndi = instance.OpenSubKey("Ndi", writable: false);
                    var service = ndi?.GetValue("Service") as string ?? instance.GetValue("Service") as string;
                    var alwaysConnected = Convert.ToString(instance.GetValue("MediaStatus"),
                        System.Globalization.CultureInfo.InvariantCulture) == "1";
                    if (!identities.TryAdd(guid, new(component, service, alwaysConnected)))
                    {
                        // Conflicting stale installation entries are not evidence
                        // that the currently enumerated interface uses either driver.
                        identities[guid] = new(null, null, false);
                        diagnostics.Add("Windows returned conflicting network-driver identities.");
                    }
                }
                catch (Exception ex) when (IsInventoryException(ex))
                {
                    diagnostics.Add($"A Windows network-driver identity could not be read: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (IsInventoryException(ex))
        {
            diagnostics.Add($"Windows network-driver inventory is incomplete: {ex.Message}");
        }
        return identities;
    }

    private static IReadOnlyList<VpnRasConnectionEvidence> ReadRasConnections(List<string> diagnostics)
    {
        var connections = new List<VpnRasConnectionEvidence>();
        IntPtr buffer = IntPtr.Zero;
        try
        {
            // RasEnumConnections + RasGetConnectStatus are required together:
            // an installed WAN miniport alone says nothing about a live VPN.
            // ras.h uses four-byte packing even in a 64-bit process.
            var rowSize = Marshal.SizeOf<RasConn>();
            uint bufferSize = (uint)rowSize;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (bufferSize < rowSize || bufferSize > 16 * 1024 * 1024)
                    throw new InvalidOperationException("Windows returned an invalid RAS buffer size.");

                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                    buffer = IntPtr.Zero;
                }
                buffer = Marshal.AllocHGlobal((int)bufferSize);
                Marshal.WriteInt32(buffer, rowSize);
                var allocatedSize = bufferSize;
                var result = RasEnumConnectionsW(buffer, ref bufferSize, out var count);
                if (result == 603) // ERROR_BUFFER_TOO_SMALL; a connection may appear during enumeration.
                    continue;
                if (result != 0)
                {
                    diagnostics.Add(DescribeError("Windows RAS connection inventory", result));
                    return connections;
                }
                if ((ulong)count * (uint)rowSize > allocatedSize)
                    throw new InvalidOperationException("Windows returned an invalid RAS connection count.");

                for (var i = 0; i < count; i++)
                {
                    var connection = Marshal.PtrToStructure<RasConn>(
                        IntPtr.Add(buffer, checked(i * rowSize)));
                    var status = new RasConnStatus { Size = (uint)Marshal.SizeOf<RasConnStatus>() };
                    result = RasGetConnectStatusW(connection.Handle, ref status);
                    if (result == 6) // ERROR_INVALID_HANDLE: it disconnected between the two calls.
                        continue;
                    if (result != 0)
                    {
                        diagnostics.Add(DescribeError("A Windows RAS connection status", result));
                        continue;
                    }

                    // The profile GUID and logon LUID in RASCONN are not interface
                    // identities. Keep this evidence separate rather than inventing
                    // an interface GUID/index or binding by a descriptive name.
                    connections.Add(new(connection.EntryName ?? string.Empty,
                        status.DeviceName, status.DeviceType, status.ConnectionState, status.ErrorCode, status.Substate)
                    {
                        RemoteEndpoint = ReadConnectedRasRemoteEndpoint(status)
                    });
                }
                return connections;
            }
            diagnostics.Add("Windows RAS connections changed repeatedly during detection; their state is uncertain.");
        }
        catch (Exception ex) when (IsInventoryException(ex))
        {
            diagnostics.Add($"Windows RAS connection inventory is incomplete: {ex.Message}");
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);
        }
        return connections;
    }

    private static bool IsInventoryException(Exception ex) =>
        ex is Win32Exception or SecurityException or UnauthorizedAccessException or IOException or
            InvalidOperationException or ArgumentException or OverflowException or ExternalException or
            DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException;

    private static string DescribeError(string operation, uint error) =>
        $"{operation} failed: {new Win32Exception((int)error).Message} (Windows error {error}).";

    private static string ReadFixedString(IntPtr structure, int offset, int characterCount)
    {
        var value = Marshal.PtrToStringUni(IntPtr.Add(structure, offset), characterCount) ?? string.Empty;
        var terminator = value.IndexOf('\0');
        return terminator < 0 ? value : value[..terminator];
    }

    private sealed record DriverIdentity(string? ComponentId, string? Service, bool TapMediaAlwaysConnected);

    private static IPAddress? ReadConnectedRasRemoteEndpoint(RasConnStatus status)
    {
        // RASCONNSTATUS.remoteEndPoint describes the VPN server, while RAS
        // projection addresses describe the payload network. Admit only a
        // successfully connected VPN session; reconnecting/dormant endpoints
        // can belong to an earlier transport and are not current evidence.
        if (status.ConnectionState != 0x2000 || status.ErrorCode != 0 ||
            status.Substate is not (0 or 0x2000) ||
            !string.Equals(status.DeviceType, "vpn", StringComparison.OrdinalIgnoreCase))
            return null;

        return ReadRasRemoteEndpoint(status.RemoteEndpoint.Type,
            [status.RemoteEndpoint.Address0, status.RemoteEndpoint.Address1,
             status.RemoteEndpoint.Address2, status.RemoteEndpoint.Address3,
             status.RemoteEndpoint.Address4, status.RemoteEndpoint.Address5,
             status.RemoteEndpoint.Address6, status.RemoteEndpoint.Address7,
             status.RemoteEndpoint.Address8, status.RemoteEndpoint.Address9,
             status.RemoteEndpoint.Address10, status.RemoteEndpoint.Address11,
             status.RemoteEndpoint.Address12, status.RemoteEndpoint.Address13,
             status.RemoteEndpoint.Address14, status.RemoteEndpoint.Address15]);
    }

    private static IPAddress? ReadRasRemoteEndpoint(uint type, byte[] addressBytes)
    {
        // ras.h: UNKNOWN=0, IPv4=1, IPv6=2. The union contains IN_ADDR or
        // IN6_ADDR in network byte order, immediately after the DWORD type.
        if (addressBytes.Length != 16 || type is not (1 or 2))
            return null;
        var address = new IPAddress(type == 1 ? addressBytes[..4] : addressBytes);
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any) || address.IsIPv6Multicast || address.IsIPv4MappedToIPv6)
            return null;
        if (type == 1 && (addressBytes[0] is 0 or >= 224 || address.Equals(IPAddress.Broadcast)))
            return null;
        return address;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIfTable2(out IntPtr table);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetUnicastIpAddressTable(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(IntPtr table);

    [DllImport("rasapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RasEnumConnectionsW(IntPtr connections, ref uint bufferSize, out uint count);

    [DllImport("rasapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RasGetConnectStatusW(IntPtr connection, ref RasConnStatus status);

    // SDK: MIB_IF_ROW2. Explicit offsets retain the full native output size.
    // https://learn.microsoft.com/windows/win32/api/netioapi/ns-netioapi-mib_if_row2
    [StructLayout(LayoutKind.Explicit, Size = 1352, CharSet = CharSet.Unicode)]
    private struct MibIfRow2
    {
        [FieldOffset(0)] internal ulong InterfaceLuid;
        [FieldOffset(8)] internal uint InterfaceIndex;
        [FieldOffset(12)] internal Guid InterfaceGuid;
        [FieldOffset(1128)] internal uint Type;
        [FieldOffset(1132)] internal uint TunnelType;
        [FieldOffset(1152)] internal byte StatusFlags;
        [FieldOffset(1156)] internal uint OperStatus;
        [FieldOffset(1164)] internal uint MediaConnectState;
    }

    // SOCKADDR_INET occupies 28 bytes, followed by eight-byte-aligned NET_LUID.
    // https://learn.microsoft.com/windows/win32/api/netioapi/ns-netioapi-mib_unicastipaddress_row
    [StructLayout(LayoutKind.Explicit, Size = 80)]
    private struct MibUnicastIpAddressRow
    {
        [FieldOffset(32)] internal ulong InterfaceLuid;
        [FieldOffset(40)] internal uint InterfaceIndex;
        [FieldOffset(52)] internal uint ValidLifetime;
        [FieldOffset(64)] internal uint DadState;
    }

    // SDK ras.h explicitly uses pshpack4.h, including for HRASCONN on x64.
    // https://learn.microsoft.com/previous-versions/windows/desktop/legacy/aa376725(v=vs.85)
    [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
    private struct RasConn
    {
        internal uint Size;
        internal IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] internal string? EntryName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] internal string? DeviceType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] internal string? DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string? Phonebook;
        internal uint SubEntry;
        internal Guid EntryGuid;
        internal uint Flags;
        internal uint LogonLuidLow;
        internal int LogonLuidHigh;
        internal Guid CorrelationGuid;
    }

    // The two RASTUNNELENDPOINT unions occupy 20 bytes each. The final
    // RASCONNSUBSTATE makes the Windows 7+ Unicode/pack-4 structure 608 bytes.
    [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
    private struct RasConnStatus
    {
        internal uint Size;
        internal uint ConnectionState;
        internal uint ErrorCode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] internal string DeviceType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] internal string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] internal string PhoneNumber;
        internal RasTunnelEndpoint LocalEndpoint;
        internal RasTunnelEndpoint RemoteEndpoint;
        internal uint Substate;
    }

    [StructLayout(LayoutKind.Explicit, Size = 20, Pack = 4)]
    private struct RasTunnelEndpoint
    {
        [FieldOffset(0)] internal uint Type;
        // SDK ras.h: DWORD followed by the IN_ADDR / IN6_ADDR union. Byte
        // fields preserve network order without integer endianness conversion.
        // https://learn.microsoft.com/previous-versions/windows/desktop/legacy/dd430579(v=vs.85)
        [FieldOffset(4)] internal byte Address0;
        [FieldOffset(5)] internal byte Address1;
        [FieldOffset(6)] internal byte Address2;
        [FieldOffset(7)] internal byte Address3;
        [FieldOffset(8)] internal byte Address4;
        [FieldOffset(9)] internal byte Address5;
        [FieldOffset(10)] internal byte Address6;
        [FieldOffset(11)] internal byte Address7;
        [FieldOffset(12)] internal byte Address8;
        [FieldOffset(13)] internal byte Address9;
        [FieldOffset(14)] internal byte Address10;
        [FieldOffset(15)] internal byte Address11;
        [FieldOffset(16)] internal byte Address12;
        [FieldOffset(17)] internal byte Address13;
        [FieldOffset(18)] internal byte Address14;
        [FieldOffset(19)] internal byte Address15;
    }
}
