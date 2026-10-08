using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NetTrace.Services;

var tests = new (string Name, Action Run)[]
{
    ("Clean install initializes the NetTrace database and version", CleanInstall),
    ("Legacy version-zero history is copied without modifying the source", LegacyUpgrade),
    ("Existing NetTrace version-zero history is preserved", ExistingUnversionedDatabase),
    ("Repeat launch keeps NetTrace data when both databases exist", RepeatLaunch),
    ("Usage totals and date windows survive reopening", PersistedTotals),
    ("Legacy copy includes uncheckpointed WAL history", WalUpgrade),
    ("Invalid legacy schema fails without publishing a target", InvalidLegacySchema),
    ("Invalid current schema fails without versioning or rebuilding it", InvalidCurrentSchema),
    ("Invalid version-one current schema is still rejected", InvalidVersionedCurrentSchema),
    ("Future current schema version fails without changing it", FutureCurrentVersion),
    ("Future legacy schema version fails without publishing a target", FutureLegacyVersion),
    ("Corrupt legacy data fails without publishing a target", CorruptLegacy),
    ("Destination creation failure preserves the legacy database", DestinationFailure),
    ("Locked legacy database fails with both paths in the diagnostic", LockedLegacy),
    ("Legacy database path that is a directory does not create blank history", LegacyPathDirectory)
};

var failed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}{Environment.NewLine}{exception}");
    }
}

Console.WriteLine($"{tests.Length - failed}/{tests.Length} regression tests passed.");
return failed == 0 ? 0 : 1;

static void CleanInstall()
{
    using var workspace = new TestWorkspace();
    var store = new UsageStore(workspace.Root);

    Equal(workspace.CurrentPath, store.DatabasePath, "Database path");
    Require(File.Exists(workspace.CurrentPath), "The current database must be created.");
    Require(!Directory.Exists(Path.GetDirectoryName(workspace.LegacyPath)), "A clean install must not create a legacy directory.");
    Equal(1L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Schema version");
    Equal((0L, 0L), store.GetTotals(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)), "Fresh totals");

    using var connection = Open(workspace.CurrentPath);
    using var command = connection.CreateCommand();
    command.CommandText = "PRAGMA table_info(daily_usage);";
    using var reader = command.ExecuteReader();
    var columns = new List<string>();
    while (reader.Read())
        columns.Add(reader.GetString(1));
    Require(columns.SequenceEqual(new[] { "day", "downloaded", "uploaded" }), "The existing three-column usage schema must remain intact.");
}

