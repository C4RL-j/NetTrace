namespace NetTrace;

public partial class MainWindow
{
    public void ShowSettings()
    {
        Dispatcher.VerifyAccess();
        SettingsTab.IsSelected = true;
    }
}
