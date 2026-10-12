using System.ServiceProcess;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AppUsageCollectorCore;
using AppUsageEtwProbe;
using AppUsageIpc;
using Microsoft.Diagnostics.Tracing.Session;

[assembly: InternalsVisibleTo("NetTrace.AppUsageServiceTests")]

namespace NetTrace.AppUsageService;

public sealed class CollectorServiceHost : ServiceBase
{
    private readonly ServiceLifecycle lifecycle;
    private readonly BoundedLifecycleLog log;
    public CollectorServiceHost(ServiceOptions options, string identityName, BoundedLifecycleLog log)
    {
        this.log = log;
        ServiceName = ServicePipeSecurity.DefaultServiceName;
        CanStop = true; CanShutdown = true; CanHandlePowerEvent = true; AutoLog = false;
        lifecycle = new(() => new CollectorServiceWorker(options, identityName, log), Fatal);
    }
    protected override void OnStart(string[] args)
    {
        // Allow the bounded readiness window and its failure cleanup to finish
        // before SCM treats a slow cold start as an unresponsive process.
        RequestAdditionalTime(60000);
        try { lifecycle.StartAsync().GetAwaiter().GetResult(); log.Write("SCM start completed; ETW and pipe ready."); }
        catch (Exception exception)
        {
            ExitCode = 1066;
            log.Write($"SCM start failed: {exception.GetType().Name}: {exception.Message}");
            throw;
        }
    }
    protected override void OnStop() => StopBounded("SCM stop");
    protected override void OnShutdown() => StopBounded("System shutdown");
    private void StopBounded(string reason)
    {
        log.Write(reason + " requested.");
        try { lifecycle.StopAsync().GetAwaiter().GetResult(); log.Write(reason + " completed; resources disposed."); }
        catch (Exception exception) { Fatal(exception); }
    }
    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        try
        {
            switch (powerStatus)
            {
                case PowerBroadcastStatus.Suspend:
                    log.Write("Suspend: stopping capture and pipe through the bounded lifecycle.");
                    lifecycle.SuspendAsync().GetAwaiter().GetResult(); break;
                case PowerBroadcastStatus.ResumeAutomatic:
                case PowerBroadcastStatus.ResumeSuspend:
                    log.Write("Resume: starting a fresh capture lifetime when suspended.");
                    lifecycle.ResumeAsync().GetAwaiter().GetResult(); break;
            }
            return true;
        }
        catch (Exception exception) { Fatal(exception); return false; }
    }
    private void Fatal(Exception exception)
    {
        log.Write($"Fatal collector failure; SCM recovery required: {exception.GetType().Name}: {exception.Message}");
        // FailFast gives SCM a genuine abnormal process termination, never a
        // falsely healthy service with a dead worker. Recovery policy is finite.
        Environment.FailFast("The App Usage service worker stopped or could not shut down safely.", exception);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) lifecycle.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }
}

internal sealed class CollectorServiceWorker : IServiceWorker
{
    private readonly ServiceOptions options;
    private readonly string identityName;
    private readonly BoundedLifecycleLog log;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CollectorRuntime? runtime;
    private IpcServer? server;
    private EtwCaptureResult? result;
    private Task<EtwCaptureResult>? captureTask;
    public Task Ready => ready.Task;
    public CollectorServiceWorker(ServiceOptions options, string identityName, BoundedLifecycleLog log)
    { this.options = options; this.identityName = identityName; this.log = log; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<EtwCaptureResult>? capture = null;
        Task? serving = null;
        string? error = null;
        Exception? cleanupFailure = null;
        try
        {
            var schema = ProviderSchema.Discover();
            if (!schema.ProviderAvailable || schema.Events.Count == 0 || schema.KeywordMask == 0)
                throw new InvalidOperationException(schema.Error ?? "The installed Kernel-Network provider has no supported transfer schema.");
            runtime = new(schema, privileged: true, identityName: identityName);
            captureTask = capture = runtime.RunAsync(stopping.Token);
            await runtime.Ready.WaitAsync(TimeSpan.FromSeconds(25), stopping.Token).ConfigureAwait(false);
            log.Write($"ETW ready; session={SessionOwnership.SessionName}; recovered stale session={runtime.ReclaimedStaleSession}.");
            server = new(options.PipeName, runtime.GetCapabilities, () => runtime.GetSnapshot(),
                serviceSecurity: new(options.ClientSid), protocolVersion: Protocol.ServiceVersion);
            serving = server.RunAsync(stopping.Token);
            log.Write($"Pipe ready; local-only authenticated service protocol={Protocol.ServiceVersion}.");
            ready.TrySetResult();
            var completed = await Task.WhenAny(capture, serving).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested)
            {
                if (completed == capture)
                {
                    result = await capture.ConfigureAwait(false);
                    throw new InvalidOperationException(result.Error ?? "ETW capture ended unexpectedly.");
                }
                await serving.ConfigureAwait(false);
                throw new InvalidOperationException("IPC listeners ended unexpectedly.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            error = $"{exception.GetType().Name}: {exception.Message}";
            log.Write("Collector worker failed: " + error);
            ready.TrySetException(exception);
            throw;
        }
        finally
        {
            stopping.Cancel();
            if (server is not null) await server.DisposeAsync().ConfigureAwait(false);
            if (capture is not null)
            {
                try { result = await AwaitCaptureCleanupAsync(capture, TimeSpan.FromSeconds(7)).ConfigureAwait(false); }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                    error ??= $"Capture cleanup: {exception.GetType().Name}: {exception.Message}";
                }
            }
            if (!ready.Task.IsCompleted) ready.TrySetCanceled();
            result ??= runtime?.FinalCaptureResult;
            WriteReport(error);
            if (result is not null)
                log.Write($"ETW stop: clean={result.ShutdownClean}; lost={result.EventsLost}; error={result.Error ?? "none"}.");
            if (cleanupFailure is not null)
                throw new InvalidOperationException("ETW cleanup did not complete safely; service recovery is required.", cleanupFailure);
            if (result is not null && (!result.ShutdownClean || result.Error is not null))
                throw new InvalidOperationException(result.Error ?? "ETW cleanup did not complete cleanly.");
        }
    }

    internal static async Task<EtwCaptureResult> AwaitCaptureCleanupAsync(Task<EtwCaptureResult> capture, TimeSpan timeout)
    {
        var completed = await capture.WaitAsync(timeout).ConfigureAwait(false);
        if (completed is null || !completed.ShutdownClean || completed.Error is not null)
            throw new InvalidOperationException(completed?.Error ?? "ETW cleanup did not return a clean completed result.");
        return completed;
    }

    private void WriteReport(string? error)
    {
        if (options.ReportPath is null) return;
        bool? absent = null;
        try { absent = !TraceEventSession.GetActiveSessionNames().Contains(SessionOwnership.SessionName, StringComparer.Ordinal); }
        catch { }
        var report = new
        {
            ServiceIdentity = identityName, CollectorElevated = EtwCapture.IsElevated,
            Capture = result, Snapshot = runtime?.GetSnapshot(countRequest: false), OwnedSessionAbsent = absent,
            ReclaimedStaleSession = runtime?.ReclaimedStaleSession ?? false, Error = error
        };
        File.WriteAllText(options.ReportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
    public async ValueTask DisposeAsync()
    {
        if (server is not null) await server.DisposeAsync().ConfigureAwait(false);
        if (captureTask is { IsCompleted: false })
            throw new InvalidOperationException("Cannot dispose process identity resources while ETW capture is still active.");
        runtime?.Dispose();
    }
}
