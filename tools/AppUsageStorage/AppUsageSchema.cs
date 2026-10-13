using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AppUsageStorage;

internal static class AppUsageSchema
{
    private const int ApplicationId = 0x4E544155; // NTAU: do not mistake the desktop usage.db for this store.
    private const string TableSql = """
        CREATE TABLE daily_app_usage (
            local_date TEXT NOT NULL CHECK(
                length(local_date) = 10
                AND local_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
                AND local_date BETWEEN '0001-01-01' AND '9999-12-31'
                AND date(local_date, '+0 days') IS NOT NULL
                AND date(local_date, '+0 days') = local_date),
            application_identity TEXT NOT NULL CHECK(
                application_identity IN ('system', 'unknown')
                OR (substr(application_identity, 1, 5) = 'path:' AND length(application_identity) > 5)),
            display_name TEXT NOT NULL CHECK(length(trim(display_name)) > 0),
            executable_name TEXT,
            executable_path TEXT,
            download_bytes INTEGER NOT NULL CHECK(typeof(download_bytes) = 'integer' AND download_bytes >= 0),
            upload_bytes INTEGER NOT NULL CHECK(typeof(upload_bytes) = 'integer' AND upload_bytes >= 0),
            last_seen_utc TEXT NOT NULL CHECK(
                length(last_seen_utc) = 33
                AND substr(last_seen_utc, 11, 1) = 'T'
                AND substr(last_seen_utc, 20, 1) = '.'
                AND substr(last_seen_utc, 28) = '+00:00'
                AND julianday(last_seen_utc) IS NOT NULL),
            PRIMARY KEY(local_date, application_identity),
            CHECK((application_identity IN ('system', 'unknown') AND executable_name IS NULL AND executable_path IS NULL)
                OR (substr(application_identity, 1, 5) = 'path:' AND length(executable_name) > 0
                    AND length(executable_path) > 0 AND executable_name IS NOT NULL AND executable_path IS NOT NULL))
        ) WITHOUT ROWID;
        """;
    private const string IndexSql = """
        CREATE INDEX ix_daily_app_usage_application_date ON daily_app_usage(application_identity, local_date);
        """;

    internal static void Initialize(SqliteConnection connection, string databasePath)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var version = Integer(command, "PRAGMA user_version;");
        var applicationId = Integer(command, "PRAGMA application_id;");
        if (version < 0 || version > DailyAppUsageStore.CurrentSchemaVersion
            || (version == 0 ? applicationId != 0 : applicationId != ApplicationId))
            throw Incompatible(databasePath, $"Unsupported schema version {version} or database application ID {applicationId}. The database was not migrated.");

        command.CommandText = "PRAGMA quick_check;";
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(0) != "ok" || reader.Read())
                throw new AppUsageStorageException(AppUsageStorageFailure.CorruptDatabase, databasePath,
                    "SQLite integrity check failed; keep the database for recovery.");
        }

        // Version 0 -> 1 is the first migration. Never adopt an unrecognized existing schema.
        if (version == 0)
        {
            command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*';";
            if (Convert.ToInt64(command.ExecuteScalar()) != 0)
                throw Incompatible(databasePath, "An unversioned database contains an unknown schema. It was not modified.");
            command.CommandText = TableSql + IndexSql;
            command.ExecuteNonQuery();
            command.CommandText = $"PRAGMA application_id={ApplicationId}; PRAGMA user_version={DailyAppUsageStore.CurrentSchemaVersion};";
            command.ExecuteNonQuery();
        }

        ValidateDefinition(command, databasePath, "table", "daily_app_usage", TableSql);
        ValidateDefinition(command, databasePath, "index", "ix_daily_app_usage_application_date", IndexSql);
        command.Parameters.Clear();
        command.CommandText = """
            SELECT COUNT(*) FROM sqlite_schema
            WHERE name NOT GLOB 'sqlite_*'
                AND name NOT IN ('daily_app_usage', 'ix_daily_app_usage_application_date');
            """;
        if (Convert.ToInt64(command.ExecuteScalar()) != 0)
            throw Incompatible(databasePath, "The database contains unexpected schema objects. It was not modified.");
        transaction.Commit();
    }

    // Read-only callers validate the existing schema in their query snapshot.
    // Version zero is unavailable rather than a request to create or migrate.
    internal static void ValidateReadOnly(SqliteConnection connection,
        SqliteTransaction transaction, string databasePath)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var version = Integer(command, "PRAGMA user_version;");
        var applicationId = Integer(command, "PRAGMA application_id;");
        if (version != DailyAppUsageStore.CurrentSchemaVersion || applicationId != ApplicationId)
            throw Incompatible(databasePath, "The existing daily application usage schema is unavailable or incompatible.");

        ValidateDefinition(command, databasePath, "table", "daily_app_usage", TableSql);
        ValidateDefinition(command, databasePath, "index", "ix_daily_app_usage_application_date", IndexSql);
        command.Parameters.Clear();
        command.CommandText = """
            SELECT COUNT(*) FROM sqlite_schema
            WHERE name NOT GLOB 'sqlite_*'
                AND name NOT IN ('daily_app_usage', 'ix_daily_app_usage_application_date');
            """;
        if (Convert.ToInt64(command.ExecuteScalar()) != 0)
            throw Incompatible(databasePath, "The existing database contains unexpected schema objects.");
    }

    private static long Integer(SqliteCommand command, string sql)
    {
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void ValidateDefinition(SqliteCommand command, string databasePath,
        string type, string name, string expected)
    {
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type=$type AND name=$name;";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        if (command.ExecuteScalar() is not string actual || Normalize(actual) != Normalize(expected))
            throw Incompatible(databasePath, $"The {name} definition is incompatible; keep the database for recovery.");
    }

    private static string Normalize(string sql) => Regex.Replace(sql.Trim().TrimEnd(';'), @"\s+", " ");
    private static AppUsageStorageException Incompatible(string path, string message) =>
        new(AppUsageStorageFailure.IncompatibleSchema, path, message);
}
