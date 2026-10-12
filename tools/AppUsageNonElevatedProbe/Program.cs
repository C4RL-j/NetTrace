using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppUsageEtwProbe;
using Microsoft.Diagnostics.Tracing.Session;

namespace AppUsageNonElevatedProbe;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help"))
        {
            Console.WriteLine("AppUsageNonElevatedProbe [--seconds 20] [--output FILE] [--baseline AppUsage000-JSON]");
            Console.WriteLine("Run from a normal terminal. Both providers are tested without elevation; owned local TCP traffic is generated, with no public-internet requests.");
            return 0;
        }
        if (EtwCapture.IsElevated)
        {
            Console.Error.WriteLine("Elevated token rejected. Run App Usage 001 from a normal unelevated terminal. No UAC request will be made.");
            return 2;
        }
        try
        {
            var options = Options(args);
            int seconds = options.TryGetValue("--seconds", out var requested) ? int.Parse(requested) : 20;
            if (seconds is < 6 or > 300) throw new ArgumentException("--seconds must be between six and 300.");
            string output = options.GetValueOrDefault("--output") ?? Path.Combine(AppContext.BaseDirectory, "non-elevated-result.json");
            string baselinePath = options.GetValueOrDefault("--baseline") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../AppUsageEtwProbe/bin/live-result.json"));
            var baseline = BaselineSummary.Read(baselinePath);
            var adapter = AdapterObservation.Create();
            bool braveRunning = ProcessRunning("brave"), netmodRunning = ProcessRunning("netmod");
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            Console.CancelKeyPress += handler;
            var schemas = ProviderDefinition.Candidates.Select(CandidateProviderSchema.Discover).ToArray();
            using var process = Process.GetCurrentProcess();
            var cpuStart = process.TotalProcessorTime;
            var memoryStart = process.WorkingSet64;
            var stopwatch = Stopwatch.StartNew();
            var transfers = new List<ControlledTransferResult>();
            var contexts = schemas.Select(schema => new ObservationContext(schema, adapter.LocalAddresses)).ToArray();
            var tasks = contexts.Select(context => context.Schema.ProviderAvailable && context.Schema.Error is null && context.Schema.KeywordMask != 0
                ? new CandidateCapture(context.Schema.Definition.Guid, context.Schema.KeywordMask, context.Schema.SafeFields)
                    .CaptureAsync(TimeSpan.FromSeconds(seconds), cancellation.Token, context.Observation.Add)
                : Task.FromResult(new CandidateCaptureResult("", false, false, false, false, null, true, false,
                    0, 0, 0, 0, 0, null, null, context.Schema.Error ?? "Provider unavailable."))).ToArray();
            CandidateCaptureResult[] captures;
            try
            {
                // CaptureAsync completes native startup before returning its first incomplete await.
                // The same owned transfers exercise every permitted candidate; permission failures
                // remain explicit even though the workload itself can run normally.
                transfers.Add(await ControlledTraffic.RunAsync(IpFamily.Ipv4, cancellation.Token));
                if (!cancellation.IsCancellationRequested)
                    transfers.Add(await ControlledTraffic.RunAsync(IpFamily.Ipv6, cancellation.Token));
                captures = await Task.WhenAll(tasks);
            }
            finally
            {
                Console.CancelKeyPress -= handler;
                foreach (var context in contexts) context.Resolver.Dispose();
            }
            stopwatch.Stop(); process.Refresh();
            var summaries = contexts.Select((context, index) =>
            {
                var capture = captures[index];
                var checks = transfers.Select(context.Observation.CheckTransfer).ToArray();
                bool controlsVerified = checks.Length == 2 && checks.All(c => c.ByteAccountingVerified);
                var evidence = new ProviderCapabilityEvidence(context.Schema.ProviderAvailable, capture.SessionStartPermitted,
                    capture.ProviderEnablePermitted, capture.AccessDenied, capture.ShutdownClean, capture.ProviderEventCount,
                    capture.EventsLost, controlsVerified, context.Observation.Events);
                var classification = CapabilityClassifier.Classify(evidence);
                bool? absent = null;
                if (!capture.SessionStartPermitted) absent = true;
                else try { absent = !TraceEventSession.GetActiveSessionNames().Contains(capture.SessionName); } catch { }
                var visibility = new
                {
                    BraveObserved = context.Observation.Processes.Any(p => p.Identity.Name.Equals("brave.exe", StringComparison.OrdinalIgnoreCase)),
                    NetModObserved = context.Observation.Processes.Any(p => p.Identity.Name.Equals("netmod.exe", StringComparison.OrdinalIgnoreCase)),
                    context.Observation.ObservedIpv4, context.Observation.ObservedIpv6, context.Observation.ObservedLoopback,
                    NetModExternalIpv4 = Sum(context.Observation.Processes, "netmod.exe", "Ipv4/NonLoopback"),
                    NetModExternalIpv6 = Sum(context.Observation.Processes, "netmod.exe", "Ipv6/NonLoopback"),
                    BraveLocalIpv4 = Sum(context.Observation.Processes, "brave.exe", "Ipv4/Loopback"),
                    BraveLocalIpv6 = Sum(context.Observation.Processes, "brave.exe", "Ipv6/Loopback")
                };
                return new ProviderSummary(context.Schema.Definition.Name, context.Schema.ProviderAvailable,
                    capture.AccessDenied ? "Yes (unchanged normal token denied)" : capture.ConsumerStarted ? "No" : "Undetermined",
                    context.Schema.Error, context.Schema.TotalEventSchemas,
                    context.Schema.Events.Count(e => e.PidField is not null && e.ByteField is not null && e.Direction is not null && e.IsTransferAccounting),
                    context.Schema.Events.OrderByDescending(e => e.IsTransferAccounting).ThenByDescending(e => e.PidField is not null)
                        .Take(40).ToArray(), context.Schema.Keywords, context.Schema.KeywordMask,
                    capture, classification, evidence, context.Observation.AttributedTransferBytes,
                    context.Observation.TransferBytesWithoutPid, context.Observation.Processes.Take(30).ToArray(),
                    context.Observation.Samples, checks, visibility, absent,
                    capture.ConsumerStarted ? "Live window observed; missing events are not proof of missing schemas."
                        : "Live emission and VPN visibility could not be assessed because no consumer started. This does not establish SRUM dependency or prove no provider events are emitted.");
            }).ToArray();
            string recommendation = summaries.Any(s => s.Classification.Suitability == ProviderSuitability.FullySuitable)
                ? "A. NON-ELEVATED ETW IS SUFFICIENT"
                : summaries.Any(s => s.Classification.Suitability == ProviderSuitability.PartiallySuitable && s.Classification.HasCoherentAccounting)
                    ? "B. NON-ELEVATED ETW WORKS WITH LIMITATIONS"
                    : baseline.Verified ? "C. ELEVATED HELPER REQUIRED" : "INCONCLUSIVE: elevated baseline not verified; no architecture decision justified";
            bool trustworthy = transfers.Count == 2 && transfers.All(t => t.ClientExitedCleanly)
                && summaries.All(s => s.Capture.ShutdownClean && s.OwnedSessionAbsent == true
                    && (s.Capture.AccessDenied || s.Capture.ConsumerStarted && s.Capture.Error is null || !s.ProviderAvailable))
                && !cancellation.IsCancellationRequested && baseline.Verified;
            var report = new
            {
                Phase = "App Usage 001", RunningElevated = false, Recommendation = recommendation,
                DiagnosticTrustworthy = trustworthy, RequestedObservationSeconds = seconds,
                ActualDiagnosticSeconds = stopwatch.Elapsed.TotalSeconds, Workloads = transfers,
                Providers = summaries, KernelNetworkBaseline = baseline,
                BaselineComparison = "The App Usage 000 elevated result is a separately recorded capability baseline, not a simultaneous observation. No UAC or elevated comparison was launched.",
                ExistingApplications = new { BraveRunning = braveRunning, NetModRunning = netmodRunning },
                Vpn = adapter.Vpn,
                PassiveObservation = summaries.Any(s => s.Capture.ConsumerStarted) ? "Observed in each permitted candidate window."
                    : "Unavailable: both candidate consumers were blocked before observation.",
                Overhead = new { ViableCandidate = summaries.Any(s => s.Classification.Suitability == ProviderSuitability.FullySuitable)
                        ? "See permitted capture event rates; whole diagnostic includes controlled transfer work."
                        : "Not applicable: no fully suitable non-elevated source was demonstrated.",
                    WholeDiagnosticCpuPercentOfOneCore = (process.TotalProcessorTime - cpuStart).TotalSeconds / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001) * 100,
                    WorkingSetStartBytes = memoryStart, WorkingSetEndBytes = process.WorkingSet64,
                    EventRates = captures.Select(c => new { c.SessionName, EventsPerSecond = c.WindowStartUtc is not null && c.WindowEndUtc is not null
                        ? c.ProviderEventCount / Math.Max((c.WindowEndUtc.Value - c.WindowStartUtc.Value).TotalSeconds, 0.001) : (double?)null }).ToArray() },
                Limitations = new[] { "Permissions apply to the current unchanged normal token, not every Windows user or delegated ETW policy.",
                    "Schema field availability is reported separately from observed live capabilities. Event-header PID is never assumed to own the network activity.",
                    "TCPIP connection identifiers and cumulative counters are not automatically correlated or added to transfer totals.",
                    "Physical adapter usage, application attribution and VPN transport remain distinct; no deduplication or production accounting is implemented.",
                    "No conclusion about SRUM dependence can be drawn from denied session startup or an empty manifest template." }
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions
                { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } }));
            Console.WriteLine($"APP USAGE 001: {recommendation}");
            Console.WriteLine("Token elevated: False; no elevation or permission changes requested.");
            foreach (var summary in summaries)
            {
                Console.WriteLine($"{summary.Name}: {summary.Classification.Suitability}; available {summary.ProviderAvailable}; session start {summary.Capture.SessionStartPermitted}; provider enabled {summary.Capture.ProviderEnablePermitted}; events {summary.Capture.ProviderEventCount}; elevation required {summary.ElevationRequired}");
                Console.WriteLine($"  Observed attribution/bytes/direction: {summary.Classification.HasAttribution}/{summary.Classification.HasByteCount}/{summary.Classification.HasDirection}; coherent accounting {summary.Classification.HasCoherentAccounting}; attributed bytes {summary.AttributedTransferBytes}");
                Console.WriteLine($"  Installed schemas {summary.InstalledEventSchemas}; direct PID/bytes/direction transfer schemas {summary.DirectAccountingSchemas}; clean shutdown {summary.Capture.ShutdownClean}; owned session absent {summary.OwnedSessionAbsent}");
                Console.WriteLine($"  {summary.Classification.Reason} {summary.Capture.Error ?? summary.SchemaError}");
            }
            foreach (var transfer in transfers)
                Console.WriteLine($"CONTROLLED {transfer.Family}: child {transfer.ClientPid}; server {transfer.ServerPid}; TX {transfer.UploadBytes}; RX {transfer.DownloadBytes}+1 marker; normal workload passed {transfer.ClientExitedCleanly}");
            Console.WriteLine($"Kernel-Network elevated baseline: verified {baseline.Verified}; {baseline.Events} events; {baseline.AttributedBytes} observed logical bytes; {baseline.Processes} PID/lifetimes (separate prior window).");
            Console.WriteLine($"Brave running {braveRunning}; NetMod running {netmodRunning}; VPN inventory {adapter.Vpn.Status}. {report.PassiveObservation}");
            Console.WriteLine($"Viable candidate overhead: {report.Overhead.ViableCandidate}");
            Console.WriteLine($"Trustworthy diagnostic: {trustworthy}; result: {Path.GetFullPath(output)}");
            return trustworthy ? 0 : 1;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or IOException or JsonException)
        {
            Console.Error.WriteLine($"Diagnostic failed: {ex.GetType().Name}.");
            return 1;
        }
    }

    private sealed class ObservationContext(CandidateProviderSchema schema, IReadOnlySet<System.Net.IPAddress> local)
    {
        public CandidateProviderSchema Schema { get; } = schema;
        public WindowsProcessResolver Resolver { get; } = new();
        private CandidateObservation? observation;
        public CandidateObservation Observation => observation ??= new(Schema, Resolver, local);
    }
    private static ByteTotals Sum(IEnumerable<CandidateProcessObservation> processes, string name, string bucket)
    {
        var totals = new ByteTotals();
        foreach (var process in processes.Where(p => p.Identity.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            if (process.Breakdown.TryGetValue(bucket, out var bytes))
            { totals.Add(TrafficDirection.Receive, bytes.Receive); totals.Add(TrafficDirection.Send, bytes.Send); }
        return totals;
    }
    private static bool ProcessRunning(string name)
    {
        try { var matches = Process.GetProcessesByName(name); bool found = matches.Length > 0; foreach (var process in matches) process.Dispose(); return found; }
        catch { return false; }
    }
    private static Dictionary<string, string> Options(string[] args)
    {
        var options = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++)
        {
            var name = args[i];
            if (name is not ("--seconds" or "--output" or "--baseline") || ++i == args.Length || args[i].StartsWith("--"))
                throw new ArgumentException("Invalid diagnostic option or missing value.");
            options.Add(name, args[i]);
        }
        return options;
    }
}

