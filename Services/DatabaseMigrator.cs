using Microsoft.Data.Sqlite;

namespace NetTrace.Services;

internal static class DatabaseMigrator
{
    internal const int CurrentSchemaVersion = 2;

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

        ValidateUsageSchema(command);
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
                case 1:
                    CreateProfileSchema(command);
                    ValidateProfileSchema(command);
                    break;
                default:
                    throw new InvalidOperationException($"No migration is available from schema version {version}.");
            }

            version++;
            command.CommandText = $"PRAGMA user_version={version};";
            command.ExecuteNonQuery();
        }

        ValidateProfileSchema(command);
        command.CommandText = "PRAGMA foreign_key_check(NetworkMappings);";
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
                throw new InvalidOperationException("The profile database contains a mapping without a valid profile. Keep the database for recovery.");
        }

        transaction.Commit();
    }

    private static void CreateProfileSchema(SqliteCommand command)
    {
        command.CommandText = """
            CREATE TABLE ConnectionProfiles (
                Id INTEGER PRIMARY KEY,
                TelcoName TEXT NOT NULL,
                DeviceName TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            CREATE TABLE NetworkMappings (
                Id INTEGER PRIMARY KEY,
                ProfileId INTEGER NOT NULL REFERENCES ConnectionProfiles(Id) ON DELETE RESTRICT,
                NetworkType TEXT NOT NULL,
                NetworkIdentifier TEXT NOT NULL COLLATE BINARY,
                FriendlyName TEXT,
                CreatedAt TEXT NOT NULL,
                UNIQUE (NetworkType, NetworkIdentifier)
            );
            CREATE INDEX IX_NetworkMappings_ProfileId ON NetworkMappings(ProfileId);
            """;
        command.ExecuteNonQuery();
    }

    private static void ValidateUsageSchema(SqliteCommand command)
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

    private static void ValidateProfileSchema(SqliteCommand command)
    {
        ValidateColumns(command, "ConnectionProfiles", new[]
        {
            ("Id", "INTEGER", false, 1L),
            ("TelcoName", "TEXT", true, 0L),
            ("DeviceName", "TEXT", true, 0L),
            ("CreatedAt", "TEXT", true, 0L),
            ("UpdatedAt", "TEXT", true, 0L)
        });
        ValidateColumns(command, "NetworkMappings", new[]
        {
            ("Id", "INTEGER", false, 1L),
            ("ProfileId", "INTEGER", true, 0L),
            ("NetworkType", "TEXT", true, 0L),
            ("NetworkIdentifier", "TEXT", true, 0L),
            ("FriendlyName", "TEXT", false, 0L),
            ("CreatedAt", "TEXT", true, 0L)
        });

        command.CommandText = "PRAGMA foreign_key_list(NetworkMappings);";
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()
                || reader.GetInt64(1) != 0
                || !reader.GetString(2).Equals("ConnectionProfiles", StringComparison.OrdinalIgnoreCase)
                || !reader.GetString(3).Equals("ProfileId", StringComparison.OrdinalIgnoreCase)
                || !reader.GetString(4).Equals("Id", StringComparison.OrdinalIgnoreCase)
                || !reader.GetString(6).Equals("RESTRICT", StringComparison.OrdinalIgnoreCase)
                || reader.Read())
                throw InvalidProfileSchema("NetworkMappings must reference ConnectionProfiles with restricted deletion.");
        }

        command.CommandText = "PRAGMA index_list(NetworkMappings);";
        var indexes = new List<(string Name, bool Unique, bool Partial)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                indexes.Add((reader.GetString(1), reader.GetInt64(2) != 0, reader.GetInt64(4) != 0));
        }

        var hasUniqueIdentity = false;
        var hasProfileIndex = false;
        foreach (var index in indexes.Where(index => !index.Partial))
        {
            command.CommandText = $"PRAGMA index_xinfo(\"{index.Name.Replace("\"", "\"\"")}\");";
            var columns = new List<(string Name, string Collation)>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (reader.GetInt64(5) != 0)
                        columns.Add((reader.IsDBNull(2) ? "" : reader.GetString(2), reader.GetString(4)));
                }
            }

            if (index.Unique && columns.Count == 2
                && columns[0].Name.Equals("NetworkType", StringComparison.OrdinalIgnoreCase)
                && columns[1].Name.Equals("NetworkIdentifier", StringComparison.OrdinalIgnoreCase)
                && columns.All(column => column.Collation.Equals("BINARY", StringComparison.OrdinalIgnoreCase)))
                hasUniqueIdentity = true;
            if (!index.Unique && columns.Count > 0 && columns[0].Name.Equals("ProfileId", StringComparison.OrdinalIgnoreCase))
                hasProfileIndex = true;
        }

        if (!hasUniqueIdentity || !hasProfileIndex)
            throw InvalidProfileSchema("NetworkMappings must enforce unique exact network identities and index ProfileId.");
    }

    private static void ValidateColumns(
        SqliteCommand command,
        string table,
        (string Name, string Type, bool NotNull, long PrimaryKey)[] expected)
    {
        command.CommandText = "SELECT type FROM sqlite_schema WHERE name = $table COLLATE NOCASE;";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$table", table);
        var type = command.ExecuteScalar()?.ToString();
        command.Parameters.Clear();
        if (type != "table")
            throw InvalidProfileSchema($"The {table} table is missing.");

        command.CommandText = $"PRAGMA table_info({table});";
        var actual = new Dictionary<string, (string Type, bool NotNull, long PrimaryKey)>(StringComparer.OrdinalIgnoreCase);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                actual.Add(reader.GetString(1), (reader.GetString(2), reader.GetInt64(3) != 0, reader.GetInt64(5)));
        }

        if (actual.Count != expected.Length || expected.Any(column =>
            !actual.TryGetValue(column.Name, out var found)
            || !found.Type.Equals(column.Type, StringComparison.OrdinalIgnoreCase)
            || found.NotNull != column.NotNull || found.PrimaryKey != column.PrimaryKey))
            throw InvalidProfileSchema($"The {table} columns do not match the supported schema.");
    }

    private static InvalidOperationException InvalidProfileSchema(string detail) => new(
        $"The connection profile schema is incompatible. {detail} Its data and schema version have not been changed.");
}
