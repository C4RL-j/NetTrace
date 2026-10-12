using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace AppUsageDiagnosticClient;

public static class BoundedStress
{
    // Owned loopback messages, 15 seconds and <=16 MiB aggregate payload.
    // There is no external traffic, packet capture or payload logging.
    public static async Task<StressResult> RunAsync(CancellationToken cancellationToken)
    {
        const int messageBytes = 256, maximumMessages = 16 * 1024 * 1024 / (2 * messageBytes);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stopping.CancelAfter(TimeSpan.FromSeconds(16));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = ServeAsync();
        int messages = 0;
        var elapsed = Stopwatch.StartNew();
        bool complete = false;
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, port, stopping.Token);
            using var stream = client.GetStream();
            var payload = new byte[messageBytes];
            while (messages < maximumMessages && elapsed.Elapsed < TimeSpan.FromSeconds(15))
            {
                await stream.WriteAsync(payload, stopping.Token);
                await stream.ReadExactlyAsync(payload, stopping.Token);
                messages++;
                if (messages % 64 == 0) await Task.Delay(30, stopping.Token);
            }
            complete = true;
        }
        finally
        {
            stopping.Cancel(); listener.Stop();
            try { await server.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException) { }
            elapsed.Stop();
        }
        return new(messages, messages * (long)messageBytes * 2, elapsed.Elapsed.TotalSeconds, complete);

        async Task ServeAsync()
        {
            using var peer = await listener.AcceptTcpClientAsync(stopping.Token);
            peer.NoDelay = true;
            using var stream = peer.GetStream();
            var payload = new byte[messageBytes];
            while (!stopping.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(payload, stopping.Token);
                await stream.WriteAsync(payload, stopping.Token);
            }
        }
    }
}
