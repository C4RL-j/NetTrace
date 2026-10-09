using System.Net.NetworkInformation;

namespace NetTrace.Models;

// Native evidence stays separate from its conservative classification for deterministic tests.
public sealed record VpnInterfaceEvidence
{
    public uint InterfaceIndex { get; init; }
    public Guid InterfaceGuid { get; init; }
    public string InterfaceName { get; init; } = "";
    public string Description { get; init; } = "";
    public uint InterfaceType { get; init; }
    public uint TunnelType { get; init; }
    public OperationalStatus OperationalStatus { get; init; }
    public uint MediaConnectState { get; init; }
    public byte StatusFlags { get; init; }
    public string? DriverComponentId { get; init; }
    public string? DriverService { get; init; }
    public bool TapMediaAlwaysConnected { get; init; }
}

// RAS phonebook GUIDs and logon LUIDs are not network interface identifiers.
public sealed record VpnRasConnectionEvidence(
    string ConnectionName,
    string DeviceName,
    string DeviceType,
    uint ConnectionState,
    uint ErrorCode = 0,
    uint ConnectionSubstate = 0);

public sealed record VpnInterfaceInventory(
    IReadOnlyList<VpnInterfaceEvidence> Interfaces,
    IReadOnlyList<VpnRasConnectionEvidence> RasConnections,
    bool IsComplete = true,
    string? Diagnostic = null);
