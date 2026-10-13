using AppUsageIpc;

namespace NetTrace.Services;

// The view requests application ranges through IPC; it never opens the service database.
public interface IAppUsageTodayClient : IAsyncDisposable
{
    Task<IpcResult<AppUsageQueryResult>> GetAppUsageRangeAsync(AppUsageRange range, CancellationToken cancellationToken);
}
