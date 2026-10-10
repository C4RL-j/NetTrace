using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using NetTrace.Models;
using NetTrace.Presentation;
using NetTrace.Services;

internal static class InternetReachabilityTests
{
    public static (string Name, Action Run)[] Cases =>
    [
        ("Reachability defaults use two DNS names, two HTTPS endpoints, 60 seconds and a four-second budget", Defaults),
        ("Reachability starts Checking without manufacturing a completed result", Checking),
        ("Successful DNS and HTTPS report Available", Run(() => State(true, true, InternetReachabilityStatus.Available))),
        ("Successful HTTPS proves Available even when separate DNS probes fail", Run(() => State(false, true, InternetReachabilityStatus.Available))),
        ("Successful DNS with all HTTPS probes failing reports Limited", Run(() => State(true, false, InternetReachabilityStatus.Limited))),
        ("Failed DNS and HTTPS report Unavailable without changing ICMP quality", Run(() => State(false, false, InternetReachabilityStatus.Unavailable))),
        ("One working HTTPS provider is sufficient when its peer fails", Run(IndependentHttpsFallback)),
        ("One working DNS hostname is sufficient when its peer fails", Run(IndependentDnsFallback)),
        ("Independent reachability probes start together and publish only a completed set", Run(ParallelProbeSet)),
        ("Reachability result includes the completion timestamp in UTC", Run(CompletionMetadata)),
        ("HTTP 200 counts as HTTPS reachability", Run(() => HttpStatus(HttpStatusCode.OK))),
        ("HTTP 204 counts as HTTPS reachability", Run(() => HttpStatus(HttpStatusCode.NoContent))),
        ("HTTP 301 counts as HTTPS reachability", Run(() => HttpStatus(HttpStatusCode.MovedPermanently))),
        ("HTTP 403 counts as HTTPS reachability", Run(() => HttpStatus(HttpStatusCode.Forbidden))),
        ("HTTP 404 counts as HTTPS reachability", Run(() => HttpStatus(HttpStatusCode.NotFound))),
        ("HTTP 503 is still a legitimate remote HTTPS response", Run(() => HttpStatus(HttpStatusCode.ServiceUnavailable))),
        ("HTTPS reachability receives headers without reading a response body", Run(HeadersOnly)),
        ("HTTPS probes reuse one client handler and send no identifiers or credentials", Run(HandlerReuseAndPrivacy)),
        ("System DNS reachability accepts a resolved IPv4 address", Run(() => SystemDns([IPAddress.Parse("192.0.2.1")], true))),
        ("System DNS reachability accepts a resolved IPv6 address", Run(() => SystemDns([IPAddress.Parse("2001:db8::1")], true))),
        ("An empty system DNS response does not prove reachability", Run(() => SystemDns([], false))),
        ("Pre-cancelled system DNS probes do not invoke the resolver", Run(PreCancelledDns)),
        ("Pre-cancelled HTTPS probes do not send a request", Run(PreCancelledHttps)),
        ("System DNS lookup failures become normal failed reachability observations", Run(DnsFailure)),
        ("HTTP connection failures become normal failed reachability observations", Run(() => HttpsFailure(new HttpRequestException("Owned fixture connection failure")))),
        ("TLS failures become normal failed reachability observations", Run(() => HttpsFailure(new HttpRequestException("Owned fixture TLS failure", new AuthenticationException("Owned fixture certificate failure"))))),
        ("Socket failures become normal failed reachability observations", Run(() => HttpsFailure(new SocketException((int)SocketError.ConnectionRefused)))),
        ("Reachability timeout is one overall parallel budget rather than four sequential timeouts", Run(OverallTimeout)),
        ("Completed DNS success survives a timed-out HTTPS set as Limited", Run(TimeoutRetainsDnsSuccess)),
        ("Completed HTTPS success survives timed-out peers as Available", Run(TimeoutRetainsHttpsSuccess)),
        ("Shutdown cancels in-flight reachability without publishing a partial result", Run(StopCancelsPending)),
        ("Slow cancellation callbacks cannot block the reachability Stop caller", Run(SlowCancellationCallback)),
        ("A non-cooperative source cannot delay reachability shutdown or fault its completion", Run(NonCooperativeShutdown)),
        ("Repeated reachability Start calls reuse one background worker", Run(RepeatedStarts)),
        ("A slow reachability set never overlaps its successor cycle", Run(NoOverlappingCycles)),
        ("Suspending reachability discards an unfinished pre-sleep set", Run(SuspendDiscards)),
        ("Resume promptly obtains a fresh set instead of waiting sixty seconds", Run(ResumeFresh)),
        ("Suspension leaves the last completed result intact", Run(SuspendPreservesCompleted)),
        ("Repeated Resume calls do not start duplicate reachability loops", Run(RepeatedResume)),
        ("A late non-cooperative result cannot replace fresh resumed reachability", Run(LatePreSleepResult)),
        ("A late fault is observed without breaking later reachability cycles", Run(LateFaultContinues)),
        ("Reachability can complete repeated background cycles without a WPF window", Run(BackgroundCycles)),
        ("Stop and disposal before Start and repeated cleanup are safe", Run(IdempotentCleanup)),
        ("Reachability endpoint configuration is defensively snapshotted", ConfigurationSnapshot),
        ("Reachability Available remains independent of all-failed ICMP history", Run(IcmpIndependence)),
    ];

