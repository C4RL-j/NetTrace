using NetTrace.Models;

namespace NetTrace.Services;

public interface IDownloadSpeedTest : IDisposable
{
    Task<DownloadSpeedTestSnapshot> RunAsync(long byteLimit,
        IProgress<DownloadSpeedTestSnapshot>? progress, CancellationToken cancellationToken);
}
