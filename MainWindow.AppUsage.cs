using System.Windows;
using AppUsageIpc;
using NetTrace.Presentation;
using NetTrace.Services;

namespace NetTrace;

public partial class MainWindow
{
    private AppUsageTodayController? _appUsageToday;

    private void InitializeAppUsageToday(IAppUsageTodayClient? client, AppUsageRangeSelection? selection,
        IAppTrackingSetupService? setupService)
    {
        _appUsageToday = new(Dispatcher, client ?? new AppUsageTodayClient(), ApplyAppUsageToday,
            selection: selection, setupService: setupService ?? new AppTrackingSetupService());
        AppUsageTodayButton.IsChecked = _appUsageToday.SelectedRange == AppUsageRange.Today;
        AppUsage7DButton.IsChecked = _appUsageToday.SelectedRange == AppUsageRange.SevenDays;
        AppUsage30DButton.IsChecked = _appUsageToday.SelectedRange == AppUsageRange.ThirtyDays;
        AppUsageContent.IsVisibleChanged += AppUsageContent_OnVisibilityChanged;
    }

    private async void AppUsageInstall_OnClick(object sender, RoutedEventArgs args)
    {
        if (!_closed && _appUsageToday is not null) await _appUsageToday.InstallAsync();
    }

    private void AppUsageRange_OnChecked(object sender, RoutedEventArgs args)
    {
        if (_appUsageToday is null || _closed) return;
        var range = ReferenceEquals(sender, AppUsage30DButton) ? AppUsageRange.ThirtyDays
            : ReferenceEquals(sender, AppUsage7DButton) ? AppUsageRange.SevenDays : AppUsageRange.Today;
        _appUsageToday.SelectRange(range);
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
        AppUsageSetupErrorText.Text = page.SetupError;
        AppUsageSetupErrorText.Visibility = page.SetupError.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        AppUsageInstallButton.Visibility = page.State is AppUsageTodayState.Missing or AppUsageTodayState.Installing
            ? Visibility.Visible : Visibility.Collapsed;
        AppUsageInstallButton.IsEnabled = page.State == AppUsageTodayState.Missing;
        AppUsageTrackedAppsText.Text = page.TrackedAppsText;
        AppUsageDownloadText.Text = page.DownloadText;
        AppUsageUploadText.Text = page.UploadText;
        AppUsageLogicalTotalText.Text = page.LogicalTotalText;
        AppUsageSummaryPanel.Visibility = page.State is AppUsageTodayState.Available or AppUsageTodayState.Empty
            ? Visibility.Visible : Visibility.Collapsed;
        AppUsageApplications.ItemsSource = page.Applications;
        AppUsageApplicationsPanel.Visibility = page.Applications.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var row in page.Applications) _ = LoadAppUsageIconAsync(row);
    }

    private async Task LoadAppUsageIconAsync(AppUsageApplicationRow row)
    {
        // A late icon belongs only to this row. Replaced ranges/closed windows
        // cannot receive old usage data through an icon completion.
        try { await row.LoadIconAsync(AppUsageIconCache.Default); }
        catch (Exception) { /* The row already displays its frozen fallback. */ }
    }

    private async Task DisposeAppUsageTodayAsync()
    {
        AppUsageContent.IsVisibleChanged -= AppUsageContent_OnVisibilityChanged;
        if (_appUsageToday is not null) await _appUsageToday.DisposeAsync();
    }
}
