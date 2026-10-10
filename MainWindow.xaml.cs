using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NetTrace.Models;
using NetTrace.Services;
using NetTrace.Utils;

namespace NetTrace;

// An application shell. Closing it never stops the application-owned monitor.
public partial class MainWindow : Window
{
    private readonly MonitoringService _monitoring;
    private readonly DispatcherTimer _timer;
    private IWindowsStartupRegistration? _startupRegistration;
    private volatile bool _closed;
    private bool _loadingAdapters = true;
    private bool _chartInitialized;
    private IcmpProbeResult? _lastChartMeasurement;

    public MainWindow(MonitoringService monitoring, IWindowsStartupRegistration? startupRegistration = null)
    {
        ArgumentNullException.ThrowIfNull(monitoring);
        _monitoring = monitoring;
        _startupRegistration = startupRegistration;
        InitializeComponent();
        RefreshStartupSetting();
        DatabasePathText.Text = monitoring.DatabasePath;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_OnTick;
        Closed += OnClosed;
        IsVisibleChanged += OnVisibilityChanged;
        DashboardContent.IsVisibleChanged += DashboardContent_OnVisibilityChanged;
        HealthContent.IsVisibleChanged += HealthContent_OnVisibilityChanged;
        SettingsContent.IsVisibleChanged += SettingsContent_OnVisibilityChanged;
        RefreshDashboard();
        _monitoring.Changed += Monitoring_OnChanged;
    }

    protected override void OnActivated(EventArgs args)
    {
        base.OnActivated(args);
        if (!_closed) RefreshStartupSetting();
    }

    protected override void OnStateChanged(EventArgs args)
    {
        base.OnStateChanged(args);
        if (_closed || _timer is null) return;
        if (WindowState == WindowState.Minimized) _timer.Stop();
        else if (IsVisible)
        {
            RefreshDashboard();
            if (HealthContent.IsVisible)
            {
                IcmpStabilityChart.InvalidateVisual();
                IcmpPacketLossChart.InvalidateVisual();
            }
            _timer.Start();
        }
    }

    private IWindowsStartupRegistration StartupRegistration =>
        _startupRegistration ??= new WindowsStartupRegistration();

    private void RefreshStartupSetting()
    {
        try
        {
            StartWithWindowsCheckBox.IsChecked = StartupRegistration.IsEnabled();
            StartWithWindowsCheckBox.IsEnabled = true;
            StartupSettingMessageText.Text = "";
        }
        catch (Exception exception)
        {
            StartWithWindowsCheckBox.IsChecked = null;
            StartWithWindowsCheckBox.IsEnabled = false;
            StartupSettingMessageText.Text = $"Windows startup setting is unavailable: {exception.Message}";
        }
    }

