using AppUsageStorage;
using Microsoft.Data.Sqlite;

internal static class SchemaFailureTests
{
    private static readonly DateOnly Day = new(2026, 10, 13);
    private static readonly DateTimeOffset Seen = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);

    public static IEnumerable<(string Name, Func<Task> Run)> Cases => new (string, Func<Task>)[]
    {
        ("Schema owns eight daily aggregate columns and composite primary key", Check.Sync(SchemaColumns)),
        ("Schema has date key and application/date index without raw event tables", Check.Sync(SchemaIndex)),
        ("Schema records version one and NetTrace application ID", Check.Sync(SchemaVersion)),
        ("Empty SQLite version zero migrates to version one", Check.Sync(EmptyDatabaseMigration)),
        ("Future schema version is rejected without changing database", Check.Sync(FutureVersion)),
        ("Populated unversioned database is rejected unchanged", Check.Sync(PopulatedUnversioned)),
        ("Incorrect application ID is rejected unchanged", Check.Sync(WrongApplicationId)),
        ("Empty version one database is rejected unchanged", Check.Sync(MissingSchema)),
        ("Malformed aggregate schema is rejected unchanged", Check.Sync(MalformedSchema)),
        ("Missing owned index is rejected unchanged", Check.Sync(MissingIndex)),
        ("Unexpected raw-event table is rejected unchanged", Check.Sync(UnexpectedTable)),
        ("Unexpected trigger is rejected unchanged on reopen", Check.Sync(UnexpectedTrigger)),
        ("Unrecognized version zero application ID is rejected unchanged", Check.Sync(UnversionedWrongId)),
        ("Corrupt non-SQLite file yields typed failure and is preserved", Check.Sync(CorruptFile)),
        ("Cannot create machine data directory yields typed open failure", Check.Sync(OpenFailure)),
        ("Deleted database fails open and does not recreate usage", Check.Sync(DeletedDatabase)),
        ("Missing aggregate table gives typed query failure", Check.Sync(QueryFailure)),
        ("Database trigger failure rolls back previous batch rows", Check.Sync(TriggerRollback)),
        ("Retention failure rolls back previously deleted rows", Check.Sync(RetentionRollback)),
        ("Schema rejects raw negative and noninteger counters", Check.Sync(EnforcedNonnegativeCounters)),
        ("Display metadata with SQL punctuation is parameterized", Check.Sync(ParameterizedMetadata)),
        ("Schema rejects invalid local calendar date", Check.Sync(EnforcedCalendarDate)),
        ("Invalid persisted timestamp yields typed corruption failure", Check.Sync(InvalidStoredTimestamp)),
        ("Upload overflow is rejected without changing row", Check.Sync(UploadOverflow)),
        ("Empty batch is a safe no-op", Check.Sync(EmptyBatch)),
        ("Null batch and null application fail before any write", Check.Sync(NullInputs)),
        ("Executable identity rejects relative and invalid executable paths", Check.Sync(InvalidIdentityPaths)),
        ("Executable image with COM extension preserves historical usage", Check.Sync(NonExeIdentity)),
        ("Extended drive and UNC executable paths normalize stably", Check.Sync(ExtendedIdentityPaths)),
        ("Retention clamps DateOnly minimum safely", Check.Sync(MinimumRetentionDate)),
    };

    private static void SchemaColumns()
    {
        using var scope = new TestStorage();
        _ = scope.Open();
        using var connection = scope.Raw();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(daily_app_usage)";
        using var reader = command.ExecuteReader();
        var columns = new List<(string Name, string Type, int NotNull, int PrimaryKey)>();
        while (reader.Read()) columns.Add((reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(5)));
        var expectedNames = new[] { "local_date", "application_identity", "display_name", "executable_name", "executable_path", "download_bytes", "upload_bytes", "last_seen_utc" };
        Check.True(columns.Select(column => column.Name).SequenceEqual(expectedNames), "daily aggregate fields");
        Check.Equal(1, columns[0].PrimaryKey, "local date first in primary key");
        Check.Equal(2, columns[1].PrimaryKey, "identity second in primary key");
        Check.True(columns.Skip(2).All(column => column.PrimaryKey == 0), "only date and identity key");
        Check.Equal("INTEGER", columns[5].Type, "download integer storage");
        Check.Equal("INTEGER", columns[6].Type, "upload integer storage");
        Check.True(columns.Where(column => column.Name is not "executable_name" and not "executable_path").All(column => column.NotNull == 1), "required aggregate fields nonnull");
    }

    private static void SchemaIndex()
    {
        using var scope = new TestStorage();
        _ = scope.Open();
        using var connection = scope.Raw();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA index_info(ix_daily_app_usage_application_date)";
        using (var reader = command.ExecuteReader())
        {
            var columns = new List<string>();
            while (reader.Read()) columns.Add(reader.GetString(2));
            Check.True(columns.SequenceEqual(new[] { "application_identity", "local_date" }), "application/date index");
        }
        command.CommandText = "SELECT sql FROM sqlite_master WHERE name='daily_app_usage'";
        Check.True(((string)command.ExecuteScalar()!).Contains("WITHOUT ROWID", StringComparison.Ordinal), "compact composite primary key");
        Check.Equal(1L, scope.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"), "only aggregate table exists");
    }

    private static void SchemaVersion()
    {
        using var scope = new TestStorage();
        _ = scope.Open();
        Check.Equal(1, DailyAppUsageStore.CurrentSchemaVersion, "initial production schema version");
        Check.Equal((long)DailyAppUsageStore.CurrentSchemaVersion, scope.Scalar("PRAGMA user_version"), "SQLite schema version");
        Check.Equal(0x4E544155L, scope.Scalar("PRAGMA application_id"), "NetTrace app usage file identification");
    }

    private static void EmptyDatabaseMigration()
    {
        using var scope = new TestStorage();
        scope.Execute("PRAGMA user_version=0; PRAGMA application_id=0; VACUUM;");
        Check.Equal(0L, scope.Scalar("PRAGMA user_version"), "version zero before migration");
        Check.Equal(0L, scope.Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%'"), "empty before migration");
        var store = scope.Open();
        Check.Equal(1L, scope.Scalar("PRAGMA user_version"), "migration completed");
        store.AddUsageDelta(Day, ApplicationIdentity.System, 17, 7, Seen);
        Check.Equal(17L, scope.Open().GetUsageForDate(Day).Single().DownloadBytes, "migrated database fully usable");
    }

    private static void RejectedUnchanged(TestStorage scope)
    {
        var before = File.ReadAllBytes(scope.DatabasePath);
        Check.Failure(() => scope.Open(), AppUsageStorageFailure.IncompatibleSchema, scope.DatabasePath);
        Check.True(before.SequenceEqual(File.ReadAllBytes(scope.DatabasePath)), "rejected database byte-for-byte unchanged");
    }

    private static void FutureVersion()
    {
        using var scope = new TestStorage();
        scope.Open().AddUsageDelta(Day, ApplicationIdentity.System, 13, 1, Seen);
        scope.Execute($"PRAGMA user_version={DailyAppUsageStore.CurrentSchemaVersion + 1};");
        RejectedUnchanged(scope);
        Check.Equal(13L, scope.Scalar("SELECT download_bytes FROM daily_app_usage"), "future database data retained");
    }

    private static void PopulatedUnversioned()
    {
        using var scope = new TestStorage();
        scope.Execute("CREATE TABLE unrelated_data(value TEXT); INSERT INTO unrelated_data VALUES ('preserve me');");
        RejectedUnchanged(scope);
        Check.Equal(0L, scope.Scalar("PRAGMA user_version"), "unknown schema not adopted");
    }

    private static void WrongApplicationId()
    {
        using var scope = new TestStorage();
        _ = scope.Open();
        scope.Execute("PRAGMA application_id=12345;");
        RejectedUnchanged(scope);
    }

    private static void MissingSchema()
    {
        using var scope = new TestStorage();
        scope.Execute("PRAGMA application_id=0x4E544155; PRAGMA user_version=1;");
        RejectedUnchanged(scope);
    }

    private static void MalformedSchema()
    {
        using var scope = new TestStorage();
        scope.Execute("CREATE TABLE daily_app_usage(local_date TEXT, application_identity TEXT, download_bytes INTEGER); PRAGMA application_id=0x4E544155; PRAGMA user_version=1;");
        RejectedUnchanged(scope);
    }

    private static void MissingIndex()
    {
        using var scope = new TestStorage();
        _ = scope.Open();
        scope.Execute("DROP INDEX ix_daily_app_usage_application_date;");
        RejectedUnchanged(scope);
    }

    private static void UnversionedWrongId()
    {
        using var scope = new TestStorage();
        scope.Execute("PRAGMA user_version=0; PRAGMA application_id=12345;");
        RejectedUnchanged(scope);
    }

    private static void UnexpectedTable()
    {
        using var scope = new TestStorage();
        _ = scope.Open();
        scope.Execute("CREATE TABLE raw_events(pid INTEGER, event_bytes INTEGER);");
        RejectedUnchanged(scope);
    }

    private static void UnexpectedTrigger()
    {
        using var scope = new TestStorage();
        _ = scope.Open();
        scope.Execute("CREATE TRIGGER unexpected_trigger AFTER INSERT ON daily_app_usage BEGIN SELECT 1; END;");
        RejectedUnchanged(scope);
    }

    private static void CorruptFile()
    {
        using var scope = new TestStorage();
        Directory.CreateDirectory(Path.GetDirectoryName(scope.DatabasePath)!);
        File.WriteAllBytes(scope.DatabasePath, Enumerable.Range(0, 512).Select(i => (byte)(i % 251)).ToArray());
        var before = File.ReadAllBytes(scope.DatabasePath);
        Check.Failure(() => scope.Open(), AppUsageStorageFailure.CorruptDatabase, scope.DatabasePath);
        Check.True(before.SequenceEqual(File.ReadAllBytes(scope.DatabasePath)), "corrupt file preserved for recovery");
    }

    private static void OpenFailure()
    {
        using var scope = new TestStorage();
        var blockedRoot = Path.Combine(scope.Root, "root-is-a-file");
        File.WriteAllText(blockedRoot, "cannot create a child directory under a file");
        var targetPath = DailyAppUsageStore.GetDatabasePath(blockedRoot);
        Check.Failure(() => new DailyAppUsageStore(blockedRoot), AppUsageStorageFailure.OpenFailed, targetPath);
        Check.True(File.Exists(blockedRoot), "blocking file preserved");
    }

    private static void DeletedDatabase()
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        store.AddUsageDelta(Day, ApplicationIdentity.System, 10, 1, Seen);
        File.Delete(scope.DatabasePath);
        Check.Failure(() => store.GetUsageForDate(Day), AppUsageStorageFailure.OpenFailed, scope.DatabasePath);
        Check.True(!File.Exists(scope.DatabasePath), "query does not recreate database");
        Check.Failure(() => store.AddUsageDelta(Day, ApplicationIdentity.System, 1, 1, Seen), AppUsageStorageFailure.OpenFailed, scope.DatabasePath);
        Check.True(!File.Exists(scope.DatabasePath), "write does not silently recreate database");
    }

    private static void QueryFailure()
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        scope.Execute("DROP TABLE daily_app_usage;");
        Check.Failure(() => store.GetUsageForDate(Day), AppUsageStorageFailure.QueryFailed, scope.DatabasePath);
    }

    private static void TriggerRollback()
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        store.AddUsageDelta(Day, ApplicationIdentity.System, 10, 1, Seen);
        scope.Execute("CREATE TRIGGER fail_unknown BEFORE INSERT ON daily_app_usage WHEN NEW.application_identity='unknown' BEGIN SELECT RAISE(ABORT, 'injected transaction failure'); END;");
        var batch = new[] { new UsageDelta(Day, ApplicationIdentity.System, 20, 2, Seen.AddMinutes(1)), new UsageDelta(Day, ApplicationIdentity.Unknown, 30, 3, Seen) };
        Check.Failure(() => store.AddUsageDeltas(batch), AppUsageStorageFailure.TransactionFailed, scope.DatabasePath);
        var row = store.GetUsageForDate(Day).Single();
        Check.Equal(10L, row.DownloadBytes, "earlier upsert rolled back");
        Check.Equal(1L, row.UploadBytes, "earlier upload rolled back");
        Check.Equal(Seen, row.LastSeen, "earlier timestamp rolled back");
    }

    private static void RetentionRollback()
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        store.AddUsageDelta(Day.AddDays(-2), ApplicationIdentity.System, 10, 1, Seen);
        store.AddUsageDelta(Day.AddDays(-1), ApplicationIdentity.Unknown, 20, 2, Seen);
        scope.Execute("CREATE TRIGGER fail_retention BEFORE DELETE ON daily_app_usage WHEN OLD.application_identity='unknown' BEGIN SELECT RAISE(ABORT, 'injected cleanup failure'); END;");
        Check.Failure(() => store.RetentionCleanup(Day), AppUsageStorageFailure.TransactionFailed, scope.DatabasePath);
        Check.Equal(2, store.GetUsageForRange(Day.AddDays(-2), Day).Count, "all retention deletions rolled back");
    }

    private static void EnforcedNonnegativeCounters()
    {
        using var scope = new TestStorage();
        scope.Open().AddUsageDelta(Day, ApplicationIdentity.System, 10, 1, Seen);
        Check.Throws<SqliteException>(() => scope.Execute("UPDATE daily_app_usage SET download_bytes=-1"), "schema rejects negative download");
        Check.Throws<SqliteException>(() => scope.Execute("UPDATE daily_app_usage SET upload_bytes=-1"), "schema rejects negative upload");
        Check.Throws<SqliteException>(() => scope.Execute("UPDATE daily_app_usage SET download_bytes=1.5"), "schema rejects fractional bytes");
        Check.Equal(10L, scope.Scalar("SELECT download_bytes FROM daily_app_usage"), "counter invariant preserved");
    }

    private static void ParameterizedMetadata()
    {
        using var scope = new TestStorage();
        const string display = "App'); DROP TABLE daily_app_usage; --";
        var app = ApplicationIdentity.FromExecutablePath(@"C:\Apps\Publisher's app\app.exe", display);
        var store = scope.Open();
        store.AddUsageDelta(Day, app, 5, 2, Seen);
        var row = store.GetUsageForDate(Day).Single();
        Check.Equal(display, row.DisplayName, "SQL-looking metadata stored literally");
        Check.Equal(app.Key, row.ApplicationIdentity, "quoted path stored literally");
        Check.Equal(1L, scope.Scalar("SELECT COUNT(*) FROM daily_app_usage"), "aggregate table intact");
    }

    private static void EnforcedCalendarDate()
    {
        using var scope = new TestStorage();
        scope.Open().AddUsageDelta(Day, ApplicationIdentity.System, 10, 1, Seen);
        Check.Throws<SqliteException>(() => scope.Execute("UPDATE daily_app_usage SET local_date='2026-02-30'"), "schema rejects impossible date");
        Check.Throws<SqliteException>(() => scope.Execute("UPDATE daily_app_usage SET local_date='2026-1-1'"), "schema rejects noncanonical date");
    }

    private static void InvalidStoredTimestamp()
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        store.AddUsageDelta(Day, ApplicationIdentity.System, 10, 1, Seen);
        scope.Execute("PRAGMA ignore_check_constraints=ON; UPDATE daily_app_usage SET last_seen_utc='not a timestamp';");
        Check.Failure(() => store.GetUsageForDate(Day), AppUsageStorageFailure.CorruptDatabase, scope.DatabasePath);
    }

    private static void UploadOverflow()
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        store.AddUsageDelta(Day, ApplicationIdentity.System, 1, long.MaxValue, Seen);
        Check.Failure(() => store.AddUsageDelta(Day, ApplicationIdentity.System, 5, 1, Seen.AddMinutes(1)), AppUsageStorageFailure.TransactionFailed, scope.DatabasePath);
        var row = store.GetUsageForDate(Day).Single();
        Check.Equal(1L, row.DownloadBytes, "download rolled back on upload overflow");
        Check.Equal(long.MaxValue, row.UploadBytes, "upload remains exact integer maximum");
    }

    private static void EmptyBatch()
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        store.AddUsageDeltas(Array.Empty<UsageDelta>());
        Check.Equal(0, store.GetUsageForDate(Day).Count, "empty batch creates no rows");
    }

    private static void NullInputs()
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        Check.Throws<ArgumentNullException>(() => store.AddUsageDeltas(null!), "null batch validation");
        Check.Throws<ArgumentNullException>(() => store.AddUsageDelta(Day, null!, 1, 1, Seen), "null application validation");
        Check.Throws<ArgumentNullException>(() => store.AddUsageDeltas(new UsageDelta[] { null! }), "null delta validation");
        Check.Equal(0, store.GetUsageForDate(Day).Count, "null input validation leaves no data");
    }

    private static void InvalidIdentityPaths()
    {
        foreach (var path in new[] { "app.exe", @"C:relative\app.exe", @"C:\Apps\", @"C:\Apps\*.exe", @"\\.\pipe\app.exe", @"C:\Apps\app.exe:stream" })
            Check.Throws<ArgumentException>(() => ApplicationIdentity.FromExecutablePath(path), $"invalid identity path {path}");
        Check.Throws<ArgumentException>(() => ApplicationIdentity.FromExecutablePath(@"C:\Apps\app.exe", " "), "blank display name");
    }

    private static void NonExeIdentity()
    {
        using var scope = new TestStorage();
        var executablePath = Path.Combine(scope.Root, "legacy-app.com");
        Check.True(!File.Exists(executablePath), "historical identity does not require image file to exist");
        var app = ApplicationIdentity.FromExecutablePath(executablePath, "Legacy application");
        Check.Equal("path:" + executablePath.ToUpperInvariant(), app.Key, "COM image uses normalized path identity");
        scope.Open().AddUsageDelta(Day, app, 17, 8, Seen);
        var row = scope.Open().GetUsageForDate(Day).Single();
        Check.Equal(app.Key, row.ApplicationIdentity, "COM identity survives reopen");
        Check.Equal("legacy-app.com", row.ExecutableName, "COM executable metadata persists");
        Check.Equal(executablePath, row.ExecutablePath, "historical COM path persists");
        Check.Equal("Legacy application", row.DisplayName, "historical display metadata persists");
        Check.Equal(17L, row.DownloadBytes, "historical COM download persists");
        Check.Equal(8L, row.UploadBytes, "historical COM upload persists");
    }

    private static void ExtendedIdentityPaths()
    {
        var drive = ApplicationIdentity.FromExecutablePath(@"C:\Apps\app.exe");
        Check.Equal(drive.Key, ApplicationIdentity.FromExecutablePath(@"\\?\C:\Apps\app.exe").Key, "extended drive prefix normalizes");
        var unc = ApplicationIdentity.FromExecutablePath(@"\\server\apps\app.exe");
        Check.Equal(unc.Key, ApplicationIdentity.FromExecutablePath(@"\\?\UNC\server\apps\app.exe").Key, "extended UNC prefix normalizes");
    }

    private static void MinimumRetentionDate()
    {
        Check.Equal(DateOnly.MinValue, DailyAppUsageStore.GetRetentionCutoff(DateOnly.MinValue), "minimum date does not underflow");
        Check.Equal(DateOnly.MinValue, DailyAppUsageStore.GetRetentionCutoff(DateOnly.MinValue.AddDays(88)), "early date cutoff clamps");
    }
}
