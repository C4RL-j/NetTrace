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
        // The capture and SQLite deadlines fit inside one finite SCM stop budget.
        lifecycle = new(() => new CollectorServiceWorker(options, identityName, log), Fatal,
            stopTimeout: TimeSpan.FromSeconds(20));
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
    private UsagePersistence? persistence;
    private SqliteUsageCheckpointStore? persistenceStore;
    private Task? checkpointTask;
    private bool finalCheckpointComplete;
    private bool captureQuiesced;
    public Task Ready => ready.Task;
    public CollectorServiceWorker(ServiceOptions options, string identityName, BoundedLifecycleLog log)
    { this.options = options; this.identityName = identityName; this.log = log; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var checkpointStopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<EtwCaptureResult>? capture = null;
        Task? serving = null;
        string? error = null;
        Exception? cleanupFailure = null;
        try
        {
            var schema = ProviderSchema.Discover();
            if (!schema.ProviderAvailable || schema.Events.Count == 0 || schema.KeywordMask == 0)
                throw new InvalidOperationException(schema.Error ?? "The installed Kernel-Network provider has no supported transfer schema.");
            persistenceStore = new(options.CommonDataRoot);
            persistence = new(persistenceStore, diagnostic: log.Write);
            // Retention runs once at startup. Failure leaves the service accepting pending deltas.
            await persistence.CheckpointAsync(stopping.Token).ConfigureAwait(false);
            runtime = new(schema, privileged: true, identityName: identityName,
                persistence: new UsageEventPersistence(persistence));
            captureTask = capture = runtime.RunAsync(stopping.Token);
            await runtime.Ready.WaitAsync(TimeSpan.FromSeconds(25), stopping.Token).ConfigureAwait(false);
            log.Write($"ETW ready; session={SessionOwnership.SessionName}; recovered stale session={runtime.ReclaimedStaleSession}.");
            var appUsageQueries = AppUsageQueryService.ForDatabase(options.CommonDataRoot, () => persistence.Health);
            server = new(options.PipeName, runtime.GetCapabilities, () => runtime.GetSnapshot(),
                serviceSecurity: new(options.ClientSid), protocolVersion: Protocol.ServiceVersion,
                appUsageQuery: appUsageQueries.QueryAsync);
            serving = server.RunAsync(stopping.Token);
            log.Write($"Pipe ready; local-only authenticated service protocol={Protocol.ServiceVersion}.");
            checkpointTask = persistence.RunAsync(checkpointStopping.Token);
            ready.TrySetResult();
            var completed = await Task.WhenAny(capture, serving, checkpointTask).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested)
            {
                if (completed == capture)
                {
                    result = await capture.ConfigureAwait(false);
                    throw new InvalidOperationException(result.Error ?? "ETW capture ended unexpectedly.");
                }
                await completed.ConfigureAwait(false);
                throw new InvalidOperationException(completed == serving
                    ? "IPC listeners ended unexpectedly." : "Persistence checkpoint worker ended unexpectedly.");
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
            checkpointStopping.Cancel();
            // Begin pipe disposal alongside native capture cleanup, rather than adding their deadlines.
            var pipeCleanup = server is null ? Task.CompletedTask
                : server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
            captureQuiesced = capture is null;
            if (capture is not null)
            {
                try
                {
                    result = await AwaitCaptureCleanupAsync(capture, TimeSpan.FromSeconds(7)).ConfigureAwait(false);
                    captureQuiesced = true;
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                    error ??= $"Capture cleanup: {exception.GetType().Name}: {exception.Message}";
                }
            }
            try { await pipeCleanup.ConfigureAwait(false); }
            catch (Exception exception)
            {
                cleanupFailure ??= exception;
                error ??= $"Pipe cleanup: {exception.GetType().Name}: {exception.Message}";
            }
            if (persistence is not null && captureQuiesced)
            {
                // Drain accepted ETW callbacks before sealing; no event can slip in after the final flush.
                persistence.Seal();
                finalCheckpointComplete = await persistence.FlushAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
                log.Write($"Final persistence checkpoint complete={finalCheckpointComplete}; pending keys={persistence.Health.PendingKeys}.");
            }
            if (checkpointTask is not null)
            {
                try { await checkpointTask.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false); }
                catch (OperationCanceledException) when (checkpointStopping.IsCancellationRequested) { }
                catch (System.TimeoutException) { log.Write("Persistence transaction still pending at the bounded stop deadline; it is not replayed or disposed concurrently."); }
            }
            if (!ready.Task.IsCompleted) ready.TrySetCanceled();
            result ??= runtime?.FinalCaptureResult;
            WriteReport(error);
            if (result is not null)
                log.Write($"ETW stop: clean={result.ShutdownClean}; lost={result.EventsLost}; error={result.Error ?? "none"}.");
            if (cleanupFailure is not null)
                throw new InvalidOperationException("Collector cleanup did not complete safely; service recovery is required.", cleanupFailure);
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
            ReclaimedStaleSession = runtime?.ReclaimedStaleSession ?? false, Error = error,
            Persistence = persistence?.Health, FinalCheckpointComplete = finalCheckpointComplete,
            CheckpointIntervalSeconds = UsagePersistence.CheckpointInterval.TotalSeconds,
            DatabasePath = persistenceStore?.DatabasePath
        };
        File.WriteAllText(options.ReportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
    public async ValueTask DisposeAsync()
    {
        if (server is not null) await server.DisposeAsync().ConfigureAwait(false);
        if (captureTask is not null && !captureQuiesced)
            throw new InvalidOperationException("Cannot dispose process identity resources before clean ETW quiescence is confirmed.");
        runtime?.Dispose();
    }
}
