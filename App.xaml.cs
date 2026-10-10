using System.Windows;
using System.Diagnostics;
using NetTrace.Services;

namespace NetTrace;

public partial class App : System.Windows.Application
{
    private TrayApplicationContext? _lifetime;

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (!e.Cancel) StopMonitoring();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StopMonitoring();
        base.OnExit(e);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        MonitoringService? monitoring = null;
        try
        {
            monitoring = MonitoringService.CreateForDataRoot(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            _lifetime = new TrayApplicationContext(this, monitoring);
            _lifetime.Start(LaunchOptions.IsBackground(e.Args));
        }
        catch (Exception exception)
        {
            if (_lifetime is not null) StopMonitoring();
            else
            {
                try { monitoring?.Dispose(); }
                catch (Exception cleanupException)
                {
                    Trace.TraceError($"Could not finish startup cleanup: {cleanupException.Message}");
                }
            }
            MessageBox.Show(
                $"NetTrace could not start.\n\n{exception.Message}\n\nMonitoring has not started. Resolve the error and try again.",
                "NetTrace startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void StopMonitoring()
    {
        try { _lifetime?.Dispose(); }
        catch (Exception exception)
        {
            Trace.TraceError($"Could not finish application shutdown cleanup: {exception.Message}");
        }
        finally { _lifetime = null; }
    }
}
