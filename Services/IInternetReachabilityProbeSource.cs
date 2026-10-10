namespace NetTrace.Services;

public interface IInternetReachabilityProbeSource
{
    // Implementations should honor cancellation. A received HTTPS response is
    // successful reachability even when its status code is not success-class.
    Task<bool> ResolveDnsAsync(string hostname, CancellationToken cancellationToken);
    Task<bool> ProbeHttpsAsync(Uri endpoint, CancellationToken cancellationToken);
}
