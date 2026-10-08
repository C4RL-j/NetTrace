namespace NetTrace.Models;

public enum ConnectionStatus
{
    Detecting,
    Disconnected,
    ConnectedIdentityUnavailable,
    ConnectedUnassigned,
    ConnectedRecognized
}

public sealed record CurrentConnectionState(
    ConnectionStatus Status,
    NetworkIdentity? Network,
    ConnectionProfile? Profile,
    string? Message)
{
    public bool CanAssign => Status == ConnectionStatus.ConnectedUnassigned &&
        Network is { HasValidIdentifier: true };
}
