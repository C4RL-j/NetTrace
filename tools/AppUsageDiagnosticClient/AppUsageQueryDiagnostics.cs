using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppUsageIpc;

namespace AppUsageDiagnosticClient;

// This diagnostic mode reads only typed logical usage over the authenticated
// service pipe. It never opens SQLite or requests physical adapter accounting.
internal static class AppUsageQueryDiagnostics
{
    public static async Task<int> RunAsync(string pipeName, IReadOnlyList<AppUsageRange> ranges,
        int queryCount, int seconds, bool recover, bool quiet, string? stopWhenFile,
        string output, CancellationToken cancellationToken)
    {
        using var process = Process.GetCurrentProcess();
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(TimeSpan.FromSeconds(seconds));
        var elapsed = Stopwatch.StartNew();
        var observations = new List<AppUsageQueryObservation>();
        var connections = new List<AppUsageQueryConnection>();
        var backoff = new ReconnectBackoff();
        IpcClient? client = null;
        AppUsageQueryResources? baseline = null;
        int completed = 0;
        bool cancelled = false;
        Console.WriteLine($"Client PID: {Environment.ProcessId}; elevated: False; protocol: {Protocol.ServiceVersion}; read-only App Usage.");
        try
        {
            while (completed < queryCount * ranges.Count)
            {
                if (stopWhenFile is not null && File.Exists(stopWhenFile)) break;
                window.Token.ThrowIfCancellationRequested();
                if (client is null)
                {
                    client = new IpcClient(pipeName, serviceName: ServicePipeSecurity.DefaultServiceName);
                    var hello = await client.ConnectAsync(window.Token);
                    Observe(hello.State, hello.Detail);
                    if (hello.State != IpcState.Connected)
                    {
                        await client.DisposeAsync(); client = null;
                        if (!recover) break;
                        await Task.Delay(backoff.NextDelay(), window.Token);
                        continue;
                    }
                    backoff.Reset();
                }
                var range = ranges[completed % ranges.Count];
                var watch = Stopwatch.StartNew();
                var result = await client.GetAppUsageRangeAsync(range, window.Token);
                watch.Stop();
                Observe(result.State, result.Detail);
                observations.Add(new(range, watch.Elapsed.TotalMilliseconds, result.State,
                    result.Value, result.Detail));
                if (result.State != IpcState.Connected || result.Value is null)
                {
                    await client.DisposeAsync(); client = null;
                    if (!recover) break;
                    await Task.Delay(backoff.NextDelay(), window.Token);
                    continue;
                }
                completed++;
                if (!quiet || completed <= ranges.Count) Print(result.Value);
                // Establish the resource baseline after one whole range set has
                // warmed serialization, SQLite on the service, and this pipe.
                if (completed == ranges.Count) baseline = Resources(process);
                if (completed < queryCount * ranges.Count)
                    await Task.Delay(TimeSpan.FromSeconds(1), window.Token);
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = cancellationToken.IsCancellationRequested;
            Observe(cancelled ? IpcState.Cancelled : IpcState.TimedOut,
                cancelled ? "Diagnostic cancelled." : "The bounded query window ended.");
        }
        finally
        {
            if (client is not null) await client.DisposeAsync();
        }
        elapsed.Stop();
        var final = Resources(process);
        var report = new AppUsageQueryReport(false, Environment.ProcessId, Protocol.ServiceVersion,
            completed, queryCount * ranges.Count, observations, connections, baseline, final,
            elapsed.Elapsed.TotalSeconds, cancelled);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        }));
        foreach (var range in ranges)
        {
            var successful = observations.Where(value => value.Range == range && value.State == IpcState.Connected)
                .Select(value => value.QueryLatencyMs).ToArray();
            if (successful.Length != 0)
                Console.WriteLine($"{Label(range)} query latency: first={successful[0]:F2} ms; median={successful.Order().ElementAt(successful.Length / 2):F2} ms; max={successful.Max():F2} ms; reads={successful.Length}.");
        }
        if (baseline is not null)
            Console.WriteLine($"Client resources after warmup: handles {baseline.HandleCount} → {final.HandleCount}; private bytes {baseline.PrivateBytes} → {final.PrivateBytes}; working set {baseline.WorkingSetBytes} → {final.WorkingSetBytes}.");
        Console.WriteLine($"Diagnostic result: {output}");
        return cancelled ? 130 : completed == queryCount * ranges.Count ? 0 : 1;

        void Observe(IpcState state, string? detail)
        {
            if (connections.LastOrDefault()?.State == state) return;
            connections.Add(new(DateTime.UtcNow, state, detail));
            Console.WriteLine($"Collector: {state}{(detail is null ? "" : " — " + detail)}");
        }
    }

    private static AppUsageQueryResources Resources(Process process)
    {
        process.Refresh();
        return new(process.HandleCount, process.PrivateMemorySize64, process.WorkingSet64);
    }

    private static void Print(AppUsageQueryResult result)
    {
        Console.WriteLine($"APP USAGE — {Label(result.Range).ToUpperInvariant()} ({result.StartLocalDate:yyyy-MM-dd} to {result.EndLocalDate:yyyy-MM-dd})");
        Console.WriteLine($"Tracked apps: {result.TrackedApplicationCount}; Download: {result.TotalDownloadBytes:N0} B; Upload: {result.TotalUploadBytes:N0} B; Logical total: {result.LogicalTotalBytes:N0} B.");
        if (result.Availability != AppUsageAvailability.Available)
            Console.WriteLine($"Usage history: {result.Availability}{(result.Diagnostic is null ? "" : " — " + result.Diagnostic)}");
        foreach (var app in result.Applications)
            Console.WriteLine($"{app.DisplayName}: Download {app.DownloadBytes:N0} B; Upload {app.UploadBytes:N0} B; Total {app.TotalBytes:N0} B.");
    }

    private static string Label(AppUsageRange range) => range switch
    {
        AppUsageRange.Today => "Today",
        AppUsageRange.SevenDays => "7D",
        AppUsageRange.ThirtyDays => "30D",
        _ => throw new ArgumentOutOfRangeException(nameof(range))
    };
}

internal sealed record AppUsageQueryObservation(AppUsageRange Range, double QueryLatencyMs,
    IpcState State, AppUsageQueryResult? Result, string? Detail);
internal sealed record AppUsageQueryConnection(DateTime ObservedAtUtc, IpcState State, string? Detail);
internal sealed record AppUsageQueryResources(int HandleCount, long PrivateBytes, long WorkingSetBytes);
internal sealed record AppUsageQueryReport(bool ClientElevated, int ClientPid, int ProtocolVersion,
    int CompletedQueries, int RequestedQueries, IReadOnlyList<AppUsageQueryObservation> Queries,
    IReadOnlyList<AppUsageQueryConnection> Connections, AppUsageQueryResources? WarmBaseline,
    AppUsageQueryResources FinalResources, double ObservationSeconds, bool Cancelled);
