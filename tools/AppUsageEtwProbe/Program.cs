using System.Diagnostics;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Session;

namespace AppUsageEtwProbe;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--tcp-client" && Enum.TryParse<IpFamily>(args[1], out var childFamily)
            && int.TryParse(args[2], out var port) && port is > 0 and <= 65535)
            return await ControlledTraffic.RunClientAsync(childFamily, port);
        if (args.Contains("--help"))
        {
            Console.WriteLine("AppUsageEtwProbe [--seconds 20] [--offline-test] [--adapter ID] [--vpn-process NAME.exe] [--output FILE] [--log FILE]");
            Console.WriteLine("Passive metadata-only observation; --offline-test adds owned local TCP transfers. Ctrl+C stops the owned session.");
            return 0;
        }
        string? logPath = null;
        try
        {
            var options = ParseOptions(args);
            logPath = options.GetValueOrDefault("--log");
            if (logPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
                Console.SetOut(new StreamWriter(logPath, false) { AutoFlush = true });
                Console.SetError(Console.Out);
            }
            int seconds = options.TryGetValue("--seconds", out var duration) ? int.Parse(duration!) : 20;
            if (seconds is < 1 or > 300) throw new ArgumentException("--seconds must be between 1 and 300.");
            bool offline = options.ContainsKey("--offline-test");
            if (offline && seconds < 6) throw new ArgumentException("The offline ETW test needs at least six seconds.");
            string output = options.GetValueOrDefault("--output") ?? Path.Combine(AppContext.BaseDirectory, "capability-result.json");
            var schema = ProviderSchema.Discover();
            var adapter = AdapterObservation.Create(options.GetValueOrDefault("--adapter"));
            using var resolver = new WindowsProcessResolver();
            var aggregate = new ActivityAggregator(resolver);
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            Console.CancelKeyPress += cancelHandler;
            var transfers = new List<ControlledTransferResult>();
            var sampleKeys = new HashSet<(int, int)>();
            var samples = new List<object>();
            long ignored = 0;
            AdapterCounterSnapshot? start = null, end = null;
            using var process = Process.GetCurrentProcess();
            var cpuStart = process.TotalProcessorTime;
            var memoryStart = process.WorkingSet64;
            var timer = Stopwatch.StartNew();
            EtwCaptureResult capture;
            try
            {
                capture = await new EtwCapture(schema).CaptureAsync(TimeSpan.FromSeconds(seconds), cancellation.Token,
                    raw =>
                    {
                        if (!ActivityParser.TryParse(raw, schema, adapter.LocalAddresses, out var activity)) { ignored++; return; }
                        aggregate.Add(activity!);
                        if (sampleKeys.Add((raw.EventId, raw.Version)) && samples.Count < 8)
                            samples.Add(new { raw.EventId, raw.Version, raw.HeaderPid, PayloadPid = activity!.Pid,
                                activity.Bytes, Direction = activity.Direction.ToString(), Family = activity.Family.ToString(),
                                Classification = activity.TrafficClass.ToString(), Fields = raw.Payload.Keys.ToArray() });
                    }, async token =>
                    {
                        if (offline)
                        {
                            if (schema.SupportsIpv4) transfers.Add(await ControlledTraffic.RunAsync(IpFamily.Ipv4, token));
                            if (schema.SupportsIpv6) transfers.Add(await ControlledTraffic.RunAsync(IpFamily.Ipv6, token));
                        }
                    }, onWindowOpening: () =>
                    {
                        start = adapter.Capture();
                        return start.CounterTimestampUtc?.UtcDateTime ?? start.CapturedAtUtc.UtcDateTime;
                    }, onWindowClosing: () =>
                    {
                        end = adapter.Capture();
                        return end.CounterTimestampUtc?.UtcDateTime ?? end.CapturedAtUtc.UtcDateTime;
                    });
            }
            finally { Console.CancelKeyPress -= cancelHandler; }
            timer.Stop(); process.Refresh();
            var elapsed = Math.Max(timer.Elapsed.TotalSeconds, 0.001);
            var windowSeconds = capture.WindowStartUtc is not null && capture.WindowEndUtc is not null
                ? (capture.WindowEndUtc.Value - capture.WindowStartUtc.Value).TotalSeconds : 0;
            var comparison = start is not null && end is not null ? AdapterObservation.Compare(start, end)
                : new AdapterComparison(false, null, null, null, 0, "ETW observation did not start.");
            var nonLoopback = aggregate.Traffic.GetValueOrDefault(TrafficClass.NonLoopback) ?? new();
            var loopback = aggregate.Traffic.GetValueOrDefault(TrafficClass.Loopback) ?? new();
            var local = aggregate.Traffic.GetValueOrDefault(TrafficClass.LocalToLocal) ?? new();
            var unknown = aggregate.Traffic.GetValueOrDefault(TrafficClass.Unknown) ?? new();
            var vpn = end?.Vpn ?? adapter.Vpn;
            bool allRelevantEventsMatchAdapter = adapter.Adapter is not null && !aggregate.NonLoopbackInterfaceMissing
                && aggregate.NonLoopbackInterfaces.Count == 1
                && aggregate.NonLoopbackInterfaces.Contains((int)adapter.Adapter.InterfaceIndex);
            var coverage = AdapterObservation.AssessCoverage(comparison, start?.Vpn ?? adapter.Vpn, vpn,
                allRelevantEventsMatchAdapter, nonLoopback.Total);
            var vpnName = options.GetValueOrDefault("--vpn-process");
            var apps = aggregate.Applications;
            var vpnApps = apps.Where(app => vpnName is not null &&
                string.Equals(app.Name, Path.GetFileName(vpnName), StringComparison.OrdinalIgnoreCase)).ToArray();
            var candidateNames = new[] { "ssh.exe", "sing-box.exe", "xray.exe", "v2ray.exe", "wireguard.exe", "openvpn.exe", "tun2socks.exe", "clash.exe", "hysteria.exe" };
            var candidates = apps.Where(app => candidateNames.Contains(app.Name, StringComparer.OrdinalIgnoreCase)).Select(a => a.Name).ToArray();
            var ownedPids = transfers.Select(t => t.ClientPid).Append(Environment.ProcessId).ToHashSet();
            bool appLocal = apps.Any(app => !vpnApps.Contains(app) && app.Processes.Any(p => !ownedPids.Contains(p.Identity.Pid) && p.Breakdown.Any(b =>
                (b.Key.EndsWith("/Loopback") || b.Key.EndsWith("/LocalToLocal")) && b.Value.Total > 0)));
            bool vpnExternal = vpnApps.Any(app => app.Processes.Any(p => p.Breakdown.Any(b => b.Key.EndsWith("/NonLoopback") && b.Value.Total > 0)));
            var brave = apps.Where(a => a.Name.Equals("brave.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
            ByteTotals SelectBytes(IEnumerable<ApplicationObservation> selectedApps, IpFamily family, params TrafficClass[] classes)
            {
                var totals = new ByteTotals();
                foreach (var item in selectedApps.SelectMany(a => a.Processes).SelectMany(p => p.Breakdown)
                    .Where(b => classes.Any(c => b.Key == $"{family}/{c}")))
                { totals.Add(TrafficDirection.Receive, item.Value.Receive); totals.Add(TrafficDirection.Send, item.Value.Send); }
                return totals;
            }
            var braveLocal4 = SelectBytes(brave, IpFamily.Ipv4, TrafficClass.Loopback, TrafficClass.LocalToLocal);
            var braveLocal6 = SelectBytes(brave, IpFamily.Ipv6, TrafficClass.Loopback, TrafficClass.LocalToLocal);
            var vpnExternal4 = SelectBytes(vpnApps, IpFamily.Ipv4, TrafficClass.NonLoopback);
            var vpnExternal6 = SelectBytes(vpnApps, IpFamily.Ipv6, TrafficClass.NonLoopback);
            var proxyPattern = new { BraveObserved = brave.Length > 0, BraveLocalIpv4 = braveLocal4, BraveLocalIpv6 = braveLocal6,
                DesignatedVpnProcess = vpnName, VpnExternalIpv4 = vpnExternal4, VpnExternalIpv6 = vpnExternal6,
                BraveLocalAndVpnExternalCoexist = braveLocal4.Total + braveLocal6.Total > 0 && vpnExternal,
                IndividualConnectionPairingProven = false };
            string doubleCount = appLocal && vpnExternal ? "Detected" : "Inconclusive";
            string finding = doubleCount == "Detected"
                ? "Local application traffic and designated VPN-process external traffic coexist. Summing them risks counting logical and transport layers twice; no individual connection pairing is proven."
                : "The short observation cannot prove or exclude duplicate logical/transport accounting. Name candidates are unconfirmed; no traffic is silently deduplicated.";
            var controlledChecks = transfers.Select(transfer =>
            {
                var observed = aggregate.Processes.Where(p => p.Identity.Pid == transfer.ClientPid).ToArray();
                long tx = observed.SelectMany(p => p.Breakdown).Where(b => b.Key == $"{transfer.Family}/Loopback").Sum(b => b.Value.Send);
                long rx = observed.SelectMany(p => p.Breakdown).Where(b => b.Key == $"{transfer.Family}/Loopback").Sum(b => b.Value.Receive);
                return new { Transfer = transfer, ObservedClientSend = tx, ObservedClientReceive = rx,
                    PidDirectionBytesLoopbackPassed = transfer.ClientExitedCleanly && tx == transfer.UploadBytes && rx == transfer.DownloadBytes + 1 };
            }).ToArray();
            bool? sessionAbsent = null;
            try { sessionAbsent = !TraceEventSession.GetActiveSessionNames().Contains(capture.SessionName, StringComparer.Ordinal); } catch { }
            bool hasPid = aggregate.Processes.Any(p => p.Identity.Pid > 0 && p.Identity.Name != $"PID {p.Identity.Pid}");
            bool hasDirection = aggregate.Processes.Any(p => p.Bytes.Receive > 0) && aggregate.Processes.Any(p => p.Bytes.Send > 0);
            bool required = capture.SessionStarted && capture.Error is null && capture.ShutdownClean && sessionAbsent == true
                && capture.EventsLost == 0 && aggregate.EventCount > 0 && hasPid && hasDirection;
            string recommendation = required ? "GO WITH LIMITATIONS" : "NO-GO";
            string? elevationRequired = capture.AccessDenied ? "Yes" : capture.SessionStarted && !EtwCapture.IsElevated ? "No" : null;
            var report = new
            {
                Phase = "App Usage 000", Recommendation = recommendation,
                schema.ProviderAvailable, ElevationRequired = elevationRequired, RunningElevated = EtwCapture.IsElevated,
                SupportsIpv4 = schema.SupportsIpv4, SupportsIpv6 = schema.SupportsIpv6,
                ObservedIpv4 = aggregate.ObservedIpv4, ObservedIpv6 = aggregate.ObservedIpv6,
                HasPid = hasPid, HasByteCount = aggregate.EventCount > 0, HasDirection = hasDirection,
                CanIdentifyLoopback = loopback.Total > 0, Schema = schema.Events, schema.Error, schema.SchemaNotes,
                Capture = capture, OwnedSessionAbsent = sessionAbsent, ObservationSeconds = windowSeconds,
                RawSamples = samples, IgnoredMalformedEvents = ignored,
                ObservedProcesses = aggregate.Processes.Take(50).ToArray(),
                Applications = apps.Take(30).Select(a => new { a.Name, a.ExecutablePath, Pids = a.Processes.Select(p => p.Identity.Pid).ToArray(), a.Bytes }).ToArray(),
                OmittedProcessLifetimes = Math.Max(0, aggregate.Processes.Count - 50),
                PhysicalAdapter = adapter.Adapter, PhysicalAdapterDelta = comparison,
                EtwObservedBytes = new { AllLogical = apps.Aggregate(0L, (total, a) => ByteTotals.SaturatingAdd(total, a.Bytes.Total)),
                    NonLoopback = nonLoopback, Loopback = loopback, LocalToLocal = local, UnknownEndpoints = unknown },
                Coverage = coverage, Vpn = vpn, VpnAtStart = start?.Vpn,
                VpnProcess = vpnName, VpnProcessObserved = vpnApps.Select(a => new { a.Name, a.Bytes }).ToArray(),
                UnconfirmedVpnProcessNameCandidates = candidates, ProxyPattern = proxyPattern,
                VpnDoubleCountFinding = doubleCount, Finding = finding,
                ControlledTests = controlledChecks,
                Overhead = new { CpuPercentOfOneCore = (process.TotalProcessorTime - cpuStart).TotalSeconds / elapsed * 100,
                    WorkingSetStartBytes = memoryStart, WorkingSetEndBytes = process.WorkingSet64, PeakWorkingSetBytes = process.PeakWorkingSet64,
                    EventsPerSecond = aggregate.EventCount / Math.Max(windowSeconds, 0.001) },
                Limitations = new[] { "Event sizes are transport telemetry, not carrier-billed or physical adapter byte accounting.",
                    "No selected-interface attribution is available in this machine's installed event schemas.",
                    "PID/path/name resolution can race exits; unresolved identities are provisional. Future persistence requires process lifetime plus stable executable identity.",
                    "Loopback and local-to-local bytes are separate and excluded from non-loopback observations. Logical and VPN transport bytes must never be added into physical usage.",
                    "Default adapter selection matches NetTrace automatic physical selection; a manual UI selection must be passed with --adapter.",
                    "Boundary VPN inventories cannot prove continuous routing or proxy ownership during the observation." }
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions
                { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
            Console.WriteLine($"ETW CAPABILITY: {recommendation}");
            Console.WriteLine($"Provider: {schema.ProviderAvailable}; elevation required: {elevationRequired ?? "Unverified (run used elevation)"}; elevated token: {EtwCapture.IsElevated}");
            Console.WriteLine($"PID/process: {hasPid}; byte counts: {aggregate.EventCount > 0}; send/receive: {hasDirection}");
            Console.WriteLine($"IPv4 schema/observed: {schema.SupportsIpv4}/{aggregate.ObservedIpv4}; IPv6 schema/observed: {schema.SupportsIpv6}/{aggregate.ObservedIpv6}; loopback observed: {loopback.Total > 0}");
            Console.WriteLine($"Window: {windowSeconds:0.00}s; events: {aggregate.EventCount}; lost: {capture.EventsLost}; clean shutdown: {capture.ShutdownClean}; owned session absent: {sessionAbsent}");
            Console.WriteLine("TOP PROCESSES (all logical network events; separate from physical usage)");
            foreach (var app in apps.Take(10))
                Console.WriteLine($"  {app.Name} [{app.Processes.Count} PID/lifetimes] RX {ByteFormatting.Format(app.Bytes.Receive)} TX {ByteFormatting.Format(app.Bytes.Send)}");
            Console.WriteLine($"COMPARISON: physical {adapter.Adapter?.Name ?? "unavailable"} RX {FormatNullable(comparison.RxBytes)} TX {FormatNullable(comparison.TxBytes)} over {comparison.ObservationSeconds:0.00}s");
            Console.WriteLine($"ETW non-loopback RX {ByteFormatting.Format(nonLoopback.Receive)} TX {ByteFormatting.Format(nonLoopback.Send)}; loopback {ByteFormatting.Format(loopback.Total)}; local-to-local {ByteFormatting.Format(local.Total)}; unknown endpoints {ByteFormatting.Format(unknown.Total)}");
            Console.WriteLine($"Coverage ratio: {coverage.Ratio?.ToString("P1") ?? "Unavailable"}; {coverage.Reason}");
            Console.WriteLine($"VPN inventory: {vpn.Status}; designated process: {vpnName ?? "unspecified"}; double-count risk: {doubleCount}");
            Console.WriteLine($"Brave local IPv4 RX {ByteFormatting.Format(braveLocal4.Receive)} TX {ByteFormatting.Format(braveLocal4.Send)}; local IPv6 RX {ByteFormatting.Format(braveLocal6.Receive)} TX {ByteFormatting.Format(braveLocal6.Send)}");
            Console.WriteLine($"VPN external IPv4 RX {ByteFormatting.Format(vpnExternal4.Receive)} TX {ByteFormatting.Format(vpnExternal4.Send)}; external IPv6 RX {ByteFormatting.Format(vpnExternal6.Receive)} TX {ByteFormatting.Format(vpnExternal6.Send)}; Brave/VPN coexist: {proxyPattern.BraveLocalAndVpnExternalCoexist} (connection pairing unproven)");
            Console.WriteLine(finding);
            foreach (var check in controlledChecks)
                Console.WriteLine($"OFFLINE {check.Transfer.Family}: child PID {check.Transfer.ClientPid}; transferred TX {check.Transfer.UploadBytes} RX {check.Transfer.DownloadBytes}+1 acknowledgement; observed TX {check.ObservedClientSend} RX {check.ObservedClientReceive}; ETW check {check.PidDirectionBytesLoopbackPassed}; {check.Transfer.Error}");
            Console.WriteLine($"Overhead: CPU {report.Overhead.CpuPercentOfOneCore:0.0}% of one core; working set {ByteFormatting.Format(process.WorkingSet64)}; events/s {report.Overhead.EventsPerSecond:0.0}");
            if (schema.Error is not null) Console.WriteLine(schema.Error);
            if (capture.Error is not null) Console.WriteLine(capture.Error);
            Console.WriteLine($"Result: {Path.GetFullPath(output)}");
            if (capture.AccessDenied) return 2;
            if (!required || offline && controlledChecks.Any(c => !c.PidDirectionBytesLoopbackPassed)) return 1;
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or IOException)
        {
            Console.Error.WriteLine($"Probe failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally { if (logPath is not null) await Console.Out.FlushAsync(); }
    }

    private static string FormatNullable(long? bytes) => bytes is null ? "Unavailable" : ByteFormatting.Format(bytes.Value);
    private static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string?>();
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            if (key == "--offline-test") result.Add(key, null);
            else if (key is "--seconds" or "--adapter" or "--vpn-process" or "--output" or "--log")
            {
                if (++i == args.Length || args[i].StartsWith("--")) throw new ArgumentException($"{key} requires a value.");
                result.Add(key, args[i]);
            }
            else throw new ArgumentException($"Unknown option: {key}");
        }
        return result;
    }
}
