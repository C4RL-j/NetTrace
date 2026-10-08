namespace NetTrace.Models;

// Availability describes detection, before any saved profile is resolved.
public enum NetworkIdentityAvailability
{
    Disconnected,
    Available,
    Unavailable
}

public sealed record NetworkIdentityResult(
    NetworkIdentityAvailability Availability,
    NetworkIdentity? Network,
    string? Message);
