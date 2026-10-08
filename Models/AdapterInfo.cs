namespace NetTrace.Models;

public sealed record AdapterInfo(
    string Id,
    string Name,
    string Description,
    string Type,
    long SpeedBitsPerSecond,
    string IPv4Address,
    bool HasGateway);