    private void StartWithWindows_OnClick(object sender, RoutedEventArgs args)
    {
        string? error = null;
        try
        {
            if (StartWithWindowsCheckBox.IsChecked == true) StartupRegistration.Enable();
            else StartupRegistration.Disable();
        }
        catch (Exception exception)
        {
            error = $"Could not update Windows startup: {exception.Message}";
        }
        // Read back the actual registration, including after a partial write failure.
        RefreshStartupSetting();
        if (error is not null) StartupSettingMessageText.Text = error;
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (_closed) return;
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            RefreshDashboard();
            _timer.Start();
        }
        else _timer.Stop();
    }

    private void DashboardContent_OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (!_closed && DashboardContent.IsVisible) RefreshDashboard();
    }

    private void SettingsContent_OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (!_closed && SettingsContent.IsVisible) RefreshStartupSetting();
    }

    private void HealthContent_OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (!_closed && HealthContent.IsVisible) RefreshHealth(refreshChart: true);
    }

    private void RefreshHealth(bool refreshChart = false)
    {
        if (_closed || !IsVisible || WindowState == WindowState.Minimized || !HealthContent.IsVisible) return;
        // Read the application-owned worker's last completed cycle. Opening this
        // page never starts or restarts probes; the existing view timer refreshes it.
        var result = _monitoring.LatestIcmpMeasurement;
        if (refreshChart || !_chartInitialized || !ReferenceEquals(result, _lastChartMeasurement))
        {
            var samples = _monitoring.RollingIcmpSamples;
            IcmpStabilityChart.UpdateSamples(samples);
            IcmpPacketLossChart.UpdateData(IcmpStabilityChart.Data);
            _lastChartMeasurement = samples.Count == 0 ? null : samples[^1];
            _chartInitialized = true;
            // Visibility may have suppressed an earlier render, even when the
            // retained samples have not changed since the page was last shown.
            if (refreshChart)
            {
                IcmpStabilityChart.InvalidateVisual();
                IcmpPacketLossChart.InvalidateVisual();
            }
        }
        HealthWaitingText.Visibility = result is null ? Visibility.Visible : Visibility.Collapsed;
        if (result is null)
        {
            IcmpTargetText.Text = "—";
            IcmpLatencyText.Text = "—";
            IcmpJitterText.Text = "—";
            IcmpPacketLossText.Text = "—";
            IcmpRepliesText.Text = "—";
            IcmpLastSampleText.Text = "—";
            return;
        }

        IcmpTargetText.Text = result.Target;
        IcmpLatencyText.Text = result.SuccessfulReplies > 0 && result.AverageLatencyMs is { } latency
            ? $"{latency:0.##} ms" : "Unavailable";
        IcmpJitterText.Text = result.SuccessfulReplies >= 2 && result.JitterMs is { } jitter
            ? $"{jitter:0.##} ms" : "Unavailable";
        IcmpPacketLossText.Text = $"{result.PacketLossPercent:0.##}%";
        IcmpRepliesText.Text = $"{result.SuccessfulReplies} / {result.SuccessfulReplies + result.FailedReplies}";
        IcmpLastSampleText.Text = result.TimestampUtc.ToLocalTime().ToString("G");
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        _closed = true;
        _timer.Stop();
        _timer.Tick -= Timer_OnTick;
        _monitoring.Changed -= Monitoring_OnChanged;
        IsVisibleChanged -= OnVisibilityChanged;
        DashboardContent.IsVisibleChanged -= DashboardContent_OnVisibilityChanged;
        HealthContent.IsVisibleChanged -= HealthContent_OnVisibilityChanged;
        SettingsContent.IsVisibleChanged -= SettingsContent_OnVisibilityChanged;
        Closed -= OnClosed;
    }

    private void Monitoring_OnChanged(object? sender, EventArgs args)
    {
        if (_closed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_closed && IsVisible && WindowState != WindowState.Minimized) RefreshDashboard();
        }));
    }

    private void Timer_OnTick(object? sender, EventArgs args) => RefreshDashboard();

    public void RefreshDashboard()
    {
        if (_closed) return;
        if (HealthTab.IsSelected) RefreshHealth();
        var snapshot = _monitoring.GetSnapshot();
        _loadingAdapters = true;
        try
        {
            if (!ReferenceEquals(AdapterComboBox.ItemsSource, snapshot.Adapters))
                AdapterComboBox.ItemsSource = snapshot.Adapters;
            AdapterComboBox.SelectedItem = snapshot.SelectedAdapter;
        }
        finally { _loadingAdapters = false; }
        UpdateAdapterDetails(snapshot.SelectedAdapter);
        DownloadRateText.Text = ByteFormatter.FormatRate(snapshot.DownloadBytesPerSecond);
        UploadRateText.Text = ByteFormatter.FormatRate(snapshot.UploadBytesPerSecond);
        DownloadPeakText.Text = $"Peak today ↓ {ByteFormatter.FormatRate(snapshot.PeakDownloadBytesPerSecond)}";
        UploadPeakText.Text = $"Peak today ↑ {ByteFormatter.FormatRate(snapshot.PeakUploadBytesPerSecond)}";
        DownloadSessionText.Text = $"App session ↓ {ByteFormatter.Format(snapshot.SessionDownloaded)}";
        UploadSessionText.Text = $"App session ↑ {ByteFormatter.Format(snapshot.SessionUploaded)}";
        StatusText.Text = snapshot.Status;
        StatusDot.Fill = new SolidColorBrush(snapshot.Status == "Monitoring"
            ? Color.FromRgb(34, 197, 94) : Color.FromRgb(239, 68, 68));
        var state = snapshot.Connection;
        CurrentProfileText.Text = state.Status switch
        {
            ConnectionStatus.ConnectedRecognized => state.Profile!.DisplayName,
            ConnectionStatus.ConnectedUnassigned => "Unassigned network",
            ConnectionStatus.ConnectedIdentityUnavailable => "Network identity unavailable",
            ConnectionStatus.Disconnected => "Disconnected",
            _ => "Detecting connection…"
        };
        CurrentProfileText.ToolTip = CurrentProfileText.Text;
        CurrentNetworkText.Text = state.Network?.DisplayName ?? state.Message ?? "No physical network connected.";
        AssignProfileButton.Visibility = state.CanAssign ? Visibility.Visible : Visibility.Collapsed;
        SessionMessageText.Text = snapshot.PersistenceMessage ?? "";
        RefreshCurrentSession(snapshot);
        if (!DashboardTab.IsSelected) return;
        try
        {
            var totals = _monitoring.GetUsageTotals();
            UpdateUsageCard(TodayTotalText, TodayBreakdownText, totals.Today.Downloaded, totals.Today.Uploaded);
            UpdateUsageCard(WeekTotalText, WeekBreakdownText, totals.Week.Downloaded, totals.Week.Uploaded);
            UpdateUsageCard(MonthTotalText, MonthBreakdownText, totals.Month.Downloaded, totals.Month.Uploaded);
        }
        catch (Exception exception)
        {
            SessionMessageText.Text = $"Usage history could not be read: {exception.Message}";
            Trace.TraceError(SessionMessageText.Text);
        }
    }

    private void RefreshCurrentSession(MonitoringSnapshot snapshot)
    {
        var session = snapshot.Session;
        if (session is null || snapshot.IsSuspended)
        {
            CurrentSessionText.Text = snapshot.IsSuspended ? "Paused for sleep" : "No active session";
            CurrentSessionUsageText.Text = "↓ 0 B   ↑ 0 B";
            return;
        }
        var duration = DateTimeOffset.UtcNow - session.StartedAtUtc;
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        CurrentSessionText.Text = duration.TotalHours >= 1
            ? $"{(long)duration.TotalHours}h {duration.Minutes}m"
            : $"{duration.Minutes}m {duration.Seconds}s";
        CurrentSessionUsageText.Text = $"↓ {ByteFormatter.Format(session.DownloadBytes)}   ↑ {ByteFormatter.Format(session.UploadBytes)}";
        if (!string.Equals(snapshot.SelectedAdapter?.Id, session.AdapterId, StringComparison.OrdinalIgnoreCase))
            CurrentSessionUsageText.Text += " • Select the current connection's adapter to record its traffic";
    }

    private void AdapterComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loadingAdapters || _closed) return;
        try
        {
            _monitoring.SelectAdapter((AdapterComboBox.SelectedItem as AdapterInfo)?.Id);
            RefreshDashboard();
        }
        catch (Exception exception) { SessionMessageText.Text = $"Could not select the network adapter: {exception.Message}"; }
    }

    private void ManageProfiles_OnClick(object sender, RoutedEventArgs args) => OpenProfiles(null);

    private void AssignProfile_OnClick(object sender, RoutedEventArgs args)
    {
        var state = _monitoring.GetSnapshot().Connection;
        if (state.CanAssign) OpenProfiles(state.Network);
    }

    private void OpenProfiles(NetworkIdentity? network)
    {
        try
        {
            var dialog = new ProfileManagerWindow(_monitoring.Profiles, network) { Owner = this };
            dialog.ProfilesChanged += (_, _) => _monitoring.RefreshRecognition();
            dialog.ShowDialog();
            _monitoring.RefreshRecognition();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Could not open connection profiles.\n\n{exception.Message}",
                "NetTrace profiles", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ViewHistory_OnClick(object sender, RoutedEventArgs args)
    {
        try
        {
            _monitoring.Flush();
            new ConnectionHistoryWindow(_monitoring.History) { Owner = this }.ShowDialog();
        }
        catch (Exception exception) { SessionMessageText.Text = $"Connection history could not be opened: {exception.Message}"; }
    }

    private void UpdateAdapterDetails(AdapterInfo? adapter)
    {
        AdapterNameText.Text = adapter?.Name ?? "No active adapter";
        AdapterTypeText.Text = adapter?.Type == "Wireless80211" ? "Wi-Fi" : adapter?.Type ?? "—";
        Ipv4Text.Text = adapter?.IPv4Address ?? "—";
        LinkRateLabel.Text = adapter?.Type switch
        {
            "Wireless80211" => "Wi-Fi link rate",
            "Ethernet" => "Ethernet link rate",
            _ => "Adapter link rate"
        };
        LinkSpeedText.Text = adapter is null ? "—" : FormatLinkRate(adapter.SpeedBitsPerSecond);
        LinkSpeedText.ToolTip = adapter?.Type == "Wireless80211"
            ? "The local Wi-Fi link between this PC and its router or hotspot. This is not measured internet speed."
            : "The local rate reported by the monitored adapter. This is not measured internet speed.";
    }

    private static string FormatLinkRate(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0) return "Unknown";
        var mbps = bitsPerSecond / 1_000_000d;
        return $"{mbps:0.##} Mbps";
    }

    private static void UpdateUsageCard(TextBlock total, TextBlock breakdown, long downloaded, long uploaded)
    {
        total.Text = ByteFormatter.Format(downloaded + uploaded);
        breakdown.Text = $"↓ {ByteFormatter.Format(downloaded)}   ↑ {ByteFormatter.Format(uploaded)}";
    }
}
