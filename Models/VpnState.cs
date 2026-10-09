using System.Net.NetworkInformation;

namespace NetTrace.Models;

public enum VpnStatus
{
    NoVpn,
    TunnelDetected,
    Indeterminate
}

// A local tunnel candidate, not a claim about routes, encryption or peer reachability.
public sealed record VpnInterfaceInfo(
    uint? InterfaceIndex,
    Guid? InterfaceGuid,
    string InterfaceName,
    string Description,
    uint? InterfaceType,
    uint? TunnelType,
    OperationalStatus? OperationalStatus,
    string Evidence);

public sealed record VpnState(
    VpnStatus Status,
    IReadOnlyList<VpnInterfaceInfo> Interfaces,
    string? Message);
