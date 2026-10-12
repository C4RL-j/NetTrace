using System.Diagnostics;
using System.Text.Json;
using AppUsageDiagnosticClient;
using AppUsageEtwProbe;
using AppUsageIpc;

if (args.Length == 3 && args[0] == "--tcp-client" &&
    Enum.TryParse<IpFamily>(args[1], out var childFamily) && int.TryParse(args[2], out var childPort))
    return await ControlledTraffic.RunClientAsync(childFamily, childPort);

if (EtwCapture.IsElevated)
{
    Console.Error.WriteLine("This diagnostic client must run without elevation. Start it from a normal terminal.");
    return 2;
}

string pipeName = NativePipeSecurity.DefaultPipeName;
string output = Path.Combine(AppContext.BaseDirectory, "client-result.json");
int seconds = 20, shutdownAfter = 0;
bool controlled = false, recover = false;
try
{
    for (int i = 0; i < args.Length; i++)
        switch (args[i])
        {
            case "--seconds": seconds = int.Parse(args[++i]); break;
            case "--pipe": pipeName = args[++i]; break;
            case "--output": output = Path.GetFullPath(args[++i]); break;
            case "--controlled": controlled = true; break;
            case "--recover": recover = true; break;
            case "--shutdown-after": shutdownAfter = int.Parse(args[++i]); break;
            default: throw new ArgumentException("Unknown diagnostic option.");
        }
    if (seconds is < 1 or > 120 || shutdownAfter < 0 || shutdownAfter >= seconds && shutdownAfter != 0)
        throw new ArgumentException("Use a 1–120 second window and an optional shutdown time inside it.");
    NativePipeSecurity.ValidatePipeName(pipeName);
}
catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or IndexOutOfRangeException)
{
    Console.Error.WriteLine("Usage: AppUsageDiagnosticClient [--seconds 20] [--pipe NAME] [--controlled] [--recover] [--shutdown-after N] [--output FILE]");
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
using var process = Process.GetCurrentProcess();
string vpnState = AdapterObservation.Create().Vpn.Status;
var initialCpu = process.TotalProcessorTime;
var elapsed = Stopwatch.StartNew();
var connections = new List<ConnectionObservation>();
var instances = new HashSet<Guid>();
var samples = new List<SnapshotObservation>();
var transfers = new List<ControlledTransferResult>();
CollectorSnapshot? lastSnapshot = null;
CollectorSnapshot? firstSnapshot = null;
TimeSpan? pollingCpuStart = null;
TimeSpan pollingStart = TimeSpan.Zero;
IpcClient? client = null;
Task? transferWork = null;
bool cancelled = false, shutdownRequested = false;
int snapshotCount = 0;

Console.WriteLine($"Client PID: {Environment.ProcessId}; elevated: False; protocol: {Protocol.Version}.");
try
{
    while (elapsed.Elapsed < TimeSpan.FromSeconds(seconds))
    {
        cancellation.Token.ThrowIfCancellationRequested();
        if (client is null)
        {
            client = new IpcClient(pipeName);
            var hello = await client.ConnectAsync(cancellation.Token);
            Observe(hello.State, hello.Detail);
            if (hello.State != IpcState.Connected)
            {
                await client.DisposeAsync(); client = null;
                if (!recover) break;
                await Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token);
                continue;
            }
            var capabilities = await client.GetCapabilitiesAsync(cancellation.Token);
            if (capabilities.State != IpcState.Connected || capabilities.Value?.CollectorElevated != true)
            {
                Observe(capabilities.State == IpcState.Connected ? IpcState.PeerRejected : capabilities.State,
                    "Collector capabilities did not establish an elevated collector.");
                await client.DisposeAsync(); client = null;
                if (!recover) break;
                await Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token);
                continue;
            }
            if (controlled && transferWork is null)
                transferWork = RunTransfersAsync(cancellation.Token);
        }

        var snapshot = await client.GetSnapshotAsync(cancellation.Token);
        Observe(snapshot.State, snapshot.Detail);
        if (snapshot.State != IpcState.Connected || snapshot.Value is null)
        {
            await client.DisposeAsync(); client = null;
            if (!recover) break;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token);
            continue;
        }
        lastSnapshot = snapshot.Value;
        firstSnapshot ??= lastSnapshot;
        if (pollingCpuStart is null)
        {
            // Report both whole-window initialization cost and recurring polling cost.
            pollingCpuStart = process.TotalProcessorTime;
            pollingStart = elapsed.Elapsed;
        }
        instances.Add(lastSnapshot.CollectorInstanceId);
        snapshotCount++;
        samples.Add(new(lastSnapshot.CapturedAtUtc, lastSnapshot.CollectorInstanceId,
            lastSnapshot.CollectorUptime, lastSnapshot.EventCount, lastSnapshot.LostEventCount));
        Console.WriteLine($"Snapshot {snapshotCount}: instance={lastSnapshot.CollectorInstanceId:N}; events={lastSnapshot.EventCount}; lost={lastSnapshot.LostEventCount}; collector elevated={lastSnapshot.Capabilities.CollectorElevated}.");

        if (shutdownAfter > 0 && !shutdownRequested && elapsed.Elapsed.TotalSeconds >= shutdownAfter)
        {
            shutdownRequested = true;
            var shutdown = await client.ShutdownForTestAsync(cancellation.Token);
            Console.WriteLine($"ShutdownForTest: {shutdown.State}.");
        }
        await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token);
    }
    if (transferWork is not null) await transferWork;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    cancelled = true;
    Observe(IpcState.Cancelled, "Diagnostic cancelled.");
}
finally
{
    if (client is not null) await client.DisposeAsync();
    cancellation.Cancel();
    if (transferWork is not null)
        try { await transferWork; } catch (OperationCanceledException) { }
}
elapsed.Stop();
process.Refresh();
var checks = transfers.Select(transfer => ControlledValidation.Check(transfer, lastSnapshot)).ToArray();
var metrics = new ClientMetrics((process.TotalProcessorTime - initialCpu).TotalSeconds /
    Math.Max(elapsed.Elapsed.TotalSeconds, .001) * 100, process.WorkingSet64,
    snapshotCount / Math.Max(elapsed.Elapsed.TotalSeconds, .001), elapsed.Elapsed.TotalSeconds,
    pollingCpuStart is null ? 0 : (process.TotalProcessorTime - pollingCpuStart.Value).TotalSeconds /
        Math.Max((elapsed.Elapsed - pollingStart).TotalSeconds, .001) * 100,
    pollingCpuStart is null ? 0 : (elapsed.Elapsed - pollingStart).TotalSeconds);
