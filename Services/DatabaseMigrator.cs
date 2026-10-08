using Microsoft.Data.Sqlite;

namespace NetTrace.Services;

internal static class DatabaseMigrator
{
    internal const int CurrentSchemaVersion = 1;

    internal static void Initialize(SqliteConnection connection, bool createSchema)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(command.ExecuteScalar());

        if (version < 0 || version > CurrentSchemaVersion)
            throw new InvalidOperationException(
                $"Unsupported database schema version {version}. This NetTrace build supports version {CurrentSchemaVersion}. " +
                "Use a compatible NetTrace build; the schema version has not been changed.");

        if (createSchema && version == 0)
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS daily_usage (
                    day TEXT PRIMARY KEY,
                    downloaded INTEGER NOT NULL DEFAULT 0,
                    uploaded INTEGER NOT NULL DEFAULT 0
                );
                """;
            command.ExecuteNonQuery();
        }

        ValidateSchema(command);
        command.CommandText = "PRAGMA quick_check;";
        if (!string.Equals(command.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The usage database failed SQLite's integrity check. Keep the database for recovery.");

        // Add future sequential migrations here. Version 0 is the original, unversioned schema.
        while (version < CurrentSchemaVersion)
        {
            switch (version)
            {
                case 0:
                    // The existing table was validated above; no rebuild or data rewrite is needed.
                    break;
                default:
                    throw new InvalidOperationException($"No migration is available from schema version {version}.");
            }

            version++;
            command.CommandText = $"PRAGMA user_version={version};";
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void ValidateSchema(SqliteCommand command)
    {
        command.CommandText = "SELECT type FROM sqlite_schema WHERE name = 'daily_usage' COLLATE NOCASE;";
        if (command.ExecuteScalar()?.ToString() != "table")
            throw new InvalidOperationException("The usage database is missing the expected daily_usage table. It has not been recreated.");

        command.CommandText = "PRAGMA table_info(daily_usage);";
        using var reader = command.ExecuteReader();
        var columns = new Dictionary<string, (string Type, bool NotNull, long PrimaryKey)>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
            columns.Add(reader.GetString(1), (reader.GetString(2), reader.GetInt64(3) != 0, reader.GetInt64(5)));

        if (columns.Count != 3
            || !columns.TryGetValue("day", out var day) || !day.Type.Equals("TEXT", StringComparison.OrdinalIgnoreCase) || day.PrimaryKey != 1
            || !columns.TryGetValue("downloaded", out var downloaded) || !IsUsageColumn(downloaded)
            || !columns.TryGetValue("uploaded", out var uploaded) || !IsUsageColumn(uploaded))
            throw new InvalidOperationException(
                "The daily_usage table does not match the supported usage schema. Its data and schema version have not been changed.");
    }

    private static bool IsUsageColumn((string Type, bool NotNull, long PrimaryKey) column) =>
        column.Type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase) && column.NotNull && column.PrimaryKey == 0;
}
