using System.Windows;
using NetTrace.Services;

namespace NetTrace;

public partial class App : System.Windows.Application
{
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (MainWindow is NetTrace.MainWindow window) window.CompleteRun();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (MainWindow is NetTrace.MainWindow window) window.CompleteRun();
        base.OnExit(e);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            MainWindow = NetTrace.MainWindow.CreateForDataRoot(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            MainWindow.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"NetTrace could not start.\n\n{exception.Message}\n\nMonitoring has not started. Resolve the error and try again.",
                "NetTrace startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