    private static Action Run(Func<Task> test) => () => test().GetAwaiter().GetResult();

    public static async Task<int> RunLiveValidationAsync()
    {
        // This explicit command is separate from Cases and never touches the database.
        using var monitor = new InternetReachabilityMonitor();
        try
        {
            Console.WriteLine("Explicit one-cycle Internet reachability validation (DNS/HTTPS only).");
            Console.WriteLine("DNS: " + string.Join(", ", monitor.DnsHostnames));
            Console.WriteLine("HTTPS: " + string.Join(", ", monitor.HttpsEndpoints));
            monitor.Start();
            var result = await ResultAsync(monitor, wait: TimeSpan.FromSeconds(10));
            Console.WriteLine($"Status={result.Status}; CheckedAtUtc={result.CheckedAtUtc:O}; DnsSucceeded={result.DnsSucceeded}; HttpsSucceeded={result.HttpsSucceeded}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Live validation did not complete: " + exception.Message);
            return 1;
        }
        finally { await StopAsync(monitor); }
    }

    private static void Defaults()
    {
        using var monitor = new InternetReachabilityMonitor(new ConstantSource());
        Equal(TimeSpan.FromSeconds(60), monitor.Interval, "default cadence");
        Equal(TimeSpan.FromSeconds(4), monitor.Timeout, "whole check deadline");
        Equal(2, monitor.DnsHostnames.Count, "independent DNS names");
        Equal(2, monitor.HttpsEndpoints.Count, "independent HTTPS endpoints");
        Equal(2, monitor.DnsHostnames.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "distinct DNS hostnames");
        Equal(2, monitor.HttpsEndpoints.Select(uri => uri.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "distinct endpoint hosts");
        foreach (var endpoint in monitor.HttpsEndpoints)
        {
            Equal("https", endpoint.Scheme, "encrypted ordinary HTTPS request");
            Check(string.IsNullOrEmpty(endpoint.UserInfo) && string.IsNullOrEmpty(endpoint.Query) && string.IsNullOrEmpty(endpoint.Fragment),
                "Default endpoints must not contain credentials, identifiers or query data.");
        }
    }

    private static void Checking()
    {
        using var monitor = new InternetReachabilityMonitor(new ConstantSource());
        Check(monitor.LatestResult is null, "Before a completed probe, UI must represent Checking.");
        Equal(InternetReachabilityStatus.Checking, default(InternetReachabilityStatus), "neutral initial state");
        Check(monitor.Completion.IsCompletedSuccessfully, "An unstarted monitor has no running worker.");
    }

    private static async Task State(bool dns, bool https, InternetReachabilityStatus expected)
    {
        using var monitor = new InternetReachabilityMonitor(new ConstantSource(dns, https));
        try
        {
            monitor.Start();
            var result = await ResultAsync(monitor);
            AssertResult(result, expected, dns, https);
        }
        finally { await StopAsync(monitor); }
    }

    private static async Task IndependentHttpsFallback()
    {
        var source = new DelegateSource((_, _) => Task.FromResult(false),
            (endpoint, _) => endpoint.Host == InternetReachabilityMonitor.DefaultHttpsEndpoints[0].Host
                ? Task.FromException<bool>(new HttpRequestException("Owned fixture first provider failure"))
                : Task.FromResult(true));
        await ExpectAsync(source, InternetReachabilityStatus.Available, false, true);
    }

    private static async Task IndependentDnsFallback()
    {
        var source = new DelegateSource((host, _) => host == InternetReachabilityMonitor.DefaultDnsHostnames[0]
            ? Task.FromException<bool>(new SocketException((int)SocketError.HostNotFound))
            : Task.FromResult(true), (_, _) => Task.FromResult(false));
        await ExpectAsync(source, InternetReachabilityStatus.Limited, true, false);
    }

    private static async Task ParallelProbeSet()
    {
        var source = new PendingSource();
        using var monitor = new InternetReachabilityMonitor(source);
        try
        {
            monitor.Start();
            await source.WaitForCallsAsync(4);
            Equal(2, source.Calls.Count(call => call.Dns), "DNS probes in the same set");
            Equal(2, source.Calls.Count(call => !call.Dns), "HTTPS probes in the same set");
            Check(monitor.LatestResult is null, "No half-finished set may be published.");
            source.Calls[0].Finish(true);
            source.Calls[1].Finish(false);
            Check(monitor.LatestResult is null, "One pending HTTPS/DNS peer keeps the set incomplete.");
            source.Calls[2].Finish(true);
            source.Calls[3].Finish(false);
            AssertResult(await ResultAsync(monitor), InternetReachabilityStatus.Available, true, true);
        }
        finally { await StopAsync(monitor); }
    }

    private static async Task CompletionMetadata()
    {
        var clock = new ObservationClock();
        var source = new PendingSource();
        using var monitor = new InternetReachabilityMonitor(source, timeProvider: clock);
        try
        {
            monitor.Start();
            await source.WaitForCallsAsync(4);
            clock.UtcNow = clock.UtcNow.AddSeconds(3);
            source.FinishRange(0, true);
            var result = await ResultAsync(monitor);
            Equal(clock.UtcNow, result.CheckedAtUtc, "completed observation time rather than cycle start");
            Equal(TimeSpan.Zero, result.CheckedAtUtc.Offset, "persistable diagnostic UTC timestamp");
        }
        finally { await StopAsync(monitor); }
    }

    private static async Task HttpStatus(HttpStatusCode status)
    {
        using var source = new SystemInternetReachabilityProbeSource(new ResponseHandler(status), (_, _) => Task.FromResult(Array.Empty<IPAddress>()));
        await ExpectAsync(source, InternetReachabilityStatus.Available, false, true);
    }

    private static async Task HeadersOnly()
    {
        var content = new DoNotReadContent();
        var handler = new ResponseHandler(HttpStatusCode.OK) { CreateContent = () => content };
        using var source = new SystemInternetReachabilityProbeSource(handler);
        Check(await source.ProbeHttpsAsync(new Uri("https://owned.invalid/check"), CancellationToken.None), "Received headers prove HTTPS reachability.");
        Equal(0, content.Reads, "ResponseHeadersRead must not buffer or read response content");
        Equal(1, content.Disposals, "response resources are released immediately");
    }

    private static async Task HandlerReuseAndPrivacy()
    {
        var handler = new ResponseHandler(HttpStatusCode.NoContent);
        using (var source = new SystemInternetReachabilityProbeSource(handler))
        {
            var endpoint = new Uri("https://owned.invalid/generate_204");
            for (var index = 0; index < 3; index++)
                Check(await source.ProbeHttpsAsync(endpoint, CancellationToken.None), "Repeated probes reuse the source.");
            Equal(3, handler.Requests.Count, "one request per explicitly requested probe");
            foreach (var request in handler.Requests)
            {
                Equal(endpoint, request.Uri, "unmodified lightweight URL");
                Check(request.Method == HttpMethod.Get || request.Method == HttpMethod.Head, "Only ordinary body-free GET/HEAD requests are needed.");
                Check(request.HeaderNames.Count == 0 && !request.HasContent,
                    "No cookies, authorization, user identifiers, telemetry or request body may be sent.");
            }
            Equal(0, handler.Disposals, "handler remains alive between probes");
        }
        Equal(1, handler.Disposals, "reusable handler is disposed once at source shutdown");
    }

    private static async Task SystemDns(IPAddress[] addresses, bool expected)
    {
        string? observedHost = null;
        using var source = new SystemInternetReachabilityProbeSource(new ResponseHandler(HttpStatusCode.NoContent),
            (host, _) => { observedHost = host; return Task.FromResult(addresses); });
        Equal(expected, await source.ResolveDnsAsync("owned.invalid", CancellationToken.None), "system resolver result");
        Equal("owned.invalid", observedHost, "DNS hostname is passed to the configured system resolver");
    }

    private static async Task PreCancelledDns()
    {
        var calls = 0;
        using var source = new SystemInternetReachabilityProbeSource(new ResponseHandler(HttpStatusCode.NoContent),
            (_, _) => { calls++; return Task.FromResult(Array.Empty<IPAddress>()); });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ThrowsCancellationAsync(() => source.ResolveDnsAsync("owned.invalid", cancellation.Token));
        Equal(0, calls, "cancelled DNS request must not start resolver work");
    }

    private static async Task PreCancelledHttps()
    {
        var handler = new ResponseHandler(HttpStatusCode.NoContent);
        using var source = new SystemInternetReachabilityProbeSource(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ThrowsCancellationAsync(() => source.ProbeHttpsAsync(new Uri("https://owned.invalid/"), cancellation.Token));
        Equal(0, handler.Requests.Count, "cancelled HTTPS request sends no network data");
    }

    private static async Task DnsFailure()
    {
        using var source = new SystemInternetReachabilityProbeSource(new ThrowingHandler(new HttpRequestException("Owned fixture failure")),
            (_, _) => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound)));
        await ExpectAsync(source, InternetReachabilityStatus.Unavailable, false, false);
    }

