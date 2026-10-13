using System.Windows;
using NetTrace.Presentation;
using NetTrace.Services;

namespace NetTrace;

public partial class MainWindow
{
    private AppUsageTodayController? _appUsageToday;

    private void InitializeAppUsageToday(IAppUsageTodayClient? client)
    {
        _appUsageToday = new(Dispatcher, client ?? new AppUsageTodayClient(), ApplyAppUsageToday);
        AppUsageContent.IsVisibleChanged += AppUsageContent_OnVisibilityChanged;
    }

    private bool AppUsageIsActive => !_closed && IsVisible && WindowState != WindowState.Minimized && AppUsageContent.IsVisible;

    private void AppUsageContent_OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) =>
        UpdateAppUsageVisibility();

    private void UpdateAppUsageVisibility() => _appUsageToday?.SetActive(AppUsageIsActive);

    private void ApplyAppUsageToday(AppUsageTodayPresentation page)
    {
        AppUsageStateText.Text = page.Message;
        AppUsageStateText.Visibility = page.Message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        AppUsageDetailText.Text = page.Detail;
        AppUsageDetailText.Visibility = page.Detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        AppUsageTrackedAppsText.Text = page.TrackedAppsText;
        AppUsageDownloadText.Text = page.DownloadText;
        AppUsageUploadText.Text = page.UploadText;
        AppUsageLogicalTotalText.Text = page.LogicalTotalText;
        AppUsageSummaryPanel.Visibility = page.State is AppUsageTodayState.Available or AppUsageTodayState.Empty
            ? Visibility.Visible : Visibility.Collapsed;
        AppUsageApplications.ItemsSource = page.Applications;
        AppUsageApplicationsPanel.Visibility = page.Applications.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task DisposeAppUsageTodayAsync()
    {
        AppUsageContent.IsVisibleChanged -= AppUsageContent_OnVisibilityChanged;
        if (_appUsageToday is not null) await _appUsageToday.DisposeAsync();
    }
}
