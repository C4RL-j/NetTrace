using System.Diagnostics;
using AppUsageEtwProbe;

internal static class ProbeOfflineTrafficTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("A separate local TCP client transfers known upload and download volumes over IPv4", () => Transfer(IpFamily.Ipv4)),
        ("A separate local TCP client transfers known upload and download volumes over IPv6", () => Transfer(IpFamily.Ipv6)),
        ("Cancelling a controlled TCP workload cleans up its child process", CancelledTransfer),
    ];

    private static async Task Transfer(IpFamily family)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var result = await ControlledTraffic.RunAsync(family, timeout.Token).WaitAsync(TimeSpan.FromSeconds(13));
        Check.Equal<string?>(null, result.Error, "offline TCP transfer succeeds");
        Check.True(result.ClientExitedCleanly, "controlled client exits cleanly");
        Check.Equal(family, result.Family, "requested address family");
        Check.Equal((long)ControlledTraffic.UploadBytes, result.UploadBytes, "known upload payload");
        Check.Equal((long)ControlledTraffic.DownloadBytes, result.DownloadBytes, "known download payload excluding completion marker");
        Check.True(result.ClientPid > 0 && result.ClientPid != Environment.ProcessId, "client is a separate attributable process");
        Check.Equal(Environment.ProcessId, result.ServerPid, "server PID is retained");
        Check.True(!IsRunning(result.ClientPid), "controlled child is no longer running after completion");
    }

    private static async Task CancelledTransfer()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await ControlledTraffic.RunAsync(IpFamily.Ipv4, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5));
        Check.True(!result.ClientExitedCleanly, "cancelled transfer is not reported successful");
        Check.True(result.Error?.Contains("CanceledException", StringComparison.Ordinal) == true,
            "controlled workload cancellation is explained");
        Check.True(result.ClientPid == 0 || !IsRunning(result.ClientPid), "cancelled workload does not leave its child alive");
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
