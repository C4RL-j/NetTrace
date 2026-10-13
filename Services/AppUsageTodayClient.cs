using AppUsageIpc;

namespace NetTrace.Services;

public sealed class AppUsageTodayClient : IAppUsageTodayClient
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly string _serviceName;
    private IpcClient? _client;
    private int _disposed;

    public AppUsageTodayClient(string serviceName = ServicePipeSecurity.ProductionServiceName)
    {
        ServicePipeSecurity.ValidateServiceName(serviceName);
        _serviceName = serviceName;
    }

    public async Task<IpcResult<AppUsageQueryResult>> GetAppUsageRangeAsync(AppUsageRange range, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(range)) throw new ArgumentOutOfRangeException(nameof(range));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        var entered = false;
        try
        {
            await _gate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            entered = true;
            if (Volatile.Read(ref _disposed) != 0) return Unavailable();
            if (_client is null)
            {
                _client = new(ServicePipeSecurity.DefaultPipeName, serviceName: _serviceName);
                var connection = await _client.ConnectAsync(cancellation.Token).ConfigureAwait(false);
                if (connection.State != IpcState.Connected)
                {
                    await CloseClientAsync().ConfigureAwait(false);
                    return new(connection.State, null);
                }
            }
            var result = await _client.GetAppUsageRangeAsync(range, cancellation.Token).ConfigureAwait(false);
            // The pipe's existing idle deadline is shorter than this page's
            // four-second cadence. Each visible refresh gets a fresh handshake.
            await CloseClientAsync().ConfigureAwait(false);
            // Only the typed state reaches presentation; raw diagnostics are unnecessary.
            return result with { Detail = null };
        }
        catch (OperationCanceledException) { return new(IpcState.Cancelled, null); }
        catch (Exception)
        {
            if (entered) await CloseClientAsync().ConfigureAwait(false);
            return Unavailable();
        }
        finally { if (entered) _gate.Release(); }
    }

    private static IpcResult<AppUsageQueryResult> Unavailable() => new(IpcState.CollectorUnavailable, null);

    private async ValueTask CloseClientAsync()
    {
        if (_client is null) return;
        await _client.DisposeAsync().ConfigureAwait(false);
        _client = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await CloseClientAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}
