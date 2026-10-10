namespace NetTrace.Models;

// VPN is an overlay on a stable physical session; it has no byte counters.
public sealed record VpnSession(
    long Id,
    long ConnectionSessionId,
    uint? InterfaceIndex,
    Guid? InterfaceGuid,
    string InterfaceName,
    string Description,
    VpnRoutingMode RoutingMode,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset LastConfirmedAtUtc,
    DateTimeOffset? EndedAtUtc,
    string? EndReason);
