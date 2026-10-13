using System.Windows.Threading;
using NetTrace.Services;

namespace NetTrace.Presentation;

// Status uses the window's existing timer. Service operations only follow a confirmed click.
public sealed class AppTrackingSettingsController : IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IAppTrackingSetupService _service;
    private readonly IAppTrackingManagementService? _management;
    private readonly IAppUsageHistoryManagementService? _historyManagement;
    private readonly IAppTrackingUpdateService? _updateService;
    private readonly Action<AppTrackingSettingsPresentation> _apply;
    private readonly Func<bool> _confirmUninstall;
    private readonly Action? _onUninstalled;
    private readonly Func<bool>? _confirmClearHistory;
    private readonly Action? _onHistoryCleared;
    private readonly TimeProvider _clock;
    private CancellationTokenSource? _statusCancellation;
    private Task? _statusRequest;
    private DateTimeOffset _nextRefresh;
    private long _generation;
    private bool _active;
    private bool _refreshOnCompletion;
    private bool _confirming;
    private bool _uninstalling;
    private bool _clearingHistory;
    private bool _updating;
    private AppTrackingServiceState _lastServiceState = AppTrackingServiceState.Unavailable;
    private AppUsageStorageState _storageState = AppUsageStorageState.Missing;
    private AppTrackingUpdateStatus _updateStatus = new(AppTrackingUpdateAvailability.Unavailable, null, null);
    private string _error = "";
    private volatile bool _disposed;

    public AppTrackingSettingsController(Dispatcher dispatcher, IAppTrackingSetupService service,
        Action<AppTrackingSettingsPresentation> apply, Func<bool> confirmUninstall,
        Action? onUninstalled = null, TimeProvider? timeProvider = null,
        Func<bool>? confirmClearHistory = null, Action? onHistoryCleared = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _management = service as IAppTrackingManagementService;
        _historyManagement = service as IAppUsageHistoryManagementService;
        _updateService = service as IAppTrackingUpdateService;
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _confirmUninstall = confirmUninstall ?? throw new ArgumentNullException(nameof(confirmUninstall));
        _onUninstalled = onUninstalled;
        _confirmClearHistory = confirmClearHistory;
        _onHistoryCleared = onHistoryCleared;
        _clock = timeProvider ?? TimeProvider.System;
        if (_management is not null) _management.StateChanged += Service_OnStateChanged;
    }

    public AppTrackingSettingsPresentation Current { get; private set; } = AppTrackingSettingsPresentation.Checking;
    public bool IsRefreshing { get; private set; }
    public bool IsUninstalling => _uninstalling;
    public bool IsClearingHistory => _clearingHistory;
    public bool IsUpdating => _updating;
    public bool CanUpdate => !_disposed && _active && !_confirming && !_uninstalling && !_clearingHistory && !_updating
        && _updateService is not null && Current.CanUpdate;
    public bool CanUninstall => !_disposed && _active && !_confirming && !_uninstalling && !_clearingHistory && !_updating
        && _management is not null && Current.CanUninstall;
    public bool CanClearHistory => !_disposed && _active && !_confirming && !_uninstalling && !_clearingHistory && !_updating
        && _historyManagement is not null && _confirmClearHistory is not null && Current.CanClearHistory;

    public void SetActive(bool active)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _active == active) return;
        _active = active;
        InvalidateStatus();
        if (active && !_uninstalling && !_clearingHistory && !_updating) StartRefresh(force: true);
    }

    public void Tick()
    {
        _dispatcher.VerifyAccess();
        StartRefresh(force: false);
    }

    public async Task UninstallAsync()
    {
        _dispatcher.VerifyAccess();
        if (!CanUninstall) return;
        bool confirmed;
        _confirming = true;
        try { confirmed = _confirmUninstall(); }
        catch
        {
            _error = "Uninstall confirmation could not be opened. Try again.";
            Publish(Current with { Error = _error });
            return;
        }
        finally { _confirming = false; }
        if (!confirmed || !CanUninstall) return;

        _uninstalling = true;
        _error = "";
        InvalidateStatus();
        Publish(PresentationFor(AppTrackingServiceState.Uninstalling));
        AppTrackingUninstallOutcome outcome;
        try
        {
            // The confirmed action elevates only the existing tool. A closed window
            // stops observing it, while the tool finishes its owned service cleanup.
            outcome = await Task.Run(() => _management!.UninstallAsync(CancellationToken.None)).ConfigureAwait(false);
        }
        catch { outcome = AppTrackingUninstallOutcome.Failed; }
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed) return;
                _uninstalling = false;
                InvalidateStatus();
                if (outcome == AppTrackingUninstallOutcome.Succeeded)
                {
                    _error = "";
                    // The existing installer verifies service absence before exit zero.
                    Publish(PresentationFor(AppTrackingServiceState.Missing));
                    _nextRefresh = _clock.GetUtcNow() + AppUsageTodayController.RefreshInterval;
                    _onUninstalled?.Invoke();
                }
                else
                {
                    _error = outcome switch
                    {
                        AppTrackingUninstallOutcome.Cancelled => "Uninstall was cancelled.",
                        AppTrackingUninstallOutcome.FilesMissing => "Uninstall files are missing. Use the complete NetTrace package.",
                        _ => "App tracking could not be uninstalled. Try again."
                    };
                    Publish(Current with { Error = _error });
                    StartRefresh(force: true);
                }
            }, DispatcherPriority.Background).Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    public async Task ClearHistoryAsync()
    {
        _dispatcher.VerifyAccess();
        if (!CanClearHistory) return;
        bool confirmed;
        _confirming = true;
        try { confirmed = _confirmClearHistory!(); }
        catch
        {
            _error = "Clear history confirmation could not be opened. Try again.";
            Publish(Current with { Error = _error });
            return;
        }
        finally { _confirming = false; }
        if (!confirmed || !CanClearHistory) return;

        _clearingHistory = true;
        _error = "";
        InvalidateStatus();
        Publish(Current with { CanUninstall = false, CanClearHistory = false, CanUpdate = false, Error = "" });
        AppUsageHistoryClearOutcome outcome;
        try
        {
            outcome = await Task.Run(() => _historyManagement!.ClearHistoryAsync(CancellationToken.None)).ConfigureAwait(false);
        }
        catch { outcome = AppUsageHistoryClearOutcome.Failed; }
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed) return;
                _clearingHistory = false;
                InvalidateStatus();
                _error = outcome switch
                {
                    AppUsageHistoryClearOutcome.Succeeded => "",
                    AppUsageHistoryClearOutcome.Cancelled => "Clear history was cancelled.",
                    AppUsageHistoryClearOutcome.FilesMissing => "Clear history files are missing. Use the complete NetTrace package.",
                    AppUsageHistoryClearOutcome.StorageMissing => "No app usage history was found.",
                    AppUsageHistoryClearOutcome.CollectorUpdateRequired => "Update App Tracking before clearing history.",
                    _ => "App usage history could not be cleared. Try again."
                };
                if (outcome == AppUsageHistoryClearOutcome.Succeeded) _onHistoryCleared?.Invoke();
                if (outcome == AppUsageHistoryClearOutcome.StorageMissing) _storageState = AppUsageStorageState.Missing;
                Publish(Current with
                {
                    Error = _error,
                    CanUninstall = Current.IsInstalled && _management is not null,
                    CanClearHistory = StorageAvailable && _historyManagement is not null && _confirmClearHistory is not null,
                    StorageAvailable = StorageAvailable
                });
                StartRefresh(force: true);
            }, DispatcherPriority.Background).Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    public async Task UpdateAsync()
    {
        _dispatcher.VerifyAccess();
        if (!CanUpdate) return;
        _updating = true;
        _error = "";
        InvalidateStatus();
        Publish(PresentationFor(AppTrackingServiceState.Updating));
        AppTrackingUpdateOutcome outcome;
        try
        {
            // The existing installer owns stop/stage/configure/start and rollback.
            // Only this button action requests elevation; its child can finish
            // even if the user navigates away or closes the desktop window.
            outcome = await Task.Run(() => _updateService!.UpdateAsync(CancellationToken.None)).ConfigureAwait(false);
        }
        catch { outcome = AppTrackingUpdateOutcome.Failed; }
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed) return;
                _updating = false;
                InvalidateStatus();
                _error = outcome switch
                {
                    AppTrackingUpdateOutcome.Succeeded => "",
                    AppTrackingUpdateOutcome.Cancelled => "Update was cancelled.",
                    AppTrackingUpdateOutcome.FilesMissing => "Update files are missing. Use the complete NetTrace package.",
                    AppTrackingUpdateOutcome.NotInstalled => "App tracking is no longer installed.",
                    _ => "App tracking could not be updated. Try again."
                };
                if (outcome == AppTrackingUpdateOutcome.Succeeded)
                {
                    _updateStatus = new(AppTrackingUpdateAvailability.Current, _updateStatus.BundledVersion, _updateStatus.BundledVersion);
                    // A concurrent updater may already have matched the package.
                    // Keep the last observed service state until SCM is read back.
                    Publish(PresentationFor(_lastServiceState));
                }
                else if (outcome == AppTrackingUpdateOutcome.NotInstalled)
                {
                    _updateStatus = new(AppTrackingUpdateAvailability.NotInstalled, null, _updateStatus.BundledVersion);
                    Publish(PresentationFor(AppTrackingServiceState.Missing));
                }
                else
                    Publish(PresentationFor(_lastServiceState));
                StartRefresh(force: true);
            }, DispatcherPriority.Background).Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    private bool StorageAvailable => _storageState is AppUsageStorageState.Exists or AppUsageStorageState.Protected;

    private AppTrackingSettingsPresentation PresentationFor(AppTrackingServiceState state)
    {
        bool updateAvailable = _updateService is not null
            && _updateStatus.Availability == AppTrackingUpdateAvailability.UpdateAvailable
            && state is AppTrackingServiceState.Running or AppTrackingServiceState.Stopped;
        bool idle = !_updating && !_clearingHistory && !_uninstalling
            && state is not (AppTrackingServiceState.Installing or AppTrackingServiceState.Uninstalling or AppTrackingServiceState.Updating);
        var presentation = AppTrackingSettingsPresentation.FromState(state, _management is not null, _error);
        return presentation with
        {
            StatusText = updateAvailable && idle ? "Update available" : presentation.StatusText,
            StorageAvailable = StorageAvailable,
            CanClearHistory = StorageAvailable && _historyManagement is not null && _confirmClearHistory is not null && idle,
            UpdateAvailable = updateAvailable || state == AppTrackingServiceState.Updating,
            CanUpdate = updateAvailable && idle,
            InstalledVersion = _updateStatus.InstalledVersion,
            BundledVersion = _updateStatus.BundledVersion
        };
    }

    private void Service_OnStateChanged(object? sender, EventArgs args)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            InvalidateStatus();
            StartRefresh(force: true);
        }), DispatcherPriority.Background);
    }

    private void InvalidateStatus()
    {
        _generation++;
        _refreshOnCompletion = false;
        _statusCancellation?.Cancel();
    }

    private void StartRefresh(bool force)
    {
        if (_disposed || !_active || _uninstalling || _clearingHistory || _updating) return;
        if (IsRefreshing)
        {
            if (force) _refreshOnCompletion = true;
            return;
        }
        if (!force && _clock.GetUtcNow() < _nextRefresh) return;
        IsRefreshing = true;
        _refreshOnCompletion = false;
        _nextRefresh = _clock.GetUtcNow() + AppUsageTodayController.RefreshInterval;
        var cancellation = new CancellationTokenSource();
        _statusCancellation = cancellation;
        var generation = _generation;
        _statusRequest = Task.Run(() => RefreshAsync(generation, cancellation));
    }

    private async Task RefreshAsync(long generation, CancellationTokenSource cancellation)
    {
        AppTrackingServiceState state;
        AppUsageStorageState storageState = AppUsageStorageState.Missing;
        AppTrackingUpdateStatus updateStatus = new(AppTrackingUpdateAvailability.Unavailable, null, null);
        try { state = await _service.GetStateAsync(cancellation.Token).ConfigureAwait(false); }
        catch { state = AppTrackingServiceState.Unavailable; }
        if (_historyManagement is not null)
        {
            try { storageState = await _historyManagement.GetStorageStateAsync(cancellation.Token).ConfigureAwait(false); }
            catch { storageState = AppUsageStorageState.Unavailable; }
        }
        if (_updateService is not null)
        {
            try { updateStatus = await _updateService.GetUpdateStatusAsync(cancellation.Token).ConfigureAwait(false); }
            catch { updateStatus = new(AppTrackingUpdateAvailability.Unavailable, null, null); }
        }
        try
        {
            if (_disposed || _dispatcher.HasShutdownStarted) return;
            await _dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_statusCancellation, cancellation)) _statusCancellation = null;
                IsRefreshing = false;
                if (!_disposed && _active && !_uninstalling && !_clearingHistory && !_updating && generation == _generation && !cancellation.IsCancellationRequested)
                {
                    _storageState = storageState;
                    _updateStatus = updateStatus;
                    _lastServiceState = state;
                    Publish(PresentationFor(state));
                }
                if (!_disposed && _active && _refreshOnCompletion) StartRefresh(force: true);
            }, DispatcherPriority.Background).Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally { cancellation.Dispose(); }
    }

    private void Publish(AppTrackingSettingsPresentation page)
    {
        Current = page;
        _apply(page);
    }

    public async ValueTask DisposeAsync()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _active = false;
        if (_management is not null) _management.StateChanged -= Service_OnStateChanged;
        InvalidateStatus();
        IsRefreshing = false;
        if (_statusRequest is not null) await _statusRequest.ConfigureAwait(false);
    }
}
