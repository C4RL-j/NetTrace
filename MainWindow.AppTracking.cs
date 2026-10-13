using System.Windows;
using NetTrace.Presentation;
using NetTrace.Services;

namespace NetTrace;

public partial class MainWindow
{
    private AppTrackingSettingsController? _appTrackingSettings;

    private void InitializeAppTrackingSettings(IAppTrackingSetupService service, Func<bool>? confirmUninstall,
        Func<bool>? confirmClearHistory)
    {
        _appTrackingSettings = new(Dispatcher, service, ApplyAppTrackingSettings,
            confirmUninstall ?? ConfirmAppTrackingUninstall, () => _appUsageToday?.ServiceUninstalled(),
            confirmClearHistory: confirmClearHistory ?? ConfirmAppUsageHistoryClear);
    }

    private bool ConfirmAppTrackingUninstall() => MessageBox.Show(this,
        "Uninstall App Tracking?\n\nBackground application tracking will stop. Your recorded usage history will be kept.",
        "Uninstall App Tracking", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private bool ConfirmAppUsageHistoryClear() => MessageBox.Show(this,
        "Clear App Usage History?\n\nAll recorded application network usage will be deleted. Tracking will continue collecting new usage.",
        "Clear App Usage History", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private async void SettingsAppTrackingUninstall_OnClick(object sender, RoutedEventArgs args)
    {
        if (!_closed && _appTrackingSettings is not null) await _appTrackingSettings.UninstallAsync();
    }

    private async void SettingsAppUsageHistoryClear_OnClick(object sender, RoutedEventArgs args)
    {
        if (!_closed && _appTrackingSettings is not null) await _appTrackingSettings.ClearHistoryAsync();
    }

    private async void SettingsAppTrackingUpdate_OnClick(object sender, RoutedEventArgs args)
    {
        if (!_closed && _appTrackingSettings is not null) await _appTrackingSettings.UpdateAsync();
    }

    private void UpdateAppTrackingSettingsVisibility() => _appTrackingSettings?.SetActive(
        !_closed && IsVisible && WindowState != WindowState.Minimized && SettingsContent.IsVisible);

    private void ApplyAppTrackingSettings(AppTrackingSettingsPresentation page)
    {
        SettingsAppTrackingStatusText.Text = page.StatusText;
        SettingsAppTrackingUninstallButton.Visibility = page.IsInstalled ? Visibility.Visible : Visibility.Collapsed;
        SettingsAppTrackingUninstallButton.IsEnabled = page.CanUninstall;
        SettingsAppTrackingUpdateButton.Visibility = page.UpdateAvailable ? Visibility.Visible : Visibility.Collapsed;
        SettingsAppTrackingUpdateButton.IsEnabled = page.CanUpdate;
        SettingsAppUsageHistoryClearButton.IsEnabled = page.CanClearHistory;
        SettingsAppTrackingErrorText.Text = page.Error;
        SettingsAppTrackingErrorText.Visibility = page.Error.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
