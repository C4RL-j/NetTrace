using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NetTrace.Presentation;
using NetTrace.Services;
using NetTrace.Utils;

namespace NetTrace;

// A visible projection of the application-owned monitor. No sampling or timer is owned here.
public partial class TrayPopupWindow : Window
{
    private readonly MonitoringService _monitoring;
    private volatile bool _observing;
    private volatile bool _closed;
    private bool _closing;
    private int _refreshQueued;

    public TrayPopupWindow(MonitoringService monitoring)
    {
        ArgumentNullException.ThrowIfNull(monitoring);
        _monitoring = monitoring;
        InitializeComponent();
        IsVisibleChanged += OnVisibilityChanged;
        Closing += (_, _) => _closing = true;
        Closed += OnClosed;
        Deactivated += (_, _) =>
        {
            if (!_closed && !_closing) Close();
        };
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            Close();
        };
    }

    public void RefreshSnapshot()
    {
        if (_closed || !IsVisible) return;
        var snapshot = _monitoring.GetSnapshot();
        DownloadSpeedText.Text = ByteRateFormatter.Format(snapshot.DownloadBytesPerSecond);
        UploadSpeedText.Text = ByteRateFormatter.Format(snapshot.UploadBytesPerSecond);
        var brush = (Brush)FindResource(StatusPresentation.ForMonitoring(snapshot));
        MonitoringStatusDot.Fill = brush;
        MonitoringStatusText.Foreground = brush;
        MonitoringStatusText.Text = snapshot.Status;

        var vpn = snapshot.VpnRoute;
        VpnKeyIndicator.Visibility = vpn.HasActiveTunnel ? Visibility.Visible : Visibility.Collapsed;
        VpnKeyTooltipText.Text = "VPN Connected" + string.Concat(vpn.ConnectedTunnelNames
            .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => Environment.NewLine + name));
        if (!vpn.HasActiveTunnel) VpnKeyToolTip.IsOpen = false;

        var samples = snapshot.TrafficSamples;
        var endUtc = samples.Count == 0 ? DateTimeOffset.UtcNow : samples[^1].TimestampUtc;
        TrafficChart.UpdateData(DashboardTrafficChartData.Create(samples, endUtc, 60));

        try
        {
            var totals = _monitoring.GetUsageTotals();
            var today = totals.Today;
            TodayTotalText.Text = ByteFormatter.Format(today.Downloaded + today.Uploaded);
            TodayDownloadText.Text = $"Download {ByteFormatter.Format(today.Downloaded)}";
            TodayUploadText.Text = $"Upload {ByteFormatter.Format(today.Uploaded)}";
            TodayUsage.ToolTip = null;
        }
        catch (Exception exception)
        {
            TodayTotalText.Text = "Unavailable";
            TodayDownloadText.Text = "Download unavailable";
            TodayUploadText.Text = "Upload unavailable";
            TodayUsage.ToolTip = "Usage history could not be read.";
            Trace.TraceError($"Tray usage history could not be read: {exception.Message}");
        }
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (_closed) return;
        if (IsVisible)
        {
            if (!_observing)
            {
                _observing = true;
                _monitoring.Changed += MonitoringOnChanged;
                _monitoring.TrafficSampled += MonitoringOnChanged;
            }
            RefreshSnapshot();
        }
        else StopObserving();
    }

    private void MonitoringOnChanged(object? sender, EventArgs args)
    {
        if (!_observing || _closed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished
            || Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (!_closed && IsVisible) RefreshSnapshot();
        }));
    }

    private void StopObserving()
    {
        if (!_observing) return;
        _observing = false;
        _monitoring.Changed -= MonitoringOnChanged;
        _monitoring.TrafficSampled -= MonitoringOnChanged;
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        _closed = true;
        StopObserving();
    }
}
