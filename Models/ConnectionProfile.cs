namespace NetTrace.Models;

public sealed record ConnectionProfile(
    long Id,
    string TelcoName,
    string DeviceName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string DisplayName => $"{TelcoName} — {DeviceName}";
}
