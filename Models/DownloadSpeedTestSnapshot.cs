namespace NetTrace.Models;

public enum DownloadSpeedTestState
{
    Idle,
    Running,
    Cancelled,
    Completed,
    Error
}

// Payload bytes only. Windows adapter counters remain the source of NetTrace's
// normal usage totals, including the request and protocol overhead of this test.
public sealed record DownloadSpeedTestSnapshot(
    DownloadSpeedTestState State,
    long BytesReceived,
    long ByteLimit,
    double MegabitsPerSecond,
    TimeSpan Elapsed,
    string? ErrorMessage = null)
{
    public const long MinimumMeasurementBytes = 1_000_000;
    public static TimeSpan MinimumMeasurementDuration => TimeSpan.FromMilliseconds(250);

    // A cancelled test with enough observations is a partial measurement rather
    // than a failed test. A complete fast download still has a valid measurement.
    public bool HasMeasurement => BytesReceived >= MinimumMeasurementBytes &&
        (Elapsed >= MinimumMeasurementDuration || State == DownloadSpeedTestState.Completed);
}
