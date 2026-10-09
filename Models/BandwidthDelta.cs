namespace NetTrace.Models;

public sealed record BandwidthDelta(long DownloadBytes, long UploadBytes, double ElapsedSeconds);