    private static async Task HttpsFailure(Exception exception)
    {
        using var source = new SystemInternetReachabilityProbeSource(new ThrowingHandler(exception),
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.0.2.1") }));
        await ExpectAsync(source, InternetReachabilityStatus.Limited, true, false);
    }

    private static async Task OverallTimeout()
    {
        var source = new PendingSource();
        using var monitor = new InternetReachabilityMonitor(source, timeout: TimeSpan.FromMilliseconds(120));
        try
        {
            var stopwatch = Stopwatch.StartNew();
            monitor.Start();
            await source.WaitForCallsAsync(4);
            AssertResult(await ResultAsync(monitor), InternetReachabilityStatus.Unavailable, false, false);
            Check(stopwatch.Elapsed < TimeSpan.FromSeconds(2), "Four failed endpoints must consume a single bounded budget.");
            await EventuallyAsync(() => source.Calls.All(call => call.CancellationToken.IsCancellationRequested));
            Equal(4, source.Calls.Length, "one parallel set rather than endpoint retries");
        }
        finally { await StopAsync(monitor); }
    }

    private static async Task TimeoutRetainsDnsSuccess() =>
        await PartialTimeoutAsync(dnsSuccess: true);

    private static async Task TimeoutRetainsHttpsSuccess() =>
        await PartialTimeoutAsync(dnsSuccess: false);

    private static async Task PartialTimeoutAsync(bool dnsSuccess)
    {
        var source = new PendingSource();
        using var monitor = new InternetReachabilityMonitor(source, timeout: TimeSpan.FromMilliseconds(150));
        try
        {
            monitor.Start();
            await source.WaitForCallsAsync(4);
            source.Calls.First(call => call.Dns == dnsSuccess).Finish(true);
            AssertResult(await ResultAsync(monitor), dnsSuccess ? InternetReachabilityStatus.Limited : InternetReachabilityStatus.Available,
                dnsSuccess, !dnsSuccess);
        }
        finally { await StopAsync(monitor); }
    }

    private static async Task StopCancelsPending()
    {
        var source = new PendingSource();
        using var monitor = new InternetReachabilityMonitor(source);
        monitor.Start();
        await source.WaitForCallsAsync(4);
        var stopwatch = Stopwatch.StartNew();
        monitor.Stop();
        Check(stopwatch.Elapsed < TimeSpan.FromSeconds(1), "Stop must not synchronously wait for network timeouts.");
        await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Check(monitor.LatestResult is null, "Shutdown must discard a partial set.");
        Check(source.Calls.All(call => call.CancellationToken.IsCancellationRequested), "Shutdown reaches every probe token.");
    }

    private static async Task NonCooperativeShutdown()
    {
        var source = new PendingSource(cooperative: false);
        using var monitor = new InternetReachabilityMonitor(source);
        monitor.Start();
        await source.WaitForCallsAsync(4);
        await StopAsync(monitor);
        source.FailRange(0);
        Check(monitor.Completion.IsCompletedSuccessfully, "Late probe faults must not fault the worker completion.");
        Check(monitor.LatestResult is null, "Abandoned shutdown work must not publish an observation.");
    }

    private static async Task SlowCancellationCallback()
    {
        using var source = new SlowCancellationSource();
        using var monitor = new InternetReachabilityMonitor(source);
        try
        {
            monitor.Start();
            await source.Pending.WaitForCallsAsync(4);
            var stopwatch = Stopwatch.StartNew();
            monitor.Stop();
            Check(stopwatch.Elapsed < TimeSpan.FromSeconds(1), "Stop must queue cancellation without waiting for a callback.");
            await source.CallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(!monitor.Completion.IsCompleted, "Asynchronous worker cleanup owns the unfinished cancellation callback.");
            source.ReleaseCallback.Set();
            await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            Check(monitor.LatestResult is null, "Cancellation must never publish the incomplete cycle.");
        }
        finally { source.ReleaseCallback.Set(); await StopAsync(monitor); }
    }

    private static async Task RepeatedStarts()
    {
        var source = new PendingSource();
        using var monitor = new InternetReachabilityMonitor(source);
        try
        {
            monitor.Start();
            var completion = monitor.Completion;
            for (var index = 0; index < 20; index++) monitor.Start();
            await source.WaitForCallsAsync(4);
            Check(ReferenceEquals(completion, monitor.Completion), "Start is idempotent and reuses the same loop.");
            Equal(4, source.Calls.Length, "one pending cycle across repeated starts");
        }
        finally { await StopAsync(monitor); }
    }

    private static async Task NoOverlappingCycles()
    {
        var source = new PendingSource(cooperative: false);
        using var monitor = new InternetReachabilityMonitor(source, interval: TimeSpan.FromMilliseconds(15), timeout: TimeSpan.FromMilliseconds(60));
        try
        {
            monitor.Start();
            await source.WaitForCallsAsync(4);
            var first = await ResultAsync(monitor);
            Equal(4, source.Calls.Length, "non-cooperative timed-out probes are drained before the next set");
            source.FinishRange(0, true);
            await source.WaitForCallsAsync(8);
            Equal(4, source.MaximumPending, "only one four-probe set is active at any time");
            source.FinishRange(4, true);
            AssertResult(await ResultAsync(monitor, first), InternetReachabilityStatus.Available, true, true);
        }
        finally { await StopAsync(monitor); source.FinishAll(false); }
    }

    private static async Task SuspendDiscards()
    {
        var source = new PendingSource();
        using var monitor = new InternetReachabilityMonitor(source);
        try
        {
            monitor.Start();
            await source.WaitForCallsAsync(4);
            source.Calls.First(call => call.Dns).Finish(true);
            monitor.Suspend();
            await EventuallyAsync(() => source.Calls.All(call => call.Task.IsCompleted));
            Check(monitor.LatestResult is null, "Sleep must not publish the partially successful pre-sleep set.");
            Equal(4, source.Calls.Length, "No new set begins while suspended.");
        }
        finally { await StopAsync(monitor); }
    }

    private static async Task ResumeFresh()
    {
        var source = new PendingSource();
        using var monitor = new InternetReachabilityMonitor(source);
        try
        {
            monitor.Start();
            await source.WaitForCallsAsync(4);
            monitor.Suspend();
            await EventuallyAsync(() => source.Calls.All(call => call.Task.IsCompleted));
            monitor.Resume();
            await source.WaitForCallsAsync(8);
            source.FinishRange(4, true);
            AssertResult(await ResultAsync(monitor), InternetReachabilityStatus.Available, true, true);
            Check(source.Calls.Take(4).All(call => call.CancellationToken.IsCancellationRequested), "Prior generation cancelled before fresh resume.");
        }
        finally { await StopAsync(monitor); }
    }

    private static async Task SuspendPreservesCompleted()
    {
        var source = new PendingSource();
        using var monitor = new InternetReachabilityMonitor(source, interval: TimeSpan.FromMilliseconds(20));
        try
        {
            monitor.Start();
            await source.WaitForCallsAsync(4);
            source.FinishRange(0, true);
            var first = await ResultAsync(monitor);
            await source.WaitForCallsAsync(8);
            monitor.Suspend();
            await EventuallyAsync(() => source.Calls.All(call => call.Task.IsCompleted));
            Check(ReferenceEquals(first, monitor.LatestResult), "Incomplete suspended work cannot overwrite the latest completed measurement.");
        }
        finally { await StopAsync(monitor); }
    }

    private static async Task RepeatedResume()
    {
        var source = new PendingSource();
        using var monitor = new InternetReachabilityMonitor(source);
        try
        {
            monitor.Start();
            await source.WaitForCallsAsync(4);
            monitor.Suspend();
            await EventuallyAsync(() => source.Calls.All(call => call.Task.IsCompleted));
            var completion = monitor.Completion;
            for (var index = 0; index < 20; index++) monitor.Resume();
            await source.WaitForCallsAsync(8);
            Equal(8, source.Calls.Length, "one fresh resumed set");
            Check(ReferenceEquals(completion, monitor.Completion), "Resume uses the existing lifecycle loop.");
        }
        finally { await StopAsync(monitor); }
    }

    private static async Task LatePreSleepResult()
    {
        var source = new PendingSource(cooperative: false);
        using var monitor = new InternetReachabilityMonitor(source);
        try
        {
            monitor.Start();
            await source.WaitForCallsAsync(4);
            monitor.Suspend();
            monitor.Resume();
            source.FinishRange(0, true);
            await source.WaitForCallsAsync(8);
            Check(monitor.LatestResult is null, "A late pre-sleep success must be discarded, not published as current.");
            source.FinishRange(4, false);
            AssertResult(await ResultAsync(monitor), InternetReachabilityStatus.Unavailable, false, false);
            Equal(4, source.MaximumPending, "Obsolete set drains before fresh work begins.");
        }
        finally { await StopAsync(monitor); source.FinishAll(false); }
    }

    private static async Task LateFaultContinues()
    {
        var source = new PendingSource(cooperative: false);
        using var monitor = new InternetReachabilityMonitor(source, interval: TimeSpan.FromMilliseconds(15), timeout: TimeSpan.FromMilliseconds(60));
        try
        {
            monitor.Start();
            await source.WaitForCallsAsync(4);
            var first = await ResultAsync(monitor);
            source.FailRange(0);
            await source.WaitForCallsAsync(8);
            source.FinishRange(4, true);
            AssertResult(await ResultAsync(monitor, first), InternetReachabilityStatus.Available, true, true);
        }
        finally { await StopAsync(monitor); source.FinishAll(false); }
    }

    private static async Task BackgroundCycles()
    {
        var source = new PendingSource();
        using var monitor = new InternetReachabilityMonitor(source, interval: TimeSpan.FromMilliseconds(20));
        try
        {
            monitor.Start();
            InternetReachabilityResult? previous = null;
            for (var cycle = 0; cycle < 3; cycle++)
            {
                await source.WaitForCallsAsync((cycle + 1) * 4);
                source.FinishRange(cycle * 4, cycle != 1);
                previous = await ResultAsync(monitor, previous);
                Equal(cycle == 1 ? InternetReachabilityStatus.Unavailable : InternetReachabilityStatus.Available,
                    previous.Status, "latest-only in-memory result updates without a dashboard");
            }
            Equal(4, source.MaximumPending, "no per-window or per-page worker exists");
        }
        finally { await StopAsync(monitor); }
    }

    private static async Task IdempotentCleanup()
    {
        var monitor = new InternetReachabilityMonitor(new ConstantSource());
        monitor.Suspend();
        monitor.Stop();
        monitor.Stop();
        monitor.Dispose();
        monitor.Dispose();
        await monitor.Completion;
        Check(monitor.LatestResult is null, "Stopping before Start does not manufacture a failed observation.");
        var rejected = false;
        try { monitor.Start(); }
        catch (ObjectDisposedException) { rejected = true; }
        Check(rejected, "A disposed monitor cannot start a new background loop.");
    }

    private static void ConfigurationSnapshot()
    {
        string[] names = ["first.owned.invalid", "second.owned.invalid"];
        Uri[] endpoints = [new("https://first.owned.invalid/check"), new("https://second.owned.invalid/check")];
        using var monitor = new InternetReachabilityMonitor(new ConstantSource(), dnsHostnames: names, httpsEndpoints: endpoints);
        names[0] = "mutated.owned.invalid";
        endpoints[0] = new Uri("https://mutated.owned.invalid/check");
        Equal("first.owned.invalid", monitor.DnsHostnames[0], "caller array mutation cannot alter the monitor's DNS targets");
        Equal("first.owned.invalid", monitor.HttpsEndpoints[0].Host, "caller array mutation cannot alter HTTPS targets");
        if (monitor.DnsHostnames is IList<string> dns) Check(dns.IsReadOnly, "Public DNS configuration must be read-only.");
        if (monitor.HttpsEndpoints is IList<Uri> https) Check(https.IsReadOnly, "Public HTTPS configuration must be read-only.");
    }

    private static async Task IcmpIndependence()
    {
        var origin = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        IcmpProbeResult[] samples = Enumerable.Range(0, 3)
            .Select(index => new IcmpProbeResult(origin.AddSeconds(15 * index), "1.1.1.1", 0, 4, null, 100, null)).ToArray();
        var before = IcmpQualityEvaluator.Evaluate(samples);
        var chartBefore = IcmpStabilityChartData.Create(samples);
        using var monitor = new InternetReachabilityMonitor(new ConstantSource(true, true));
        try
        {
            monitor.Start();
            Equal(InternetReachabilityStatus.Available, (await ResultAsync(monitor)).Status, "HTTPS available when ICMP is blocked");
            Equal(IcmpQuality.Unavailable, before.Quality, "all-failed ICMP quality remains Unavailable");
            Equal(before, IcmpQualityEvaluator.Evaluate(samples), "Reachability cannot rewrite latency, jitter, loss or ICMP quality.");
            Equal(3, chartBefore.Samples.Count, "Existing rolling snapshot is unchanged.");
            Check(chartBefore.PacketLossPoints.All(point => point.Percent == 100), "All-failed ICMP loss bars remain independent.");
            Check(samples.All(sample => sample.AverageLatencyMs is null && sample.JitterMs is null && sample.PacketLossPercent == 100),
                "No reachability result can synthesize ICMP measurements.");
        }
        finally { await StopAsync(monitor); }
    }

    private static async Task ExpectAsync(IInternetReachabilityProbeSource source, InternetReachabilityStatus status, bool dns, bool https)
    {
        using var monitor = new InternetReachabilityMonitor(source);
        try { monitor.Start(); AssertResult(await ResultAsync(monitor), status, dns, https); }
        finally { await StopAsync(monitor); }
    }

    private static async Task<InternetReachabilityResult> ResultAsync(InternetReachabilityMonitor monitor,
        InternetReachabilityResult? previous = null, TimeSpan? wait = null)
    {
        await EventuallyAsync(() => monitor.LatestResult is { } result && !ReferenceEquals(result, previous), wait);
        return monitor.LatestResult!;
    }

    private static async Task StopAsync(InternetReachabilityMonitor monitor)
    {
        monitor.Stop();
        await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task EventuallyAsync(Func<bool> condition, TimeSpan? wait = null)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed >= (wait ?? TimeSpan.FromSeconds(2))) throw new TimeoutException("Owned reachability fixture did not reach its expected state.");
            await Task.Delay(5);
        }
    }

    private static async Task ThrowsCancellationAsync(Func<Task<bool>> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("A pre-cancelled source request must observe cancellation.");
    }

    private static void AssertResult(InternetReachabilityResult result, InternetReachabilityStatus status, bool dns, bool https)
    {
        Equal(status, result.Status, "classified reachability state");
        Equal(dns, result.DnsSucceeded, "aggregate DNS success evidence");
        Equal(https, result.HttpsSucceeded, "aggregate HTTPS success evidence");
        Equal(TimeSpan.Zero, result.CheckedAtUtc.Offset, "UTC result timestamp");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }

    private static void Equal<T>(T expected, T actual, string description)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{description}: expected {expected}, got {actual}.");
    }

    private sealed class ConstantSource(bool dns = false, bool https = false) : IInternetReachabilityProbeSource
    {
        public Task<bool> ResolveDnsAsync(string hostname, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(dns); }
        public Task<bool> ProbeHttpsAsync(Uri endpoint, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(https); }
    }

    private sealed class DelegateSource(Func<string, CancellationToken, Task<bool>> dns,
        Func<Uri, CancellationToken, Task<bool>> https) : IInternetReachabilityProbeSource
    {
        public Task<bool> ResolveDnsAsync(string hostname, CancellationToken cancellationToken) => dns(hostname, cancellationToken);
        public Task<bool> ProbeHttpsAsync(Uri endpoint, CancellationToken cancellationToken) => https(endpoint, cancellationToken);
    }

    private sealed class PendingSource(bool cooperative = true) : IInternetReachabilityProbeSource
    {
        private readonly object _gate = new();
        private readonly List<PendingCall> _calls = [];
        private int _maximumPending;
        public PendingCall[] Calls { get { lock (_gate) return _calls.ToArray(); } }
        public int MaximumPending { get { lock (_gate) return _maximumPending; } }
        public Task<bool> ResolveDnsAsync(string hostname, CancellationToken cancellationToken) => Begin(true, cancellationToken);
        public Task<bool> ProbeHttpsAsync(Uri endpoint, CancellationToken cancellationToken) => Begin(false, cancellationToken);
        private Task<bool> Begin(bool dns, CancellationToken token)
        {
            var call = new PendingCall(dns, token, cooperative);
            lock (_gate)
            {
                _calls.Add(call);
                _maximumPending = Math.Max(_maximumPending, _calls.Count(existing => !existing.Task.IsCompleted));
            }
            return call.Task;
        }
        public Task WaitForCallsAsync(int count) => EventuallyAsync(() => Calls.Length >= count);
        public void FinishRange(int start, bool success)
        { foreach (var call in Calls.Skip(start).Take(4)) call.Finish(success); }
        public void FailRange(int start)
        { foreach (var call in Calls.Skip(start).Take(4)) call.Fail(); }
        public void FinishAll(bool success)
        { foreach (var call in Calls) call.Finish(success); }
    }

    private sealed class PendingCall
    {
        private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;
        public bool Dns { get; }
        public CancellationToken CancellationToken { get; }
        public Task<bool> Task => _result.Task;
        public PendingCall(bool dns, CancellationToken token, bool cooperative)
        {
            Dns = dns;
            CancellationToken = token;
            if (cooperative) _registration = token.Register(() => _result.TrySetCanceled(token));
            _ = Task.ContinueWith(_ => _registration.Dispose(), System.Threading.CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        public void Finish(bool success) => _result.TrySetResult(success);
        public void Fail() => _result.TrySetException(new HttpRequestException("Owned late fixture failure"));
    }

    private sealed class SlowCancellationSource : IInternetReachabilityProbeSource, IDisposable
    {
        private CancellationTokenRegistration _registration;
        private int _registered;
        public PendingSource Pending { get; } = new();
        public TaskCompletionSource CallbackEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim ReleaseCallback { get; } = new(false);
        public Task<bool> ResolveDnsAsync(string hostname, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _registered, 1) == 0)
                _registration = cancellationToken.Register(() => { CallbackEntered.TrySetResult(); ReleaseCallback.Wait(); });
            return Pending.ResolveDnsAsync(hostname, cancellationToken);
        }
        public Task<bool> ProbeHttpsAsync(Uri endpoint, CancellationToken cancellationToken) => Pending.ProbeHttpsAsync(endpoint, cancellationToken);
        public void Dispose() { ReleaseCallback.Set(); _registration.Dispose(); ReleaseCallback.Dispose(); }
    }

    private sealed class ObservationClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 11, 5, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed record CapturedRequest(Uri? Uri, HttpMethod Method, IReadOnlyList<string> HeaderNames, bool HasContent);

    private sealed class ResponseHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public ConcurrentQueue<CapturedRequest> Requests { get; } = new();
        public Func<HttpContent>? CreateContent { get; init; }
        public int Disposals { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(new CapturedRequest(request.RequestUri, request.Method, request.Headers.Select(header => header.Key).ToArray(), request.Content is not null));
            return Task.FromResult(new HttpResponseMessage(status) { Content = CreateContent?.Invoke() });
        }
        protected override void Dispose(bool disposing)
        { if (disposing) Disposals++; base.Dispose(disposing); }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class DoNotReadContent : HttpContent
    {
        public int Reads { get; private set; }
        public int Disposals { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        { Reads++; return Task.FromException(new InvalidOperationException("A headers-only probe must not download content.")); }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }
}
