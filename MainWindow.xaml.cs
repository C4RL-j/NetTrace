using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NetTrace.Models;
using NetTrace.Presentation;
using NetTrace.Services;
using NetTrace.Utils;

namespace NetTrace;

// An application shell. Closing it never stops the application-owned monitor.
public partial class MainWindow : Window
{
    private readonly MonitoringService _monitoring;
    private readonly DispatcherTimer _timer;
    private readonly WifiRadioService _wifiRadio;
    private IWindowsStartupRegistration? _startupRegistration;
    private volatile bool _closed;
    private bool _loadingAdapters = true;
    private bool _chartInitialized;
    private IcmpProbeResult? _lastChartMeasurement;
    private IcmpQualitySummary? _qualitySummary;
    private InternetReachabilityResult? _lastReachabilityMeasurement;

    public MainWindow(MonitoringService monitoring, IWindowsStartupRegistration? startupRegistration = null,
        WifiRadioService? wifiRadio = null, IAppUsageTodayClient? appUsageClient = null)
    {
        ArgumentNullException.ThrowIfNull(monitoring);
        _monitoring = monitoring;
        _wifiRadio = wifiRadio ?? new WifiRadioService();
        _startupRegistration = startupRegistration;
        InitializeComponent();
        InitializeAppUsageToday(appUsageClient);
        _loadingAdapters = false;
        HistoryUsage7DButton.IsChecked = monitoring.SelectedHistoryUsageRange == HistoryUsageRange.SevenDays;
        HistoryUsage30DButton.IsChecked = monitoring.SelectedHistoryUsageRange == HistoryUsageRange.ThirtyDays;
        RefreshStartupSetting();
        DatabasePathText.Text = monitoring.DatabasePath;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_OnTick;
        Closed += OnClosed;
        IsVisibleChanged += OnVisibilityChanged;
        DashboardContent.IsVisibleChanged += DashboardContent_OnVisibilityChanged;
        HealthContent.IsVisibleChanged += HealthContent_OnVisibilityChanged;
        ConnectionsContent.IsVisibleChanged += ConnectionsContent_OnVisibilityChanged;
        HistoryContent.IsVisibleChanged += HistoryContent_OnVisibilityChanged;
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
        if (WindowState == WindowState.Minimized)
        {
            _timer.Stop();
            UpdateAppUsageVisibility();
            InvalidateWifiRadio();
        }
        else if (IsVisible)
        {
            UpdateAppUsageVisibility();
            RefreshDashboard();
            RefreshHistoryOverview();
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
        UpdateAppUsageVisibility();
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            RefreshDashboard();
            RefreshHistoryOverview();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            InvalidateWifiRadio();
        }
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

    private void ConnectionsContent_OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (!_closed && ConnectionsContent.IsVisible) RefreshDashboard();
        else InvalidateWifiRadio();
    }

