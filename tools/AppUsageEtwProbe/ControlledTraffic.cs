using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace AppUsageEtwProbe;

public sealed record ControlledTransferResult(IpFamily Family, int ClientPid, int ServerPid,
    long UploadBytes, long DownloadBytes, bool ClientExitedCleanly, string? Error);

public static class ControlledTraffic
{
    public const int UploadBytes = 65536;
    public const int DownloadBytes = 131072;

    public static async Task<ControlledTransferResult> RunAsync(IpFamily family, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var token = timeout.Token;
        var listener = new TcpListener(family == IpFamily.Ipv4 ? IPAddress.Loopback : IPAddress.IPv6Loopback, 0);
        Process? child = null;
        try
        {
            listener.Start();
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(typeof(ControlledTraffic).Assembly.Location);
            start.ArgumentList.Add("--tcp-client");
            start.ArgumentList.Add(family.ToString());
            start.ArgumentList.Add(((IPEndPoint)listener.LocalEndpoint).Port.ToString());
            child = Process.Start(start) ?? throw new InvalidOperationException("Controlled child did not start.");
            using var socket = await listener.AcceptTcpClientAsync(token);
            using var stream = socket.GetStream();
            await ReadExactAsync(stream, UploadBytes, token);
            await WriteBytesAsync(stream, DownloadBytes, token);
            // Keep the known child alive until ETW has delivered its buffered events.
            await Task.Delay(1200, token);
            await stream.WriteAsync(new byte[1], token);
            await child.WaitForExitAsync(token);
            var exited = child.ExitCode == 0;
            return new(family, child.Id, Environment.ProcessId, UploadBytes, DownloadBytes, exited,
                exited ? null : "Controlled child transfer failed.");
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or InvalidOperationException)
        {
            return new(family, child?.Id ?? 0, Environment.ProcessId, 0, 0, false,
                $"Local controlled transfer unavailable: {ex.GetType().Name}");
        }
        finally
        {
            listener.Stop();
            if (child is not null)
            {
                if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(CancellationToken.None); }
                child.Dispose();
            }
        }
    }

    public static async Task<int> RunClientAsync(IpFamily family, int port)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            using var client = new TcpClient(family == IpFamily.Ipv4 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6);
            await client.ConnectAsync(family == IpFamily.Ipv4 ? IPAddress.Loopback : IPAddress.IPv6Loopback, port, timeout.Token);
            using var stream = client.GetStream();
            await WriteBytesAsync(stream, UploadBytes, timeout.Token);
            await ReadExactAsync(stream, DownloadBytes + 1, timeout.Token);
            return 0;
        }
        catch { return 1; }
    }

    private static async Task ReadExactAsync(NetworkStream stream, int count, CancellationToken token)
    {
        var buffer = new byte[16384];
        while (count > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(count, buffer.Length)), token);
            if (read == 0) throw new IOException("Local peer closed before the expected byte count.");
            count -= read;
        }
    }
    private static async Task WriteBytesAsync(NetworkStream stream, int count, CancellationToken token)
    {
        var buffer = new byte[16384];
        while (count > 0)
        {
            var write = Math.Min(count, buffer.Length);
            await stream.WriteAsync(buffer.AsMemory(0, write), token);
            count -= write;
        }
    }
}
