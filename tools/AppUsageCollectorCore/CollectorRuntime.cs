using System.Net;
using System.Net.NetworkInformation;
using AppUsageElevatedCollector;
using AppUsageEtwProbe;
using AppUsageIpc;

namespace AppUsageCollectorCore;

// Hosting and pipe authorization remain outside this class. Both diagnostic
// collector and service consume the same parser, aggregation and capture engine.
public sealed class CollectorRuntime : IDisposable
{
    private readonly EtwCapture capture;
    private readonly bool enforceOwnership;
    private readonly IDisposable? ownedResolver;
    private readonly string identityName;
    private readonly bool privileged;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int started;
    public CollectorAggregation Aggregation { get; }
    public DateTime StartedAtUtc => Aggregation.StartedAtUtc;
    public Task Ready => ready.Task;
    public bool ReclaimedStaleSession { get; private set; }
    public EtwCaptureResult? FinalCaptureResult { get; private set; }
    public static bool CleanupOwnedSession() => SessionOwnership.CleanupOwnedSession();

    public CollectorRuntime(ProviderSchema schema, bool privileged = true, string? identityName = null,
        bool enforceOwnership = true, EtwCapture? capture = null, IProcessResolver? resolver = null,
        IReadOnlySet<IPAddress>? localAddresses = null, TimeProvider? timeProvider = null,
        UsageEventPersistence? persistence = null)
    {
        this.enforceOwnership = enforceOwnership;
        this.identityName = identityName ?? "Diagnostic";
        this.privileged = privileged;
        if (resolver is null) { var native = new WindowsProcessResolver(); resolver = native; ownedResolver = native; }
        Aggregation = new CollectorAggregation(schema, resolver, localAddresses ?? ReadLocalAddresses(),
            timeProvider: timeProvider, collectorElevated: EtwCapture.IsElevated,
            snapshotCacheInterval: TimeSpan.FromSeconds(1),
            acceptedActivity: persistence is null ? null : persistence.RecordActivity);
        this.capture = capture ?? new EtwCapture(schema, sessionNamePrefix: "NetTrace-AppUsage003",
            exactSessionName: SessionOwnership.SessionName, exactSessionGuid: SessionOwnership.SessionGuid);
    }

    public async Task<EtwCaptureResult> RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("A collector runtime represents one lifetime.");
        ServiceOwnershipLease? ownership = null;
        try
        {
            if (enforceOwnership)
            {
                ownership = ServiceOwnershipLease.Acquire();
                ReclaimedStaleSession = SessionOwnership.RecoverExactSession();
            }
            var result = await capture.CaptureContinuousAsync(cancellationToken, value => Aggregation.Add(value), _ =>
            {
                Aggregation.UpdateCaptureStatus(capture.GetLiveStatus());
                ready.TrySetResult();
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            FinalCaptureResult = result;
            Aggregation.UpdateCaptureStatus(capture.GetLiveStatus());
            if (!ready.Task.IsCompleted)
            {
                if (result.Cancelled) ready.TrySetCanceled(cancellationToken);
                else ready.TrySetException(new InvalidOperationException(result.Error ?? "Collector capture did not start."));
            }
            return result;
        }
        catch (Exception exception) { ready.TrySetException(exception); throw; }
        finally { ownership?.Dispose(); }
    }

    public CollectorCapabilities GetCapabilities()
    {
        Aggregation.UpdateCaptureStatus(capture.GetLiveStatus());
        return Aggregation.GetCapabilities() with { ServiceIdentity = identityName };
    }

    public CollectorSnapshot GetSnapshot(bool countRequest = true)
    {
        var status = capture.GetLiveStatus();
        Aggregation.UpdateCaptureStatus(status);
        var snapshot = Aggregation.GetSnapshot(countRequest);
        return snapshot with
        {
            Capabilities = snapshot.Capabilities with { ServiceIdentity = identityName },
            Health = new(StartedAtUtc, status.IsCapturing, status.SessionName ?? SessionOwnership.SessionName,
                Aggregation.LastEventAtUtc, snapshot.Applications.Count, identityName, privileged)
        };
    }

    public bool? OwnedSessionAbsent()
    {
        if (!enforceOwnership) return null;
        try { return NativeEtwController.Query(SessionOwnership.SessionName) is null; }
        catch { return null; }
    }
    public void Dispose() => ownedResolver?.Dispose();

    private static IReadOnlySet<IPAddress> ReadLocalAddresses()
    {
        var values = new HashSet<IPAddress> { IPAddress.Loopback, IPAddress.IPv6Loopback };
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    foreach (var address in adapter.GetIPProperties().UnicastAddresses)
                        values.Add(ActivityParser.Normalize(address.Address));
                }
                catch (NetworkInformationException) { }
            }
        }
        catch (NetworkInformationException) { }
        return values;
    }
}