    private void HistoryContent_OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (!_closed && HistoryContent.IsVisible) RefreshHistoryOverview();
    }

    private void RefreshHistoryOverview(bool refresh = true)
    {
        if (_closed || !IsVisible || WindowState == WindowState.Minimized || !HistoryContent.IsVisible) return;
        try
        {
            var page = _monitoring.GetHistoryPageData(refresh);
            ApplyHistoryOverview(page.Overview);
            ApplyHistoryUsageChart(page.Usage);
            HistoryMessageText.Text = "";
        }
        catch (Exception exception)
        {
            HistoryMessageText.Text = $"Connection history could not be loaded: {exception.Message}";
        }
    }

    private void HistoryUsageRange_OnChecked(object sender, RoutedEventArgs args)
    {
        if (_loadingAdapters || _closed) return;
        _monitoring.SelectedHistoryUsageRange = ReferenceEquals(sender, HistoryUsage30DButton)
            ? HistoryUsageRange.ThirtyDays : HistoryUsageRange.SevenDays;
        RefreshHistoryUsage();
    }

    private void RefreshHistoryUsage(bool refresh = true) => RefreshHistoryOverview(refresh);

    private void ApplyHistoryUsageChart(HistoryUsageChartData data)
    {
        HistoryUsageChart.UpdateData(data);
        HistoryUsageMessageText.Text = "";
    }

    private void ApplyHistoryOverview(HistoryOverview overview)
    {
        HistoryTodaySessionsText.Text = overview.TodaySessionCount.ToString();
        HistoryTodayConnectedTimeText.Text = overview.TodayConnectedTimeText;
        HistoryTodayDownloadText.Text = overview.TodayDownloadText;
        HistoryTodayUploadText.Text = overview.TodayUploadText;
        HistoryRecentSessions.ItemsSource = overview.RecentSessions;
        var hasSessions = overview.RecentSessions.Count > 0;
        HistoryTodayPanel.Visibility = hasSessions ? Visibility.Visible : Visibility.Collapsed;
        HistoryEmptyState.Visibility = hasSessions ? Visibility.Collapsed : Visibility.Visible;
        HistoryRecentSessions.Visibility = hasSessions ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshHealth(bool refreshChart = false)
    {
        if (_closed || !IsVisible || WindowState == WindowState.Minimized || !HealthContent.IsVisible) return;
        var snapshot = _monitoring.GetSnapshot();
        var route = VpnRoutePresentation.Create(snapshot.VpnRoute, snapshot.Connection.Network, snapshot.Adapters);
        HealthVpnStatusText.Text = route.VpnStatus;
        HealthVpnStatusText.ToolTip = route.DetailToolTip;
        HealthVpnStatusText.Foreground = StatusBrush(StatusPresentation.ForVpn(snapshot.VpnRoute.Status));
        var reachability = _monitoring.LatestInternetReachability;
        var reachabilityStatus = reachability?.Status ?? InternetReachabilityStatus.Checking;
        if (!ReferenceEquals(reachability, _lastReachabilityMeasurement))
        {
            InternetReachabilityText.Text = reachabilityStatus.ToString();
            _lastReachabilityMeasurement = reachability;
        }
        InternetReachabilityText.Foreground = StatusBrush(StatusPresentation.ForReachability(reachabilityStatus));
        // Read the application-owned worker's last completed cycle. Opening this
        // page never starts or restarts probes; the existing view timer refreshes it.
        var result = _monitoring.LatestIcmpMeasurement;
        if (refreshChart || !_chartInitialized || !ReferenceEquals(result, _lastChartMeasurement))
        {
            var samples = _monitoring.RollingIcmpSamples;
            IcmpStabilityChart.UpdateSamples(samples);
            IcmpPacketLossChart.UpdateData(IcmpStabilityChart.Data);
            _qualitySummary = IcmpQualityEvaluator.Evaluate(IcmpStabilityChart.Data.Samples);
            IcmpQualityText.Text = _qualitySummary.Quality.ToString();
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
        var quality = _qualitySummary?.Quality ?? IcmpQuality.Measuring;
        IcmpQualityText.Foreground = StatusBrush(StatusPresentation.ForIcmpQuality(quality));
        HealthDisclaimerText.Text = StatusPresentation.HealthExplanation(snapshot.VpnRoute.Status, reachabilityStatus, quality);
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
        _ = DisposeAppUsageTodayAsync();
        _timer.Stop();
        _timer.Tick -= Timer_OnTick;
        _monitoring.Changed -= Monitoring_OnChanged;
        _wifiRadio.Dispose();
        IsVisibleChanged -= OnVisibilityChanged;
        DashboardContent.IsVisibleChanged -= DashboardContent_OnVisibilityChanged;
        HealthContent.IsVisibleChanged -= HealthContent_OnVisibilityChanged;
        ConnectionsContent.IsVisibleChanged -= ConnectionsContent_OnVisibilityChanged;
        HistoryContent.IsVisibleChanged -= HistoryContent_OnVisibilityChanged;
        SettingsContent.IsVisibleChanged -= SettingsContent_OnVisibilityChanged;
        Closed -= OnClosed;
    }

    private void Monitoring_OnChanged(object? sender, EventArgs args)
    {
        if (_closed || Dispatcher.HasShutdownStarted) return;
        // Discard a pre-sleep association before any deferred UI work. This is
        // thread-safe and does not query WLAN or access a WPF control.
        if (_monitoring.GetSnapshot().IsSuspended) _wifiRadio.Invalidate();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_closed && IsVisible && WindowState != WindowState.Minimized)
            {
                RefreshDashboard();
                RefreshHistoryOverview();
            }
        }));
    }

    private void Timer_OnTick(object? sender, EventArgs args)
    {
        RefreshDashboard();
        _appUsageToday?.Tick();
    }

    public void RefreshDashboard()
    {
        if (_closed) return;
        if (HealthTab.IsSelected) RefreshHealth();
        RefreshHistoryOverview(refresh: false);
        var snapshot = _monitoring.GetSnapshot();
        RefreshConnections(snapshot);
        ApplyLiveSpeed(snapshot.DownloadBytesPerSecond, snapshot.UploadBytesPerSecond);
        DownloadPeakText.Text = $"Peak today ↓ {ByteFormatter.FormatRate(snapshot.PeakDownloadBytesPerSecond)}";
        UploadPeakText.Text = $"Peak today ↑ {ByteFormatter.FormatRate(snapshot.PeakUploadBytesPerSecond)}";
        DownloadSessionText.Text = $"App session ↓ {ByteFormatter.Format(snapshot.SessionDownloaded)}";
        UploadSessionText.Text = $"App session ↑ {ByteFormatter.Format(snapshot.SessionUploaded)}";
        StatusText.Text = snapshot.Status;
        var monitoringBrush = StatusBrush(StatusPresentation.ForMonitoring(snapshot));
        StatusDot.Fill = monitoringBrush;
        StatusText.Foreground = monitoringBrush;
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

    private void ApplyLiveSpeed(double downloadBytesPerSecond, double uploadBytesPerSecond)
    {
        var download = LiveSpeedPresentation.Create(downloadBytesPerSecond);
        var upload = LiveSpeedPresentation.Create(uploadBytesPerSecond);
        // One snapshot and one UI cycle supply both formats for each direction.
        DownloadRateText.Text = download.ByteRateText;
        DownloadMbpsText.Text = download.MbpsText;
        UploadRateText.Text = upload.ByteRateText;
        UploadMbpsText.Text = upload.MbpsText;
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
            var dialog = new ProfileManagerWindow(_monitoring.Profiles, network, _monitoring) { Owner = this };
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
        catch (Exception exception) { HistoryMessageText.Text = $"Connection history could not be opened: {exception.Message}"; }
    }

    private void RefreshConnections(MonitoringSnapshot snapshot)
    {
        if (_closed || !IsVisible || WindowState == WindowState.Minimized || !ConnectionsContent.IsVisible) return;
        var route = VpnRoutePresentation.Create(snapshot.VpnRoute, snapshot.Connection.Network, snapshot.Adapters);
        ConnectionsVpnStatusText.Text = route.VpnStatus;
        ConnectionsVpnStatusText.ToolTip = route.DetailToolTip;
        ConnectionsVpnStatusText.Foreground = StatusBrush(StatusPresentation.ForVpn(snapshot.VpnRoute.Status));
        VpnTransportText.Text = route.VpnTransport;
        VpnTransportText.ToolTip = route.TransportToolTip;
        VpnTransportText.Foreground = StatusBrush(StatusPresentation.ForVpnTransport(snapshot.VpnRoute.TransportFamily));
        IPv4RouteText.Text = route.IPv4Route;
        IPv4RouteText.ToolTip = route.DetailToolTip;
        IPv4RouteText.Foreground = StatusBrush(StatusPresentation.ForRoute(snapshot.VpnRoute.IPv4RouteKind));
        IPv6RouteText.Text = route.IPv6Route;
        IPv6RouteText.ToolTip = route.DetailToolTip;
        IPv6RouteText.Foreground = StatusBrush(StatusPresentation.ForRoute(snapshot.VpnRoute.IPv6RouteKind));
        SetDetail(PhysicalAdapterText, route.PhysicalAdapter);
        _loadingAdapters = true;
        try
        {
            if (!ReferenceEquals(AdapterComboBox.ItemsSource, snapshot.Adapters))
                AdapterComboBox.ItemsSource = snapshot.Adapters;
            AdapterComboBox.SelectedItem = snapshot.SelectedAdapter;
        }
        finally { _loadingAdapters = false; }
        var details = AdapterDetailsPresentation.Create(snapshot.SelectedAdapter, snapshot.SelectedAdapterDetails, snapshot.Connection);
        SetDetail(AdapterNameText, details.Adapter);
        SetDetail(AdapterStatusText, details.Status);
        AdapterStatusText.Foreground = StatusBrush(StatusPresentation.ForAdapter(details.Status));
        SetDetail(AdapterNetworkText, details.Network);
        SetDetail(AdapterProfileText, details.Profile);
        SetDetail(AdapterTypeText, details.Type);
        SetDetail(Ipv4Text, details.IPv4);
        SetDetail(Ipv6Text, details.IPv6);
        Ipv6Text.ToolTip = $"{details.IPv6}\nA usable global or unique-local IPv6 address. Link-local-only IPv6 is shown as Unavailable.";
        SetDetail(DefaultGatewayText, details.DefaultGateway);
        SetDetail(DnsText, details.Dns);
        LinkRateLabel.Text = details.LinkRateLabel;
        LinkSpeedText.Text = details.LinkRate;
        LinkSpeedText.ToolTip = details.LinkRateToolTip;
        RefreshWifiRadio(snapshot);
    }

    private void RefreshWifiRadio(MonitoringSnapshot snapshot)
    {
        WifiRadioSnapshot? radio = null;
        if (snapshot.IsSuspended) _wifiRadio.Invalidate();
        else radio = _wifiRadio.RequestSnapshot(snapshot.SelectedAdapter, snapshot.SelectedAdapterDetails);
        var presentation = WifiRadioPresentation.Create(snapshot.SelectedAdapter, snapshot.SelectedAdapterDetails, radio);
        WifiRadioSection.Visibility = presentation.Show ? Visibility.Visible : Visibility.Collapsed;
        SetDetail(WifiSignalQualityText, presentation.SignalQuality);
        SetDetail(WifiRssiText, presentation.Rssi);
        SetDetail(WifiChannelText, presentation.Channel);
        SetDetail(WifiBandFrequencyText, presentation.BandFrequency);
        SetDetail(WifiBssidText, presentation.Bssid);
        SetDetail(WifiPhyText, presentation.Phy);
        SetDetail(WifiReceiveLinkRateText, presentation.ReceiveLinkRate);
        SetDetail(WifiTransmitLinkRateText, presentation.TransmitLinkRate);
        var generalRate = presentation.Show && presentation.HasSeparateLinkRates ? Visibility.Collapsed : Visibility.Visible;
        LinkRateLabelPanel.Visibility = generalRate;
        LinkRateLabel.Visibility = generalRate;
        LinkRateHelp.Visibility = generalRate;
        LinkSpeedText.Visibility = generalRate;
    }

    private void InvalidateWifiRadio()
    {
        _wifiRadio.Invalidate();
        WifiRadioSection.Visibility = Visibility.Collapsed;
    }

    private static void SetDetail(TextBlock control, string value)
    {
        control.Text = value;
        control.ToolTip = value;
    }

    private Brush StatusBrush(string resourceKey) => (Brush)FindResource(resourceKey);

    private static void UpdateUsageCard(TextBlock total, TextBlock breakdown, long downloaded, long uploaded)
    {
        total.Text = ByteFormatter.Format(downloaded + uploaded);
        breakdown.Text = $"↓ {ByteFormatter.Format(downloaded)}   ↑ {ByteFormatter.Format(uploaded)}";
    }
}
