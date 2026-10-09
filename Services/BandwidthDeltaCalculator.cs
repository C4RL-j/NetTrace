using NetTrace.Models;

namespace NetTrace.Services;

// The dashboard and connection sessions consume the same valid counter delta.
public static class BandwidthDeltaCalculator
{
    public static BandwidthDelta? Calculate(NetworkSnapshot? previous, NetworkSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (previous is null || current.Timestamp <= previous.Timestamp) return null;
        return new BandwidthDelta(
            Math.Max(0, current.BytesReceived - previous.BytesReceived),
            Math.Max(0, current.BytesSent - previous.BytesSent),
            Math.Max(0.001, (current.Timestamp - previous.Timestamp).TotalSeconds));
    }
}
