using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using NetTrace.Models;

namespace NetTrace.Services;

// A user-started, single download through normal Windows routing. This service
// never writes or subtracts usage: the existing adapter monitor counts its traffic.
public sealed class DownloadSpeedTest : IDownloadSpeedTest
{
    public const long Megabyte = 1_000_000;
    public static IReadOnlyList<long> ByteLimits { get; } = Array.AsReadOnly(new[]
    {
        25 * Megabyte, 50 * Megabyte, 100 * Megabyte, 250 * Megabyte
    });
    public static Uri DefaultEndpoint { get; } = new("https://speed.cloudflare.com/__down");
    public static TimeSpan DefaultTimeout => TimeSpan.FromMinutes(5);

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);
    private readonly object _gate = new();
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly Uri _endpoint;
    private readonly TimeSpan _timeout;
    private CancellationTokenSource? _activeCancellation;
    private bool _disposed;

    public DownloadSpeedTest(HttpClient? httpClient = null, Uri? endpoint = null, TimeSpan? timeout = null)
    {
        _endpoint = endpoint ?? DefaultEndpoint;
        if (!_endpoint.IsAbsoluteUri || _endpoint.UserInfo.Length != 0 ||
            (_endpoint.Scheme != Uri.UriSchemeHttps &&
                !(_endpoint.Scheme == Uri.UriSchemeHttp && _endpoint.IsLoopback)))
            throw new ArgumentException("Use HTTPS, or an HTTP loopback endpoint for testing, without credentials.", nameof(endpoint));
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero || _timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            Credentials = null,
            DefaultProxyCredentials = null,
            PreAuthenticate = false,
            // Cancellation must close the body, rather than downloading the rest
            // in the background to reuse the connection.
            MaxResponseDrainSize = 0
        }, disposeHandler: true)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };
    }

    public Task<DownloadSpeedTestSnapshot> RunAsync(long byteLimit,
        IProgress<DownloadSpeedTestSnapshot>? progress, CancellationToken cancellationToken)
    {
        if (!ByteLimits.Contains(byteLimit))
            throw new ArgumentOutOfRangeException(nameof(byteLimit), "Choose 25, 50, 100 or 250 MB.");

        CancellationTokenSource cancellation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeCancellation is not null)
                throw new InvalidOperationException("A speed test is already running.");
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellation.CancelAfter(_timeout);
            _activeCancellation = cancellation;
        }
        return RunCoreAsync(byteLimit, progress, cancellationToken, cancellation);
    }

    private async Task<DownloadSpeedTestSnapshot> RunCoreAsync(long byteLimit,
        IProgress<DownloadSpeedTestSnapshot>? progress, CancellationToken callerToken,
        CancellationTokenSource cancellation)
    {
        var watch = Stopwatch.StartNew();
        long bytesReceived = 0;
        var lastProgress = TimeSpan.Zero;
        var state = DownloadSpeedTestState.Running;
        string? errorMessage = null;

        DownloadSpeedTestSnapshot Snapshot() => new(state, bytesReceived, byteLimit,
            watch.Elapsed.TotalSeconds > 0 ? bytesReceived * 8d / watch.Elapsed.TotalSeconds / 1_000_000 : 0,
            watch.Elapsed, errorMessage);

        bool WasCancelledByUser()
        {
            lock (_gate) return callerToken.IsCancellationRequested || _disposed;
        }

        try
        {
            try
            {
                var token = cancellation.Token;
                token.ThrowIfCancellationRequested();
                progress?.Report(Snapshot());
                using var request = new HttpRequestMessage(HttpMethod.Get, BuildRequestUri(byteLimit));
                request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
                request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
                request.Headers.ConnectionClose = true;

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    token).ConfigureAwait(false);
                // Also abort the response stream if a pending read needs disposal
                // to interrupt it. The production handler disables response drain.
                using var stopResponse = token.UnsafeRegister(static value =>
                {
                    try { ((HttpResponseMessage)value!).Dispose(); }
                    // An already interrupted stream must not make the caller's
                    // Cancel action throw. Pending reads also receive the token.
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                }, response);
                token.ThrowIfCancellationRequested();
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"Speed-test server returned HTTP {(int)response.StatusCode}.",
                        null, response.StatusCode);
                if (response.Content.Headers.ContentEncoding.Any(value =>
                    !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("The speed-test server returned compressed data.");
                if (response.Content.Headers.ContentLength is { } contentLength && contentLength != byteLimit)
                    throw new InvalidDataException("The speed-test server returned an unexpected data size.");

                // Measure the actual body transfer, excluding DNS, connection,
                // TLS and response-header setup. The linked timeout still covers
                // the entire operation, including that setup.
                watch.Restart();
                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    while (bytesReceived < byteLimit)
                    {
                        token.ThrowIfCancellationRequested();
                        var remaining = (int)Math.Min(buffer.Length, byteLimit - bytesReceived);
                        var received = await stream.ReadAsync(buffer.AsMemory(0, remaining), token).ConfigureAwait(false);
                        if (received == 0)
                            throw new EndOfStreamException("The speed-test download ended before the selected data limit.");
                        bytesReceived += received;
                        if (watch.Elapsed - lastProgress >= ProgressInterval)
                        {
                            lastProgress = watch.Elapsed;
                            progress?.Report(Snapshot());
                        }
                    }
                    token.ThrowIfCancellationRequested();
                    state = DownloadSpeedTestState.Completed;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (OperationCanceledException)
            {
                state = WasCancelledByUser() ? DownloadSpeedTestState.Cancelled : DownloadSpeedTestState.Error;
                if (state == DownloadSpeedTestState.Error) errorMessage = "The speed test timed out. Try again.";
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or ObjectDisposedException)
            {
                state = WasCancelledByUser() ? DownloadSpeedTestState.Cancelled : DownloadSpeedTestState.Error;
                if (state == DownloadSpeedTestState.Error)
                    errorMessage = cancellation.IsCancellationRequested
                        ? "The speed test timed out. Try again."
                        : ex.Message;
            }

            watch.Stop();
            var result = Snapshot();
            progress?.Report(result);
            return result;
        }
        finally
        {
            watch.Stop();
            lock (_gate)
            {
                if (ReferenceEquals(_activeCancellation, cancellation)) _activeCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private Uri BuildRequestUri(long byteLimit)
    {
        // Cloudflare documents __down as its download API and "bytes" as the
        // requested response-body size. No upload or extra measurement requests.
        // https://github.com/cloudflare/speedtest/blob/main/README.md
        // https://github.com/cloudflare/worker-speedtest-template/blob/master/README.md
        var builder = new UriBuilder(_endpoint)
        {
            Query = $"bytes={byteLimit.ToString(CultureInfo.InvariantCulture)}&nonce={Guid.NewGuid():N}"
        };
        return builder.Uri;
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            cancellation = _activeCancellation;
        }
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        if (_ownsClient) _httpClient.Dispose();
    }
}
