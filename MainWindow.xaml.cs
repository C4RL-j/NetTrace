using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NetTrace.Models;
using NetTrace.Services;
using NetTrace.Utils;

namespace NetTrace;

public partial class MainWindow : Window
{
    private readonly NetworkMonitor _monitor = new();
    private readonly UsageStore _store;
    private readonly DispatcherTimer _timer;

    private AdapterInfo? _selectedAdapter;
    private NetworkSnapshot? _previousSnapshot;

    private long _sessionDownloaded;
    private long _sessionUploaded;
    private long _pendingDownloaded;
    private long _pendingUploaded;

    private DateTimeOffset _lastFlush = DateTimeOffset.Now;
    private DateOnly _pendingDay = DateOnly.FromDateTime(DateTime.Now);

    public MainWindow()
        : this(new UsageStore())
    {
    }

    public MainWindow(UsageStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        InitializeComponent();

        DatabasePathText.Text = _store.DatabasePath;
        LoadAdapters();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += Timer_OnTick;

        Closed += (_, _) => FlushPending();
        RefreshTotals();
        _timer.Start();
    }

    private void LoadAdapters()
    {
        var adapters = _monitor.GetActiveAdapters();
        AdapterComboBox.ItemsSource = adapters;

        if (adapters.Count > 0)
        {
            AdapterComboBox.SelectedIndex = 0;
        }
        else
        {
            SetOffline("No active network adapter");
        }
    }

    private void AdapterComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        FlushPending();

        _selectedAdapter = AdapterComboBox.SelectedItem as AdapterInfo;
        _previousSnapshot = _selectedAdapter is null
            ? null
            : _monitor.Capture(_selectedAdapter.Id);

        UpdateAdapterDetails();
    }

    private void Timer_OnTick(object? sender, EventArgs e)
    {
        if (_selectedAdapter is null)
        {
            SetOffline("No adapter selected");
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        if (today != _pendingDay)
        {
            FlushPending();
            _pendingDay = today;
        }

        var current = _monitor.Capture(_selectedAdapter.Id);
        if (current is null)
        {
            SetOffline("Adapter disconnected");
            _previousSnapshot = null;
            return;
        }

        SetOnline();

        if (_previousSnapshot is not null)
        {
            var elapsedSeconds = Math.Max(
                0.001,
                (current.Timestamp - _previousSnapshot.Timestamp).TotalSeconds);

            var receivedDelta = current.BytesReceived - _previousSnapshot.BytesReceived;
            var sentDelta = current.BytesSent - _previousSnapshot.BytesSent;

            // A negative delta means the Windows adapter counter reset/restarted.
            if (receivedDelta < 0) receivedDelta = 0;
            if (sentDelta < 0) sentDelta = 0;

            _sessionDownloaded += receivedDelta;
            _sessionUploaded += sentDelta;
            _pendingDownloaded += receivedDelta;
            _pendingUploaded += sentDelta;

            DownloadRateText.Text = ByteFormatter.FormatRate(receivedDelta / elapsedSeconds);
            UploadRateText.Text = ByteFormatter.FormatRate(sentDelta / elapsedSeconds);
            DownloadSessionText.Text = $"Session: {ByteFormatter.Format(_sessionDownloaded)}";
            UploadSessionText.Text = $"Session: {ByteFormatter.Format(_sessionUploaded)}";
        }

        _previousSnapshot = current;

        if (DateTimeOffset.Now - _lastFlush >= TimeSpan.FromSeconds(30))
            FlushPending();

        RefreshTotals();
    }

    private void FlushPending()
    {
        if (_pendingDownloaded <= 0 && _pendingUploaded <= 0)
        {
            _lastFlush = DateTimeOffset.Now;
            return;
        }

        _store.AddUsage(_pendingDay, _pendingDownloaded, _pendingUploaded);
        _pendingDownloaded = 0;
        _pendingUploaded = 0;
        _lastFlush = DateTimeOffset.Now;
    }

    private void RefreshTotals()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);

        var todayStored = _store.GetTotals(today, today);
        var weekStored = _store.GetTotals(today.AddDays(-6), today);
        var monthStored = _store.GetTotals(today.AddDays(-29), today);

        UpdateUsageCard(
            TodayTotalText,
            TodayBreakdownText,
            todayStored.Downloaded + _pendingDownloaded,
            todayStored.Uploaded + _pendingUploaded);

        UpdateUsageCard(
            WeekTotalText,
            WeekBreakdownText,
            weekStored.Downloaded + _pendingDownloaded,
            weekStored.Uploaded + _pendingUploaded);

        UpdateUsageCard(
            MonthTotalText,
            MonthBreakdownText,
            monthStored.Downloaded + _pendingDownloaded,
            monthStored.Uploaded + _pendingUploaded);
    }

    private static void UpdateUsageCard(TextBlock totalText, TextBlock breakdownText, long downloaded, long uploaded)
    {
        totalText.Text = ByteFormatter.Format(downloaded + uploaded);
        breakdownText.Text = $"↓ {ByteFormatter.Format(downloaded)}   ↑ {ByteFormatter.Format(uploaded)}";
    }

    private void UpdateAdapterDetails()
    {
        if (_selectedAdapter is null)
        {
            AdapterNameText.Text = "No active adapter";
            AdapterTypeText.Text = "—";
            Ipv4Text.Text = "—";
            LinkSpeedText.Text = "—";
            return;
        }

        AdapterNameText.Text = _selectedAdapter.Name;
        AdapterTypeText.Text = _selectedAdapter.Type;
        Ipv4Text.Text = _selectedAdapter.IPv4Address;
        LinkSpeedText.Text = FormatLinkSpeed(_selectedAdapter.SpeedBitsPerSecond);
    }

    private static string FormatLinkSpeed(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0)
            return "Unknown";

        var mbps = bitsPerSecond / 1_000_000d;
        return mbps >= 1000
            ? $"{mbps / 1000d:0.##} Gbps"
            : $"{mbps:0.##} Mbps";
    }

    private void SetOnline()
    {
        StatusText.Text = "Monitoring";
        StatusDot.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));
    }

    private void SetOffline(string message)
    {
        StatusText.Text = message;
        StatusDot.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
        DownloadRateText.Text = "0 B/s";
        UploadRateText.Text = "0 B/s";
    }
}
