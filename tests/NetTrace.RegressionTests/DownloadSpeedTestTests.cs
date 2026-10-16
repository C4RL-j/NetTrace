using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using NetTrace.Models;
using NetTrace.Services;

internal static class DownloadSpeedTestTests
{
    private const long Limit = 25_000_000;

    public static (string Name, Action Run)[] Cases =>
    [
        ("Download speed test accepts only the four decimal data limits", () => LimitsAsync().GetAwaiter().GetResult()),
        ("A real streamed HTTP download completes at the requested payload cap", () => CompleteAsync().GetAwaiter().GetResult()),
        ("Cancelling a pending body read promptly closes the download and preserves a partial result", () => CancelBodyAsync().GetAwaiter().GetResult()),
        ("Cancelling before HTTP response headers promptly aborts the request", () => CancelHeadersAsync().GetAwaiter().GetResult()),
        ("HTTP failure stays an error rather than a fabricated speed result", () => HttpErrorAsync().GetAwaiter().GetResult()),
        ("A mismatched response length cannot download beyond the selected cap", () => WrongLengthAsync().GetAwaiter().GetResult()),
        ("An interrupted HTTP body preserves measured bytes and reports an error", () => ShortBodyAsync().GetAwaiter().GetResult()),
        ("Concurrent speed tests are rejected and disposing stops the active download", () => DisposeAsync().GetAwaiter().GetResult())
    ];

