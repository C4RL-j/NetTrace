using AppUsageStorage;
using Microsoft.Data.Sqlite;

internal static class ClearHistoryTests
{
    private static readonly DateOnly Today = new(2026, 10, 13);
    private static readonly DateTimeOffset Seen = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);

    public static IEnumerable<(string Name, Func<Task> Run)> Cases => new (string, Func<Task>)[]
    {
        ("Clear History makes Today empty", Check.Sync(() => EmptyRange(0))),
        ("Clear History makes 7D empty", Check.Sync(() => EmptyRange(6))),
        ("Clear History makes 30D empty", Check.Sync(() => EmptyRange(29))),
        ("Clear History removes older App Usage dates too", Check.Sync(AllDates)),
        ("Clear History preserves exact schema and metadata", Check.Sync(PreserveSchema)),
        ("Clear History preserves database and unrelated files", Check.Sync(PreserveFiles)),
        ("Clear History supports repeated empty clear", Check.Sync(RepeatedClear)),
        ("Clear History permits new usage immediately", Check.Sync(NewUsage)),
        ("Existing writable open does not create missing storage", Check.Sync(MissingStorage)),
        ("Clear History rejects a foreign database without adoption", Check.Sync(ForeignDatabase)),
        ("Clear History rejects corrupt storage without replacement", Check.Sync(CorruptDatabase)),
        ("Clear History rejects unexpected schema objects", Check.Sync(UnexpectedTable)),
        ("Clear History refuses unexpected deletion triggers", Check.Sync(UnexpectedTrigger)),
        ("Read-only storage cannot clear history", Check.Sync(ReadOnlyStore)),
        ("Failed clear transaction preserves rows for retry", Check.Sync(LockedDatabase)),
    };

    private static DailyAppUsageStore Populate(TestStorage scope)
    {
        var store = scope.Open();
        store.AddUsageDeltas(Enumerable.Range(0, 40)
            .Select(day => new UsageDelta(Today.AddDays(-day), ApplicationIdentity.Unknown,
                100 + day, 20 + day, Seen.AddDays(-day))).ToArray());
        return store;
    }

    private static void EmptyRange(int daysAgo)
    {
        using var scope = new TestStorage();
        Populate(scope);
        var store = DailyAppUsageStore.OpenExistingWritable(scope.Root);
        Check.Equal(40, store.ClearHistory(), "cleared row count");
        Check.Equal(0, DailyAppUsageStore.OpenReadOnly(scope.Root)
            .GetUsageForRange(Today.AddDays(-daysAgo), Today).Count, "selected range empty");
    }

    private static void AllDates()
    {
        using var scope = new TestStorage();
        var store = Populate(scope);
        store.ClearHistory();
        Check.Equal(0L, scope.Scalar("SELECT COUNT(*) FROM daily_app_usage;"), "all persisted dates cleared");
    }

    private static void PreserveSchema()
    {
        using var scope = new TestStorage();
        var store = Populate(scope);
        var before = Definition(scope);
        var version = scope.Scalar("PRAGMA user_version;");
        var applicationId = scope.Scalar("PRAGMA application_id;");
        store.ClearHistory();
        Check.Equal(before, Definition(scope), "table and index SQL unchanged");
        Check.Equal(version, scope.Scalar("PRAGMA user_version;"), "schema version unchanged");
        Check.Equal(applicationId, scope.Scalar("PRAGMA application_id;"), "App Usage identity unchanged");
        Check.Equal(1L, scope.Scalar("SELECT COUNT(*) FROM pragma_index_list('daily_app_usage') WHERE name='ix_daily_app_usage_application_date';"), "query index retained");
    }

    private static string Definition(TestStorage scope)
    {
        using var connection = scope.Raw();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, sql FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*' ORDER BY name;";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add($"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetString(2)}");
        return string.Join('\n', rows);
    }

    private static void PreserveFiles()
    {
        using var scope = new TestStorage();
        var store = Populate(scope);
        var physicalDatabase = Path.Combine(scope.Root, "NetTrace", "usage.db");
        var history = Path.Combine(scope.Root, "NetTrace", "connection-sessions.json");
        File.WriteAllText(physicalDatabase, "physical-dashboard-accounting");
        File.WriteAllText(history, "connection-history-and-sessions");
        store.ClearHistory();
        Check.True(File.Exists(scope.DatabasePath), "App Usage database retained");
        Check.Equal("physical-dashboard-accounting", File.ReadAllText(physicalDatabase), "physical accounting untouched");
        Check.Equal("connection-history-and-sessions", File.ReadAllText(history), "connection history untouched");
        Check.Equal(3, Directory.GetFiles(Path.GetDirectoryName(scope.DatabasePath)!).Length, "no directory/file removal");
    }

    private static void RepeatedClear()
    {
        using var scope = new TestStorage();
        var store = Populate(scope);
        store.ClearHistory();
        Check.Equal(0, store.ClearHistory(), "empty clear succeeds without records");
        Check.Equal(0, scope.Open().GetUsageForDate(Today).Count, "reinitialize remains usable");
    }

    private static void NewUsage()
    {
        using var scope = new TestStorage();
        var store = Populate(scope);
        store.ClearHistory();
        store.AddUsageDelta(Today, ApplicationIdentity.System, 43, 17, Seen.AddMinutes(1));
        var row = DailyAppUsageStore.OpenReadOnly(scope.Root).GetUsageForDate(Today).Single();
        Check.Equal(43L, row.DownloadBytes, "only new download retained");
        Check.Equal(17L, row.UploadBytes, "only new upload retained");
        Check.Equal(ApplicationIdentity.System.Key, row.ApplicationIdentity, "new app identity retained");
    }

    private static void MissingStorage()
    {
        using var scope = new TestStorage();
        var store = DailyAppUsageStore.OpenExistingWritable(scope.Root);
        Check.True(!Directory.Exists(Path.GetDirectoryName(scope.DatabasePath)), "open has no directory side effect");
        Check.Failure(() => store.ClearHistory(), AppUsageStorageFailure.OpenFailed, scope.DatabasePath);
        Check.True(!Directory.Exists(Path.GetDirectoryName(scope.DatabasePath)), "failed clear does not create a directory");
        Check.True(!File.Exists(scope.DatabasePath), "failed clear does not create a database");
    }

    private static void ForeignDatabase()
    {
        using var scope = new TestStorage();
        scope.Execute("CREATE TABLE connection_sessions(value TEXT); INSERT INTO connection_sessions VALUES ('keep');");
        var bytes = File.ReadAllBytes(scope.DatabasePath);
        Check.Failure(() => DailyAppUsageStore.OpenExistingWritable(scope.Root).ClearHistory(),
            AppUsageStorageFailure.IncompatibleSchema, scope.DatabasePath);
        Check.True(bytes.SequenceEqual(File.ReadAllBytes(scope.DatabasePath)), "foreign database byte content unchanged");
        Check.Equal(1L, scope.Scalar("SELECT COUNT(*) FROM connection_sessions;"), "foreign records retained");
        Check.Equal(0L, scope.Scalar("PRAGMA user_version;"), "foreign schema not adopted");
    }

    private static void CorruptDatabase()
    {
        using var scope = new TestStorage();
        Directory.CreateDirectory(Path.GetDirectoryName(scope.DatabasePath)!);
        var bytes = System.Text.Encoding.UTF8.GetBytes("invalid-sqlite-preserve-for-recovery");
        File.WriteAllBytes(scope.DatabasePath, bytes);
        Check.Failure(() => DailyAppUsageStore.OpenExistingWritable(scope.Root).ClearHistory(),
            AppUsageStorageFailure.CorruptDatabase, scope.DatabasePath);
        Check.True(bytes.SequenceEqual(File.ReadAllBytes(scope.DatabasePath)), "corrupt database not replaced");
    }

    private static void UnexpectedTable()
    {
        using var scope = new TestStorage();
        Populate(scope);
        scope.Execute("CREATE TABLE unrelated(value INTEGER); INSERT INTO unrelated VALUES (7);");
        Check.Failure(() => DailyAppUsageStore.OpenExistingWritable(scope.Root).ClearHistory(),
            AppUsageStorageFailure.IncompatibleSchema, scope.DatabasePath);
        Check.Equal(40L, scope.Scalar("SELECT COUNT(*) FROM daily_app_usage;"), "usage preserved on incompatible schema");
        Check.Equal(7L, scope.Scalar("SELECT value FROM unrelated;"), "unrelated records preserved");
    }

    private static void UnexpectedTrigger()
    {
        using var scope = new TestStorage();
        Populate(scope);
        scope.Execute("CREATE TRIGGER unexpected_delete AFTER DELETE ON daily_app_usage BEGIN SELECT RAISE(ABORT, 'unexpected'); END;");
        Check.Failure(() => DailyAppUsageStore.OpenExistingWritable(scope.Root).ClearHistory(),
            AppUsageStorageFailure.IncompatibleSchema, scope.DatabasePath);
        Check.Equal(40L, scope.Scalar("SELECT COUNT(*) FROM daily_app_usage;"), "unknown trigger never executes");
    }

    private static void ReadOnlyStore()
    {
        using var scope = new TestStorage();
        Populate(scope);
        Check.Throws<InvalidOperationException>(() => DailyAppUsageStore.OpenReadOnly(scope.Root).ClearHistory(), "read-only write guard");
        Check.Equal(40L, scope.Scalar("SELECT COUNT(*) FROM daily_app_usage;"), "read-only clear preserves rows");
    }

    private static void LockedDatabase()
    {
        using var scope = new TestStorage();
        var store = Populate(scope);
        using (var blocker = scope.Raw())
        using (var transaction = blocker.BeginTransaction())
        {
            Check.Failure(() => store.ClearHistory(), AppUsageStorageFailure.TransactionFailed, scope.DatabasePath);
            transaction.Rollback();
        }
        Check.Equal(40, store.GetUsageForRange(Today.AddDays(-39), Today).Count, "lock failure preserves every row");
        Check.Equal(40, store.ClearHistory(), "unlocked retry clears successfully");
    }
}
