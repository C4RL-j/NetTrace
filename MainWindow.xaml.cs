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
    private readonly ConnectionHistoryStore _history;
    private readonly ConnectionSessionTracker _sessions;
    private readonly VpnSessionMonitor? _vpnSessions;
    private readonly DatabaseRunLease _runLease;
    private readonly WindowsPowerLifecycle _powerLifecycle;
    private volatile bool _closing;
    private bool _suspended;
    private bool _sessionPersistenceFailed;
    private bool _persistNextObservation;
    private string? _usagePersistenceMessage;
    private long _lastHandledObservationSequence;
    private long _sampleSequence;
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

    public static MainWindow CreateForDataRoot(string localApplicationDataDirectory, INetworkIdentityService? identityService = null)
    {
        DatabaseRunLease? lease = null;
        var ownershipTransferred = false;
        try
        {
            lease = new DatabaseRunLease(UsageStore.GetDatabasePath(localApplicationDataDirectory));
            var store = new UsageStore(localApplicationDataDirectory, lease);
            ownershipTransferred = true;
            return new MainWindow(store, identityService ?? new NetworkIdentityService(), lease);
        }
        catch
        {
            try
            {
                if (!ownershipTransferred) identityService?.Dispose();
            }
            catch (Exception cleanupException)
            {
                Trace.TraceWarning($"Could not clean up network identity detection after startup failure: {cleanupException.Message}");
            }
            finally { lease?.Dispose(); }
            throw;
        }
    }

    public MainWindow(UsageStore store, INetworkIdentityService identityService, DatabaseRunLease? runLease = null,
        VpnRouteVerifier? vpnVerifier = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(identityService);
        _store = store;
        try
        {
            _runLease = runLease ?? new DatabaseRunLease(store.DatabasePath);
            _runLease.EnsureOwns(store.DatabasePath);
            _history = new ConnectionHistoryStore(store);
            _sessions = new ConnectionSessionTracker(_history);
            _vpnSessions = new VpnSessionMonitor(_sessions, new VpnSessionStore(store), vpnVerifier);
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
            _powerLifecycle = new WindowsPowerLifecycle(this);
            _powerLifecycle.Suspending += (_, _) => SuspendMonitoring();
            _powerLifecycle.Resuming += (_, _) => ResumeMonitoring();
            RefreshTotals();
            _profileMonitor.ObservationReceived += ProfileMonitor_OnObservationReceived;
            _profileMonitor.Start();
            _vpnSessions.Start();
            _timer.Start();
            Closed += (_, _) => CompleteRun();
        }
        catch
        {
            _vpnSessions?.Dispose();
            _timer?.Stop();
            try { _powerLifecycle?.Dispose(); }
            finally
            {
                try
                {
                    if (_profileMonitor is not null) _profileMonitor.Dispose();
                    else identityService.Dispose();
                }
                finally { _runLease?.Dispose(); }
            }
            throw;
        }
    }

    private void ProfileMonitor_OnObservationReceived(object? sender, ConnectionObservationEventArgs observation)
    {
        if (_closing || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing || _suspended || observation.Sequence <= _lastHandledObservationSequence) return;
            _lastHandledObservationSequence = observation.Sequence;
            var state = observation.State;
            var previousSessionId = _sessions.Current?.Id;
            var physicalChanged = !SamePhysicalNetwork(state.Network, _lastPhysicalNetwork);
            if (physicalChanged) _previousSnapshot = null;
            UpdateSessionHistory(() =>
            {
                _sessions.ObserveConnection(state, observation.ObservedAtUtc);
                if (_persistNextObservation)
                {
                    _sessions.Flush();
                    _persistNextObservation = false;
                }
            });
            _vpnSessions?.Refresh();
            if (state.Network != _lastPhysicalNetwork || state.Message != _lastDetectionMessage
                || state.Status != _lastConnectionStatus || _selectedAdapter is null)
            {
                _lastPhysicalNetwork = state.Network;
                _lastDetectionMessage = state.Message;
                _lastConnectionStatus = state.Status;
                try { LoadAdapters(physicalChanged ? state.Network?.AdapterId : null); }
                catch (System.Net.NetworkInformation.NetworkInformationException exception)
                {
                    Trace.TraceWarning($"Could not refresh network adapters: {exception.Message}");
                }
            }
            // A new SSID on the same adapter also needs a fresh counter baseline.
            if (physicalChanged || previousSessionId != _sessions.Current?.Id) _previousSnapshot = null;
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
            RefreshCurrentSession();
        }));
    }

    private static bool SamePhysicalNetwork(NetworkIdentity? left, NetworkIdentity? right) =>
        left is null ? right is null : right is not null
        && left.NetworkType == right.NetworkType && left.NetworkIdentifier == right.NetworkIdentifier
        && string.Equals(left.AdapterId, right.AdapterId, StringComparison.OrdinalIgnoreCase);

    private void UpdateSessionHistory(Action action)
    {
        try
        {
            action();
            _sessionPersistenceFailed = false;
            SessionMessageText.Text = _usagePersistenceMessage ?? "";
        }
        catch (Exception exception)
        {
            _sessionPersistenceFailed = true;
            SessionMessageText.Text = $"Connection history could not be saved: {exception.Message}";
            Trace.TraceError(SessionMessageText.Text);
        }
    }

    private void RefreshCurrentSession()
    {
        var session = _sessions.Current;
        if (session is null || _sessions.IsSleeping)
        {
            CurrentSessionText.Text = _suspended ? "Paused for sleep" : "No active session";
            CurrentSessionUsageText.Text = "↓ 0 B   ↑ 0 B";
            return;
        }
        var duration = DateTimeOffset.UtcNow - session.StartedAtUtc;
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        CurrentSessionText.Text = duration.TotalHours >= 1
            ? $"{(long)duration.TotalHours}h {duration.Minutes}m"
            : $"{duration.Minutes}m {duration.Seconds}s";
        CurrentSessionUsageText.Text = $"↓ {ByteFormatter.Format(session.DownloadBytes)}   ↑ {ByteFormatter.Format(session.UploadBytes)}";
        if (!string.Equals(_selectedAdapter?.Id, session.AdapterId, StringComparison.OrdinalIgnoreCase))
            CurrentSessionUsageText.Text += " • Select the current connection's adapter to record its traffic";
    }

    private void ViewHistory_OnClick(object sender, RoutedEventArgs e)
    {
        UpdateSessionHistory(() => _sessions.Flush());
        try { new ConnectionHistoryWindow(_history) { Owner = this }.ShowDialog(); }
        catch (Exception exception)
        {
            SessionMessageText.Text = $"Connection history could not be opened: {exception.Message}";
        }
    }

    private void SuspendMonitoring()
    {
        if (_closing || _suspended) return;
        _suspended = true;
        _timer.Stop();
        _previousSnapshot = null;
        try { FlushPendingForLifecycle(); }
        finally
        {
            UpdateSessionHistory(() => _sessions.Sleep());
            _profileMonitor.Refresh(invalidatePending: true);
            RefreshCurrentSession();
        }
    }

    private void ResumeMonitoring()
    {
        if (_closing || !_suspended) return;
        _lastHandledObservationSequence = _profileMonitor.Refresh(invalidatePending: true);
        UpdateSessionHistory(() => _sessions.Resume());
        _previousSnapshot = null;
        _lastPhysicalNetwork = null;
        _suspended = false;
        _timer.Start();
        RefreshCurrentSession();
    }

    public void CompleteRun()
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        _vpnSessions?.Dispose();
        try { FlushPendingForLifecycle(); }
        finally
        {
            try { UpdateSessionHistory(() => _sessions.Close()); }
            finally
            {
                try { _powerLifecycle.Dispose(); }
                finally
                {
                    try { _profileMonitor.Dispose(); }
                    finally { _runLease.Dispose(); }
                }
            }
        }
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

    private void LoadAdapters(string? preferredAdapterId = null)
    {
        var adapters = _monitor.GetActiveAdapters();
        var selected = adapters.FirstOrDefault(adapter => string.Equals(adapter.Id, preferredAdapterId, StringComparison.OrdinalIgnoreCase))
            ?? adapters.FirstOrDefault(adapter => adapter.Id == _selectedAdapter?.Id)
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
            _previousSnapshot = null;
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
        _previousSnapshot = null;

        UpdateAdapterDetails();
    }

    private void Timer_OnTick(object? sender, EventArgs e)
    {
        if (_suspended || _closing) return;
        if (_selectedAdapter is null)
        {
            SetOffline("No adapter selected");
            PersistWhenDue();
            RefreshCurrentSession();
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
            PersistWhenDue();
            RefreshCurrentSession();
            return;
        }

        SetOnline();

        var latest = _profileMonitor.Current;
        var pendingPhysicalChange = latest.Status != ConnectionStatus.Detecting
            && !SamePhysicalNetwork(latest.Network, _lastPhysicalNetwork);
        var delta = BandwidthDeltaCalculator.Calculate(pendingPhysicalChange ? null : _previousSnapshot, current);
        if (delta is not null)
        {
            var receivedDelta = delta.DownloadBytes;
            var sentDelta = delta.UploadBytes;

            _sessionDownloaded += receivedDelta;
            _sessionUploaded += sentDelta;
            _pendingDownloaded += receivedDelta;
            _pendingUploaded += sentDelta;

            DownloadRateText.Text = ByteFormatter.FormatRate(receivedDelta / delta.ElapsedSeconds);
            UploadRateText.Text = ByteFormatter.FormatRate(sentDelta / delta.ElapsedSeconds);
            DownloadSessionText.Text = $"App session: {ByteFormatter.Format(_sessionDownloaded)}";
            UploadSessionText.Text = $"App session: {ByteFormatter.Format(_sessionUploaded)}";
        }
        else
        {
            DownloadRateText.Text = "0 B/s";
            UploadRateText.Text = "0 B/s";
        }

        if (!pendingPhysicalChange && _sessions.Current is { } session
            && latest.Network is { } network && network.NetworkType == session.NetworkType
            && network.NetworkIdentifier == session.NetworkIdentifier
            && string.Equals(network.AdapterId, session.AdapterId, StringComparison.OrdinalIgnoreCase))
            _sessions.RecordSample(session.Id, _selectedAdapter.Id, ++_sampleSequence,
                current.Timestamp.ToUniversalTime(), delta?.DownloadBytes ?? 0, delta?.UploadBytes ?? 0);

        _previousSnapshot = current;

        PersistWhenDue();
        RefreshTotals();
        RefreshCurrentSession();
    }

    private void PersistWhenDue()
    {
        if (DateTimeOffset.Now - _lastFlush < TimeSpan.FromSeconds(30)) return;
        var retryObservation = _sessionPersistenceFailed;
        FlushPending();
        var retryResume = _sessions.IsSleeping && !_suspended;
        if (retryResume) UpdateSessionHistory(() => _sessions.Resume());
        // Refresh identity at the existing batch interval to confirm idle/manual-selection sessions.
        // This adds no counter and provides a real observation for crash recovery.
        _persistNextObservation = true;
        var cutoff = _profileMonitor.Refresh(invalidatePending: retryObservation || retryResume);
        if (retryResume) _lastHandledObservationSequence = Math.Max(_lastHandledObservationSequence, cutoff);
    }

    private void FlushPending()
    {
        if (_pendingDownloaded > 0 || _pendingUploaded > 0)
        {
            _store.AddUsage(_pendingDay, _pendingDownloaded, _pendingUploaded);
            _pendingDownloaded = 0;
            _pendingUploaded = 0;
            _usagePersistenceMessage = null;
        }
        UpdateSessionHistory(() => _sessions.Flush());
        _lastFlush = DateTimeOffset.Now;
    }

    private void FlushPendingForLifecycle()
    {
        try { FlushPending(); }
        catch (Exception exception)
        {
            // Keep the failed batch and still handle the Windows sleep/exit boundary.
            _usagePersistenceMessage = $"Usage history could not be saved: {exception.Message}";
            SessionMessageText.Text = _usagePersistenceMessage;
            Trace.TraceError(_usagePersistenceMessage);
        }
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
