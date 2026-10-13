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
    private readonly AppUsageRangeSelection _selection;
    private readonly IAppTrackingSetupService? _setupService;
    private CancellationTokenSource? _requestCancellation;
    private Task? _inFlight;
    private DateTimeOffset _nextRefresh;
    private long _generation;
    private bool _active;
    private bool _refreshing;
    private bool _refreshOnCompletion;
    private bool _installing;
    private string _installError = "";
    private volatile bool _disposed;

    public AppUsageTodayController(Dispatcher dispatcher, IAppUsageTodayClient client,
        Action<AppUsageTodayPresentation> apply, TimeProvider? timeProvider = null,
        AppUsageRangeSelection? selection = null, IAppTrackingSetupService? setupService = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _clock = timeProvider ?? TimeProvider.System;
        _selection = selection ?? new AppUsageRangeSelection();
        _setupService = setupService;
        if (_setupService is IAppTrackingManagementService management)
            management.StateChanged += Service_OnStateChanged;
        if (_setupService is IAppUsageHistoryManagementService history)
            history.HistoryCleared += History_OnCleared;
    }

    public AppUsageTodayPresentation Current { get; private set; } = AppUsageTodayPresentation.Loading;
    public bool IsRefreshing => _refreshing;
    public AppUsageRange SelectedRange => _selection.SelectedRange;
    public bool IsInstalling => _installing;
    public bool CanInstall => !_disposed && _active && !_installing && _setupService is not null
        && Current.State == AppUsageTodayState.Missing;

    public void ServiceUninstalled()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _generation++;
        _refreshOnCompletion = false;
        _requestCancellation?.Cancel();
        _installError = "";
        _nextRefresh = _clock.GetUtcNow() + RefreshInterval;
        // Removal is already verified by the installer. Clear cached usage even
        // while this page is hidden, without sending another IPC request.
        Publish(AppUsageTodayPresentation.SetupRequired());
    }

    public void RefreshAfterHistoryClear()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _generation++;
        _refreshOnCompletion = false;
        _requestCancellation?.Cancel();
        _nextRefresh = _clock.GetUtcNow() + RefreshInterval;
        if (Current.State is not (AppUsageTodayState.Missing or AppUsageTodayState.Stopped or AppUsageTodayState.Unavailable))
            Publish(AppUsageTodayPresentation.Cleared(SelectedRange));
        StartRefresh(force: true);
    }

    private void History_OnCleared(object? sender, EventArgs args)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(new Action(RefreshAfterHistoryClear), DispatcherPriority.Background);
    }

    private void Service_OnStateChanged(object? sender, EventArgs args)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            // An installation initiated here already owns its completion refresh.
            if (_disposed || _installing) return;
            _generation++;
            _refreshOnCompletion = false;
            _requestCancellation?.Cancel();
            StartRefresh(force: true);
        }), DispatcherPriority.Background);
    }

    public async Task InstallAsync()
    {
        _dispatcher.VerifyAccess();
        if (!CanInstall) return;
        _installing = true;
        _installError = "";
        _generation++;
        _refreshOnCompletion = false;
        _requestCancellation?.Cancel();
        Publish(AppUsageTodayPresentation.Installing);
        AppTrackingInstallOutcome outcome;
        try
        {
            // Elevation belongs only to this explicit action. An already started
            // installer may finish safely even when the view closes or is hidden.
            outcome = await Task.Run(() => _setupService!.InstallAsync(CancellationToken.None)).ConfigureAwait(false);
        }
        catch (Exception) { outcome = AppTrackingInstallOutcome.Failed; }
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed) return;
                _installing = false;
                _generation++;
                _refreshOnCompletion = false;
                _requestCancellation?.Cancel();
                if (outcome == AppTrackingInstallOutcome.Succeeded)
                {
                    if (_active)
                    {
                        Publish(AppUsageTodayPresentation.Loading);
                        StartRefresh(force: true);
                    }
                }
                else
                {
                    _installError = outcome switch
                    {
                        AppTrackingInstallOutcome.Cancelled => "Installation was cancelled.",
                        AppTrackingInstallOutcome.FilesMissing => "Installation files are missing. Use the complete NetTrace package.",
                        _ => "App tracking could not be installed. Try again."
                    };
                    _nextRefresh = _clock.GetUtcNow() + RefreshInterval;
                    if (_active) Publish(AppUsageTodayPresentation.SetupRequired(_installError));
                }
            }, DispatcherPriority.Background).Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    public void SelectRange(AppUsageRange range)
    {
        _dispatcher.VerifyAccess();
        if (!Enum.IsDefined(range)) throw new ArgumentOutOfRangeException(nameof(range));
        if (_disposed || SelectedRange == range) return;
        _selection.SelectedRange = range;
        _generation++;
        _refreshOnCompletion = false;
        _requestCancellation?.Cancel();
        if (!_active) return;
        Publish(_installing ? AppUsageTodayPresentation.Installing : AppUsageTodayPresentation.Loading);
        StartRefresh(force: true);
    }

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
        Publish(_installing ? AppUsageTodayPresentation.Installing : AppUsageTodayPresentation.Loading);
        StartRefresh(force: true);
    }

    public void Tick()
    {
        _dispatcher.VerifyAccess();
        StartRefresh(force: false);
    }

    private void StartRefresh(bool force)
    {
        if (_disposed || !_active || _installing) return;
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
        _inFlight = RefreshAsync(_generation, SelectedRange, cancellation);
    }

    private async Task RefreshAsync(long generation, AppUsageRange range, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        IpcResult<AppUsageQueryResult> result;
        var serviceState = AppTrackingServiceState.Running;
        try
        {
            // Even connection identity verification and a synchronously completing
            // client run away from the dispatcher. Controls change only below.
            result = await Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                if (_setupService is not null)
                    serviceState = await _setupService.GetStateAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return serviceState == AppTrackingServiceState.Running
                    ? await _client.GetAppUsageRangeAsync(range, token).ConfigureAwait(false)
                    : new IpcResult<AppUsageQueryResult>(IpcState.CollectorUnavailable, null);
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
                if (!_disposed && _active && generation == _generation && range == SelectedRange && !token.IsCancellationRequested)
                    Publish(serviceState switch
                    {
                        AppTrackingServiceState.Missing => AppUsageTodayPresentation.SetupRequired(_installError),
                        AppTrackingServiceState.Stopped => AppUsageTodayPresentation.Stopped,
                        AppTrackingServiceState.Installing => AppUsageTodayPresentation.Installing,
                        AppTrackingServiceState.Uninstalling => AppUsageTodayPresentation.Unavailable,
                        AppTrackingServiceState.Updating => AppUsageTodayPresentation.Unavailable,
                        AppTrackingServiceState.Unavailable => AppUsageTodayPresentation.Unavailable,
                        _ => AppUsageTodayPresentation.FromResult(result, range)
                    });
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
        if (_setupService is IAppTrackingManagementService management)
            management.StateChanged -= Service_OnStateChanged;
        if (_setupService is IAppUsageHistoryManagementService history)
            history.HistoryCleared -= History_OnCleared;
        _active = false;
        _generation++;
        _refreshOnCompletion = false;
        _requestCancellation?.Cancel();
        _refreshing = false;
        if (_inFlight is not null) await _inFlight.ConfigureAwait(false);
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
