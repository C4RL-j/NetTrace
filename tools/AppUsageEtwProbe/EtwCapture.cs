using System.ComponentModel;
using System.Net;
using System.Security.Principal;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace AppUsageEtwProbe;

public sealed record EtwCaptureResult(string SessionName, bool SessionStarted, bool AccessDenied,
    bool ShutdownClean, bool Cancelled, int EventsLost, long ProviderEventCount,
    long UnsupportedEventCount, long MalformedEventCount, DateTime? WindowStartUtc,
    DateTime? WindowEndUtc, string? Error)
{
    public int? NativeErrorCode { get; init; }
    public string? FailureStage { get; init; }
}

// Live diagnostics are separate from the completed-window result. A real-time
// source's startup loss snapshot is not an authoritative live controller count.
public sealed record EtwCaptureLiveStatus(string? SessionName, bool SessionStarted, bool IsCapturing,
    bool LostEventCountAvailable, int? EventsLost, long ProviderEventCount,
    long UnsupportedEventCount, long MalformedEventCount);

public interface IEtwCaptureStatusSession
{
    bool TryGetEventsLost(out int count);
}

// The same ownership and shutdown path is exercised by deterministic tests without requiring ETW rights.
public interface IEtwCaptureSession : IDisposable
{
    void Start();
    void Process();
    void Stop();
    int EventsLost { get; }
    long ProviderEventCount { get; }
    long UnsupportedEventCount { get; }
    long MalformedEventCount { get; }
}