public sealed record ProviderSummary(string Name, bool ProviderAvailable, string ElevationRequired, string? SchemaError,
    int InstalledEventSchemas, int DirectAccountingSchemas, IReadOnlyList<CandidateEventDescriptor> SchemaSamples,
    IReadOnlyList<CandidateKeywordDescriptor> Keywords, ulong KeywordMask, CandidateCaptureResult Capture,
    CapabilityClassification Classification, ProviderCapabilityEvidence Evidence, long AttributedTransferBytes,
    long TransferBytesWithoutPid, IReadOnlyList<CandidateProcessObservation> Processes, IReadOnlyList<object> Samples,
    IReadOnlyList<ControlledCandidateCheck> ControlledChecks, object Visibility, bool? OwnedSessionAbsent, string Interpretation);

public sealed record BaselineSummary(bool Verified, string? SourceFile, long? Events, long? AttributedBytes,
    int? Processes, bool? Ipv4, bool? Ipv6, bool? Loopback, string Reason)
{
    public static BaselineSummary Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return new(false, path, null, null, null, null, null, null, "Prior elevated App Usage 000 result unavailable; no elevation was attempted.");
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;
            var capture = root.GetProperty("capture");
            bool verified = root.GetProperty("phase").GetString() == "App Usage 000" && root.GetProperty("runningElevated").GetBoolean()
                && root.GetProperty("providerAvailable").GetBoolean() && root.GetProperty("hasPid").GetBoolean()
                && root.GetProperty("hasByteCount").GetBoolean() && root.GetProperty("hasDirection").GetBoolean()
                && root.GetProperty("ownedSessionAbsent").GetBoolean() && capture.GetProperty("sessionStarted").GetBoolean()
                && capture.GetProperty("providerEventCount").GetInt64() > 0
                && root.GetProperty("etwObservedBytes").GetProperty("allLogical").GetInt64() > 0
                && capture.GetProperty("shutdownClean").GetBoolean()
                && capture.GetProperty("eventsLost").GetInt32() == 0 && capture.GetProperty("error").ValueKind == JsonValueKind.Null;
            return new(verified, path, capture.GetProperty("providerEventCount").GetInt64(),
                root.GetProperty("etwObservedBytes").GetProperty("allLogical").GetInt64(),
                root.GetProperty("observedProcesses").GetArrayLength(), root.GetProperty("observedIpv4").GetBoolean(),
                root.GetProperty("observedIpv6").GetBoolean(), root.GetProperty("canIdentifyLoopback").GetBoolean(),
                "Previously recorded elevated capability evidence; not a simultaneous comparison or current measurement.");
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        { return new(false, path, null, null, null, null, null, null, "Prior result is missing required trustworthy baseline evidence."); }
    }
}
