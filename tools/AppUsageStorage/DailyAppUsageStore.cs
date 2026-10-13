using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AppUsageStorage;

public sealed class DailyAppUsageStore
{
    public const int CurrentSchemaVersion = 1;
    public const int RetentionDays = 90;
    private readonly string connectionString;
    private readonly bool readOnly;

    public DailyAppUsageStore()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData))
    {
    }

    // Only tests/deployment tooling should override the machine data root.
    public DailyAppUsageStore(string commonApplicationDataDirectory)
        : this(commonApplicationDataDirectory, readOnly: false)
    {
        Initialize();
    }

    private DailyAppUsageStore(string commonApplicationDataDirectory, bool readOnly)
    {
        this.readOnly = readOnly;
        DatabasePath = GetDatabasePath(commonApplicationDataDirectory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = readOnly ? 1 : 5
        }.ToString();
    }

    public string DatabasePath { get; }

    // This reader never creates directories/databases or initializes/migrates a
    // schema. Open and schema failures are reported when a query is attempted.
    public static DailyAppUsageStore OpenReadOnly(string? commonApplicationDataDirectory = null) =>
        new(commonApplicationDataDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), readOnly: true);

    // Privileged maintenance opens only an existing database. Unlike the public
    // writer constructor, this never creates a directory or initializes a schema.
    public static DailyAppUsageStore OpenExistingWritable(string? commonApplicationDataDirectory = null) =>
        new(commonApplicationDataDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), readOnly: false);

    public static string GetDatabasePath(string commonApplicationDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commonApplicationDataDirectory);
        if (!Path.IsPathFullyQualified(commonApplicationDataDirectory))
            throw new ArgumentException("The machine data root must be an absolute path.", nameof(commonApplicationDataDirectory));
        return Path.GetFullPath(Path.Combine(commonApplicationDataDirectory, "NetTrace", "app-usage.db"));
    }

    public void Initialize()
    {
        EnsureWritable();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Failure(AppUsageStorageFailure.OpenFailed, "Could not create the machine data directory.", exception);
        }

        using var connection = Open(create: true);
        try
        {
            AppUsageSchema.Initialize(connection, DatabasePath);
        }
        catch (SqliteException exception)
        {
            var failure = IsCorrupt(exception) ? AppUsageStorageFailure.CorruptDatabase
                : exception.SqliteErrorCode is 1 or 17 ? AppUsageStorageFailure.IncompatibleSchema
                : exception.SqliteErrorCode is 3 or 8 or 14 ? AppUsageStorageFailure.OpenFailed
                : AppUsageStorageFailure.TransactionFailed;
            throw Failure(failure, "Initialization or schema migration failed; keep the database for recovery.", exception);
        }
    }

    public void AddUsageDelta(DateOnly localDate, ApplicationIdentity application,
        long downloadDelta, long uploadDelta, DateTimeOffset lastSeen) =>
        AddUsageDeltas([new(localDate, application, downloadDelta, uploadDelta, lastSeen)]);

    // One batch is one transaction: a failed row leaves every previous row unchanged.
    public void AddUsageDeltas(IReadOnlyCollection<UsageDelta> deltas)
    {
        EnsureWritable();
        ArgumentNullException.ThrowIfNull(deltas);
        var batch = deltas.ToArray();
        foreach (var delta in batch)
        {
            ArgumentNullException.ThrowIfNull(delta);
            ArgumentNullException.ThrowIfNull(delta.Application);
            if (delta.DownloadBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(deltas), "Download deltas cannot be negative.");
            if (delta.UploadBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(deltas), "Upload deltas cannot be negative.");
        }
        if (batch.Length == 0) return;

        using var connection = Open();
        try
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO daily_app_usage
                    (local_date, application_identity, display_name, executable_name, executable_path,
                     download_bytes, upload_bytes, last_seen_utc)
                VALUES ($date, $identity, $display, $name, $path, $download, $upload, $seen)
                ON CONFLICT(local_date, application_identity) DO UPDATE SET
                    download_bytes = download_bytes + excluded.download_bytes,
                    upload_bytes = upload_bytes + excluded.upload_bytes,
                    display_name = CASE WHEN excluded.last_seen_utc >= last_seen_utc
                        THEN excluded.display_name ELSE display_name END,
                    executable_name = CASE WHEN excluded.last_seen_utc >= last_seen_utc
                        THEN excluded.executable_name ELSE executable_name END,
                    executable_path = CASE WHEN excluded.last_seen_utc >= last_seen_utc
                        THEN excluded.executable_path ELSE executable_path END,
                    last_seen_utc = MAX(last_seen_utc, excluded.last_seen_utc);
                """;
            command.Parameters.Add("$date", SqliteType.Text);
            command.Parameters.Add("$identity", SqliteType.Text);
            command.Parameters.Add("$display", SqliteType.Text);
            command.Parameters.Add("$name", SqliteType.Text);
            command.Parameters.Add("$path", SqliteType.Text);
            command.Parameters.Add("$download", SqliteType.Integer);
            command.Parameters.Add("$upload", SqliteType.Integer);
            command.Parameters.Add("$seen", SqliteType.Text);
            command.Prepare();
            foreach (var delta in batch)
            {
                command.Parameters["$date"].Value = Date(delta.LocalDate);
                command.Parameters["$identity"].Value = delta.Application.Key;
                command.Parameters["$display"].Value = delta.Application.DisplayName;
                command.Parameters["$name"].Value = (object?)delta.Application.ExecutableName ?? DBNull.Value;
                command.Parameters["$path"].Value = (object?)delta.Application.ExecutablePath ?? DBNull.Value;
                command.Parameters["$download"].Value = delta.DownloadBytes;
                command.Parameters["$upload"].Value = delta.UploadBytes;
                command.Parameters["$seen"].Value = delta.LastSeen.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        catch (SqliteException exception)
        {
            throw Failure(IsCorrupt(exception) ? AppUsageStorageFailure.CorruptDatabase
                : AppUsageStorageFailure.TransactionFailed, "Usage batch was not committed.", exception);
        }
    }

    public IReadOnlyList<DailyApplicationUsage> GetUsageForDate(DateOnly date) => GetUsageForRange(date, date);

    public IReadOnlyList<DailyApplicationUsage> GetUsageForRange(DateOnly startDate, DateOnly endDate) =>
        GetUsageForRange(startDate, endDate, CancellationToken.None);

    public IReadOnlyList<DailyApplicationUsage> GetUsageForRange(DateOnly startDate, DateOnly endDate,
        CancellationToken cancellationToken)
    {
        if (endDate < startDate)
            throw new ArgumentException("The inclusive date range must end on or after its start.", nameof(endDate));
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        try
        {
            // A short deferred read transaction holds one consistent snapshot
            // for schema validation and all selected rows while checkpoints
            // retain their existing independent write transactions.
            using var transaction = readOnly ? connection.BeginTransaction(deferred: true) : null;
            if (readOnly) AppUsageSchema.ValidateReadOnly(connection, transaction!, DatabasePath);
            cancellationToken.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT local_date, application_identity, display_name, executable_name, executable_path,
                       download_bytes, upload_bytes, last_seen_utc
                FROM daily_app_usage WHERE local_date >= $start AND local_date <= $end
                ORDER BY local_date, application_identity;
                """;
            command.Parameters.AddWithValue("$start", Date(startDate));
            command.Parameters.AddWithValue("$end", Date(endDate));
            using var reader = command.ExecuteReader();
            var rows = new List<DailyApplicationUsage>();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!reader.Read()) break;
                rows.Add(new(DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6),
                    DateTimeOffset.ParseExact(reader.GetString(7), "O", CultureInfo.InvariantCulture)));
            }
            reader.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            transaction?.Commit();
            return rows.AsReadOnly();
        }
        catch (SqliteException exception)
        {
            throw Failure(IsCorrupt(exception) ? AppUsageStorageFailure.CorruptDatabase
                : AppUsageStorageFailure.QueryFailed, "Could not read daily application usage.", exception);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            throw Failure(AppUsageStorageFailure.CorruptDatabase, "A daily aggregate contains invalid data; keep the database for recovery.", exception);
        }
    }

    public static DateOnly GetRetentionCutoff(DateOnly today) =>
        DateOnly.FromDayNumber(Math.Max(DateOnly.MinValue.DayNumber, today.DayNumber - (RetentionDays - 1)));

    // The cutoff itself is retained. Scheduling and choosing 'today' belong to the future service.
    public int RetentionCleanup(DateOnly cutoffDate)
    {
        EnsureWritable();
        using var connection = Open();
        try
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM daily_app_usage WHERE local_date < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", Date(cutoffDate));
            var removed = command.ExecuteNonQuery();
            transaction.Commit();
            return removed;
        }
        catch (SqliteException exception)
        {
            throw Failure(IsCorrupt(exception) ? AppUsageStorageFailure.CorruptDatabase
                : AppUsageStorageFailure.TransactionFailed, "Retention cleanup was not committed.", exception);
        }
    }

    // Validate the known App Usage schema inside the same write transaction as
    // deletion. Keep the database, table, index and schema metadata intact.
    public int ClearHistory()
    {
        EnsureWritable();
        using var connection = Open();
        try
        {
            using var transaction = connection.BeginTransaction();
            AppUsageSchema.ValidateReadOnly(connection, transaction, DatabasePath);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM daily_app_usage;";
            var removed = command.ExecuteNonQuery();
            transaction.Commit();
            return removed;
        }
        catch (SqliteException exception)
        {
            throw Failure(IsCorrupt(exception) ? AppUsageStorageFailure.CorruptDatabase
                : AppUsageStorageFailure.TransactionFailed, "Application usage history was not cleared.", exception);
        }
    }

    private SqliteConnection Open(bool create = false)
    {
        var settings = new SqliteConnectionStringBuilder(connectionString)
        {
            Mode = readOnly ? SqliteOpenMode.ReadOnly
                : create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite
        };
        var connection = new SqliteConnection(settings.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch (SqliteException exception)
        {
            connection.Dispose();
            throw Failure(IsCorrupt(exception) ? AppUsageStorageFailure.CorruptDatabase
                : AppUsageStorageFailure.OpenFailed, "Could not open the daily application usage database.", exception);
        }
    }

    private AppUsageStorageException Failure(AppUsageStorageFailure failure, string message, Exception exception) =>
        new(failure, DatabasePath, message, exception);
    private void EnsureWritable()
    {
        if (readOnly) throw new InvalidOperationException("This daily application usage store is read-only.");
    }
    private static bool IsCorrupt(SqliteException exception) => exception.SqliteErrorCode is 11 or 26;
    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
