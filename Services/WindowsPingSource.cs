using System.Net;
using System.Net.NetworkInformation;
using NetTrace.Models;

namespace NetTrace.Services;

public sealed class WindowsPingSource : IPingSource
{
    public async Task<IcmpPingReply> SendAsync(IPAddress target, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        using var ping = new Ping();
        var reply = await ping.SendPingAsync(target, timeout, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new IcmpPingReply(reply.Status, reply.RoundtripTime);
    }
}
