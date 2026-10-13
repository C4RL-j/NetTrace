using System.Windows.Threading;
using AppUsageIpc;
using NetTrace.Services;

namespace NetTrace.Presentation;

// The existing view timer calls Tick. This owns no timer, sorting loop or database.
public sealed class AppUsageTodayController : IAsyncDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(4);
    private readonly Dispatcher _dispatcher;
    private readonly IAppUsageTodayClient _client;
    private readonly Action<AppUsageTodayPresentation> _apply;
    private readonly TimeProvider _clock;
    private CancellationTokenSource? _requestCancellation;
    private Task? _inFlight;
    private DateTimeOffset _nextRefresh;
    private long _generation;
    private bool _active;
    private bool _refreshing;
    private bool _refreshOnCompletion;
    private volatile bool _disposed;

    public AppUsageTodayController(Dispatcher dispatcher, IAppUsageTodayClient client,
        Action<AppUsageTodayPresentation> apply, TimeProvider? timeProvider = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _clock = timeProvider ?? TimeProvider.System;
    }

    public AppUsageTodayPresentation Current { get; private set; } = AppUsageTodayPresentation.Loading;
    public bool IsRefreshing => _refreshing;

    public void SetActive(bool active)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _active == active) return;
        _active = active;
        _generation++;
        if (!active)
        {
            _refreshOnCompletion = false;
            _requestCancellation?.Cancel();
            return;
        }
        Publish(AppUsageTodayPresentation.Loading);
        StartRefresh(force: true);
    }

    public void Tick()
    {
        _dispatcher.VerifyAccess();
        StartRefresh(force: false);
    }

    private void StartRefresh(bool force)
    {
        if (_disposed || !_active) return;
        if (_refreshing)
        {
            if (force) _refreshOnCompletion = true;
            return;
        }
        var now = _clock.GetUtcNow();
        if (!force && now < _nextRefresh) return;
        _nextRefresh = now + RefreshInterval;
        _refreshing = true;
        _refreshOnCompletion = false;
        var cancellation = _requestCancellation = new CancellationTokenSource();
        _inFlight = RefreshAsync(_generation, cancellation);
    }

    private async Task RefreshAsync(long generation, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        IpcResult<AppUsageQueryResult> result;
        try
        {
            // Even connection identity verification and a synchronously completing
            // client run away from the dispatcher. Controls change only below.
            result = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                return _client.GetTodayAsync(token);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { result = new(IpcState.Cancelled, null); }
        catch (Exception) { result = new(IpcState.CollectorUnavailable, null); }
        try
        {
            if (_disposed || _dispatcher.HasShutdownStarted) return;
            await _dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_requestCancellation, cancellation)) _requestCancellation = null;
                _refreshing = false;
                if (!_disposed && _active && generation == _generation && !token.IsCancellationRequested)
                    Publish(AppUsageTodayPresentation.FromResult(result));
                if (!_disposed && _active && _refreshOnCompletion) StartRefresh(force: true);
            }, DispatcherPriority.Background).Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally { cancellation.Dispose(); }
    }

    private void Publish(AppUsageTodayPresentation presentation)
    {
        Current = presentation;
        _apply(presentation);
    }

    public async ValueTask DisposeAsync()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _active = false;
        _generation++;
        _refreshOnCompletion = false;
        _requestCancellation?.Cancel();
        _refreshing = false;
        if (_inFlight is not null) await _inFlight.ConfigureAwait(false);
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