var report = new DiagnosticReport(false, Environment.ProcessId, Protocol.Version, vpnState, snapshotCount,
    connections, instances.ToArray(), checks, samples, firstSnapshot, lastSnapshot, metrics, cancelled, shutdownRequested);

Directory.CreateDirectory(Path.GetDirectoryName(output)!);
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
foreach (var check in checks)
    Console.WriteLine($"Controlled {check.Family}: PID={check.ClientPid}; RX={check.ChildRxBytes}; TX={check.ChildTxBytes}; exact={check.ChildBytesExact && check.ServerBytesExact}; family={check.FamilyPreserved}; loopback={check.LoopbackPreserved}.");
if (lastSnapshot is not null)
    foreach (var app in lastSnapshot.Applications.Where(app =>
        app.ProcessName.Equals("brave.exe", StringComparison.OrdinalIgnoreCase) ||
        app.ProcessName.Equals("netmod.exe", StringComparison.OrdinalIgnoreCase)))
        Console.WriteLine($"{app.ProcessName}: RX={app.Counters.RxBytes}; TX={app.Counters.TxBytes}; PIDs={string.Join(',', app.Processes.Select(value => value.ProcessId))}.");
Console.WriteLine($"Client CPU={metrics.CpuPercentOfOneCore:F2}% of one core; working set={metrics.WorkingSetBytes / 1048576d:F1} MiB; snapshots={metrics.SnapshotsPerSecond:F2}/s.");
Console.WriteLine($"Client CPU after first snapshot={metrics.PollingCpuPercentOfOneCore:F2}% of one core over {metrics.PollingObservationSeconds:F1}s.");
Console.WriteLine($"Diagnostic result: {output}");
return cancelled ? 130 : snapshotCount == 0 || controlled && checks.Any(check =>
    !check.ChildAttributed || !check.ServerAttributed || !check.ChildBytesExact || !check.ServerBytesExact ||
    !check.FamilyPreserved || !check.LoopbackPreserved) ? 1 : 0;

void Observe(IpcState state, string? detail)
{
    if (state == IpcState.Cancelled) cancelled = true;
    if (connections.LastOrDefault()?.State == state.ToString()) return;
    connections.Add(new(DateTime.UtcNow, state.ToString(), detail));
    Console.WriteLine($"Collector: {state}{(detail is null ? "" : " — " + detail)}");
}

async Task RunTransfersAsync(CancellationToken token)
{
    foreach (var family in new[] { IpFamily.Ipv4, IpFamily.Ipv6 })
    {
        token.ThrowIfCancellationRequested();
        transfers.Add(await ControlledTraffic.RunAsync(family, token));
    }
}
