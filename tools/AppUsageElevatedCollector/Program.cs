using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using AppUsageEtwProbe;
using AppUsageIpc;
using Microsoft.Diagnostics.Tracing.Session;

namespace AppUsageElevatedCollector;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help"))
        {
            Console.WriteLine("AppUsageElevatedCollector [--seconds 90] [--pipe NAME] [--allow-test-shutdown] [--output FILE] [--log FILE]");
            Console.WriteLine("Explicitly launched elevated diagnostics only. Collects network metadata counters; no UI, service, database or generated network traffic.");
            return 0;
        }
        if (!EtwCapture.IsElevated)
        {
            Console.Error.WriteLine("Collector requires an explicitly elevated launch. This normal token was rejected; no UAC request or ETW session was started.");
            return 2;
        }

        StreamWriter? log = null;
        try
        {
            var options = ParseOptions(args);
            int seconds = options.TryGetValue("--seconds", out var duration) ? int.Parse(duration!) : 90;
            if (seconds is < 1 or > 300) throw new ArgumentException("--seconds must be between one and 300.");
            var pipeName = options.GetValueOrDefault("--pipe") ?? NativePipeSecurity.DefaultPipeName;
            bool testShutdown = options.ContainsKey("--allow-test-shutdown");
            var output = options.GetValueOrDefault("--output");
            if (options.TryGetValue("--log", out var logPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath!))!);
                log = new StreamWriter(logPath!, false) { AutoFlush = true };
                Console.SetOut(log);
                Console.SetError(log);
            }
            var schema = ProviderSchema.Discover();
            if (!schema.ProviderAvailable || schema.Events.Count == 0 || schema.KeywordMask == 0)
            {
                Console.Error.WriteLine(schema.Error ?? "No validated Kernel-Network transfer schemas are available.");
                return 1;
            }

            using var resolver = new WindowsProcessResolver();
            var aggregation = new CollectorAggregation(schema, resolver, ReadLocalAddresses(),
                shutdownForTestEnabled: testShutdown, collectorElevated: true);
            var capture = new EtwCapture(schema, sessionNamePrefix: "NetTrace-AppUsage002");
            using var collectorCancellation = new CancellationTokenSource();
            using var serverCancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                collectorCancellation.Cancel();
            };
            Console.CancelKeyPress += handler;
            IpcServer? server = null;
            Task? serverTask = null;
            string? serverError = null;
            EtwCaptureResult result;
            CollectorCapabilities Capabilities()
            {
                aggregation.UpdateCaptureStatus(capture.GetLiveStatus());
                return aggregation.GetCapabilities();
            }
            CollectorSnapshot Snapshot()
            {
                aggregation.UpdateCaptureStatus(capture.GetLiveStatus());
                return aggregation.GetSnapshot();
            }
            try
            {
                result = await capture.CaptureAsync(TimeSpan.FromSeconds(seconds), collectorCancellation.Token,
                    raw => aggregation.Add(raw), workToken =>
                    {
                        // Session creation, provider enablement, consumer construction and
                        // window initialization have completed before the pipe is opened.
                        aggregation.UpdateCaptureStatus(capture.GetLiveStatus());
                        server = new IpcServer(pipeName, Capabilities, Snapshot,
                            testShutdown ? collectorCancellation.Cancel : null);
                        serverTask = server.RunAsync(serverCancellation.Token);
                        _ = serverTask.ContinueWith(completed =>
                        {
                            if (completed.IsFaulted) collectorCancellation.Cancel();
                        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        Console.WriteLine($"Collector ready: instance {aggregation.CollectorInstanceId}; pipe {pipeName}; bounded window {seconds}s.");
                        return Task.CompletedTask;
                    });
            }
            finally
            {
                // CaptureAsync returns only after stopping and disposing its owned
                // session and joining the consumer. The server is closed afterwards.
                Console.CancelKeyPress -= handler;
                serverCancellation.Cancel();
                if (serverTask is not null)
                {
                    try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (OperationCanceledException) when (serverCancellation.IsCancellationRequested) { }
                    catch (Exception exception) { serverError = $"IPC server shutdown failed: {exception.GetType().Name}."; }
                }
                if (server is not null)
                {
                    try { await server.DisposeAsync(); }
                    catch (Exception exception) { serverError = $"IPC server disposal failed: {exception.GetType().Name}."; }
                }
            }

            aggregation.UpdateCaptureStatus(capture.GetLiveStatus());
            var finalSnapshot = aggregation.GetSnapshot(countRequest: false);
            bool? ownedSessionAbsent = null;
            try
            {
                ownedSessionAbsent = !TraceEventSession.GetActiveSessionNames()
                    .Contains(result.SessionName, StringComparer.Ordinal);
            }
            catch { }
            var diagnostic = new
            {
                Phase = "App Usage 002", result.SessionName, Capture = result,
                OwnedSessionAbsent = ownedSessionAbsent, IpcShutdownError = serverError,
                Snapshot = finalSnapshot
            };
            if (output is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                await File.WriteAllTextAsync(output, JsonSerializer.Serialize(diagnostic, new JsonSerializerOptions
                    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
            }
            Console.WriteLine($"Collector stopped: {finalSnapshot.EventCount} accepted events; lost events {(finalSnapshot.Capabilities.LostEventCountAvailable ? finalSnapshot.LostEventCount.ToString() : "unavailable")}; snapshots served {finalSnapshot.Metrics.SnapshotsServed}.");
            Console.WriteLine($"Owned ETW session absent: {ownedSessionAbsent}; clean capture shutdown: {result.ShutdownClean}; IPC shutdown error: {serverError ?? "none"}.");
            if (result.Error is not null) Console.Error.WriteLine(result.Error);
            return result.SessionStarted && result.ShutdownClean && result.Error is null
                && ownedSessionAbsent == true && serverError is null ? 0 : 1;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException
            or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"Collector failed: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
        finally
        {
            if (log is not null)
            {
                await log.FlushAsync();
                await log.DisposeAsync();
            }
        }
    }

    private static IReadOnlySet<IPAddress> ReadLocalAddresses()
    {
        var addresses = new HashSet<IPAddress> { IPAddress.Loopback, IPAddress.IPv6Loopback };
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    foreach (var address in adapter.GetIPProperties().UnicastAddresses)
                        addresses.Add(ActivityParser.Normalize(address.Address));
                }
                catch (NetworkInformationException) { }
            }
        }
        catch (NetworkInformationException) { }
        return addresses;
    }

    private static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string?>();
        for (int index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (option == "--allow-test-shutdown") result.Add(option, null);
            else if (option is "--seconds" or "--pipe" or "--output" or "--log")
            {
                if (++index == args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"{option} requires a value.");
                result.Add(option, args[index]);
            }
            else throw new ArgumentException($"Unknown collector option: {option}");
        }
        return result;
    }
}
