using AppUsageIpc;

namespace NetTrace.Services;

// The view requests Today through IPC; it never opens the service database.
public interface IAppUsageTodayClient : IAsyncDisposable
{
    Task<IpcResult<AppUsageQueryResult>> GetTodayAsync(CancellationToken cancellationToken);
}
