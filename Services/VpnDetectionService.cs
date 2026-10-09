using System.Net.NetworkInformation;
using NetTrace.Models;

namespace NetTrace.Services;

// On-demand foundation only: no callbacks, routes, database, UI or traffic accounting.
public sealed class VpnDetectionService
{
    private readonly IVpnInterfaceSource _source;

    // Stable component/service IDs from the vendors' driver INF files, not display names.
    private static readonly HashSet<string> TunnelDrivers = new(StringComparer.OrdinalIgnoreCase)
    {
        "wireguard", "wintun", "tap0901", "ovpn-dco"
    };
    private static readonly HashSet<string> OrdinaryVirtualDrivers = new(StringComparer.OrdinalIgnoreCase)
    {
        "vms_mp", "ms_vms_mp", "vmsmp", "vmnetadapter", "vmxnet3",
        "vboxnetadp", "vboxnetadp6", "sun_vboxnetadp", "ms_loop"
    };

    public VpnDetectionService(IVpnInterfaceSource? source = null) =>
        _source = source ?? new WindowsVpnNative();

    public VpnState GetCurrentState()
    {
        try { return Classify(_source.Read()); }
        catch (Exception exception)
        {
            return new(VpnStatus.Indeterminate, Array.Empty<VpnInterfaceInfo>(),
                $"Windows could not determine VPN/tunnel state: {exception.Message}");
        }
    }

    public static VpnState Classify(VpnInterfaceInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var active = new List<VpnInterfaceInfo>();
        var uncertain = new List<VpnInterfaceInfo>();
        foreach (var connection in inventory.RasConnections)
        {
            if (!connection.DeviceType.Equals("vpn", StringComparison.OrdinalIgnoreCase)) continue;
            if (connection.ErrorCode != 0)
            {
                uncertain.Add(RasInfo(connection, "Windows RAS reported a VPN connection error."));
                continue;
            }
            if (connection.ConnectionState != 0x2000) continue; // RASCS_Connected
            // IKEv2 can retain Connected while its tunnel is dormant or reconnecting.
            if (connection.ConnectionSubstate is not (0 or 0x2000)) // Unknown / Reconnected
            {
                uncertain.Add(RasInfo(connection, "Windows RAS reports a dormant or unconfirmed VPN substate."));
                continue;
            }
            active.Add(RasInfo(connection, "Windows RAS confirms a connected VPN."));
        }

        foreach (var item in inventory.Interfaces)
        {
            const byte hardware = 1, filter = 2, notAuthenticated = 8,
                notMediaConnected = 16, paused = 32, lowPower = 64, endpoint = 128;
            if (item.InterfaceType == 24 || (item.StatusFlags & (hardware | filter | endpoint)) != 0
                || Matches(OrdinaryVirtualDrivers, item)) continue;

            var driver = Matches(TunnelDrivers, item);
            var tunnel = item.InterfaceType == 131;
            // Standard IPv6 transition mechanisms are not VPN recognition evidence.
            if (!driver && (!tunnel || item.TunnelType is 11 or 13 or 14)) continue;
            if (item.OperationalStatus is OperationalStatus.Down or OperationalStatus.Dormant
                or OperationalStatus.NotPresent or OperationalStatus.LowerLayerDown
                || item.MediaConnectState == 2
                || (item.StatusFlags & (notMediaConnected | paused | lowPower)) != 0) continue;

            var tapAlwaysUp = item.TapMediaAlwaysConnected
                && (IsId(item.DriverComponentId, "tap0901") || IsId(item.DriverService, "tap0901"));
            var validDriverType = item.InterfaceType is 6 or 53 or 131;
            if (item.OperationalStatus != OperationalStatus.Up || (item.StatusFlags & notAuthenticated) != 0
                || item.MediaConnectState > 2
                || tapAlwaysUp || (driver && !validDriverType)
                || (!driver && (item.MediaConnectState != 1 || item.TunnelType is not (1 or 2 or 15))))
            {
                uncertain.Add(Info(item, tapAlwaysUp
                    ? "TAP is configured Always Connected; link state cannot confirm an active tunnel."
                    : "Windows supplied incomplete or ambiguous tunnel status."));
                continue;
            }
            active.Add(Info(item, driver
                ? "An identified tunnel driver has an operational local interface."
                : "Windows reports an operational encapsulation tunnel interface."));
        }

        if (active.Count > 0)
            return new(VpnStatus.TunnelDetected, active.AsReadOnly(), inventory.Diagnostic);
        if (!inventory.IsComplete || uncertain.Count > 0)
            return new(VpnStatus.Indeterminate, uncertain.AsReadOnly(), inventory.Diagnostic
                ?? "Windows could not conclusively determine whether a supported local tunnel is active.");
        return new(VpnStatus.NoVpn, Array.Empty<VpnInterfaceInfo>(), null);
    }

    private static bool Matches(HashSet<string> ids, VpnInterfaceEvidence item) =>
        (item.DriverComponentId is { } component && ids.Contains(component))
        || (item.DriverService is { } service && ids.Contains(service));

    private static bool IsId(string? value, string id) => string.Equals(value, id, StringComparison.OrdinalIgnoreCase);

    private static VpnInterfaceInfo Info(VpnInterfaceEvidence item, string evidence) => new(
        item.InterfaceIndex == 0 ? null : item.InterfaceIndex,
        item.InterfaceGuid == Guid.Empty ? null : item.InterfaceGuid,
        item.InterfaceName, item.Description, item.InterfaceType, item.TunnelType,
        item.OperationalStatus, evidence);

    private static VpnInterfaceInfo RasInfo(VpnRasConnectionEvidence connection, string evidence) => new(
        null, null, connection.ConnectionName, connection.DeviceName, null, null, null, evidence);
}
