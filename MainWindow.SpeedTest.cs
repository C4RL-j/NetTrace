using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using NetTrace.Models;
using NetTrace.Services;

namespace NetTrace;

public partial class MainWindow
{
    private IDownloadSpeedTest? _downloadSpeedTest;
    private Func<long, bool>? _confirmDownloadSpeedTest;
    private volatile CancellationTokenSource? _downloadSpeedTestCancellation;
    private volatile string? _downloadSpeedTestAdapterId;
    private volatile ConnectionSession? _downloadSpeedTestSession;
    private DownloadSpeedTestSnapshot _downloadSpeedTestSnapshot =
        new(DownloadSpeedTestState.Idle, 0, 25 * DownloadSpeedTest.Megabyte, 0, TimeSpan.Zero);

    private void InitializeDownloadSpeedTest(IDownloadSpeedTest? service, Func<long, bool>? confirmation)
    {
        _downloadSpeedTest = service ?? new DownloadSpeedTest();
        _confirmDownloadSpeedTest = confirmation ?? ConfirmDownloadSpeedTest;
        ApplyDownloadSpeedTestSnapshot(_downloadSpeedTestSnapshot);
    }

    private long SelectedDownloadSpeedTestLimit() =>
        Convert.ToInt64(((ComboBoxItem)SpeedTestLimitSelector.SelectedItem).Tag, CultureInfo.InvariantCulture)
            * DownloadSpeedTest.Megabyte;

