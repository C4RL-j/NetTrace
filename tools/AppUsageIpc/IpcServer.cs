using System.IO.Pipes;

namespace AppUsageIpc;

public sealed class IpcServer : IAsyncDisposable
{
    private readonly List<NamedPipeServerStream> pipes = [];
    private readonly Func<CollectorCapabilities> capabilities;
    private readonly Func<CollectorSnapshot> snapshot;
    private readonly Action? shutdownForTest;
    private readonly Func<NamedPipeServerStream, bool> verifyClient;
    private readonly CancellationTokenSource stopping = new();
    private Task? running;
    private int disposed;
    private long accepted, rejectedClients, rejectedRequests;
    public long AcceptedConnections => Interlocked.Read(ref accepted);
    public long RejectedClients => Interlocked.Read(ref rejectedClients);
    public long RejectedRequests => Interlocked.Read(ref rejectedRequests);

    public IpcServer(string pipeName, Func<CollectorCapabilities> capabilities, Func<CollectorSnapshot> snapshot,
        Action? shutdownForTest = null, Func<NamedPipeServerStream, bool>? clientVerifier = null)
    {
        this.capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        this.snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        this.shutdownForTest = shutdownForTest;
        // Test-only hook is never exposed as a protocol or CLI option. Default authentication is real.
        verifyClient = clientVerifier ?? (pipe => NativePipeSecurity.VerifyClient(pipe.SafePipeHandle));
        try
        {
            for (int i = 0; i < NativePipeSecurity.MaximumClients; i++)
                pipes.Add(NativePipeSecurity.CreateServer(pipeName, firstInstance: i == 0));
        }
        catch { foreach (var pipe in pipes) pipe.Dispose(); throw; }
    }

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (running is not null) throw new InvalidOperationException("The IPC server is already running.");
        return running = RunCoreAsync(cancellationToken);
    }

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        async Task ServeGuardedAsync(NamedPipeServerStream pipe)
        {
            try { await ServeAsync(pipe, linked.Token).ConfigureAwait(false); }
            catch
            {
                // A failed listener must stop its siblings so the owning collector
                // sees the fault immediately instead of waiting for its duration.
                linked.Cancel();
                throw;
            }
        }
        await Task.WhenAll(pipes.Select(ServeGuardedAsync)).ConfigureAwait(false);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                if (!verifyClient(pipe)) { Interlocked.Increment(ref rejectedClients); continue; }
                Interlocked.Increment(ref accepted);
                bool handshaken = false;
                while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
                {
                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    requestTimeout.CancelAfter(Protocol.RequestTimeout);
                    var request = await IpcProtocol.ReadRequestAsync(pipe, requestTimeout.Token).ConfigureAwait(false);
                    var response = IpcProtocol.Dispatch(request, handshaken, capabilities, snapshot, shutdownForTest is not null);
                    await IpcProtocol.WriteResponseAsync(pipe, response, requestTimeout.Token).ConfigureAwait(false);
                    if (response.Status != "Ok")
                    {
                        Interlocked.Increment(ref rejectedRequests);
                        // DisconnectNamedPipe discards unread response bytes.
                        // Give the client a bounded opportunity to consume the
                        // rejection and close, using cancellable overlapped IO.
                        await AwaitClientEndAsync(pipe, requestTimeout.Token, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    if (request.Operation == "Hello") handshaken = true;
                    if (request.Operation == "ShutdownForTest")
                    {
                        // The client closes after consuming this acknowledgement.
                        // Async EOF is bounded and cancellable, unlike synchronous
                        // WaitForPipeDrain / FlushFileBuffers on a blocked client.
                        int extra = await AwaitClientEndAsync(pipe, requestTimeout.Token, cancellationToken).ConfigureAwait(false);
                        if (extra != 0) Interlocked.Increment(ref rejectedRequests);
                        shutdownForTest!();
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                if (!cancellationToken.IsCancellationRequested) Interlocked.Increment(ref rejectedRequests);
            }
            finally
            {
                try { if (pipe.IsConnected) pipe.Disconnect(); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
            }
        }
        // Keep the same instances throughout RunAsync; the first guarded handle
        // stays alive, so disconnected clients never create a pipe-name ownership gap.
    }

    private static async Task<int> AwaitClientEndAsync(NamedPipeServerStream pipe,
        CancellationToken timeout, CancellationToken stopping)
    {
        try { return await pipe.ReadAsync(new byte[1], timeout).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested) { return 0; }
        catch (IOException) { return 0; }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stopping.Cancel();
        if (running is not null)
        {
            try { await running.WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or ObjectDisposedException) { }
        }
        foreach (var pipe in pipes) pipe.Dispose();
        stopping.Dispose();
    }
}
