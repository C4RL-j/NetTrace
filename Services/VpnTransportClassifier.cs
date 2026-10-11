using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetTrace.Models;

namespace NetTrace.Services;

// Uses connected RAS server endpoints from the same local observation. DNS,
// ordinary sockets, adapter presence and application routing never prove a peer.
public static class VpnTransportClassifier
{
    public static (VpnTransportFamily Family, string Reason) Classify(VpnInterfaceInventory inventory,
        VpnTunnelStatus status, IReadOnlyList<VpnInterfaceEvidence> routeParticipants)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(routeParticipants);
        (VpnTransportFamily, string) Unknown(string reason) =>
            (VpnTransportFamily.Unknown, "Insufficient transport evidence: " + reason);
        if (status == VpnTunnelStatus.Inactive)
            return (VpnTransportFamily.Inactive, "No active VPN/tunnel is established by the effective internet routes.");
        if (status != VpnTunnelStatus.Active)
            return Unknown("an active VPN/tunnel has not been established.");
        if (!inventory.IsComplete)
            return Unknown("the local session/interface inventory is incomplete or unavailable.");

        var vpn = inventory.RasConnections.Where(connection =>
            string.Equals(connection.DeviceType, "vpn", StringComparison.OrdinalIgnoreCase)).Distinct().ToArray();
        if (vpn.Any(connection => connection.ErrorCode != 0 ||
            connection.ConnectionState == 0x2000 && connection.ConnectionSubstate is not (0 or 0x2000)))
            return Unknown("a VPN session has an error or an unconfirmed reconnect state.");
        var connected = vpn.Where(connection => connection.ConnectionState == 0x2000 &&
            connection.ErrorCode == 0 && connection.ConnectionSubstate is 0 or 0x2000).ToArray();
        if (connected.Length == 0)
            return Unknown("Windows exposes no confirmed connected RAS VPN server endpoint for this tunnel.");
        if (connected.Any(connection => string.IsNullOrWhiteSpace(connection.ConnectionName)) ||
            connected.Select(connection => connection.ConnectionName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != connected.Length)
            return Unknown("connected RAS VPN sessions do not have distinct confirmed identities.");

        var families = new HashSet<AddressFamily>();
        foreach (var connection in connected)
        {
            if (!IsUsablePeer(connection.RemoteEndpoint))
                return Unknown("a connected VPN session does not expose a usable remote server endpoint.");
            families.Add(connection.RemoteEndpoint!.AddressFamily);
        }
        // Two independent native VPN sessions prove active outer transport over
        // both possible families. This global Mixed claim needs no guess about
        // which single-family peer belongs to an opaque routed tunnel.
        if (families.Count == 2)
            return (VpnTransportFamily.Mixed,
                "Evidence: remote VPN server endpoints from independently confirmed active Windows RAS sessions over both IPv4 and IPv6. Application route families are evaluated separately.");

        // RAS phonebook GUIDs are not interface GUIDs. A unique PPP/session
        // association is required; an unrelated RAS peer cannot explain a custom
        // tunnel's transport. No process-name or socket heuristics are used.
        if (routeParticipants.Count == 0)
            return Unknown("the active tunnel has no confirmed route participant.");
        var associated = new HashSet<VpnRasConnectionEvidence>();
        foreach (var participant in routeParticipants)
        {
            if (participant.InterfaceType != 23 || participant.OperationalStatus != OperationalStatus.Up ||
                participant.InterfaceIndex == 0 || participant.InterfaceGuid == Guid.Empty ||
                !inventory.Interfaces.Contains(participant))
                return Unknown("the effective tunnel cannot be associated with a Windows RAS session.");
            var matches = connected.Where(connection =>
                Matches(participant.InterfaceName, connection.ConnectionName) ||
                Matches(participant.Description, connection.DeviceName)).ToArray();
            if (matches.Length != 1)
                return Unknown("the effective PPP tunnel has no unique connected RAS session association.");
            if (!associated.Add(matches[0]))
                return Unknown("multiple effective PPP tunnels share an ambiguous RAS session association.");
        }
        if (associated.Count != connected.Length)
            return Unknown("another connected RAS VPN session cannot be associated with the effective tunnel participants.");

        // Unknown or unassociated active siblings prevent a single-family claim.
        var family = families.Contains(AddressFamily.InterNetworkV6) ? VpnTransportFamily.IPv6 : VpnTransportFamily.IPv4;
        return (family, "Evidence: connected Windows RAS remote VPN server endpoint" +
            (connected.Length == 1 ? "." : "s from independently confirmed active sessions.") +
            " The effective PPP tunnel has a unique RAS session association; application route families are evaluated separately.");
    }

    private static bool Matches(string left, string right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsUsablePeer(IPAddress? address)
    {
        if (address is null || IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => bytes[0] is > 0 and < 224 &&
                !(bytes[0] == 169 && bytes[1] == 254),
            AddressFamily.InterNetworkV6 => !address.Equals(IPAddress.IPv6Any) &&
                !address.IsIPv6Multicast && !address.IsIPv6LinkLocal && !address.IsIPv4MappedToIPv6,
            _ => false
        };
    }
}
