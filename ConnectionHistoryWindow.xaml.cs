using System.Windows;
using NetTrace.Services;
using NetTrace.Utils;

namespace NetTrace;

public partial class ConnectionHistoryWindow : Window
{
    private readonly ConnectionHistoryStore _store;

    public ConnectionHistoryWindow(ConnectionHistoryStore store)
    {
        _store = store;
        InitializeComponent();
        RefreshHistory();
    }

    private void Refresh_OnClick(object sender, RoutedEventArgs e) => RefreshHistory();

    private void RefreshHistory()
    {
        try
        {
            SessionsGrid.ItemsSource = _store.GetRecentSessions(100).Select(session => new
            {
                Started = session.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                Ended = session.EndedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "Active",
                Profile = session.ProfileDisplayName ?? "Unassigned",
                Network = session.NetworkDisplayName,
                Download = ByteFormatter.Format(session.DownloadBytes),
                Upload = ByteFormatter.Format(session.UploadBytes),
                Reason = session.EndReason ?? "—"
            }).ToList();
            EventsGrid.ItemsSource = _store.GetRecentEvents(200).Select(connectionEvent => new
            {
                Time = connectionEvent.OccurredAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                Type = connectionEvent.EventType,
                connectionEvent.Message
            }).ToList();
            HistoryMessageText.Text = "Latest 100 sessions and 200 events. Times are displayed in local time.";
        }
        catch (Exception exception)
        {
            HistoryMessageText.Text = $"Connection history could not be loaded: {exception.Message}";
        }
    }
}