static void LegacyUpgrade()
{
    using var workspace = new TestWorkspace();
    CreateFixture(workspace.LegacyPath);
    var original = Hash(workspace.LegacyPath);

    var store = new UsageStore(workspace.Root);

    Equal(workspace.CurrentPath, store.DatabasePath, "Migrated database path");
    AssertFixtureTotals(store);
    Equal(1L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Migrated schema version");
    Equal(0L, ReadInteger(workspace.LegacyPath, "PRAGMA user_version;"), "Legacy schema version");
    Equal(1L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM legacy_metadata WHERE value = 'keep this table';"), "Other legacy tables");
    Equal(original, Hash(workspace.LegacyPath), "Legacy database bytes");
}

static void ExistingUnversionedDatabase()
{
    using var workspace = new TestWorkspace();
    CreateFixture(workspace.CurrentPath);

    var store = new UsageStore(workspace.Root);

    AssertFixtureTotals(store);
    Equal(1L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Existing database schema version");
    Equal(1L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM legacy_metadata;"), "Existing unrelated table");
    Require(!File.Exists(workspace.LegacyPath), "Existing NetTrace startup must not create a legacy database.");
}

static void RepeatLaunch()
{
    using var workspace = new TestWorkspace();
    CreateFixture(workspace.LegacyPath);
    var sourceHash = Hash(workspace.LegacyPath);
    var first = new UsageStore(workspace.Root);
    var day = new DateOnly(2026, 1, 15);
    first.AddUsage(day, 500, 100);

    var second = new UsageStore(workspace.Root);

    Equal((123_956L, 7_990L), second.GetTotals(day, day), "Current data takes precedence over legacy data");
    Equal(sourceHash, Hash(workspace.LegacyPath), "Legacy database after repeated launch");
    Equal(0L, ReadInteger(workspace.LegacyPath, "PRAGMA user_version;"), "Legacy version after repeated launch");
}

static void PersistedTotals()
{
    using var workspace = new TestWorkspace();
    var today = new DateOnly(2026, 1, 15);
    var store = new UsageStore(workspace.Root);
    store.AddUsage(today, 1_000, 200);
    store.AddUsage(today, 500, 100);
    store.AddUsage(today.AddDays(-6), 3_000, 400);
    store.AddUsage(today.AddDays(-29), 5_000, 600);
    store.AddUsage(today.AddDays(-30), 9_000, 800);
    SqliteConnection.ClearAllPools();

    var reopened = new UsageStore(workspace.Root);

    Equal((1_500L, 300L), reopened.GetTotals(today, today), "Today totals");
    Equal((4_500L, 700L), reopened.GetTotals(today.AddDays(-6), today), "Seven-day inclusive totals");
    Equal((9_500L, 1_300L), reopened.GetTotals(today.AddDays(-29), today), "Thirty-day inclusive totals");
    Equal(1L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Version after reopening");
}

static void WalUpgrade()
{
    using var workspace = new TestWorkspace();
    CreateFixture(workspace.LegacyPath);
    // Keep the writer open so SQLite cannot checkpoint the WAL on last close.
    using var writer = Open(workspace.LegacyPath);
    Execute(writer, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
    Execute(writer, "INSERT INTO daily_usage VALUES ('2026-01-16', 987654, 321);");
    var walPath = workspace.LegacyPath + "-wal";
    Require(File.Exists(walPath) && new FileInfo(walPath).Length > 0, "The fixture must contain an uncheckpointed WAL.");
    var databaseHash = Hash(workspace.LegacyPath);
    var walHash = Hash(walPath);

    var store = new UsageStore(workspace.Root);

    Equal((987_654L, 321L), store.GetTotals(new DateOnly(2026, 1, 16), new DateOnly(2026, 1, 16)), "WAL-only history");
    AssertFixtureTotals(store);
    Equal(1L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "WAL copy schema version");
    Equal(databaseHash, Hash(workspace.LegacyPath), "Legacy main file during WAL migration");
    Equal(walHash, Hash(walPath), "Legacy WAL during migration");
    Equal(0L, ReadInteger(workspace.LegacyPath, "PRAGMA user_version;"), "Legacy WAL schema version");
}

static void InvalidLegacySchema()
{
    using var workspace = new TestWorkspace();
    CreateFixture(workspace.LegacyPath, schema: "CREATE TABLE daily_usage (day TEXT PRIMARY KEY, downloaded INTEGER NOT NULL DEFAULT 0);");
    var original = Hash(workspace.LegacyPath);

    ExpectStartupFailure(workspace.Root);

    Require(!File.Exists(workspace.CurrentPath), "An invalid legacy schema must not become the current database.");
    Equal(original, Hash(workspace.LegacyPath), "Invalid legacy database bytes");
    Equal(0L, ReadInteger(workspace.LegacyPath, "PRAGMA user_version;"), "Invalid legacy schema version");
}

static void InvalidCurrentSchema()
{
    using var workspace = new TestWorkspace();
    CreateFixture(workspace.CurrentPath, schema: "CREATE TABLE daily_usage (day TEXT PRIMARY KEY, downloaded INTEGER NOT NULL DEFAULT 0);");
    var original = Hash(workspace.CurrentPath);

    ExpectStartupFailure(workspace.Root);

    Equal(original, Hash(workspace.CurrentPath), "Invalid current database bytes");
    Equal(0L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Invalid current schema version");
}

static void FutureCurrentVersion()
{
    using var workspace = new TestWorkspace();
    CreateFixture(workspace.CurrentPath, version: 2);
    var original = Hash(workspace.CurrentPath);

    ExpectStartupFailure(workspace.Root);

    Equal(original, Hash(workspace.CurrentPath), "Future current database bytes");
    Equal(2L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Future current version");
}

static void InvalidVersionedCurrentSchema()
{
    using var workspace = new TestWorkspace();
    CreateFixture(workspace.CurrentPath, version: 1, schema: "CREATE TABLE daily_usage (day TEXT PRIMARY KEY, downloaded INTEGER NOT NULL DEFAULT 0);");
    var original = Hash(workspace.CurrentPath);

    ExpectStartupFailure(workspace.Root);

    Equal(original, Hash(workspace.CurrentPath), "Invalid version-one current database bytes");
    Equal(1L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Rejected current schema version");
}

static void FutureLegacyVersion()
{
    using var workspace = new TestWorkspace();
    CreateFixture(workspace.LegacyPath, version: 2);
    var original = Hash(workspace.LegacyPath);

    ExpectStartupFailure(workspace.Root);

    Require(!File.Exists(workspace.CurrentPath), "A future legacy schema must not be published as a usable current database.");
    Equal(original, Hash(workspace.LegacyPath), "Future legacy database bytes");
    Equal(2L, ReadInteger(workspace.LegacyPath, "PRAGMA user_version;"), "Future legacy version");
}

static void CorruptLegacy()
{
    using var workspace = new TestWorkspace();
    Directory.CreateDirectory(Path.GetDirectoryName(workspace.LegacyPath)!);
    File.WriteAllText(workspace.LegacyPath, "Deliberately invalid SQLite fixture.");
    var original = Hash(workspace.LegacyPath);

    ExpectStartupFailure(workspace.Root);

    Require(!File.Exists(workspace.CurrentPath), "A corrupt legacy file must not leave a new database that hides the failed migration.");
    Equal(original, Hash(workspace.LegacyPath), "Corrupt legacy bytes");
}

static void DestinationFailure()
{
    using var workspace = new TestWorkspace();
    CreateFixture(workspace.LegacyPath);
    var original = Hash(workspace.LegacyPath);
    var blockedDirectory = Path.GetDirectoryName(workspace.CurrentPath)!;
    File.WriteAllText(blockedDirectory, "A file obstructs creation of the destination directory.");

    ExpectStartupFailure(workspace.Root);

    Equal(original, Hash(workspace.LegacyPath), "Legacy database after destination failure");
    Require(!File.Exists(workspace.CurrentPath), "A destination failure must not report a published target.");
    Require(File.Exists(blockedDirectory), "An obstructing existing file must remain untouched.");
}

static void LockedLegacy()
{
    using var workspace = new TestWorkspace();
    CreateFixture(workspace.LegacyPath);
    var original = Hash(workspace.LegacyPath);
    Exception failure;

    using (File.Open(workspace.LegacyPath, FileMode.Open, FileAccess.Read, FileShare.None))
        failure = ExpectStartupFailure(workspace.Root);

    Require(failure.Message.Contains(workspace.LegacyPath, StringComparison.Ordinal), "The copy failure must identify its legacy source.");
    Require(failure.Message.Contains(workspace.CurrentPath, StringComparison.Ordinal), "The copy failure must identify its NetTrace destination.");
    Require(!File.Exists(workspace.CurrentPath), "A locked legacy source must not publish a current database.");
    Equal(original, Hash(workspace.LegacyPath), "Locked legacy database bytes after release");
    Equal(0L, ReadInteger(workspace.LegacyPath, "PRAGMA user_version;"), "Locked legacy schema version after release");
}

static void LegacyPathDirectory()
{
    using var workspace = new TestWorkspace();
    Directory.CreateDirectory(workspace.LegacyPath);
    var sentinel = Path.Combine(workspace.LegacyPath, "preserve.txt");
    File.WriteAllText(sentinel, "Preserve this existing directory and its contents.");
    var original = Hash(sentinel);

    var failure = ExpectStartupFailure(workspace.Root);

    Require(failure.Message.Contains(workspace.LegacyPath, StringComparison.Ordinal), "The diagnostic must identify the invalid legacy database path.");
    Require(!File.Exists(workspace.CurrentPath), "An invalid legacy path must not be treated as absent and replaced by blank current history.");
    Require(Directory.Exists(workspace.LegacyPath), "The existing legacy path directory must remain.");
    Equal(original, Hash(sentinel), "Legacy path directory contents");
}

static void CreateFixture(string path, int version = 0, string? schema = null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var connection = Open(path);
    Execute(connection, schema ?? """
        CREATE TABLE daily_usage (
            day TEXT PRIMARY KEY,
            downloaded INTEGER NOT NULL DEFAULT 0,
            uploaded INTEGER NOT NULL DEFAULT 0
        );
        INSERT INTO daily_usage VALUES ('2026-01-15', 123456, 7890);
        INSERT INTO daily_usage VALUES ('2026-01-10', 1000, 100);
        INSERT INTO daily_usage VALUES ('2025-12-20', 2000, 200);
        CREATE TABLE legacy_metadata (value TEXT NOT NULL);
        INSERT INTO legacy_metadata VALUES ('keep this table');
        """);
    Execute(connection, $"PRAGMA user_version={version};");
}

static void AssertFixtureTotals(UsageStore store)
{
    Equal((123_456L, 7_890L), store.GetTotals(new DateOnly(2026, 1, 15), new DateOnly(2026, 1, 15)), "Migrated daily totals");
    Equal((124_456L, 7_990L), store.GetTotals(new DateOnly(2026, 1, 9), new DateOnly(2026, 1, 15)), "Migrated seven-day totals");
    Equal((126_456L, 8_190L), store.GetTotals(new DateOnly(2025, 12, 17), new DateOnly(2026, 1, 15)), "Migrated thirty-day totals");
}

static Exception ExpectStartupFailure(string root)
{
    try
    {
        _ = new UsageStore(root);
    }
    catch (Exception exception)
    {
        Require(!string.IsNullOrWhiteSpace(exception.Message), "Startup failures must provide a diagnostic message.");
        return exception;
    }

    throw new InvalidOperationException("Startup unexpectedly accepted an unusable database or destination.");
}

static SqliteConnection Open(string path)
{
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Pooling = false,
        DefaultTimeout = 1
    }.ToString());
    connection.Open();
    return connection;
}

static long ReadInteger(string path, string sql)
{
    using var connection = Open(path);
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    return Convert.ToInt64(command.ExecuteScalar());
}

static void Execute(SqliteConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.ExecuteNonQuery();
}

static string Hash(string path)
{
    using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    return Convert.ToHexString(SHA256.HashData(stream));
}

static void Equal<T>(T expected, T actual, string description)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{description}: expected {expected}, got {actual}.");
}

static void Require(bool condition, string description)
{
    if (!condition)
        throw new InvalidOperationException(description);
}

sealed class TestWorkspace : IDisposable
{
    private readonly string _parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NetTrace.RegressionTests"));
    public string Root { get; }
    public string CurrentPath => Path.Combine(Root, "NetTrace", "usage.db");
    public string LegacyPath => Path.Combine(Root, "DataUsageMonitor", "usage.db");

    public TestWorkspace()
    {
        Root = Path.Combine(_parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        var resolved = Path.GetFullPath(Root);
        if (!resolved.StartsWith(_parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to remove a test directory outside the temporary test parent.");
        Directory.Delete(resolved, recursive: true);
    }
}
