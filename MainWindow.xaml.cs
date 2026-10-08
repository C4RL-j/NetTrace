using System.Diagnostics;
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
    private readonly ProfileStore _profiles;
    private readonly ConnectionProfileMonitor _profileMonitor;
    private volatile bool _closing;
    private bool _loadingAdapters;
    private NetworkIdentity? _lastPhysicalNetwork;
    private string? _lastDetectionMessage;
    private ConnectionStatus? _lastConnectionStatus;

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
        : this(store, new NetworkIdentityService())
    {
    }

    public MainWindow(UsageStore store, INetworkIdentityService identityService)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _profiles = new ProfileStore(store);
        _profileMonitor = new ConnectionProfileMonitor(identityService, new ProfileResolver(_profiles));
        InitializeComponent();

        DatabasePathText.Text = _store.DatabasePath;
        LoadAdapters();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += Timer_OnTick;

        Closed += (_, _) =>
        {
            _closing = true;
            _timer.Stop();
            try { FlushPending(); }
            finally { _profileMonitor.Dispose(); }
        };
        RefreshTotals();
        _profileMonitor.StateChanged += ProfileMonitor_OnStateChanged;
        _profileMonitor.Start();
        _timer.Start();
    }

    private void ProfileMonitor_OnStateChanged(object? sender, EventArgs e)
    {
        if (_closing || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing) return;
            var state = _profileMonitor.Current;
            if (state.Network != _lastPhysicalNetwork || state.Message != _lastDetectionMessage
                || state.Status != _lastConnectionStatus || _selectedAdapter is null)
            {
                _lastPhysicalNetwork = state.Network;
                _lastDetectionMessage = state.Message;
                _lastConnectionStatus = state.Status;
                try { LoadAdapters(); }
                catch (System.Net.NetworkInformation.NetworkInformationException exception)
                {
                    Trace.TraceWarning($"Could not refresh network adapters: {exception.Message}");
                }
            }
            CurrentProfileText.Text = state.Status switch
            {
                ConnectionStatus.ConnectedRecognized => state.Profile!.DisplayName,
                ConnectionStatus.ConnectedUnassigned => "Unassigned network",
                ConnectionStatus.ConnectedIdentityUnavailable => "Network identity unavailable",
                ConnectionStatus.Disconnected => "Disconnected",
                _ => "Detecting connection…"
            };
            CurrentProfileText.ToolTip = CurrentProfileText.Text;
            CurrentNetworkText.Text = state.Network is null
                ? state.Message ?? "No physical network connected."
                : state.Network.DisplayName;
            AssignProfileButton.Visibility = state.CanAssign
                ? Visibility.Visible : Visibility.Collapsed;
        }));
    }

    private void ManageProfiles_OnClick(object sender, RoutedEventArgs e) => OpenProfiles(null);

    private void AssignProfile_OnClick(object sender, RoutedEventArgs e)
    {
        var state = _profileMonitor.Current;
        if (state.CanAssign) OpenProfiles(state.Network);
    }

    private void OpenProfiles(NetworkIdentity? network)
    {
        try
        {
            var dialog = new ProfileManagerWindow(_profiles, network) { Owner = this };
            dialog.ProfilesChanged += (_, _) => _profileMonitor.Refresh();
            dialog.ShowDialog();
            _profileMonitor.Refresh();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Could not open connection profiles.\n\n{exception.Message}",
                "NetTrace profiles", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadAdapters()
    {
        var adapters = _monitor.GetActiveAdapters();
        var selected = adapters.FirstOrDefault(adapter => adapter.Id == _selectedAdapter?.Id)
            ?? adapters.FirstOrDefault();
        _loadingAdapters = true;
        try
        {
            AdapterComboBox.ItemsSource = adapters;
            AdapterComboBox.SelectedItem = selected;
        }
        finally { _loadingAdapters = false; }

        if (selected?.Id != _selectedAdapter?.Id)
        {
            FlushPending();
            _previousSnapshot = selected is null ? null : _monitor.Capture(selected.Id);
        }
        _selectedAdapter = selected;
        UpdateAdapterDetails();
        if (selected is null) SetOffline("No active network adapter");
    }

    private void AdapterComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingAdapters) return;
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
