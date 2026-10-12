using System.Globalization;
using AppUsageEtwProbe;

internal static class ProbeAggregationTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Parsed event bytes aggregate into the attributed process and matching direction", Check.Sync(ParsedProcessTotals)),
        ("Multiple executable PIDs group once while retaining each process observation", Check.Sync(ApplicationGrouping)),
        ("Different executable locations keep distinct application groups", Check.Sync(DistinctApplications)),
        ("Reused PID observations remain separate process lifetimes in one application group", Check.Sync(ReusedPid)),
        ("A racing or denied process lookup preserves bytes under a PID fallback", Check.Sync(UnknownProcess)),
        ("System and zero-PID traffic remain visible in diagnostic accounting", Check.Sync(SystemAndUnattributed)),
        ("Invalid activities cannot create process entries or reduce any counter", Check.Sync(InvalidActivities)),
        ("Loopback and local proxy bytes remain separate from external observed bytes", Check.Sync(SeparateTrafficClasses)),
        ("Observed family capability flags reflect accepted traffic rather than schema availability", Check.Sync(ObservedFamilies)),
        ("Interface coverage evidence tracks external traffic and excludes loopback and local proxy events", Check.Sync(NonLoopbackInterfaceEvidence)),
        ("Diagnostic byte quantities format binary units without negative values or exponent notation", Check.Sync(ByteFormattingExamples)),
        ("Diagnostic byte formatting is stable under a non-English machine culture", Check.Sync(ByteFormattingCulture)),
    ];

    private static void ParsedProcessTotals()
    {
        var aggregator = NewAggregator();
        aggregator.Add(ProbeParsingTests.Parse(ProbeParsingTests.Event(pid: 1234U, bytes: 100U)));
        aggregator.Add(ProbeParsingTests.Parse(ProbeParsingTests.Event(701, pid: 1234U, bytes: 200U)));
        var process = aggregator.Processes.Single();
        Check.Equal(1234, process.Identity.Pid, "event PID resolves process");
        Check.Equal(100L, process.Bytes.Send, "process send bytes");
        Check.Equal(200L, process.Bytes.Receive, "process receive bytes");
        Check.Equal(2L, process.EventCount, "process events counted once");
    }

    private static void ApplicationGrouping()
    {
        var aggregator = NewAggregator((pid, _) => Identity(pid,
            pid == 1234 ? @"C:\Apps\Brave\brave.exe" : @"c:\apps\brave\BRAVE.EXE"));
        aggregator.Add(ProbeParsingTests.Parse(ProbeParsingTests.Event(pid: 1234U, bytes: 100U)));
        aggregator.Add(ProbeParsingTests.Parse(ProbeParsingTests.Event(701, pid: 5678U, bytes: 200U)));
        Check.Equal(2, aggregator.Processes.Count, "each PID retained");
        var application = aggregator.Applications.Single();
        Check.Equal(2, application.Processes.Count, "one executable group contains both PIDs");
        Check.Equal(100L, application.Bytes.Send, "group send");
        Check.Equal(200L, application.Bytes.Receive, "group receive");
        Check.Equal(300L, application.Bytes.Total, "group total counts each event once");
    }

    private static void DistinctApplications()
    {
        var aggregator = NewAggregator((pid, _) => Identity(pid,
            pid == 1234 ? @"C:\Apps\One\client.exe" : @"C:\Apps\Two\client.exe") with { Name = "client.exe" });
        aggregator.Add(ProbeParsingTests.Parse(ProbeParsingTests.Event(pid: 1234U, bytes: 100U)));
        aggregator.Add(ProbeParsingTests.Parse(ProbeParsingTests.Event(pid: 5678U, bytes: 200U)));
        Check.Equal(2, aggregator.Applications.Count, "executable path keeps same-name programs separate");
    }

    private static void ReusedPid()
    {
        var aggregator = NewAggregator((pid, timestamp) => Identity(pid) with
        {
            StartTimeUtc = timestamp < Epoch.AddMinutes(1) ? Epoch : Epoch.AddMinutes(1)
        });
        var first = ProbeParsingTests.Parse(ProbeParsingTests.Event(bytes: 100U));
        aggregator.Add(first);
        aggregator.Add(first with { TimestampUtc = Epoch.AddMinutes(2), Bytes = 200 });
        Check.Equal(2, aggregator.Processes.Count, "PID reuse splits lifetimes");
        Check.True(aggregator.Processes.Select(process => process.Identity.LifetimeKey).Distinct().Count() == 2,
            "lifetime identities differ");
        Check.Equal(2, aggregator.Applications.Single().Processes.Count, "same executable preserves both lifetimes");
        Check.Equal(300L, aggregator.Applications.Single().Bytes.Total, "lifetime grouping retains all bytes");
    }

    private static void UnknownProcess()
    {
        var aggregator = NewAggregator((_, _) => throw new InvalidOperationException("Process exited"));
        aggregator.Add(ProbeParsingTests.Parse(ProbeParsingTests.Event(pid: 4321U, bytes: 512U)));
        var process = aggregator.Processes.Single();
        Check.Equal(4321, process.Identity.Pid, "lookup failure retains event PID");
        Check.Equal("PID 4321", process.Identity.Name, "unknown process fallback");
        Check.Equal<string?>(null, process.Identity.ExecutablePath, "unresolved path not fabricated");
        Check.Equal<DateTime?>(null, process.Identity.StartTimeUtc, "unresolved lifetime not fabricated");
        Check.True(process.Identity.LifetimeKey.EndsWith("unverified", StringComparison.Ordinal),
            "unresolved lifetime remains explicitly provisional");
        Check.Equal(512L, aggregator.Applications.Single().Bytes.Send, "unknown process bytes are retained");
    }

    private static void SystemAndUnattributed()
    {
        using var resolver = new WindowsProcessResolver();
        var aggregator = new ActivityAggregator(resolver);
        aggregator.Add(ProbeParsingTests.Parse(ProbeParsingTests.Event(pid: 0U, bytes: 64U)));
        aggregator.Add(ProbeParsingTests.Parse(ProbeParsingTests.Event(701, pid: 4U, bytes: 128U)));
        Check.Equal(2, aggregator.Applications.Count, "system and unattributed buckets remain visible");
        Check.Equal(64L, aggregator.Applications.Single(app => app.Name == "Unknown / Unattributed").Bytes.Send,
            "zero PID send traffic");
        Check.Equal(128L, aggregator.Applications.Single(app => app.Name == "System").Bytes.Receive,
            "system receive traffic");
    }

    private static void InvalidActivities()
    {
        var resolverCalls = 0;
        var aggregator = NewAggregator((pid, _) =>
        {
            resolverCalls++;
            return Identity(pid);
        });
        var valid = ProbeParsingTests.Parse(ProbeParsingTests.Event(bytes: 100U));
        aggregator.Add(valid);
        aggregator.Add(valid with { Bytes = -1 });
        aggregator.Add(valid with { Pid = -1 });
        Check.Equal(1, resolverCalls, "invalid activities never perform process lookup");
        Check.Equal(1L, aggregator.EventCount, "invalid activities never count as accepted events");
        Check.Equal(100L, aggregator.Processes.Single().Bytes.Total, "invalid activities cannot alter total");
    }

    private static void SeparateTrafficClasses()
    {
        var aggregator = NewAggregator();
        var activity = ProbeParsingTests.Parse(ProbeParsingTests.Event(bytes: 100U));
        aggregator.Add(activity);
        aggregator.Add(activity with { TrafficClass = TrafficClass.Loopback, Bytes = 200 });
        aggregator.Add(activity with { TrafficClass = TrafficClass.LocalToLocal, Bytes = 300 });
        aggregator.Add(activity with { TrafficClass = TrafficClass.Unknown, Bytes = 400 });
        Check.Equal(100L, aggregator.Traffic[TrafficClass.NonLoopback].Total, "external observations");
        Check.Equal(200L, aggregator.Traffic[TrafficClass.Loopback].Total, "loopback observations");
        Check.Equal(300L, aggregator.Traffic[TrafficClass.LocalToLocal].Total, "known local proxy observations");
        Check.Equal(400L, aggregator.Traffic[TrafficClass.Unknown].Total, "unknown endpoint observations");
        Check.Equal(1000L, aggregator.Applications.Single().Bytes.Total, "logical aggregate remains explainable");
    }

    private static void ObservedFamilies()
    {
        var aggregator = NewAggregator();
        Check.True(!aggregator.ObservedIpv4 && !aggregator.ObservedIpv6, "no traffic implies no observed family evidence");
        var activity = ProbeParsingTests.Parse(ProbeParsingTests.Event());
        aggregator.Add(activity);
        Check.True(aggregator.ObservedIpv4 && !aggregator.ObservedIpv6, "IPv4 evidence alone does not prove IPv6 observation");
        aggregator.Add(activity with { Family = IpFamily.Ipv6 });
        Check.True(aggregator.ObservedIpv4 && aggregator.ObservedIpv6, "both observed families retained");
    }

    private static void ByteFormattingExamples()
    {
        foreach (var (bytes, expected) in new (long, string)[]
        {
            (-1, "0 B"), (0, "0 B"), (1023, "1023 B"), (1024, "1 KiB"),
            (1280, "1.25 KiB"), (1_048_576, "1 MiB"), (1_073_741_824, "1 GiB"),
            (long.MaxValue, "8 EiB")
        }) Check.Equal(expected, ByteFormatting.Format(bytes), "readable byte quantity");
    }

    private static void NonLoopbackInterfaceEvidence()
    {
        var aggregator = NewAggregator();
        var activity = ProbeParsingTests.Parse(ProbeParsingTests.Event());
        aggregator.Add(activity with { InterfaceIndex = 7 });
        aggregator.Add(activity with { TrafficClass = TrafficClass.Loopback, InterfaceIndex = null });
        aggregator.Add(activity with { TrafficClass = TrafficClass.LocalToLocal, InterfaceIndex = 99 });
        Check.True(aggregator.NonLoopbackInterfaces.SetEquals(new[] { 7 }),
            "only external event interfaces participate in selected-adapter coverage");
        Check.True(!aggregator.NonLoopbackInterfaceMissing,
            "loopback without an interface does not invalidate external interface evidence");
        aggregator.Add(activity with { InterfaceIndex = 8 });
        Check.True(aggregator.NonLoopbackInterfaces.SetEquals(new[] { 7, 8 }),
            "external events on another interface prevent a selected-interface-only claim");
        aggregator.Add(activity with { InterfaceIndex = null });
        Check.True(aggregator.NonLoopbackInterfaceMissing,
            "missing interface on external traffic is retained as uncertainty");
    }

    private static void ByteFormattingCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Check.Equal("1.25 KiB", ByteFormatting.Format(1280), "invariant diagnostic quantity format");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static ActivityAggregator NewAggregator(Func<int, DateTime, ProcessIdentity>? resolver = null) =>
        new(new DelegateResolver(resolver ?? ((pid, _) => Identity(pid))));

    private static ProcessIdentity Identity(int pid, string path = @"C:\Apps\Brave\brave.exe") =>
        new(pid, "brave.exe", path, Epoch, "Resolved fixture");

    private sealed class DelegateResolver(Func<int, DateTime, ProcessIdentity> resolve) : IProcessResolver
    {
        public ProcessIdentity Resolve(int pid, DateTime eventTimeUtc) => resolve(pid, eventTimeUtc);
    }
}
