using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace NetTrace.Services;

public sealed class UsageStore
{
    private readonly string _connectionString;

    public string DatabasePath { get; }

    public UsageStore()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
    {
    }

    // Accept an explicit root so migration can be verified without touching real user data.
    public UsageStore(string localApplicationDataDirectory)
        : this(localApplicationDataDirectory, null)
    {
    }

    public static string GetDatabasePath(string localApplicationDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationDataDirectory);
        return Path.GetFullPath(Path.Combine(localApplicationDataDirectory, "NetTrace", "usage.db"));
    }

    // Application startup supplies its lifetime lease; isolated storage initialization uses a scope.
    public UsageStore(string localApplicationDataDirectory, DatabaseRunLease? initializationLease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationDataDirectory);
        var directory = Path.Combine(localApplicationDataDirectory, "NetTrace");
        var legacyPath = Path.Combine(localApplicationDataDirectory, "DataUsageMonitor", "usage.db");
        DatabasePath = GetDatabasePath(localApplicationDataDirectory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString();

        DatabaseRunLease? scopedLease = null;
        try
        {
            if (initializationLease is null) scopedLease = new DatabaseRunLease(DatabasePath);
            else initializationLease.EnsureOwns(DatabasePath);
            Directory.CreateDirectory(directory);
            var createSchema = !DatabaseFileExists(DatabasePath);
            if (createSchema && DatabaseFileExists(legacyPath))
            {
                CopyLegacyDatabase(legacyPath);
                createSchema = false;
            }

            Initialize(createSchema);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or SqliteException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Could not initialize NetTrace history at '{DatabasePath}'.\n{exception.Message}",
                exception);
        }
        finally
        {
            scopedLease?.Dispose();
        }
    }

    private static bool DatabaseFileExists(string path)
    {
        try
        {
            // File.Exists also returns false on access errors; those must stop migration.
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                throw new IOException($"The database path '{path}' refers to a directory.");
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private void CopyLegacyDatabase(string legacyPath)
    {
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(DatabasePath)!, $".usage-migration-{Guid.NewGuid():N}.db");

        try
        {
            var sourceConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = legacyPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
            var destinationConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = temporaryPath,
                Pooling = false
            }.ToString();

            using (var source = new SqliteConnection(sourceConnectionString))
            using (var destination = new SqliteConnection(destinationConnectionString))
            {
                source.Open();
                destination.Open();
                // A raw file copy can omit committed usage still present in the legacy WAL.
                source.BackupDatabase(destination);

                // Make the staged copy self-contained before renaming it into place.
                using var journal = destination.CreateCommand();
                journal.CommandText = "PRAGMA journal_mode=DELETE;";
                journal.ExecuteNonQuery();
                DatabaseMigrator.Initialize(destination, createSchema: false);
            }

            try
            {
                // Never replace an existing database, including one created by another startup.
                File.Move(temporaryPath, DatabasePath, overwrite: false);
            }
            catch (IOException)
            {
                if (!DatabaseFileExists(DatabasePath))
                    throw;
                // A concurrent startup won the race; its NetTrace database takes precedence.
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or SqliteException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Could not copy legacy history from '{legacyPath}' to '{DatabasePath}'. " +
                $"The legacy database was not modified or deleted.\n{exception.Message}",
                exception);
        }
        finally
        {
            // Only remove files belonging to this attempt, never either usage.db.
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                try
                {
                    File.Delete(temporaryPath + suffix);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Trace.TraceError($"Could not remove migration temporary file '{temporaryPath + suffix}': {exception.Message}");
                }
            }
        }
    }

    private void Initialize(bool createSchema)
    {
        var initializationConnectionString = new SqliteConnectionStringBuilder(_connectionString)
        {
            Mode = createSchema ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite
        }.ToString();
        using var connection = new SqliteConnection(initializationConnectionString);
        connection.Open();

        DatabaseMigrator.Initialize(connection, createSchema);

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
    }

    public void AddUsage(DateOnly day, long downloaded, long uploaded)
    {
        if (downloaded <= 0 && uploaded <= 0)
            return;

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO daily_usage(day, downloaded, uploaded)
            VALUES ($day, $downloaded, $uploaded)
            ON CONFLICT(day) DO UPDATE SET
                downloaded = downloaded + excluded.downloaded,
                uploaded = uploaded + excluded.uploaded;
            """;
        command.Parameters.AddWithValue("$day", day.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$downloaded", Math.Max(0, downloaded));
        command.Parameters.AddWithValue("$uploaded", Math.Max(0, uploaded));
        command.ExecuteNonQuery();
    }

    public (long Downloaded, long Uploaded) GetTotals(DateOnly start, DateOnly end)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COALESCE(SUM(downloaded), 0),
                COALESCE(SUM(uploaded), 0)
            FROM daily_usage
            WHERE day >= $start AND day <= $end;
            """;
        command.Parameters.AddWithValue("$start", start.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$end", end.ToString("yyyy-MM-dd"));

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return (0, 0);

        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    public (double DownloadBytesPerSecond, double UploadBytesPerSecond) GetTrafficPeaks(DateOnly day)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT downloaded_peak_bytes_per_second, uploaded_peak_bytes_per_second
            FROM daily_traffic_peaks WHERE day = $day;
            """;
        command.Parameters.AddWithValue("$day", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetDouble(0), reader.GetDouble(1)) : (0, 0);
    }

    public void SaveTrafficPeaks(DateOnly day, double downloadBytesPerSecond, double uploadBytesPerSecond)
    {
        if (!double.IsFinite(downloadBytesPerSecond) || downloadBytesPerSecond < 0)
            throw new ArgumentOutOfRangeException(nameof(downloadBytesPerSecond), "A traffic peak must be finite and nonnegative.");
        if (!double.IsFinite(uploadBytesPerSecond) || uploadBytesPerSecond < 0)
            throw new ArgumentOutOfRangeException(nameof(uploadBytesPerSecond), "A traffic peak must be finite and nonnegative.");

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO daily_traffic_peaks(day, downloaded_peak_bytes_per_second, uploaded_peak_bytes_per_second)
            VALUES ($day, $downloaded, $uploaded)
            ON CONFLICT(day) DO UPDATE SET
                downloaded_peak_bytes_per_second = MAX(downloaded_peak_bytes_per_second, excluded.downloaded_peak_bytes_per_second),
                uploaded_peak_bytes_per_second = MAX(uploaded_peak_bytes_per_second, excluded.uploaded_peak_bytes_per_second);
            """;
        command.Parameters.AddWithValue("$day", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$downloaded", downloadBytesPerSecond);
        command.Parameters.AddWithValue("$uploaded", uploadBytesPerSecond);
        command.ExecuteNonQuery();
    }
}
