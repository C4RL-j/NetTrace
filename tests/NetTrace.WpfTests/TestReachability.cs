using NetTrace.Services;

// Every owned monitoring fixture supplies this source (including child processes).
// The full WPF suite must never inherit real DNS/HTTPS probes from production defaults.
internal static class TestReachability
{
    public static InternetReachabilityMonitor Create() => new(new OfflineSource());

    private sealed class OfflineSource : IInternetReachabilityProbeSource
    {
        public Task<bool> ResolveDnsAsync(string hostname, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }

        public Task<bool> ProbeHttpsAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }
    }
}
