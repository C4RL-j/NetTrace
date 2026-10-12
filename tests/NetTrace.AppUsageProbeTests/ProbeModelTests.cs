using System.Net;
using AppUsageEtwProbe;

internal static class ProbeModelTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Send bytes accumulate without becoming receive bytes", Check.Sync(SendAggregation)),
        ("Receive bytes accumulate without becoming send bytes", Check.Sync(ReceiveAggregation)),
        ("Negative byte observations cannot reduce accumulated totals", Check.Sync(NegativeCounts)),
        ("Large send and receive counters saturate without wrapping negative", Check.Sync(OverflowSafety)),
        ("IPv4 and IPv6 observations retain independent directional breakdowns", Check.Sync(FamilyBreakdowns)),
        ("Loopback, known local traffic, and external traffic retain separate counters", Check.Sync(TrafficBreakdowns)),
        ("Observation bounds follow event timestamps even when events arrive out of order", Check.Sync(TimestampBounds)),
    ];

    private static void SendAggregation()
    {
        var totals = new ByteTotals();
        totals.Add(TrafficDirection.Send, 64);
        totals.Add(TrafficDirection.Send, 128);
        Check.Equal(192L, totals.Send, "send total");
        Check.Equal(0L, totals.Receive, "receive total");
        Check.Equal(192L, totals.Total, "combined total");
    }

    private static void ReceiveAggregation()
    {
        var totals = new ByteTotals();
        totals.Add(TrafficDirection.Receive, 512);
        totals.Add(TrafficDirection.Receive, 1024);
        Check.Equal(1536L, totals.Receive, "receive total");
        Check.Equal(0L, totals.Send, "send total");
        Check.Equal(1536L, totals.Total, "combined total");
    }

    private static void NegativeCounts()
    {
        var totals = new ByteTotals();
        totals.Add(TrafficDirection.Send, 100);
        totals.Add(TrafficDirection.Receive, 200);
        totals.Add(TrafficDirection.Send, -1);
        totals.Add(TrafficDirection.Receive, long.MinValue);
        Check.Equal(100L, totals.Send, "negative send ignored");
        Check.Equal(200L, totals.Receive, "negative receive ignored");
        Check.Equal(300L, totals.Total, "negative counts cannot affect combined total");
    }

    private static void OverflowSafety()
    {
        var totals = new ByteTotals();
        totals.Add(TrafficDirection.Send, long.MaxValue - 4);
        totals.Add(TrafficDirection.Send, 8);
        totals.Add(TrafficDirection.Receive, long.MaxValue);
        totals.Add(TrafficDirection.Receive, 1);
        Check.Equal(long.MaxValue, totals.Send, "send saturation");
        Check.Equal(long.MaxValue, totals.Receive, "receive saturation");
        Check.Equal(long.MaxValue, totals.Total, "combined saturation");
    }

    private static void FamilyBreakdowns()
    {
        var observation = new ProcessObservation(Identity());
        observation.Add(Activity(100, TrafficDirection.Send, IpFamily.Ipv4));
        observation.Add(Activity(200, TrafficDirection.Receive, IpFamily.Ipv4));
        observation.Add(Activity(300, TrafficDirection.Send, IpFamily.Ipv6));
        observation.Add(Activity(400, TrafficDirection.Receive, IpFamily.Ipv6));
        Check.Equal(100L, observation.Breakdown["Ipv4/NonLoopback"].Send, "IPv4 send");
        Check.Equal(200L, observation.Breakdown["Ipv4/NonLoopback"].Receive, "IPv4 receive");
        Check.Equal(300L, observation.Breakdown["Ipv6/NonLoopback"].Send, "IPv6 send");
        Check.Equal(400L, observation.Breakdown["Ipv6/NonLoopback"].Receive, "IPv6 receive");
        Check.Equal(1000L, observation.Bytes.Total, "family aggregate");
    }

    private static void TrafficBreakdowns()
    {
        var observation = new ProcessObservation(Identity());
        observation.Add(Activity(100, classification: TrafficClass.Loopback));
        observation.Add(Activity(200, classification: TrafficClass.LocalToLocal));
        observation.Add(Activity(300, classification: TrafficClass.NonLoopback));
        Check.Equal(100L, observation.Breakdown["Ipv4/Loopback"].Send, "loopback traffic");
        Check.Equal(200L, observation.Breakdown["Ipv4/LocalToLocal"].Send, "local endpoint traffic");
        Check.Equal(300L, observation.Breakdown["Ipv4/NonLoopback"].Send, "non-loopback traffic");
        Check.Equal(600L, observation.Bytes.Total, "logical observation includes each event once");
    }

    private static void TimestampBounds()
    {
        var observation = new ProcessObservation(Identity());
        observation.Add(Activity(100) with { TimestampUtc = Epoch.AddSeconds(5) });
        observation.Add(Activity(200) with { TimestampUtc = Epoch });
        observation.Add(Activity(300) with { TimestampUtc = Epoch.AddSeconds(10) });
        Check.Equal<DateTime?>(Epoch, observation.FirstEventUtc, "first timestamp");
        Check.Equal<DateTime?>(Epoch.AddSeconds(10), observation.LastEventUtc, "last timestamp");
        Check.Equal(3L, observation.EventCount, "received events");
    }

    private static ProcessIdentity Identity(int pid = 101, string path = @"C:\Apps\Brave\brave.exe") =>
        new(pid, "brave", path, Epoch, "Resolved");

    private static NetworkActivity Activity(long bytes, TrafficDirection direction = TrafficDirection.Send,
        IpFamily family = IpFamily.Ipv4, TrafficClass classification = TrafficClass.NonLoopback) =>
        new(101, Epoch, bytes, direction, family, classification,
            IPAddress.Parse("192.0.2.1"), IPAddress.Parse("192.0.2.2"), null);
}