    private bool ConfirmDownloadSpeedTest(long byteLimit) => MessageBox.Show(this,
        $"This test consumes real internet data: up to {byteLimit / DownloadSpeedTest.Megabyte} MB of download data, plus network overhead.\n\n" +
        "All speed-test traffic remains included in NetTrace's normal usage totals. You can cancel at any time.\n\nStart the download?",
        "Speed Test — real data usage", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
        == MessageBoxResult.Yes;

    private void SpeedTestLimitSelector_OnSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_downloadSpeedTest is not null && _downloadSpeedTestSnapshot.State == DownloadSpeedTestState.Idle)
            ApplyDownloadSpeedTestSnapshot(_downloadSpeedTestSnapshot with { ByteLimit = SelectedDownloadSpeedTestLimit() });
    }

    private async void SpeedTestActionButton_OnClick(object sender, RoutedEventArgs args)
    {
        if (_downloadSpeedTestCancellation is { } active)
        {
            // Cancel the actual I/O first. Never wait for the next UI refresh.
            try { active.Cancel(); }
            catch (ObjectDisposedException) { }
            SpeedTestActionButton.IsEnabled = false;
            SpeedTestStatusText.Text = "Stopping…";
            return;
        }

        var byteLimit = SelectedDownloadSpeedTestLimit();
        if (!CanStartDownloadSpeedTest(_monitoring.GetSnapshot(), byteLimit)) return;
        if (!_confirmDownloadSpeedTest!(byteLimit) || _closed) return;
        // Establish the normal counter baseline even if the first one-second
        // monitoring tick has not happened yet. Payload progress never writes usage.
        _monitoring.SampleNow();
        var monitoring = _monitoring.GetSnapshot();
        if (!CanStartDownloadSpeedTest(monitoring, byteLimit)) return;

        var cancellation = new CancellationTokenSource();
        _downloadSpeedTestAdapterId = monitoring.SelectedAdapter!.Id;
        _downloadSpeedTestSession = monitoring.Session;
        _downloadSpeedTestCancellation = cancellation;
        ApplyDownloadSpeedTestSnapshot(new(DownloadSpeedTestState.Running, 0, byteLimit, 0, TimeSpan.Zero));
        var progress = new Progress<DownloadSpeedTestSnapshot>(snapshot =>
        {
            if (_closed || Dispatcher.HasShutdownStarted) return;
            // Always dispatch explicitly: callers need not have a synchronization
            // context, and a queued reading must not overwrite a final result.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_closed && ReferenceEquals(_downloadSpeedTestCancellation, cancellation) &&
                    !cancellation.IsCancellationRequested && snapshot.State == DownloadSpeedTestState.Running &&
                    _downloadSpeedTestSnapshot.State == DownloadSpeedTestState.Running)
                    ApplyDownloadSpeedTestSnapshot(snapshot);
            }));
        });
        DownloadSpeedTestSnapshot? result = null;
        Exception? failure = null;
        var wasCancelled = false;
        try
        {
            result = await _downloadSpeedTest!.RunAsync(byteLimit, progress, cancellation.Token);
        }
        catch (Exception exception)
        {
            failure = exception;
            wasCancelled = cancellation.IsCancellationRequested;
        }
        finally
        {
            if (ReferenceEquals(_downloadSpeedTestCancellation, cancellation))
            {
                _downloadSpeedTestCancellation = null;
                _downloadSpeedTestAdapterId = null;
                _downloadSpeedTestSession = null;
            }
            cancellation.Dispose();
            if (!_closed) await Dispatcher.InvokeAsync(() =>
            {
                if (_closed) return;
                ApplyDownloadSpeedTestSnapshot(result ?? _downloadSpeedTestSnapshot with
                {
                    State = wasCancelled ? DownloadSpeedTestState.Cancelled : DownloadSpeedTestState.Error,
                    ErrorMessage = failure?.Message
                });
                SpeedTestActionButton.IsEnabled = true;
                SpeedTestLimitSelector.IsEnabled = true;
                // Include a fast transfer's last bytes through the same ordinary
                // sampler, without waiting for another tick or counting twice.
                _monitoring.SampleNow();
                RefreshDashboard();
            });
        }
    }

    private bool CanStartDownloadSpeedTest(MonitoringSnapshot snapshot, long byteLimit)
    {
        if (snapshot.IsRunning && !snapshot.IsSuspended && snapshot.SelectedAdapter is not null &&
            snapshot.Session is not null && string.Equals(snapshot.Connection.Network?.AdapterId,
                snapshot.SelectedAdapter.Id, StringComparison.OrdinalIgnoreCase)) return true;
        ApplyDownloadSpeedTestSnapshot(new(DownloadSpeedTestState.Error, 0, byteLimit, 0, TimeSpan.Zero,
            "Monitor the current physical connection's adapter before starting so speed-test traffic is counted."));
        return false;
    }

    private void CancelDownloadSpeedTestIfMonitoringChanged(MonitoringSnapshot snapshot)
    {
        var cancellation = _downloadSpeedTestCancellation;
        if (cancellation is null) return;
        if (snapshot.IsRunning && !snapshot.IsSuspended &&
            string.Equals(snapshot.SelectedAdapter?.Id, _downloadSpeedTestAdapterId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(snapshot.Connection.Network?.AdapterId, _downloadSpeedTestAdapterId, StringComparison.OrdinalIgnoreCase) &&
            snapshot.Session?.Id == _downloadSpeedTestSession?.Id) return;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void ApplyDownloadSpeedTestSnapshot(DownloadSpeedTestSnapshot snapshot)
    {
        _downloadSpeedTestSnapshot = snapshot;
        var idle = snapshot.State == DownloadSpeedTestState.Idle;
        var running = snapshot.State == DownloadSpeedTestState.Running;
        SpeedTestRateText.Text = idle ? "Test your download speed" :
            running || snapshot.HasMeasurement ? $"{snapshot.MegabitsPerSecond:0.0} Mbps" : "— Mbps";
        SpeedTestRateText.FontSize = idle ? 10.5 : 13;
        SpeedTestRateText.FontWeight = idle ? FontWeights.Normal : FontWeights.SemiBold;
        SpeedTestRateText.Foreground = idle ? (System.Windows.Media.Brush)FindResource("SubtleBrush") : Foreground;
        SpeedTestDataText.Text = idle ? "" : running
            ? $"{snapshot.BytesReceived / (double)DownloadSpeedTest.Megabyte:0.0} / {snapshot.ByteLimit / DownloadSpeedTest.Megabyte} MB"
            : $"{snapshot.BytesReceived / (double)DownloadSpeedTest.Megabyte:0.0} MB used";
        SpeedTestStatusText.Text = snapshot.State switch
        {
            DownloadSpeedTestState.Running => snapshot.BytesReceived == 0 ? "Connecting…" : "Testing…",
            DownloadSpeedTestState.Completed => "Completed",
            DownloadSpeedTestState.Cancelled => snapshot.HasMeasurement ? "Cancelled · Partial test" : "Cancelled",
            DownloadSpeedTestState.Error => snapshot.HasMeasurement ? "Error · Partial result" : "Error",
            _ => ""
        };
        SpeedTestStatusText.ToolTip = snapshot.ErrorMessage ??
            (snapshot.State == DownloadSpeedTestState.Cancelled && !snapshot.HasMeasurement
                ? "Stopped before enough data was measured. All consumed data remains counted." : null);
        SpeedTestActionButton.Content = running ? "Cancel" : idle ? "Start Test" : "Test Again";
        SpeedTestActionButton.IsEnabled = true;
        SpeedTestLimitSelector.IsEnabled = !running;
        SpeedTestProgress.Visibility = running ? Visibility.Visible : Visibility.Hidden;
        SpeedTestProgress.Value = snapshot.ByteLimit > 0 ? Math.Clamp(snapshot.BytesReceived * 100d / snapshot.ByteLimit, 0, 100) : 0;
    }

    private void DisposeDownloadSpeedTest()
    {
        try { _downloadSpeedTestCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        _downloadSpeedTest?.Dispose();
    }
}
