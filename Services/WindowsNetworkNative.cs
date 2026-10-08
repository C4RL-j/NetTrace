using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NetTrace.Services;

// Small Win32 bindings; no external WLAN/COM dependency. COM callback interfaces
// must be public for Windows to query their managed callable wrappers.
[ComVisible(false)]
public static class WindowsNetworkNative
{
    private static readonly Guid NetworkListManagerClassId = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

    internal static object CreateNetworkListManager() => Activator.CreateInstance(
        Type.GetTypeFromCLSID(NetworkListManagerClassId, throwOnError: true)!)!;

    internal static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.ReleaseComObject(value);
    }

    internal static bool IsConnectedHardwareInterface(uint interfaceIndex)
    {
        var row = new MibIfRow2 { InterfaceIndex = interfaceIndex };
        var result = GetIfEntry2(ref row);
        if (result != 0)
            throw new Win32Exception((int)result, "Windows could not read the network adapter's physical connection information.");

        const byte hardware = 1;
        const byte filter = 2;
        const byte notMediaConnected = 16;
        const byte endpoint = 128;
        return (row.StatusFlags & hardware) != 0 &&
            (row.StatusFlags & (filter | notMediaConnected | endpoint)) == 0 && row.OperStatus == 1;
    }

    internal static Dictionary<uint, uint> GetPhysicalDefaultRouteMetrics()
    {
        // This is a local route-table read, with no packets or health probes.
        var metrics = new Dictionary<uint, uint>();
        uint size = 0;
        if (GetIpForwardTable(IntPtr.Zero, ref size, false) != 122 || size == 0)
            return metrics;

        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            if (GetIpForwardTable(buffer, ref size, false) != 0)
                return metrics;
            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibIpForwardRow>();
            if (count < 0 || (long)count * rowSize + sizeof(uint) > size)
                return metrics;

            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibIpForwardRow>(IntPtr.Add(buffer, sizeof(uint) + i * rowSize));
                if (row.Destination == 0 && row.Mask == 0 && row.Type != 2 &&
                    (!metrics.TryGetValue(row.InterfaceIndex, out var previous) || row.Metric1 < previous))
                    metrics[row.InterfaceIndex] = row.Metric1;
            }
            return metrics;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void WlanNotificationCallback(IntPtr notificationData, IntPtr context);

    [DllImport("wlanapi.dll")]
    internal static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved,
        out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    internal static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    internal static extern uint WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, uint opcode,
        IntPtr reserved, out uint dataSize, out IntPtr data, IntPtr opcodeValueType);

    [DllImport("wlanapi.dll")]
    internal static extern uint WlanRegisterNotification(IntPtr clientHandle, uint notificationSource,
        [MarshalAs(UnmanagedType.Bool)] bool ignoreDuplicate, WlanNotificationCallback? callback,
        IntPtr callbackContext, IntPtr reserved, out uint previousSource);

    [DllImport("wlanapi.dll")]
    internal static extern void WlanFreeMemory(IntPtr memory);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIfEntry2(ref MibIfRow2 row);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIpForwardTable(IntPtr table, ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool order);

    // Prefix of WLAN_CONNECTION_ATTRIBUTES through DOT11_SSID. The remaining
    // association/security fields are deliberately unused.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WlanConnectionPrefix
    {
        internal int InterfaceState;
        internal int ConnectionMode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        internal string ProfileName;
        internal Dot11Ssid Ssid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Dot11Ssid
    {
        internal uint Length;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        internal byte[] Bytes;
    }

    // MIB_IF_ROW2 has a fixed 1,352-byte Windows SDK layout (32/64 bit).
    // Only these fields are needed; the explicit size retains space for every
    // field that GetIfEntry2 writes, including its trailing statistics.
    // https://learn.microsoft.com/windows/win32/api/netioapi/ns-netioapi-mib_if_row2
    [StructLayout(LayoutKind.Explicit, Size = 1352)]
    private struct MibIfRow2
    {
        [FieldOffset(8)] internal uint InterfaceIndex;
        [FieldOffset(1152)] internal byte StatusFlags;
        [FieldOffset(1156)] internal uint OperStatus;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpForwardRow
    {
        internal uint Destination;
        internal uint Mask;
        internal uint Policy;
        internal uint NextHop;
        internal uint InterfaceIndex;
        internal uint Type;
        internal uint Protocol;
        internal uint Age;
        internal uint NextHopAs;
        internal uint Metric1;
        internal uint Metric2;
        internal uint Metric3;
        internal uint Metric4;
        internal uint Metric5;
    }

    // These dual COM interfaces retain the SDK method order. Only their
    // prefixes through the final method we use are declared.
    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface INetworkListManager
    {
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetworks(int flags);
        INetwork GetNetwork(Guid networkId);
        IEnumNetworkConnections GetNetworkConnections();
    }

    [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface IEnumNetworkConnections
    {
        [return: MarshalAs(UnmanagedType.Interface)] object GetNewEnumerator();
        [PreserveSig]
        int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out INetworkConnection connection, out uint fetched);
    }

    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface INetworkConnection
    {
        INetwork GetNetwork();
        bool IsConnectedToInternet { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        bool IsConnected { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
    }

    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)] string GetName();
        void SetName([MarshalAs(UnmanagedType.BStr)] string name);
        [return: MarshalAs(UnmanagedType.BStr)] string GetDescription();
        void SetDescription([MarshalAs(UnmanagedType.BStr)] string description);
        Guid GetNetworkId();
    }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyBag
    {
        [PreserveSig]
        int Read([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Struct)] out object value,
            IntPtr errorLog);
    }

    [ComVisible(true), Guid("DCB00004-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface INetworkEvents
    {
        void NetworkAdded(Guid networkId);
        void NetworkDeleted(Guid networkId);
        void NetworkConnectivityChanged(Guid networkId, int connectivity);
        void NetworkPropertyChanged(Guid networkId, int flags);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class NetworkEventsSink : INetworkEvents
    {
        private readonly Action _changed;
        internal NetworkEventsSink(Action changed) => _changed = changed;
        public void NetworkAdded(Guid networkId) => _changed();
        public void NetworkDeleted(Guid networkId) => _changed();
        public void NetworkConnectivityChanged(Guid networkId, int connectivity) => _changed();
        public void NetworkPropertyChanged(Guid networkId, int flags) => _changed();
    }
}
