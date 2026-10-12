namespace NetTrace.AppUsageService;

public interface IServiceWorker : IAsyncDisposable
{
    Task Ready { get; }
    Task RunAsync(CancellationToken cancellationToken);
}

// SCM and deterministic tests share exactly this lifecycle. Every start/resume
// creates a new worker and lifetime. Faults cause process termination in the host.
public sealed class ServiceLifecycle : IAsyncDisposable
{
    private readonly Func<IServiceWorker> workerFactory;
    private readonly Action<Exception> fatal;
    private readonly TimeSpan startTimeout, stopTimeout;
    private readonly SemaphoreSlim gate = new(1, 1);
    private CancellationTokenSource? cancellation;
    private IServiceWorker? worker;
    private Task? completion;
    private bool suspended;
    public bool Running { get; private set; }
    public Task Completion => completion ?? Task.CompletedTask;

    public ServiceLifecycle(Func<IServiceWorker> workerFactory, Action<Exception> fatal,
        TimeSpan? startTimeout = null, TimeSpan? stopTimeout = null)
    {
        this.workerFactory = workerFactory ?? throw new ArgumentNullException(nameof(workerFactory));
        this.fatal = fatal ?? throw new ArgumentNullException(nameof(fatal));
        // Cold native pipe creation can take longer on a busy machine. Readiness
        // remains bounded and still requires both ETW and IPC to be usable.
        this.startTimeout = startTimeout ?? TimeSpan.FromSeconds(35);
        this.stopTimeout = stopTimeout ?? TimeSpan.FromSeconds(10);
        if (this.startTimeout <= TimeSpan.Zero || this.stopTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(startTimeout));
    }

    public async Task StartAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (worker is not null) throw new InvalidOperationException("The collector worker is already started.");
            worker = workerFactory(); cancellation = new();
            var ownedWorker = worker; var ownedCancellation = cancellation;
            var running = Task.Run(() => ownedWorker.RunAsync(ownedCancellation.Token));
            try
            {
                var winner = await Task.WhenAny(ownedWorker.Ready, running)
                    .WaitAsync(startTimeout).ConfigureAwait(false);
                if (winner == running)
                {
                    await running.ConfigureAwait(false);
                    throw new InvalidOperationException("The collector stopped before becoming ready.");
                }
                await ownedWorker.Ready.WaitAsync(startTimeout).ConfigureAwait(false);
                Running = true;
                completion = MonitorAsync(running, ownedCancellation);
            }
            catch
            {
                ownedCancellation.Cancel();
                try { await running.WaitAsync(stopTimeout).ConfigureAwait(false); } catch { }
                if (running.IsCompleted)
                    try { await ownedWorker.DisposeAsync().AsTask().WaitAsync(stopTimeout).ConfigureAwait(false); } catch { }
                ownedCancellation.Dispose(); worker = null; cancellation = null; Running = false;
                throw;
            }
        }
        finally { gate.Release(); }
    }

    private async Task MonitorAsync(Task running, CancellationTokenSource ownedCancellation)
    {
        try
        {
            await running.ConfigureAwait(false);
            if (!ownedCancellation.IsCancellationRequested)
                throw new InvalidOperationException("The collector worker stopped unexpectedly.");
        }
        catch (Exception exception)
        {
            if (!ownedCancellation.IsCancellationRequested)
            {
                Running = false;
                fatal(exception);
            }
            else if (exception is not OperationCanceledException) throw;
        }
    }

    public async Task StopAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (worker is null) { Running = false; return; }
            Running = false; cancellation!.Cancel();
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            Exception? failure = null;
            try
            {
                if (completion is not null)
                {
                    try { await completion.WaitAsync(TimeSpan.FromTicks(stopTimeout.Ticks * 4 / 5)).ConfigureAwait(false); }
                    catch (Exception exception) { failure = exception; }
                    if (!completion.IsCompleted)
                    {
                        // The host's fatal policy terminates a stubborn capture.
                        // Disposing its resolver concurrently would race callbacks
                        // and could falsely report a successful resource cleanup.
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure
                            ?? new TimeoutException("Service capture did not stop before its deadline.")).Throw();
                    }
                }
                var remaining = stopTimeout - deadline.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("Service stop exceeded its deadline.");
                await worker.DisposeAsync().AsTask().WaitAsync(remaining).ConfigureAwait(false);
                if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
            finally
            {
                cancellation.Dispose(); cancellation = null; worker = null; completion = null;
            }
        }
        finally { gate.Release(); }
    }

    public async Task SuspendAsync() { suspended |= Running; await StopAsync().ConfigureAwait(false); }
    public async Task ResumeAsync()
    {
        if (!suspended) return;
        suspended = false;
        await StartAsync().ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