/// <summary>Owns one bounded, real-time, metadata-only session; never attaches to other sessions.</summary>
public sealed class EtwCapture(ProviderSchema schema,
    Func<string, ProviderSchema, Action<RawNetworkEvent>, IEtwCaptureSession>? sessionFactory = null,
    string sessionNamePrefix = "NetTrace-AppUsage000", string? exactSessionName = null,
    Guid? exactSessionGuid = null)
{
    private readonly object _statusGate = new();
    private IEtwCaptureSession? _liveSession;
    private EtwCaptureLiveStatus _liveStatus = new(null, false, false, false, null, 0, 0, 0);

    public EtwCaptureLiveStatus GetLiveStatus()
    {
        lock (_statusGate)
            return _liveSession is null ? _liveStatus : ReadStatus(_liveSession, _liveStatus.SessionName,
                _liveStatus.SessionStarted, _liveStatus.IsCapturing);
    }

    private static EtwCaptureLiveStatus ReadStatus(IEtwCaptureSession session, string? name,
        bool started, bool capturing)
    {
        int? lost = null;
        try
        {
            if (session is IEtwCaptureStatusSession source && source.TryGetEventsLost(out var count) && count >= 0)
                lost = count;
        }
        catch { }
        return new(name, started, capturing, lost is not null, lost,
            session.ProviderEventCount, session.UnsupportedEventCount, session.MalformedEventCount);
    }

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public Task<EtwCaptureResult> CaptureAsync(TimeSpan duration, CancellationToken cancellationToken,
        Action<RawNetworkEvent> onEvent, Func<CancellationToken, Task>? onStarted = null,
        Action? onWindowEnding = null, Func<DateTime?>? onWindowOpening = null,
        Func<DateTime?>? onWindowClosing = null)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(duration), "The diagnostic window must be between zero and five minutes.");
        return CaptureCoreAsync(duration, cancellationToken, onEvent, onStarted, onWindowEnding, onWindowOpening, onWindowClosing);
    }

    public Task<EtwCaptureResult> CaptureContinuousAsync(CancellationToken cancellationToken,
        Action<RawNetworkEvent> onEvent, Func<CancellationToken, Task>? onStarted = null) =>
        CaptureCoreAsync(Timeout.InfiniteTimeSpan, cancellationToken, onEvent, onStarted, null, null, null);

    private async Task<EtwCaptureResult> CaptureCoreAsync(TimeSpan duration, CancellationToken cancellationToken,
        Action<RawNetworkEvent> onEvent, Func<CancellationToken, Task>? onStarted,
        Action? onWindowEnding, Func<DateTime?>? onWindowOpening, Func<DateTime?>? onWindowClosing)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        if (string.IsNullOrWhiteSpace(sessionNamePrefix) || sessionNamePrefix.Length > 96
            || sessionNamePrefix.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("The owned session name prefix must contain only letters, digits, hyphens or underscores.");
        if (exactSessionName is not null && (string.IsNullOrWhiteSpace(exactSessionName)
            || exactSessionName.Length > 120 || exactSessionGuid is null || exactSessionGuid == Guid.Empty))
            throw new ArgumentException("Exact session ownership requires a bounded name and project GUID.");
        string sessionName = exactSessionName ?? $"{sessionNamePrefix}-{Environment.ProcessId}-{Guid.NewGuid():N}";
        lock (_statusGate) _liveStatus = new(sessionName, false, false, false, null, 0, 0, 0);
        bool started = false, denied = false, clean = true, cancelled = cancellationToken.IsCancellationRequested;
        int eventsLost = 0;
        string? error = null;
        string stage = "SchemaValidation";
        string? failureStage = null;
        int? nativeErrorCode = null;
        long startTicks = 0, endTicks = DateTime.MaxValue.Ticks;
        var windowGate = new object();
        IEtwCaptureSession? session = null;
        Task? consumer = null;
        Task? startedWork = null;
        using var workCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (!schema.ProviderAvailable || schema.Events.Count == 0 || schema.KeywordMask == 0)
            return Result(schema.Error ?? "No validated provider/schema is available.");
        if (cancelled) return Result(null);

        try
        {
            session = sessionFactory is null ? new NativeSession(sessionName, schema, Accept, exactSessionGuid)
                : sessionFactory(sessionName, schema, Accept);
            // Do not assume that a non-admin token fails: ETW permissions can be delegated on Windows.
            stage = "SessionStart";
            session.Start();
            started = true;
            lock (_statusGate)
            {
                _liveSession = session;
                _liveStatus = new(sessionName, true, true, false, null, 0, 0, 0);
            }
            consumer = Task.Run(session.Process, CancellationToken.None);
            lock (windowGate)
            {
                // The counter snapshot supplies its actual read timestamp. Holding the gate
                // keeps concurrent callbacks from crossing a boundary before it is known.
                DateTime start = onWindowOpening?.Invoke() ?? DateTime.UtcNow;
                Interlocked.Exchange(ref startTicks, start.ToUniversalTime().Ticks);
            }
            var window = Task.Delay(duration, cancellationToken);
            stage = "ReadyCallback";
            startedWork = onStarted?.Invoke(workCancellation.Token) ?? Task.CompletedTask;
            stage = "CaptureProcessing";
            var first = await Task.WhenAny(window, consumer, startedWork).ConfigureAwait(false);
            if (first == startedWork)
            {
                await startedWork.ConfigureAwait(false);
                first = await Task.WhenAny(window, consumer).ConfigureAwait(false);
            }
            if (first == consumer)
            {
                await consumer.ConfigureAwait(false);
                error = "The ETW consumer ended before the observation window completed.";
            }
            else
            {
                await window.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            failureStage = stage;
            nativeErrorCode = NativeError(ex);
            denied = IsAccessDenied(ex);
            error = denied ? $"ETW access denied during {stage}. Administrator rights or delegated ETW permissions are required on this system. Native error: {nativeErrorCode?.ToString() ?? "unavailable"}; {ex.Message}"
                : $"ETW capture failed: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            if (started)
            {
                lock (windowGate)
                {
                    try
                    {
                        // ETW may deliver buffered events after the physical counter read.
                        // Every forwarded event must fall inside these exact counter timestamps.
                        DateTime end = onWindowClosing?.Invoke() ?? DateTime.UtcNow;
                        Interlocked.Exchange(ref endTicks, end.ToUniversalTime().Ticks);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Exchange(ref endTicks, DateTime.UtcNow.Ticks);
                        AddError($"End-of-window snapshot failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                try { onWindowEnding?.Invoke(); }
                catch (Exception ex) { AddError($"End-of-window callback failed: {ex.GetType().Name}: {ex.Message}"); }
            }
            workCancellation.Cancel();
            if (session is not null)
            {
                // Stop flushes the owned real-time session. Process drains those buffered events;
                // timestamp filtering keeps the adapter comparison inside the same window.
                try { session.Stop(); }
                catch (Exception ex) { clean = false; AddError($"ETW stop failed: {ex.GetType().Name}: {ex.Message}"); }
                if (consumer is not null)
                {
                    try { await consumer.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                    catch (Exception ex) { clean = false; AddError($"ETW consumer shutdown failed: {ex.GetType().Name}: {ex.Message}"); }
                }
                if (started)
                {
                    // NativeSession preserves its final loss snapshot when disabling/flushing the provider.
                    try { eventsLost = session.EventsLost; }
                    catch (Exception ex) { AddError($"ETW loss-counter query failed: {ex.GetType().Name}: {ex.Message}"); }
                }
                try { session.Dispose(); }
                catch (Exception ex) { clean = false; AddError($"ETW dispose failed: {ex.GetType().Name}: {ex.Message}"); }
                if (consumer is { IsCompleted: false })
                {
                    // Disposing the native session also closes its real-time source.
                    // If the normal flush/drain timed out, join that forced stop before
                    // allowing callers to release state referenced by event callbacks.
                    try { await consumer.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                    catch (Exception ex) { clean = false; AddError($"ETW consumer did not exit after disposal: {ex.GetType().Name}: {ex.Message}"); }
                }
                lock (_statusGate)
                {
                    _liveStatus = ReadStatus(session, sessionName, started, false);
                    _liveSession = null;
                }
            }
            if (startedWork is not null)
            {
                try { await startedWork.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (OperationCanceledException) when (workCancellation.IsCancellationRequested) { }
                catch (Exception ex) { AddError($"Controlled workload failed: {ex.GetType().Name}: {ex.Message}"); }
            }
        }
        return Result(error);

        void Accept(RawNetworkEvent value)
        {
            lock (windowGate)
            {
                long start = Interlocked.Read(ref startTicks), end = Interlocked.Read(ref endTicks);
                if (start != 0 && value.TimestampUtc.Ticks >= start && value.TimestampUtc.Ticks <= end) onEvent(value);
            }
        }
        void AddError(string message) => error = error is null ? message : error + " " + message;
        EtwCaptureResult Result(string? message) => new(sessionName, started, denied, clean, cancelled, eventsLost,
            session?.ProviderEventCount ?? 0, session?.UnsupportedEventCount ?? 0, session?.MalformedEventCount ?? 0,
            startTicks == 0 ? null : new DateTime(startTicks, DateTimeKind.Utc),
            !started ? null : new DateTime(endTicks, DateTimeKind.Utc), message)
            { NativeErrorCode = nativeErrorCode, FailureStage = failureStage };
    }

    private static bool IsAccessDenied(Exception ex) => ex is UnauthorizedAccessException or System.Security.SecurityException
        || ex.HResult == unchecked((int)0x80070005) || ex is Win32Exception { NativeErrorCode: 5 }
        || ex.InnerException is not null && IsAccessDenied(ex.InnerException);
    private static int? NativeError(Exception exception) => exception is Win32Exception native
        ? native.NativeErrorCode : exception.HResult == unchecked((int)0x80070005) ? 5
        : exception.InnerException is not null ? NativeError(exception.InnerException) : null;

    /// <summary>Apply installed field types to TDH values; addresses remain in memory only.</summary>
    public static object? NormalizePayloadValue(PayloadField field, object? value)
    {
        if (field.OutputType == "win:IPv4")
        {
            if (value is IPAddress address && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                return address;
            uint number = value switch { uint u => u, int i => unchecked((uint)i), _ => throw new FormatException("Invalid IPv4 field type.") };
            return new IPAddress(BitConverter.GetBytes(number));
        }
        if (field.OutputType == "win:IPv6")
            return value is byte[] { Length: 16 } bytes ? new IPAddress(bytes)
                : value is IPAddress address && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                    ? address : throw new FormatException("Invalid IPv6 field type/length.");
        // TDH's dynamic parser can represent an unsigned manifest integer as a boxed signed integer.
        return field.Type switch
        {
            "win:UInt32" when value is int signed => unchecked((uint)signed),
            "win:UInt16" when value is short signed => unchecked((ushort)signed),
            "win:UInt16" when value is int signed => unchecked((ushort)signed),
            _ => value
        };
    }

    private sealed class NativeSession(string sessionName, ProviderSchema providerSchema,
        Action<RawNetworkEvent> onEvent, Guid? sessionGuid) : IEtwCaptureSession, IEtwCaptureStatusSession
    {
        private TraceEventSession? trace;
        private NativeEtwController? controller;
        private ETWTraceEventSource? source;
        private readonly Dictionary<(int, int), NetworkEventSchema> schemas = providerSchema.Events
            .ToDictionary(e => (e.EventId, e.Version));
        private long total, unsupported, malformed;
        private int finalEventsLost;
        private bool finalLossAvailable;
        private readonly object controllerGate = new();
        private readonly Dictionary<(int, int), (PayloadField Field, int Index)[]> payloadPlans = new();
        public int EventsLost => finalEventsLost;
        public long ProviderEventCount => Interlocked.Read(ref total);
        public long UnsupportedEventCount => Interlocked.Read(ref unsupported);
        public long MalformedEventCount => Interlocked.Read(ref malformed);

        public bool TryGetEventsLost(out int count)
        {
            lock (controllerGate)
            {
                count = finalEventsLost;
                if (finalLossAvailable) return true;
                if (controller is null && trace?.IsActive != true) return false;
                try { count = controller?.EventsLost ?? trace!.EventsLost; return count >= 0; }
                catch { return false; }
            }
        }

        public void Start()
        {
            if (sessionGuid is Guid guid)
            {
                controller = new NativeEtwController(sessionName, guid);
                controller.Start(providerSchema.KeywordMask);
                source = new ETWTraceEventSource(sessionName, TraceEventSourceType.Session);
            }
            else
            {
                trace = new TraceEventSession(sessionName, TraceEventSessionOptions.Create | TraceEventSessionOptions.NoRestartOnCreate)
                {
                    StopOnDispose = true,
                    BufferSizeMB = 16,
                    EnableProviderTimeoutMSec = 5000
                };
                trace.EnableProvider(ProviderSchema.ProviderGuid, TraceEventLevel.Verbose, providerSchema.KeywordMask);
                source = trace.Source;
            }
            // Dynamic includes registered TDH manifests and avoids TraceEvent's fixed kernel payload parser.
            source.Dynamic.All += Observe;
            source.AllEvents += value =>
            {
                if (value.ProviderGuid == ProviderSchema.ProviderGuid) Interlocked.Increment(ref total);
            };
            source.UnhandledEvents += value =>
            {
                if (value.ProviderGuid == ProviderSchema.ProviderGuid) Interlocked.Increment(ref malformed);
            };
        }

        private void Observe(TraceEvent value)
        {
            if (value.ProviderGuid != ProviderSchema.ProviderGuid) return;
            if (!schemas.TryGetValue(((int)value.ID, value.Version), out var schema))
            {
                Interlocked.Increment(ref unsupported);
                return;
            }
            Dictionary<string, object?> payload;
            try
            {
                if (schema.MinimumPayloadBytes is int minimum && value.EventDataLength < minimum)
                    throw new FormatException("Observed payload is shorter than its installed field template.");
                if (!payloadPlans.TryGetValue((schema.EventId, schema.Version), out var plan))
                {
                    var names = value.PayloadNames;
                    var required = new HashSet<string>(StringComparer.Ordinal)
                    { schema.PidField, schema.SizeField, schema.SourceAddressField, schema.DestinationAddressField };
                    if (schema.InterfaceIndexField is not null) required.Add(schema.InterfaceIndexField);
                    plan = schema.Fields.Where(field => required.Contains(field.Name)).Select(field =>
                    {
                        int index = Array.IndexOf(names, field.Name);
                        if (index < 0) throw new FormatException("Observed payload fields disagree with the installed schema.");
                        return (field, index);
                    }).ToArray();
                    if (plan.Length != required.Count) throw new FormatException("Required accounting metadata is missing.");
                    payloadPlans[(schema.EventId, schema.Version)] = plan;
                }
                payload = new Dictionary<string, object?>(plan.Length, StringComparer.Ordinal);
                foreach (var (field, index) in plan)
                    payload.Add(field.Name, NormalizePayloadValue(field, value.PayloadValue(index)));
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or IndexOutOfRangeException
                or ArgumentException or OverflowException)
            {
                Interlocked.Increment(ref malformed);
                return;
            }
            // Payload PID remains authoritative. The event header's PID can be a kernel execution context.
            onEvent(new((int)value.ID, value.Version, value.ProcessID, value.TimeStamp.ToUniversalTime(), payload));
        }

        public void Process() => source?.Process();
        public void Stop()
        {
            lock (controllerGate)
            {
                if (controller is not null)
                {
                    try
                    {
                        controller.DisableAndFlush();
                        finalEventsLost = controller.EventsLost;
                        finalLossAvailable = finalEventsLost >= 0;
                    }
                    finally { controller.Stop(); }
                    return;
                }
                if (trace?.IsActive != true) return;
                try
                {
                    // Freeze input before querying the controller's authoritative loss counter. The
                    // source's EventsLost property is only its startup snapshot for real-time sessions.
                    trace.DisableProvider(ProviderSchema.ProviderGuid);
                    trace.Flush();
                    finalEventsLost = trace.EventsLost;
                    finalLossAvailable = finalEventsLost >= 0;
                }
                finally
                {
                    if (!trace.Stop()) throw new InvalidOperationException("Owned session did not stop.");
                }
            }
        }
        public void Dispose()
        {
            lock (controllerGate)
            {
                try { controller?.Dispose(); }
                finally
                {
                    try { trace?.Dispose(); }
                    finally
                    {
                        // A refused/failed controller stop must not prevent
                        // closing the owned consumer handle and unblocking Process.
                        if (controller is not null) source?.Dispose();
                    }
                }
            }
        }
    }
}
