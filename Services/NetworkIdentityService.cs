using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using NetTrace.Models;

namespace NetTrace.Services;

// Reads Windows' physical connection identity independently of bandwidth adapter selection.
// Native callbacks only queue changes; consumers debounce and query outside the UI thread.
public sealed class NetworkIdentityService : INetworkIdentityService
{
    private readonly object _gate = new();
    private readonly WindowsNetworkNative.WlanNotificationCallback _wlanCallback;
    private readonly WindowsNetworkNative.NetworkEventsSink _networkEventsSink;
    private IntPtr _wlanHandle;
    private object? _notificationManager;
    private IConnectionPoint? _connectionPoint;
    private int _connectionPointCookie;
    private volatile bool _disposed;
    private string? _detectionMessage;
    private string? _wlanError;

    public NetworkIdentityService()
    {
        _wlanCallback = (_, _) => QueueNetworkChanged();
        _networkEventsSink = new WindowsNetworkNative.NetworkEventsSink(QueueNetworkChanged);
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        EnsureWlanHandle();
        SubscribeNetworkListEvents();
    }

    public event EventHandler? NetworkChanged;

    public NetworkIdentityResult GetCurrentNetwork()
    {
        lock (_gate)
        {
            if (_disposed)
                return new(NetworkIdentityAvailability.Unavailable, null,
                    "Network identity detection is unavailable.");

            _detectionMessage = null;
            try
            {
                var routeMetrics = WindowsNetworkNative.GetPhysicalDefaultRouteMetrics();
                var candidates = new List<PhysicalCandidate>();
                var unreadableInterface = false;
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up ||
                        adapter.NetworkInterfaceType is not (NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet))
                        continue;

                    try
                    {
                        var properties = adapter.GetIPProperties();
                        var index = adapter.Supports(NetworkInterfaceComponent.IPv4)
                            ? properties.GetIPv4Properties()?.Index
                            : properties.GetIPv6Properties()?.Index;
                        if (index is null)
                        {
                            unreadableInterface = true;
                            continue;
                        }
                        if (!WindowsNetworkNative.IsConnectedHardwareInterface((uint)index.Value))
                            continue;

                        var hasGateway = properties.GatewayAddresses.Any(x =>
                            !x.Address.Equals(IPAddress.Any) && !x.Address.Equals(IPAddress.IPv6Any));
                        candidates.Add(new PhysicalCandidate(adapter, hasGateway,
                            routeMetrics.GetValueOrDefault((uint)index.Value, uint.MaxValue)));
                    }
                    catch (NetworkInformationException)
                    {
                        // A disconnect can remove an interface between enumeration and query.
                        unreadableInterface = true;
                    }
                }

                var selected = candidates.OrderByDescending(x => x.HasGateway)
                    .ThenBy(x => x.DefaultRouteMetric)
                    .ThenBy(x => x.Adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1)
                    .ThenBy(x => x.Adapter.Id, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (selected is null)
                {
                    return unreadableInterface
                        ? new(NetworkIdentityAvailability.Unavailable, null,
                            "Windows could not read the physical connection information. Its network identity is unavailable.")
                        : new(NetworkIdentityAvailability.Disconnected, null,
                            "No active physical Wi-Fi or Ethernet connection.");
                }

                var network = selected.Adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                    ? ReadWifiNetwork(selected.Adapter)
                    : ReadEthernetNetwork(selected.Adapter);
                return network is { HasValidIdentifier: true }
                    ? new(NetworkIdentityAvailability.Available, network, null)
                    : new(NetworkIdentityAvailability.Unavailable, null,
                        _detectionMessage ?? "Windows could not provide the connected network identity. Profile assignment is unavailable.");
            }
            catch (Exception ex)
            {
                _detectionMessage = $"Windows could not provide the network identity: {ex.Message}";
                Trace.TraceWarning(_detectionMessage);
                return new(NetworkIdentityAvailability.Unavailable, null, _detectionMessage);
            }
        }
    }

    private NetworkIdentity? ReadWifiNetwork(NetworkInterface adapter)
    {
        EnsureWlanHandle();
        if (_wlanHandle == IntPtr.Zero)
        {
            _detectionMessage = _wlanError;
            return null;
        }

        if (!Guid.TryParse(adapter.Id, out var adapterGuid))
        {
            _detectionMessage = "Windows did not provide a valid Wi-Fi adapter identifier.";
            return null;
        }

        IntPtr connectionData = IntPtr.Zero;
        try
        {
            // WLAN_INTF_OPCODE_CURRENT_CONNECTION = 7. Its SSID is the current
            // association's DOT11_SSID, not the Windows WLAN profile name.
            var result = WindowsNetworkNative.WlanQueryInterface(_wlanHandle, ref adapterGuid, 7,
                IntPtr.Zero, out var size, out connectionData, IntPtr.Zero);
            if (result != 0)
            {
                _detectionMessage = result switch
                {
                    5 => "Windows could not provide the Wi-Fi network identity (access denied). Check location access for desktop apps in Windows Settings, then reopen NetTrace.",
                    5023 => "The Wi-Fi connection is changing. Its SSID is not available yet.",
                    _ => $"Windows could not read the connected Wi-Fi SSID: {DescribeWindowsError(result)}"
                };
                return null;
            }

            if (size < Marshal.SizeOf<WindowsNetworkNative.WlanConnectionPrefix>())
                throw new InvalidOperationException("Windows returned incomplete Wi-Fi connection information.");
            var connection = Marshal.PtrToStructure<WindowsNetworkNative.WlanConnectionPrefix>(connectionData);
            if (connection.InterfaceState != 1 || connection.Ssid.Length is 0 or > 32)
            {
                _detectionMessage = "The connected Wi-Fi SSID is not available yet.";
                return null;
            }

            return CreateWifiIdentity(connection.Ssid.Bytes.AsSpan(0, (int)connection.Ssid.Length), adapter.Id);
        }
        finally
        {
            if (connectionData != IntPtr.Zero)
                WindowsNetworkNative.WlanFreeMemory(connectionData);
        }
    }

    internal static NetworkIdentity CreateWifiIdentity(ReadOnlySpan<byte> ssidBytes, string adapterId)
    {
        if (ssidBytes.Length is 0 or > 32)
            throw new ArgumentException("A Wi-Fi SSID must contain 1 to 32 bytes.", nameof(ssidBytes));

        try
        {
            // Strict UTF-8 prevents two different byte sequences from collapsing
            // into the same replacement characters. Do not trim or normalize SSIDs.
            var ssid = new UTF8Encoding(false, true).GetString(ssidBytes);
            if (!ssid.Any(char.IsControl))
                return new NetworkIdentity("WiFi", ssid, ssid, adapterId);
        }
        catch (DecoderFallbackException)
        {
            // SSIDs are arbitrary bytes; some do not have a text representation.
        }

        // A distinct mapping type prevents collisions with a literal hex-like SSID.
        var hex = Convert.ToHexString(ssidBytes);
        return new NetworkIdentity("WiFiBytes", hex, $"SSID bytes: {hex}", adapterId);
    }

    private NetworkIdentity? ReadEthernetNetwork(NetworkInterface adapter)
    {
        if (!Guid.TryParse(adapter.Id, out var adapterGuid))
        {
            _detectionMessage = "Ethernet is connected, but Windows did not provide a valid adapter identifier.";
            return null;
        }

        object? managerObject = null;
        WindowsNetworkNative.IEnumNetworkConnections? connections = null;
        try
        {
            managerObject = WindowsNetworkNative.CreateNetworkListManager();
            connections = ((WindowsNetworkNative.INetworkListManager)managerObject).GetNetworkConnections();
            while (connections.Next(1, out var connection, out var fetched) == 0 && fetched == 1)
            {
                WindowsNetworkNative.INetwork? network = null;
                try
                {
                    if (!connection.IsConnected || connection.GetAdapterId() != adapterGuid)
                        continue;

                    network = connection.GetNetwork();
                    var propertyBag = (WindowsNetworkNative.IPropertyBag)network;
                    if (propertyBag.Read("NA_NetworkClass", out var networkClass, IntPtr.Zero) != 0 ||
                        Convert.ToInt32(networkClass) != 2)
                    {
                        _detectionMessage = "Ethernet is connected, but Windows has not identified its network. Profile assignment will be available when a stable network identity is available.";
                        return null;
                    }

                    var networkId = network.GetNetworkId();
                    if (networkId == Guid.Empty)
                        break;
                    var name = network.GetName();
                    return new NetworkIdentity("Ethernet", networkId.ToString("D"),
                        string.IsNullOrWhiteSpace(name) ? "Ethernet network" : name, adapter.Id);
                }
                finally
                {
                    WindowsNetworkNative.ReleaseComObject(network);
                    WindowsNetworkNative.ReleaseComObject(connection);
                }
            }

            _detectionMessage = "Ethernet is connected, but Windows did not provide a stable network identity. This network cannot be assigned yet.";
            return null;
        }
        finally
        {
            WindowsNetworkNative.ReleaseComObject(connections);
            WindowsNetworkNative.ReleaseComObject(managerObject);
        }
    }

    private void EnsureWlanHandle()
    {
        if (_wlanHandle != IntPtr.Zero || _disposed)
            return;

        try
        {
            var result = WindowsNetworkNative.WlanOpenHandle(2, IntPtr.Zero, out _, out _wlanHandle);
            if (result != 0)
            {
                _wlanHandle = IntPtr.Zero;
                _wlanError = $"Wi-Fi identity is unavailable. Check that the Windows WLAN AutoConfig service is running: {DescribeWindowsError(result)}";
                return;
            }

            result = WindowsNetworkNative.WlanRegisterNotification(_wlanHandle, 8, true,
                _wlanCallback, IntPtr.Zero, IntPtr.Zero, out _);
            if (result != 0)
                Trace.TraceWarning($"Wi-Fi network change notifications are unavailable: {DescribeWindowsError(result)}");
        }
        catch (Exception ex)
        {
            _wlanError = $"Windows Native Wi-Fi is unavailable: {ex.Message}";
            Trace.TraceWarning(_wlanError);
        }
    }

    private void SubscribeNetworkListEvents()
    {
        try
        {
            _notificationManager = WindowsNetworkNative.CreateNetworkListManager();
            var eventId = typeof(WindowsNetworkNative.INetworkEvents).GUID;
            ((IConnectionPointContainer)_notificationManager).FindConnectionPoint(ref eventId, out _connectionPoint);
            _connectionPoint!.Advise(_networkEventsSink, out _connectionPointCookie);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Windows network identity notifications are unavailable: {ex.Message}");
            WindowsNetworkNative.ReleaseComObject(_connectionPoint);
            WindowsNetworkNative.ReleaseComObject(_notificationManager);
            _connectionPoint = null;
            _notificationManager = null;
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs args) => QueueNetworkChanged();
    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args) => QueueNetworkChanged();

    private void QueueNetworkChanged()
    {
        if (_disposed)
            return;

        // Never synchronously enter consumer code from a WLAN callback: native
        // unregistration waits for callbacks to exit and could otherwise deadlock.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (_disposed)
                return;
            try { NetworkChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { Trace.TraceWarning($"Network change handler failed: {ex.Message}"); }
        });
    }

    public void Dispose()
    {
        _disposed = true;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        lock (_gate)
        {
            if (_wlanHandle != IntPtr.Zero)
            {
                WindowsNetworkNative.WlanRegisterNotification(_wlanHandle, 0, true,
                    null, IntPtr.Zero, IntPtr.Zero, out _);
                WindowsNetworkNative.WlanCloseHandle(_wlanHandle, IntPtr.Zero);
                _wlanHandle = IntPtr.Zero;
                GC.KeepAlive(_wlanCallback);
            }

            try
            {
                if (_connectionPointCookie != 0)
                    _connectionPoint?.Unadvise(_connectionPointCookie);
            }
            catch (COMException ex) { Trace.TraceWarning($"Windows network notification cleanup failed: {ex.Message}"); }
            finally
            {
                _connectionPointCookie = 0;
                WindowsNetworkNative.ReleaseComObject(_connectionPoint);
                WindowsNetworkNative.ReleaseComObject(_notificationManager);
                _connectionPoint = null;
                _notificationManager = null;
                GC.KeepAlive(_networkEventsSink);
            }
        }
    }

    private static string DescribeWindowsError(uint error) =>
        $"{new Win32Exception((int)error).Message} (Windows error {error}).";

    private sealed record PhysicalCandidate(NetworkInterface Adapter, bool HasGateway, uint DefaultRouteMetric);
}
