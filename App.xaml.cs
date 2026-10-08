using System.Windows;
using NetTrace.Services;

namespace NetTrace;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var store = new UsageStore();
            MainWindow = new MainWindow(store);
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
