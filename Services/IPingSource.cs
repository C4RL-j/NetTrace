using System.Net;
using NetTrace.Models;

namespace NetTrace.Services;

public interface IPingSource
{
    // Implementations should honor both the timeout and cancellation token.
    Task<IcmpPingReply> SendAsync(IPAddress target, TimeSpan timeout, CancellationToken cancellationToken);
}
