using System.IO.Pipes;

namespace AppUsageIpc;

public enum IpcState
{
    Connected,
    CollectorUnavailable,
    AccessDenied,
    PeerRejected,
    IncompatibleProtocol,
    MalformedResponse,
    Disconnected,
    TimedOut,
    Cancelled
}

public sealed record IpcResult<T>(IpcState State, T? Value, string? Detail = null);

// One request at a time keeps byte-stream frames ordered. Every connection and
// request has a deadline; failures close the pipe instead of reusing partial frames.
public sealed class IpcClient : IAsyncDisposable
{
    private readonly string pipeName;
    private readonly Func<NamedPipeClientStream, bool> verifyServer;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private NamedPipeClientStream? pipe;
    private CollectorSnapshot? previousSnapshot;
    private bool handshaken;
    private int disposed;

    public IpcClient(string pipeName, Func<NamedPipeClientStream, bool>? serverVerifier = null)
    {
        NativePipeSecurity.ValidatePipeName(pipeName);
        this.pipeName = pipeName;
        // This seam exists only for deterministic diagnostic tests, never IPC or CLI.
        verifyServer = serverVerifier ?? (stream => NativePipeSecurity.VerifyServer(stream.SafePipeHandle));
    }

    public async Task<IpcResult<CollectorCapabilities>> ConnectAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = Deadline(cancellationToken, Protocol.ConnectTimeout);
        bool entered = false;
        try
        {
            await gate.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            if (Volatile.Read(ref disposed) != 0)
                return Failure<CollectorCapabilities>(IpcState.Disconnected, "The diagnostic client is disposed.");
            ClosePipe();
            pipe = NativePipeSecurity.CreateClient(pipeName);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            if (!verifyServer(pipe))
                return Failure<CollectorCapabilities>(IpcState.PeerRejected, "The collector peer did not pass local identity and elevation checks.");
            // The Hello exchange gets the normal request deadline after connect.
            using var helloTimeout = Deadline(cancellationToken, Protocol.RequestTimeout);
            var response = await ExchangeAsync("Hello", helloTimeout.Token).ConfigureAwait(false);
            var state = ValidateResponse(response, "Hello");
            if (state is not null) return Failure<CollectorCapabilities>(state.Value, Detail(state.Value));
            handshaken = true;
            return new(IpcState.Connected, response.Capabilities);
        }
        catch (OperationCanceledException)
        {
            return Failure<CollectorCapabilities>(CancellationState(cancellationToken, connecting: true), "The collector connection did not complete.", entered);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure<CollectorCapabilities>(IpcState.AccessDenied, "Access to the local collector pipe was denied.", entered);
        }
        catch (IpcProtocolException)
        {
            return Failure<CollectorCapabilities>(IpcState.MalformedResponse, "The collector returned a malformed response.", entered);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return Failure<CollectorCapabilities>(IpcState.Disconnected, "The collector disconnected during connection or handshake.", entered);
        }
        finally { if (entered) gate.Release(); }
    }

    public Task<IpcResult<CollectorCapabilities>> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        RequestAsync("GetCapabilities", response => response.Capabilities!, cancellationToken);

    public Task<IpcResult<CollectorSnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        RequestAsync("GetSnapshot", response => response.Snapshot!, cancellationToken);

    public Task<IpcResult<bool>> ShutdownForTestAsync(CancellationToken cancellationToken = default) =>
        RequestAsync("ShutdownForTest", _ => true, cancellationToken);

    private async Task<IpcResult<T>> RequestAsync<T>(string operation, Func<PipeResponse, T> value,
        CancellationToken cancellationToken)
    {
        using var timeout = Deadline(cancellationToken, Protocol.RequestTimeout);
        bool entered = false;
        try
        {
            await gate.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            if (Volatile.Read(ref disposed) != 0 || !handshaken || pipe is null || !pipe.IsConnected)
                return Failure<T>(IpcState.Disconnected, "The collector is disconnected.");
            var response = await ExchangeAsync(operation, timeout.Token).ConfigureAwait(false);
            var state = ValidateResponse(response, operation);
            if (state is not null) return Failure<T>(state.Value, Detail(state.Value));
            if (operation == "GetSnapshot")
            {
                var current = response.Snapshot!;
                if (previousSnapshot is not null && !SnapshotIsMonotonic(previousSnapshot, current))
                    return Failure<T>(IpcState.MalformedResponse, "Cumulative collector counters moved backwards.");
                previousSnapshot = current;
            }
            var result = new IpcResult<T>(IpcState.Connected, value(response));
            if (operation == "ShutdownForTest")
            {
                // Closing after consuming the acknowledgement is the server's
                // bounded drain signal, avoiding synchronous FlushFileBuffers.
                ClosePipe();
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            return Failure<T>(CancellationState(cancellationToken, connecting: false), "The collector request did not complete.", entered);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure<T>(IpcState.AccessDenied, "Access to the local collector pipe was denied.", entered);
        }
        catch (IpcProtocolException)
        {
            return Failure<T>(IpcState.MalformedResponse, "The collector returned a malformed response.", entered);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return Failure<T>(IpcState.Disconnected, "The collector disconnected during a response.", entered);
        }
        finally { if (entered) gate.Release(); }
    }

    private async Task<PipeResponse> ExchangeAsync(string operation, CancellationToken cancellationToken)
    {
        await IpcProtocol.WriteRequestAsync(pipe!, new(Protocol.Version, operation), cancellationToken).ConfigureAwait(false);
        return await IpcProtocol.ReadResponseAsync(pipe!, cancellationToken).ConfigureAwait(false);
    }

    private static IpcState? ValidateResponse(PipeResponse response, string operation)
    {
        if (response.ProtocolVersion != Protocol.Version || response.Status == "IncompatibleProtocol")
            return IpcState.IncompatibleProtocol;
        if (response.Status != "Ok")
        {
            if (response.Capabilities is not null || response.Snapshot is not null || string.IsNullOrWhiteSpace(response.Error))
                return IpcState.MalformedResponse;
            return response.Status == "ShutdownDisabled" && operation == "ShutdownForTest"
                ? IpcState.AccessDenied : IpcState.MalformedResponse;
        }
        if (response.Error is not null) return IpcState.MalformedResponse;
        return operation switch
        {
            "Hello" or "GetCapabilities" => response.Snapshot is null && ValidCapabilities(response.Capabilities)
                ? null : IpcState.MalformedResponse,
            "GetSnapshot" => response.Capabilities is null && ValidSnapshot(response.Snapshot)
                ? null : IpcState.MalformedResponse,
            "ShutdownForTest" => response.Capabilities is null && response.Snapshot is null
                ? null : IpcState.MalformedResponse,
            _ => IpcState.MalformedResponse
        };
    }

    private static bool ValidCapabilities(CollectorCapabilities? capabilities) => capabilities is not null
        && capabilities.CollectorElevated && capabilities.CumulativeSinceStart
        && capabilities.Provider == "Microsoft-Windows-Kernel-Network"
        && !string.IsNullOrWhiteSpace(capabilities.CounterSemantics);

    private static bool ValidSnapshot(CollectorSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.CollectorInstanceId == Guid.Empty
            || snapshot.CapturedAtUtc.Kind != DateTimeKind.Utc || snapshot.CapturedAtUtc == default
            || snapshot.CollectorUptime < TimeSpan.Zero || snapshot.EventCount < 0
            || snapshot.ProviderEventCount < 0 || snapshot.UnsupportedEventCount < 0 || snapshot.MalformedEventCount < 0
            || !ValidCapabilities(snapshot.Capabilities)
            || (snapshot.Capabilities.LostEventCountAvailable ? snapshot.LostEventCount < 0 : snapshot.LostEventCount != -1)
            || !double.IsFinite(snapshot.Metrics.CpuPercentOfOneCore) || snapshot.Metrics.CpuPercentOfOneCore < 0
            || snapshot.Metrics.WorkingSetBytes < 0 || snapshot.Metrics.SnapshotsServed < 0)
            return false;
        var identities = new HashSet<string>(StringComparer.Ordinal);
        long events = 0;
        foreach (var application in snapshot.Applications.Append(snapshot.System).Append(snapshot.Unknown))
        {
            if (!ValidApplication(application) || !identities.Add(application.Identity)) return false;
            events = Sum(events, application.EventCount);
        }
        return events == snapshot.EventCount
            && snapshot.System.Identity == "system" && snapshot.System.Processes.All(process => process.ProcessId == 4)
            && snapshot.Unknown.Identity == "unknown" && snapshot.Unknown.Processes.All(process => process.ProcessId != 4);
    }

    private static bool ValidApplication(ApplicationSummary application)
    {
        if (string.IsNullOrWhiteSpace(application.Identity) || string.IsNullOrWhiteSpace(application.ProcessName)
            || application.EventCount < 0 || !ValidCounters(application.Counters)) return false;
        long events = 0;
        var totals = new long[14];
        var lifetimes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var process in application.Processes)
        {
            if (process.ProcessId < 0 || string.IsNullOrWhiteSpace(process.ProcessName)
                || string.IsNullOrWhiteSpace(process.LifetimeIdentity) || !lifetimes.Add(process.LifetimeIdentity)
                || string.IsNullOrWhiteSpace(process.AttributionStatus) || process.EventCount < 0
                || process.StartTimeUtc is DateTime start && start.Kind != DateTimeKind.Utc
                || !ValidCounters(process.Counters)) return false;
            events = Sum(events, process.EventCount);
            var fields = CounterValues(process.Counters);
            for (int i = 0; i < totals.Length; i++) totals[i] = Sum(totals[i], fields[i]);
        }
        return events == application.EventCount && totals.SequenceEqual(CounterValues(application.Counters));
    }

    private static bool ValidCounters(TrafficCounters counters)
    {
        var fields = CounterValues(counters);
        if (fields.Any(field => field < 0)) return false;
        return Sum(counters.Ipv4RxBytes, counters.Ipv6RxBytes) == counters.RxBytes
            && Sum(counters.Ipv4TxBytes, counters.Ipv6TxBytes) == counters.TxBytes
            && Sum(Sum(counters.LoopbackRxBytes, counters.NonLoopbackRxBytes),
                Sum(counters.LocalToLocalRxBytes, counters.UnclassifiedRxBytes)) == counters.RxBytes
            && Sum(Sum(counters.LoopbackTxBytes, counters.NonLoopbackTxBytes),
                Sum(counters.LocalToLocalTxBytes, counters.UnclassifiedTxBytes)) == counters.TxBytes;
    }

    private static long[] CounterValues(TrafficCounters counters) =>
    [counters.RxBytes, counters.TxBytes, counters.Ipv4RxBytes, counters.Ipv4TxBytes,
        counters.Ipv6RxBytes, counters.Ipv6TxBytes, counters.LoopbackRxBytes, counters.LoopbackTxBytes,
        counters.NonLoopbackRxBytes, counters.NonLoopbackTxBytes, counters.LocalToLocalRxBytes,
        counters.LocalToLocalTxBytes, counters.UnclassifiedRxBytes, counters.UnclassifiedTxBytes];

    private static bool SnapshotIsMonotonic(CollectorSnapshot previous, CollectorSnapshot current)
    {
        // A connected pipe belongs to one collector instance. Reconnection clears
        // the baseline so a legitimate restart can begin again with empty counters.
        if (previous.CollectorInstanceId != current.CollectorInstanceId
            || current.CapturedAtUtc < previous.CapturedAtUtc || current.CollectorUptime < previous.CollectorUptime
            || current.EventCount < previous.EventCount || current.ProviderEventCount < previous.ProviderEventCount
            || current.UnsupportedEventCount < previous.UnsupportedEventCount || current.MalformedEventCount < previous.MalformedEventCount
            || current.Metrics.SnapshotsServed < previous.Metrics.SnapshotsServed
            || previous.LostEventCount >= 0 && current.LostEventCount >= 0 && current.LostEventCount < previous.LostEventCount)
            return false;
        var applications = current.Applications.Append(current.System).Append(current.Unknown)
            .ToDictionary(app => app.Identity, StringComparer.Ordinal);
        foreach (var oldApplication in previous.Applications.Append(previous.System).Append(previous.Unknown))
        {
            if (!applications.TryGetValue(oldApplication.Identity, out var application)
                || application.EventCount < oldApplication.EventCount
                || !CountersMonotonic(oldApplication.Counters, application.Counters)) return false;
            var processes = application.Processes.ToDictionary(process => process.LifetimeIdentity, StringComparer.Ordinal);
            foreach (var oldProcess in oldApplication.Processes)
                if (!processes.TryGetValue(oldProcess.LifetimeIdentity, out var process)
                    || process.EventCount < oldProcess.EventCount || !CountersMonotonic(oldProcess.Counters, process.Counters))
                    return false;
        }
        return true;
    }

    private static bool CountersMonotonic(TrafficCounters previous, TrafficCounters current) =>
        CounterValues(previous).Zip(CounterValues(current)).All(pair => pair.Second >= pair.First);

    private static long Sum(long first, long second) => long.MaxValue - first < second ? long.MaxValue : first + second;

    private static string Detail(IpcState state) => state switch
    {
        IpcState.IncompatibleProtocol => "The collector protocol version is incompatible.",
        IpcState.AccessDenied => "Diagnostic shutdown is disabled by the collector.",
        _ => "The collector response does not satisfy the summary protocol."
    };

    private CancellationTokenSource Deadline(CancellationToken cancellationToken, TimeSpan duration)
    {
        var result = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        result.CancelAfter(duration);
        return result;
    }

    private IpcState CancellationState(CancellationToken cancellationToken, bool connecting) =>
        cancellationToken.IsCancellationRequested ? IpcState.Cancelled
        : stopping.IsCancellationRequested ? IpcState.Disconnected
        : connecting && pipe?.IsConnected != true ? IpcState.CollectorUnavailable : IpcState.TimedOut;

    private IpcResult<T> Failure<T>(IpcState state, string detail, bool closePipe = true)
    {
        if (closePipe) ClosePipe();
        return new(state, default, detail);
    }

    private void ClosePipe()
    {
        handshaken = false;
        previousSnapshot = null;
        pipe?.Dispose();
        pipe = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stopping.Cancel();
        // An in-flight operation is cancelled before waiting for its serialized
        // cleanup. Keep synchronization primitives alive for late disposed calls.
        await gate.WaitAsync().ConfigureAwait(false);
        try { ClosePipe(); }
        finally { gate.Release(); }
    }
}
