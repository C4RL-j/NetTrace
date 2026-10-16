using System.Diagnostics;
using System.Windows;
using NetTrace.Presentation;

namespace NetTrace.Services;

/// <summary>Owns monitoring for the application lifetime, independently of dashboard windows.</summary>
public sealed class TrayApplicationContext : IDisposable
{
    private readonly Application _application;
    private readonly SystemTrayIcon _trayIcon;
    private readonly WindowsPowerLifecycle _powerLifecycle;
    private readonly Func<MonitoringService, MainWindow> _createDashboard;
    private readonly Action _shutdown;
    private readonly AppUsageRangeSelection _appUsageRangeSelection = new();
    private readonly IAppTrackingSetupService _appTrackingSetupService = new AppTrackingSetupService();
    private bool _disposed;

    public MonitoringService Monitoring { get; }
    public MainWindow? Dashboard { get; private set; }
    public TrayPopupWindow? Popup { get; private set; }

    public TrayApplicationContext(Application application, MonitoringService monitoring,
        Func<MonitoringService, MainWindow>? dashboardFactory = null, Action? shutdownAction = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(monitoring);
        application.Dispatcher.VerifyAccess();
        _application = application;
        _application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Monitoring = monitoring;
        _createDashboard = dashboardFactory ?? (service => new MainWindow(service,
            appUsageRangeSelection: _appUsageRangeSelection, appTrackingSetupService: _appTrackingSetupService));
        _shutdown = shutdownAction ?? (() => application.Shutdown());
        _powerLifecycle = new WindowsPowerLifecycle();
        _powerLifecycle.Suspending += PowerLifecycle_OnSuspending;
        _powerLifecycle.Resuming += PowerLifecycle_OnResuming;
        try
        {
            _trayIcon = new SystemTrayIcon(application.Dispatcher, TogglePopup,
                ShowDashboard, ShowSettings, ClosePopup, Exit);
        }
        catch
        {
            _powerLifecycle.Dispose();
            throw;
        }
    }

    public void Start(bool background = false)
    {
        _application.Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        Monitoring.Start();
        if (!background) ShowDashboard();
    }

    public void ShowDashboard()
    {
        _application.Dispatcher.VerifyAccess();
        if (_disposed || _application.Dispatcher.HasShutdownStarted) return;
        ClosePopup();
        if (Dashboard is null)
        {
            var window = _createDashboard(Monitoring);
            window.Closed += Dashboard_OnClosed;
            Dashboard = window;
            _application.MainWindow = window;
        }
        Dashboard.Show();
        if (Dashboard.WindowState == WindowState.Minimized) Dashboard.WindowState = WindowState.Normal;
        Dashboard.Activate();
    }

    public void ShowSettings()
    {
        ShowDashboard();
        Dashboard?.ShowSettings();
    }

    public void TogglePopup()
    {
        _application.Dispatcher.VerifyAccess();
        if (_disposed || _application.Dispatcher.HasShutdownStarted) return;
        if (Popup is not null)
        {
            ClosePopup();
            return;
        }
        var popup = new TrayPopupWindow(Monitoring);
        popup.Closed += Popup_OnClosed;
        Popup = popup;
        try
        {
            TrayPopupPlacement.Show(popup, _trayIcon.PopupAnchor, _trayIcon.PopupTaskbarBounds);
            if (ReferenceEquals(_application.MainWindow, popup)) _application.MainWindow = Dashboard;
        }
        catch
        {
            ClosePopup();
            throw;
        }
    }

    private void ClosePopup()
    {
        var popup = Popup;
        Popup = null;
        if (popup is null) return;
        popup.Closed -= Popup_OnClosed;
        if (ReferenceEquals(_application.MainWindow, popup)) _application.MainWindow = Dashboard;
        popup.Close();
    }

    private void Popup_OnClosed(object? sender, EventArgs e)
    {
        if (sender is TrayPopupWindow popup) popup.Closed -= Popup_OnClosed;
        if (ReferenceEquals(sender, Popup)) Popup = null;
        // WPF temporarily assigns the first opened window as MainWindow on a
        // background launch. The transient popup never owns the application.
        if (ReferenceEquals(sender, _application.MainWindow)) _application.MainWindow = Dashboard;
    }

    private void PowerLifecycle_OnSuspending(object? sender, EventArgs e)
    {
        if (!_disposed) Monitoring.Suspend();
    }

    private void PowerLifecycle_OnResuming(object? sender, EventArgs e)
    {
        if (!_disposed) Monitoring.Resume();
    }

    private void Dashboard_OnClosed(object? sender, EventArgs e)
    {
        if (sender is MainWindow window) window.Closed -= Dashboard_OnClosed;
        if (ReferenceEquals(sender, Dashboard)) Dashboard = null;
        if (ReferenceEquals(sender, _application.MainWindow)) _application.MainWindow = null;
    }

    public void Exit()
    {
        _application.Dispatcher.VerifyAccess();
        if (_disposed) return;
        try { Dispose(); }
        catch (Exception exception)
        {
            Trace.TraceError($"Could not finish tray shutdown cleanup: {exception.Message}");
        }
        finally { _shutdown(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _application.Dispatcher.VerifyAccess();
        _disposed = true;
        try
        {
            try { ClosePopup(); }
            finally
            {
                try { _powerLifecycle.Dispose(); }
                finally { Monitoring.Dispose(); }
            }
        }
        finally
        {
            try { _trayIcon.Dispose(); }
            finally
            {
                var window = Dashboard;
                Dashboard = null;
                if (ReferenceEquals(window, _application.MainWindow)) _application.MainWindow = null;
                if (window is not null)
                {
                    window.Closed -= Dashboard_OnClosed;
                    try
                    {
                        foreach (Window owned in window.OwnedWindows.Cast<Window>().ToArray()) owned.Close();
                        window.Close();
                    }
                    catch (Exception exception)
                    {
                        Trace.TraceWarning($"Could not close the dashboard during shutdown: {exception.Message}");
                    }
                }
            }
        }
    }
}
