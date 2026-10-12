using System.ComponentModel;
using System.Net;
using AppUsageEtwProbe;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace AppUsageNonElevatedProbe;

public sealed record CandidateRawEvent(int EventId, int Version, int HeaderPid,
    DateTime TimestampUtc, IReadOnlyDictionary<string, object?> Payload);

public sealed record CandidateCaptureResult(string SessionName, bool SessionStartPermitted,
    bool ProviderEnablePermitted, bool ConsumerStarted, bool AccessDenied, string? AccessDeniedStage,
    bool ShutdownClean, bool Cancelled, int EventsLost, long ProviderEventCount,
    long SelectedEventCount, long UnsupportedEventCount, long MalformedEventCount,
    DateTime? WindowStartUtc, DateTime? WindowEndUtc, string? Error);

public interface ICandidateSession : IDisposable
{
    void Start();
    void Process();
    void Stop();
    bool SessionStartPermitted { get; }
    bool ProviderEnablePermitted { get; }
    string Stage { get; }
    int EventsLost { get; }
    long ProviderEventCount { get; }
    long SelectedEventCount { get; }
    long UnsupportedEventCount { get; }
    long MalformedEventCount { get; }
}

/// <summary>
/// Tests one installed provider from the current normal token. No elevation, group,
/// service or provider-registration changes are requested. ETW data is never saved.
/// </summary>
public sealed class CandidateCapture(Guid providerGuid, ulong keywordMask,
    IReadOnlyDictionary<(int EventId, int Version), IReadOnlyList<PayloadField>> selectedSafeFields,
    Func<string, Action<CandidateRawEvent>, ICandidateSession>? sessionFactory = null,
    Func<bool>? isElevated = null)
{
    public async Task<CandidateCaptureResult> CaptureAsync(TimeSpan duration,
        CancellationToken cancellationToken, Action<CandidateRawEvent> onEvent,
        Func<CancellationToken, Task>? onStarted = null)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(duration), "A candidate observation must last at most five minutes.");
        ArgumentNullException.ThrowIfNull(onEvent);
        string sessionName = $"NetTrace-AppUsage001-{Environment.ProcessId}-{Guid.NewGuid():N}";
        bool consumerStarted = false, accessDenied = false, clean = true;
        bool cancelled = cancellationToken.IsCancellationRequested;
        string? deniedStage = null, error = null;
        long startTicks = 0, endTicks = DateTime.MaxValue.Ticks;
        int lost = 0;
        ICandidateSession? session = null;
        Task? consumer = null, startedWork = null;
        using var workCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // The injection supports deterministic guard tests. The real CLI supplies no override.
        if ((isElevated ?? (() => EtwCapture.IsElevated))())
            return Result("Elevated token rejected. This probe must run from a normal unelevated process.");
        if (cancelled) return Result(null);

        try
        {
            session = sessionFactory is null
                ? new NativeSession(sessionName, providerGuid, keywordMask, selectedSafeFields, Accept)
                : sessionFactory(sessionName, Accept);
            session.Start();
            Interlocked.Exchange(ref startTicks, DateTime.UtcNow.Ticks);
            consumer = Task.Run(session.Process, CancellationToken.None);
            consumerStarted = true;
            var window = Task.Delay(duration, cancellationToken);
            startedWork = onStarted?.Invoke(workCancellation.Token) ?? Task.CompletedTask;
            var first = await Task.WhenAny(window, consumer, startedWork).ConfigureAwait(false);
            if (first == startedWork)
            {
                await startedWork.ConfigureAwait(false);
                first = await Task.WhenAny(window, consumer).ConfigureAwait(false);
            }
            if (first == consumer)
            {
                await consumer.ConfigureAwait(false);
                error = "The candidate ETW consumer ended before its observation window completed.";
            }
            else await window.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            accessDenied = IsAccessDenied(ex);
            if (accessDenied)
            {
                deniedStage = session?.Stage ?? "SessionStart";
                error = $"Non-elevated ETW access denied at {deniedStage}. No elevation or permissions change was attempted.";
            }
            else error = Failure(session?.Stage ?? "Setup", ex);
        }
        finally
        {
            if (consumerStarted) Interlocked.Exchange(ref endTicks, DateTime.UtcNow.Ticks);
            workCancellation.Cancel();
            if (session is not null)
            {
                try { session.Stop(); }
                catch (Exception ex) { clean = false; AddError(Failure("Owned session stop", ex)); }
                if (consumer is not null)
                {
                    try { await consumer.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                    catch (Exception ex) { clean = false; AddError(Failure("Consumer shutdown", ex)); }
                }
                if (session.SessionStartPermitted)
                {
                    try { lost = session.EventsLost; }
                    catch (Exception ex) { AddError(Failure("Loss-counter read", ex)); }
                }
                try { session.Dispose(); }
                catch (Exception ex) { clean = false; AddError(Failure("Owned session dispose", ex)); }
            }
            if (startedWork is not null)
            {
                try { await startedWork.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (OperationCanceledException) when (workCancellation.IsCancellationRequested) { }
                catch (Exception ex) { AddError(Failure("Controlled workload", ex)); }
            }
        }
        return Result(error);

        void Accept(CandidateRawEvent value)
        {
            long start = Interlocked.Read(ref startTicks), end = Interlocked.Read(ref endTicks);
            if (start != 0 && value.TimestampUtc.Ticks >= start && value.TimestampUtc.Ticks <= end) onEvent(value);
        }
        void AddError(string message) => error = error is null ? message : error + " " + message;
        CandidateCaptureResult Result(string? message) => new(sessionName,
            session?.SessionStartPermitted ?? false, session?.ProviderEnablePermitted ?? false,
            consumerStarted, accessDenied, deniedStage, clean, cancelled, lost,
            session?.ProviderEventCount ?? 0, session?.SelectedEventCount ?? 0,
            session?.UnsupportedEventCount ?? 0, session?.MalformedEventCount ?? 0,
            startTicks == 0 ? null : new DateTime(startTicks, DateTimeKind.Utc),
            consumerStarted ? new DateTime(endTicks, DateTimeKind.Utc) : null, message);
    }

    private static bool IsAccessDenied(Exception ex) => ex is UnauthorizedAccessException or System.Security.SecurityException
        || ex.HResult == unchecked((int)0x80070005) || ex is Win32Exception { NativeErrorCode: 5 }
        || ex.InnerException is not null && IsAccessDenied(ex.InnerException);

    // Avoid incorporating event values or callback exception messages into persisted diagnostics.
    private static string Failure(string stage, Exception ex) =>
        $"{stage} failed ({ex.GetType().Name}, HRESULT 0x{ex.HResult:X8}).";

    /// <summary>Only scalar metadata and explicitly typed endpoint blobs can leave the consumer callback.</summary>
    public static object? NormalizeSafeValue(PayloadField field, object? value)
    {
        if (value is null) return null;
        if (field.Type == "win:Binary")
        {
            if (field.OutputType == "win:IPv6") return EtwCapture.NormalizePayloadValue(field, value);
            if (field.OutputType == "win:SocketAddress" && value is byte[] { Length: > 0 and <= 128 } socketAddress)
                return socketAddress.ToArray();
            throw new FormatException("A selected binary field is not a bounded endpoint metadata field.");
        }
        if (field.OutputType == "win:IPv4") return EtwCapture.NormalizePayloadValue(field, value);
        if (field.Type is not ("win:UInt8" or "win:Int8" or "win:UInt16" or "win:Int16"
            or "win:UInt32" or "win:Int32" or "win:UInt64" or "win:Int64" or "win:HexInt32"
            or "win:HexInt64" or "win:Pointer" or "win:Boolean"))
            throw new FormatException("A selected payload field has a type outside the safe metadata allowlist.");
        if (value is not (byte or sbyte or ushort or short or uint or int or ulong or long or bool or IntPtr or UIntPtr))
            throw new FormatException("A selected scalar metadata field has an unexpected value type.");
        if (field.Type == "win:UInt64" && value is long signed) return unchecked((ulong)signed);
        return EtwCapture.NormalizePayloadValue(field, value);
    }

    private sealed class NativeSession(string sessionName, Guid providerGuid, ulong keywordMask,
        IReadOnlyDictionary<(int EventId, int Version), IReadOnlyList<PayloadField>> selectedSafeFields,
        Action<CandidateRawEvent> onEvent) : ICandidateSession
    {
        private TraceEventSession? trace;
        private ETWTraceEventSource? source;
        private long total, selected, unsupported, malformed;
        private int finalLost;
        public bool SessionStartPermitted { get; private set; }
        public bool ProviderEnablePermitted { get; private set; }
        public string Stage { get; private set; } = "SessionStart";
        public int EventsLost => finalLost;
        public long ProviderEventCount => Interlocked.Read(ref total);
        public long SelectedEventCount => Interlocked.Read(ref selected);
        public long UnsupportedEventCount => Interlocked.Read(ref unsupported);
        public long MalformedEventCount => Interlocked.Read(ref malformed);

        public void Start()
        {
            trace = new TraceEventSession(sessionName,
                TraceEventSessionOptions.Create | TraceEventSessionOptions.NoRestartOnCreate)
            {
                StopOnDispose = true,
                BufferSizeMB = 16,
                EnableProviderTimeoutMSec = 5000
            };
            try
            {
                // Source calls EnsureStarted (StartTrace) before OpenTrace. EnableProvider has
                // not run yet, so a denied session cannot be mistaken for provider-specific denial.
                source = trace.Source;
                SessionStartPermitted = true;
            }
            catch
            {
                SessionStartPermitted = trace.IsActive;
                if (SessionStartPermitted) Stage = "ConsumerAttach";
                throw;
            }
            Stage = "ProviderEnable";
            trace.EnableProvider(providerGuid, TraceEventLevel.Verbose, keywordMask);
            ProviderEnablePermitted = true;
            source.Dynamic.All += Observe;
            source.AllEvents += value =>
            {
                if (value.ProviderGuid == providerGuid) Interlocked.Increment(ref total);
            };
            source.UnhandledEvents += value =>
            {
                if (value.ProviderGuid == providerGuid) Interlocked.Increment(ref malformed);
            };
            Stage = "Consumption";
        }

        private void Observe(TraceEvent value)
        {
            if (value.ProviderGuid != providerGuid) return;
            if (!selectedSafeFields.TryGetValue(((int)value.ID, value.Version), out var fields))
            {
                Interlocked.Increment(ref unsupported);
                return;
            }
            var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
            try
            {
                // Empty installed templates still count as observed provider activity. Fields
                // such as URLs, DNS strings, arbitrary blobs and packet contents are never read.
                foreach (var field in fields)
                {
                    int index = Array.IndexOf(value.PayloadNames, field.Name);
                    if (index < 0) throw new FormatException("The observed event disagrees with its installed metadata.");
                    payload.Add(field.Name, NormalizeSafeValue(field, value.PayloadValue(index)));
                }
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or IndexOutOfRangeException
                or ArgumentException or OverflowException)
            {
                Interlocked.Increment(ref malformed);
                return;
            }
            Interlocked.Increment(ref selected);
            onEvent(new((int)value.ID, value.Version, value.ProcessID, value.TimeStamp.ToUniversalTime(), payload));
        }

        public void Process() => source?.Process();
        public void Stop()
        {
            Stage = "Shutdown";
            if (trace?.IsActive != true) return;
            try
            {
                if (ProviderEnablePermitted) trace.DisableProvider(providerGuid);
                trace.Flush();
                finalLost = trace.EventsLost;
            }
            finally
            {
                if (!trace.Stop()) throw new InvalidOperationException("The owned candidate session did not stop.");
            }
        }
        public void Dispose() => trace?.Dispose();
    }
}