    private static async Task LimitsAsync()
    {
        using var server = new LocalServer(async (_, stream, token) =>
            await Headers(stream, "500 Internal Server Error", 0, token));
        using var client = Client();
        using var test = new DownloadSpeedTest(client, server.Endpoint);
        foreach (var unsupported in new long[] { -1, 0, 1, 25 * 1024 * 1024, 30_000_000, 251_000_000 })
        {
            var rejected = false;
            try { await test.RunAsync(unsupported, null, CancellationToken.None); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Require(rejected, "An unsupported or binary-sized limit was accepted: " + unsupported);
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        foreach (var allowed in new long[] { 25_000_000, 50_000_000, 100_000_000, 250_000_000 })
        {
            var result = await test.RunAsync(allowed, null, cancelled.Token);
            Require(result.State == DownloadSpeedTestState.Cancelled && result.ByteLimit == allowed &&
                result.BytesReceived == 0 && !result.HasMeasurement,
                "A supported pre-cancelled request changed its cap or manufactured a speed result.");
        }
        Require(server.Requests.Count == 0, "A pre-cancelled test issued network traffic.");
    }

    private static async Task CompleteAsync()
    {
        using var server = new LocalServer(async (_, stream, token) =>
        {
            await Headers(stream, "200 OK", Limit, token);
            await Payload(stream, Limit, TimeSpan.FromMilliseconds(3), token);
        });
        using var client = Client();
        using var test = new DownloadSpeedTest(client, server.Endpoint);
        var reports = new Reports();
        var result = await test.RunAsync(Limit, reports, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        Require(result.State == DownloadSpeedTestState.Completed && result.BytesReceived == Limit &&
            result.ByteLimit == Limit && result.HasMeasurement && result.Elapsed > TimeSpan.Zero &&
            double.IsFinite(result.MegabitsPerSecond) && result.MegabitsPerSecond > 0,
            "The complete real HTTP payload did not produce a measured capped result.");
        var expectedRate = result.BytesReceived * 8d / result.Elapsed.TotalSeconds / 1_000_000;
        Require(Math.Abs(result.MegabitsPerSecond - expectedRate) < Math.Max(0.01, expectedRate * 0.001),
            "Final Mbps was not calculated from the received bytes and measured elapsed time.");
        var samples = reports.Items.ToArray();
        Require(samples.Any(sample => sample.State == DownloadSpeedTestState.Running && sample.BytesReceived > 0 &&
            sample.BytesReceived < Limit && sample.MegabitsPerSecond > 0) && samples[^1].State == DownloadSpeedTestState.Completed,
            "The streamed test did not report a live reading before completion.");
        Require(samples.All(sample => sample.BytesReceived >= 0 && sample.BytesReceived <= Limit) &&
            samples.Zip(samples.Skip(1), (first, next) => next.BytesReceived >= first.BytesReceived).All(value => value),
            "Progress exceeded the cap or moved backwards.");
        Require(server.Requests.Single().Contains("bytes=25000000", StringComparison.Ordinal),
            "The real endpoint request did not ask for exactly the selected data limit.");
    }

    private static async Task CancelBodyAsync()
    {
        var waitingForRead = NewSignal();
        var disconnected = NewSignal();
        using var server = new LocalServer(async (_, stream, token) =>
        {
            await Headers(stream, "200 OK", Limit, token);
            await Payload(stream, 2_000_000, TimeSpan.FromMilliseconds(25), token);
            waitingForRead.TrySetResult();
            await ObserveDisconnect(stream, disconnected, token);
        });
        using var client = Client();
        using var test = new DownloadSpeedTest(client, server.Endpoint);
        using var cancellation = new CancellationTokenSource();
        var running = test.RunAsync(Limit, new Reports(), cancellation.Token);
        await waitingForRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The response is now stalled: cancelling must interrupt an outstanding read,
        // rather than merely preventing the next read after more data arrives.
        await Task.Delay(80);
        var elapsed = Stopwatch.StartNew();
        cancellation.Cancel();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(2));
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Require(elapsed.Elapsed < TimeSpan.FromSeconds(2) && result.State == DownloadSpeedTestState.Cancelled &&
            result.BytesReceived is >= 1_000_000 and < Limit && result.HasMeasurement && result.MegabitsPerSecond > 0,
            "Cancelling the blocked body did not stop promptly with a useful partial measurement.");
        var received = result.BytesReceived;
        await Task.Delay(80);
        Require(result.BytesReceived == received && server.Requests.Count == 1,
            "Cancelled testing restarted the network transfer.");
    }

    private static async Task CancelHeadersAsync()
    {
        var requested = NewSignal();
        var disconnected = NewSignal();
        using var server = new LocalServer(async (_, stream, token) =>
        {
            requested.TrySetResult();
            await ObserveDisconnect(stream, disconnected, token);
        });
        using var client = Client();
        using var test = new DownloadSpeedTest(client, server.Endpoint);
        using var cancellation = new CancellationTokenSource();
        var running = test.RunAsync(Limit, null, cancellation.Token);
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(2));
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Require(result.State == DownloadSpeedTestState.Cancelled && result.BytesReceived == 0 && !result.HasMeasurement,
            "Cancellation before headers looked like an error or invented a partial speed.");
    }

    private static async Task HttpErrorAsync()
    {
        using var server = new LocalServer(async (_, stream, token) =>
            await Headers(stream, "503 Service Unavailable", 0, token));
        using var client = Client();
        using var test = new DownloadSpeedTest(client, server.Endpoint);
        var result = await test.RunAsync(Limit, null, CancellationToken.None);
        Require(result.State == DownloadSpeedTestState.Error && result.BytesReceived == 0 && !result.HasMeasurement &&
            !string.IsNullOrWhiteSpace(result.ErrorMessage), "HTTP failure was not reported as an actionable error.");
    }

    private static async Task WrongLengthAsync()
    {
        using var server = new LocalServer(async (_, stream, token) =>
            await Headers(stream, "200 OK", Limit + 1, token));
        using var client = Client();
        using var test = new DownloadSpeedTest(client, server.Endpoint);
        var result = await test.RunAsync(Limit, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        Require(result.State == DownloadSpeedTestState.Error && result.BytesReceived == 0 &&
            !string.IsNullOrWhiteSpace(result.ErrorMessage), "A server exceeding the requested cap was accepted.");
    }

    private static async Task ShortBodyAsync()
    {
        using var server = new LocalServer(async (_, stream, token) =>
        {
            await Headers(stream, "200 OK", Limit, token);
            await Payload(stream, 2_000_000, TimeSpan.FromMilliseconds(20), token);
        });
        using var client = Client();
        using var test = new DownloadSpeedTest(client, server.Endpoint);
        var result = await test.RunAsync(Limit, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Require(result.State == DownloadSpeedTestState.Error && result.BytesReceived == 2_000_000 && result.HasMeasurement &&
            !string.IsNullOrWhiteSpace(result.ErrorMessage), "An interrupted transfer lost real bytes or claimed completion.");
    }

    private static async Task DisposeAsync()
    {
        var requested = NewSignal();
        var disconnected = NewSignal();
        using var server = new LocalServer(async (_, stream, token) =>
        {
            await Headers(stream, "200 OK", Limit, token);
            requested.TrySetResult();
            await ObserveDisconnect(stream, disconnected, token);
        });
        using var client = Client();
        var test = new DownloadSpeedTest(client, server.Endpoint);
        try
        {
            var running = test.RunAsync(Limit, null, CancellationToken.None);
            await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var rejected = false;
            try { await test.RunAsync(Limit, null, CancellationToken.None); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected && server.Requests.Count == 1, "A second simultaneous test issued another download.");
            test.Dispose();
            var result = await running.WaitAsync(TimeSpan.FromSeconds(2));
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Require(result.State == DownloadSpeedTestState.Cancelled, "Disposal did not stop the active transfer.");
            // The caller's HttpClient remains usable after service disposal.
            using var response = await client.GetAsync(server.Endpoint, HttpCompletionOption.ResponseHeadersRead)
                .WaitAsync(TimeSpan.FromSeconds(3));
            Require(response.IsSuccessStatusCode, "Disposing the service also disposed its injected client.");
        }
        finally { test.Dispose(); }
    }

    private static HttpClient Client() => new(new SocketsHttpHandler
    {
        UseProxy = false, AutomaticDecompression = DecompressionMethods.None,
        MaxResponseDrainSize = 0, ConnectTimeout = TimeSpan.FromSeconds(3)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task Headers(NetworkStream stream, string status, long length, CancellationToken token)
    {
        var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: {length}\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, token);
        await stream.FlushAsync(token);
    }

    private static async Task Payload(NetworkStream stream, long length, TimeSpan delay, CancellationToken token)
    {
        var chunk = new byte[128 * 1024];
        while (length > 0)
        {
            var count = (int)Math.Min(length, chunk.Length);
            await stream.WriteAsync(chunk.AsMemory(0, count), token);
            length -= count;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
        }
        await stream.FlushAsync(token);
    }

    private static async Task ObserveDisconnect(NetworkStream stream, TaskCompletionSource disconnected, CancellationToken token)
    {
        try
        {
            var buffer = new byte[1];
            if (await stream.ReadAsync(buffer, token) == 0) disconnected.TrySetResult();
        }
        catch (IOException) { disconnected.TrySetResult(); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Reports : IProgress<DownloadSpeedTestSnapshot>
    {
        public ConcurrentQueue<DownloadSpeedTestSnapshot> Items { get; } = new();
        public void Report(DownloadSpeedTestSnapshot value) => Items.Enqueue(value);
    }

    private sealed class LocalServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Func<string, NetworkStream, CancellationToken, Task> _respond;
        private readonly Task _accept;
        private readonly ConcurrentBag<Task> _connections = new();
        public ConcurrentQueue<string> Requests { get; } = new();
        public Uri Endpoint { get; }

        public LocalServer(Func<string, NetworkStream, CancellationToken, Task> respond)
        {
            _respond = respond;
            _listener.Start();
            Endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/__down");
            _accept = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                    _connections.Add(RespondAsync(client));
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) when (_lifetime.IsCancellationRequested) { }
        }

        private async Task RespondAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                try
                {
                    var header = new List<byte>();
                    var one = new byte[1];
                    while (header.Count < 16_384 && await stream.ReadAsync(one, _lifetime.Token) > 0)
                    {
                        header.Add(one[0]);
                        if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10)
                            break;
                    }
                    var request = Encoding.ASCII.GetString(header.ToArray());
                    Requests.Enqueue(request);
                    await _respond(request, stream, _lifetime.Token);
                }
                catch (IOException) { }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            }
        }

        public void Dispose()
        {
            _lifetime.Cancel();
            _listener.Stop();
            try
            {
                _accept.Wait(TimeSpan.FromSeconds(2));
                Task.WhenAll(_connections).Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException) { }
            _lifetime.Dispose();
        }
    }
}
