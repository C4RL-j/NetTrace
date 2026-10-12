using AppUsageEtwProbe;
using AppUsageIpc;

namespace AppUsageDiagnosticClient;

public sealed record ConnectionObservation(DateTime TimestampUtc, string State, string? Detail);
public sealed record ControlledIpcCheck(IpFamily Family, int ClientPid, int ServerPid,
    bool WorkloadCompleted, bool ChildAttributed, bool ServerAttributed,
    long ChildRxBytes, long ChildTxBytes, bool ChildBytesExact, bool ServerBytesExact,
    bool LoopbackPreserved, bool FamilyPreserved);
public sealed record ClientMetrics(double CpuPercentOfOneCore, long WorkingSetBytes,
    double SnapshotsPerSecond, double ObservationSeconds, double PollingCpuPercentOfOneCore,
    double PollingObservationSeconds);
public sealed record SnapshotObservation(DateTime TimestampUtc, Guid CollectorInstanceId,
    TimeSpan CollectorUptime, long EventCount, int LostEventCount);
public sealed record DiagnosticReport(bool ClientElevated, int ClientPid, int ProtocolVersion,
    string VpnStateAtStart, int SnapshotCount, IReadOnlyList<ConnectionObservation> Connections,
    IReadOnlyList<Guid> CollectorInstances, IReadOnlyList<ControlledIpcCheck> ControlledChecks,
    IReadOnlyList<SnapshotObservation> Samples, CollectorSnapshot? FirstSnapshot, CollectorSnapshot? LastSnapshot,
    ClientMetrics Metrics, bool Cancelled, bool TestShutdownRequested)
{
    public StressResult? Stress { get; init; }
}

public sealed record StressResult(int Messages, long PayloadBytes, double Seconds, bool Completed);

public static class ControlledValidation
{
    public static ControlledIpcCheck Check(ControlledTransferResult transfer, CollectorSnapshot? snapshot)
    {
        var all = snapshot is null ? [] : snapshot.Applications.Concat([snapshot.System, snapshot.Unknown])
            .SelectMany(application => application.Processes).ToArray();
        var child = all.Where(process => process.ProcessId == transfer.ClientPid).ToArray();
        var server = all.Where(process => process.ProcessId == transfer.ServerPid).ToArray();
        long Sum(IEnumerable<ProcessSummary> processes, Func<TrafficCounters, long> select) =>
            processes.Aggregate(0L, (total, process) => ByteTotals.SaturatingAdd(total, select(process.Counters)));
        long FamilyRx(TrafficCounters value) => transfer.Family == IpFamily.Ipv4 ? value.Ipv4RxBytes : value.Ipv6RxBytes;
        long FamilyTx(TrafficCounters value) => transfer.Family == IpFamily.Ipv4 ? value.Ipv4TxBytes : value.Ipv6TxBytes;
        var rx = Sum(child, value => value.RxBytes);
        var tx = Sum(child, value => value.TxBytes);
        bool exact = transfer.ClientExitedCleanly && rx == transfer.DownloadBytes + 1 && tx == transfer.UploadBytes;
        bool serverExact = Sum(server, FamilyRx) == transfer.UploadBytes && Sum(server, FamilyTx) == transfer.DownloadBytes + 1;
        // Other diagnostic work is pipe-only. Family-specific totals isolate each controlled server transfer.
        return new(transfer.Family, transfer.ClientPid, transfer.ServerPid, transfer.ClientExitedCleanly,
            child.Length > 0, server.Length > 0, rx, tx, exact, serverExact,
            exact && Sum(child, value => value.LoopbackRxBytes) == rx && Sum(child, value => value.LoopbackTxBytes) == tx,
            exact && Sum(child, FamilyRx) == rx && Sum(child, FamilyTx) == tx);
    }
}
