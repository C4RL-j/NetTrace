using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NetTrace.Models;
using NetTrace.Services;
using NetTrace.Utils;

namespace NetTrace;

// A dashboard view. Closing it never stops the application-owned monitor.
public partial class MainWindow : Window
{
    private readonly MonitoringService _monitoring;
    private readonly DispatcherTimer _timer;
    private volatile bool _closed;
    private bool _loadingAdapters = true;

    public MainWindow(MonitoringService monitoring)
    {
        ArgumentNullException.ThrowIfNull(monitoring);
        _monitoring = monitoring;
        InitializeComponent();
        DatabasePathText.Text = monitoring.DatabasePath;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_OnTick;
        Closed += OnClosed;
        IsVisibleChanged += OnVisibilityChanged;
        RefreshDashboard();
        _monitoring.Changed += Monitoring_OnChanged;
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (_closed) return;
        if (IsVisible)
        {
            RefreshDashboard();
            _timer.Start();
        }
        else _timer.Stop();
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        _closed = true;
        _timer.Stop();
        _timer.Tick -= Timer_OnTick;
        _monitoring.Changed -= Monitoring_OnChanged;
        IsVisibleChanged -= OnVisibilityChanged;
        Closed -= OnClosed;
    }

    private void Monitoring_OnChanged(object? sender, EventArgs args)
    {
        if (_closed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_closed && IsVisible) RefreshDashboard();
        }));
    }

    private void Timer_OnTick(object? sender, EventArgs args) => RefreshDashboard();

    public void RefreshDashboard()
    {
        if (_closed) return;
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
        DownloadSessionText.Text = $"App session: {ByteFormatter.Format(snapshot.SessionDownloaded)}";
        UploadSessionText.Text = $"App session: {ByteFormatter.Format(snapshot.SessionUploaded)}";
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
        AdapterTypeText.Text = adapter?.Type ?? "—";
        Ipv4Text.Text = adapter?.IPv4Address ?? "—";
        LinkSpeedText.Text = adapter is null ? "—" : FormatLinkSpeed(adapter.SpeedBitsPerSecond);
    }

    private static string FormatLinkSpeed(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0) return "Unknown";
        var mbps = bitsPerSecond / 1_000_000d;
        return mbps >= 1000 ? $"{mbps / 1000d:0.##} Gbps" : $"{mbps:0.##} Mbps";
    }

    private static void UpdateUsageCard(TextBlock total, TextBlock breakdown, long downloaded, long uploaded)
    {
        total.Text = ByteFormatter.Format(downloaded + uploaded);
        breakdown.Text = $"↓ {ByteFormatter.Format(downloaded)}   ↑ {ByteFormatter.Format(uploaded)}";
    }
}
