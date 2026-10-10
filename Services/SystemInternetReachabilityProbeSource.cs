using System.Net;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;

namespace NetTrace.Services;

// One reusable client uses Windows routing, system DNS/proxy behavior and normal
// TLS validation. Probes send no account, device, usage or NetTrace information.
public sealed class SystemInternetReachabilityProbeSource : IInternetReachabilityProbeSource, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _dnsResolver;
    private int _disposed;

    public SystemInternetReachabilityProbeSource(HttpMessageHandler? handler = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? dnsResolver = null)
    {
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            Credentials = null,
            DefaultProxyCredentials = null,
            PreAuthenticate = false,
            // Disposing a headers-only response must not drain an unexpectedly
            // large response body just to reuse the connection.
            MaxResponseDrainSize = 0
        };
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            // A single monitor-owned deadline bounds all requests in a cycle.
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };
        _dnsResolver = dnsResolver ?? ((hostname, cancellationToken) =>
            Dns.GetHostAddressesAsync(hostname, cancellationToken));
    }

    public async Task<bool> ResolveDnsAsync(string hostname, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var addresses = await _dnsResolver(hostname, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return addresses.Length > 0;
        }
        catch (SocketException) { return false; }
    }

    public async Task<bool> ProbeHttpsAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps || endpoint.UserInfo.Length != 0)
            throw new ArgumentException("A reachability endpoint must be an HTTPS URI without credentials.", nameof(endpoint));
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // Do not call EnsureSuccessStatusCode: 301, 403 and 404 prove that
            // a remote HTTPS service responded just as 200 and 204 do.
            return (int)response.StatusCode is >= 100 and <= 599;
        }
        catch (HttpRequestException) { return false; }
        catch (IOException) { return false; }
        catch (AuthenticationException) { return false; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _httpClient.Dispose();
    }
}
