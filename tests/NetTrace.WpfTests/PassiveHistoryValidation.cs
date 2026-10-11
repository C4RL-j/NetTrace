using System.Reflection;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using NetTrace;
using NetTrace.Services;

// Explicit local read-only snapshot validation. Production monitoring is never
// constructed against the live database or the copied historical database.
internal static class PassiveHistoryValidation
{
    public static int Run()
    {
        try
        {
            var sourcePath = UsageStore.GetDatabasePath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            if (!System.IO.File.Exists(sourcePath))
                throw new InvalidOperationException("No existing real NetTrace database is available for passive History validation.");
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NetTrace-history-passive-" + Guid.NewGuid().ToString("N"));
            var snapshotRoot = System.IO.Path.Combine(root, "snapshot");
            var snapshotPath = UsageStore.GetDatabasePath(snapshotRoot);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(snapshotPath)!);
            var sourceConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = sourcePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
            var destinationConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = snapshotPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();
            using (var source = new SqliteConnection(sourceConnectionString))
            using (var destination = new SqliteConnection(destinationConnectionString))
            {
                source.Open();
                using var queryOnly = source.CreateCommand();
                queryOnly.CommandText = "PRAGMA query_only=ON;";
                queryOnly.ExecuteNonQuery();
                destination.Open();
                // SQLite backup includes committed WAL rows in a coherent snapshot;
                // a raw filesystem copy would not necessarily include those records.
                source.BackupDatabase(destination);
            }

            var store = new UsageStore(snapshotRoot);
            var history = new ConnectionHistoryStore(store);
            var before = RecordSignature(snapshotPath);
            var active = history.GetActiveSession();
            // An unfinished database row alone does not prove current connectivity.
            // Use its last confirmation rather than extending time or claiming Active.
            var overview = new HistoryOverviewService(history).GetOverview(null);
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using var monitoring = HistoryOverviewChecks.CreateMonitoring(new UsageStore(System.IO.Path.Combine(root, "empty-shell")));
            var main = HistoryOverviewChecks.CreateWindow(monitoring);
            var detailed = new ConnectionHistoryWindow(history)
                { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            try
            {
                main.Show();
                HistoryOverviewChecks.StopViewTimer(main);
                HistoryOverviewChecks.SelectHistory(main);
                typeof(MainWindow).GetMethod("ApplyHistoryOverview", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(main, new object[] { overview });
                main.UpdateLayout();
                HistoryOverviewChecks.Pump();
                if (HistoryOverviewChecks.Control<System.Windows.Controls.ItemsControl>(main, "HistoryRecentSessions").Items.Count != overview.RecentSessions.Count)
                    throw new InvalidOperationException("The real-history snapshot overview did not render its bounded recent sessions.");
                HistoryOverviewChecks.SavePreview(main, System.IO.Path.Combine(root, "760x600-real-history.png"));
                var page = (ScrollViewer)HistoryOverviewChecks.Control<TabItem>(main, "HistoryTab").Content;
                if (page.ScrollableWidth > 0.01)
                    throw new InvalidOperationException("The real-history main page has horizontal overflow at 760x600.");
                page.ScrollToEnd();
                main.UpdateLayout();
                HistoryOverviewChecks.Pump();
                HistoryOverviewChecks.SavePreview(main, System.IO.Path.Combine(root, "760x600-real-history-bottom.png"));
                detailed.Show();
                detailed.UpdateLayout();
                HistoryOverviewChecks.Pump();
                HistoryOverviewChecks.SavePreview(detailed, System.IO.Path.Combine(root, "real-detailed-sessions.png"));
                var tabs = HistoryOverviewChecks.Descendants((DependencyObject)detailed.Content).OfType<TabControl>().Single();
                tabs.SelectedIndex = 1;
                detailed.UpdateLayout();
                HistoryOverviewChecks.Pump();
                HistoryOverviewChecks.SavePreview(detailed, System.IO.Path.Combine(root, "real-detailed-events.png"));
                var after = RecordSignature(snapshotPath);
                if (before != after)
                    throw new InvalidOperationException("Passive rendering altered copied connection session/event records.");
                Console.WriteLine($"Passive real-history snapshot: {before.Sessions} sessions, {before.Events} events; " +
                    $"today {overview.TodaySessionCount} sessions, {overview.TodayConnectedTimeText}, " +
                    $"download {overview.TodayDownloadText}, upload {overview.TodayUploadText}; {overview.RecentSessions.Count} recent rows.");
                Console.WriteLine($"Preserved detailed window: {HistoryOverviewChecks.Control<DataGrid>(detailed, "SessionsGrid").Items.Count} sessions, " +
                    $"{HistoryOverviewChecks.Control<DataGrid>(detailed, "EventsGrid").Items.Count} events.");
                Console.WriteLine("The real database was opened ReadOnly with query_only enabled; only a temporary SQLite backup and isolated empty UI store were written. Existing app/network/monitoring were untouched.");
                Console.WriteLine(active is null
                    ? "The snapshot has no persisted active session."
                    : "An unfinished snapshot session is shown as Last confirmed, using only persisted time and counters; live connectivity and unflushed counters from another process are unavailable to this passive check.");
                Console.WriteLine("Passive History screenshots: " + root);
                return 0;
            }
            finally
            {
                detailed.Close();
                main.Close();
                Application.Current.Shutdown();
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Passive History validation failed: " + exception);
            return 1;
        }
    }

    private static (long Sessions, long Events, string SessionRows, string EventRows) RecordSignature(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ConnectionSessions;";
        var sessions = (long)command.ExecuteScalar()!;
        command.CommandText = "SELECT COUNT(*) FROM ConnectionEvents;";
        var events = (long)command.ExecuteScalar()!;
        return (sessions, events, RowDigest(connection, "ConnectionSessions"), RowDigest(connection, "ConnectionEvents"));
    }

    private static string RowDigest(SqliteConnection connection, string table)
    {
        // Compare every persisted column while streaming one row at a time; the
        // validation never loads the user's lifetime history into a collection.
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table} ORDER BY Id;";
        using var reader = command.ExecuteReader();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (reader.Read())
        {
            for (var column = 0; column < reader.FieldCount; column++)
            {
                if (reader.IsDBNull(column)) { hash.AppendData(BitConverter.GetBytes(-1)); continue; }
                var bytes = Encoding.UTF8.GetBytes(Convert.ToString(reader.GetValue(column), CultureInfo.InvariantCulture)!);
                hash.AppendData(BitConverter.GetBytes(bytes.Length));
                hash.AppendData(bytes);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
