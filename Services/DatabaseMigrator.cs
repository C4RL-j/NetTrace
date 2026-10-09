using Microsoft.Data.Sqlite;

namespace NetTrace.Services;

internal static class DatabaseMigrator
{
    internal const int CurrentSchemaVersion = 3;

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
                case 2:
                    ValidateProfileSchema(command);
                    CreateHistorySchema(command);
                    ValidateHistorySchema(command);
                    break;
                default:
                    throw new InvalidOperationException($"No migration is available from schema version {version}.");
            }

            version++;
            command.CommandText = $"PRAGMA user_version={version};";
            command.ExecuteNonQuery();
        }

        ValidateProfileSchema(command);
        ValidateHistorySchema(command);
        EnsureHistoryRelationshipIndexes(command);
        command.CommandText = "PRAGMA foreign_key_check;";
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
                throw new InvalidOperationException("The connection database contains an invalid history or profile relationship. Keep the database for recovery.");
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

    private static void CreateHistorySchema(SqliteCommand command)
    {
        command.CommandText = """
            CREATE TABLE ConnectionSessions (
                Id INTEGER PRIMARY KEY,
                ProfileId INTEGER REFERENCES ConnectionProfiles(Id) ON DELETE RESTRICT,
                NetworkType TEXT NOT NULL CHECK(length(trim(NetworkType)) > 0),
                NetworkIdentifier TEXT NOT NULL COLLATE BINARY CHECK(length(NetworkIdentifier) > 0),
                NetworkFriendlyName TEXT NOT NULL,
                AdapterId TEXT NOT NULL,
                StartedAtUtc TEXT NOT NULL,
                LastConfirmedAtUtc TEXT NOT NULL,
                EndedAtUtc TEXT,
                EndReason TEXT,
                DownloadBytes INTEGER NOT NULL DEFAULT 0 CHECK(DownloadBytes >= 0),
                UploadBytes INTEGER NOT NULL DEFAULT 0 CHECK(UploadBytes >= 0),
                CHECK(LastConfirmedAtUtc >= StartedAtUtc),
                CHECK(EndedAtUtc IS NULL OR EndedAtUtc >= LastConfirmedAtUtc),
                CHECK((EndedAtUtc IS NULL AND EndReason IS NULL) OR
                      (EndedAtUtc IS NOT NULL AND EndReason IS NOT NULL))
            );
            CREATE UNIQUE INDEX UX_ConnectionSessions_Active ON ConnectionSessions((1)) WHERE EndedAtUtc IS NULL;
            CREATE INDEX IX_ConnectionSessions_StartedAtUtc ON ConnectionSessions(StartedAtUtc DESC, Id DESC);
            CREATE INDEX IX_ConnectionSessions_ProfileId ON ConnectionSessions(ProfileId, StartedAtUtc DESC);
            CREATE TABLE ConnectionEvents (
                Id INTEGER PRIMARY KEY,
                OccurredAtUtc TEXT NOT NULL,
                EventType TEXT NOT NULL CHECK(length(trim(EventType)) > 0),
                SessionId INTEGER REFERENCES ConnectionSessions(Id) ON DELETE RESTRICT,
                ProfileId INTEGER REFERENCES ConnectionProfiles(Id) ON DELETE RESTRICT,
                NetworkType TEXT,
                NetworkIdentifier TEXT COLLATE BINARY,
                Message TEXT NOT NULL
            );
            CREATE INDEX IX_ConnectionEvents_OccurredAtUtc ON ConnectionEvents(OccurredAtUtc DESC, Id DESC);
            CREATE INDEX IX_ConnectionEvents_SessionId ON ConnectionEvents(SessionId);
            CREATE INDEX IX_ConnectionEvents_ProfileId ON ConnectionEvents(ProfileId);
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureHistoryRelationshipIndexes(SqliteCommand command)
    {
        // Early version-3 databases may predate these additive indexes. The surrounding
        // initialization transaction adds them without rewriting history or changing its version.
        command.CommandText = """
            CREATE INDEX IF NOT EXISTS IX_ConnectionEvents_SessionId ON ConnectionEvents(SessionId);
            CREATE INDEX IF NOT EXISTS IX_ConnectionEvents_ProfileId ON ConnectionEvents(ProfileId);
            """;
        command.ExecuteNonQuery();
        ValidateHistoryIndex(command, "IX_ConnectionEvents_SessionId", "ConnectionEvents", [("SessionId", false)]);
        ValidateHistoryIndex(command, "IX_ConnectionEvents_ProfileId", "ConnectionEvents", [("ProfileId", false)]);
    }

    private static void ValidateHistorySchema(SqliteCommand command)
    {
        ValidateColumns(command, "ConnectionSessions", new[]
        {
            ("Id", "INTEGER", false, 1L),
            ("ProfileId", "INTEGER", false, 0L),
            ("NetworkType", "TEXT", true, 0L),
            ("NetworkIdentifier", "TEXT", true, 0L),
            ("NetworkFriendlyName", "TEXT", true, 0L),
            ("AdapterId", "TEXT", true, 0L),
            ("StartedAtUtc", "TEXT", true, 0L),
            ("LastConfirmedAtUtc", "TEXT", true, 0L),
            ("EndedAtUtc", "TEXT", false, 0L),
            ("EndReason", "TEXT", false, 0L),
            ("DownloadBytes", "INTEGER", true, 0L),
            ("UploadBytes", "INTEGER", true, 0L)
        });
        ValidateColumns(command, "ConnectionEvents", new[]
        {
            ("Id", "INTEGER", false, 1L),
            ("OccurredAtUtc", "TEXT", true, 0L),
            ("EventType", "TEXT", true, 0L),
            ("SessionId", "INTEGER", false, 0L),
            ("ProfileId", "INTEGER", false, 0L),
            ("NetworkType", "TEXT", false, 0L),
            ("NetworkIdentifier", "TEXT", false, 0L),
            ("Message", "TEXT", true, 0L)
        });
        ValidateHistoryRelationships(command, "ConnectionSessions", new[]
        {
            ("ProfileId", "ConnectionProfiles")
        });
        ValidateHistoryRelationships(command, "ConnectionEvents", new[]
        {
            ("SessionId", "ConnectionSessions"), ("ProfileId", "ConnectionProfiles")
        });
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='index' AND name='UX_ConnectionSessions_Active';";
        var activeIndex = command.ExecuteScalar()?.ToString();
        var normalized = activeIndex is null ? "" : string.Concat(activeIndex.Where(character => !char.IsWhiteSpace(character)));
        if (!normalized.Equals(
            "CREATEUNIQUEINDEXUX_ConnectionSessions_ActiveONConnectionSessions((1))WHEREEndedAtUtcISNULL",
            StringComparison.OrdinalIgnoreCase))
            throw InvalidHistorySchema("The database must enforce at most one unfinished session.");
        ValidateHistoryIndex(command, "IX_ConnectionSessions_StartedAtUtc", "ConnectionSessions",
            [("StartedAtUtc", true), ("Id", true)]);
        ValidateHistoryIndex(command, "IX_ConnectionSessions_ProfileId", "ConnectionSessions",
            [("ProfileId", false), ("StartedAtUtc", true)]);
        ValidateHistoryIndex(command, "IX_ConnectionEvents_OccurredAtUtc", "ConnectionEvents",
            [("OccurredAtUtc", true), ("Id", true)]);
    }

    private static void ValidateHistoryIndex(
        SqliteCommand command, string index, string table, (string Name, bool Descending)[] expected)
    {
        command.CommandText = $"PRAGMA index_list({table});";
        var found = false;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                found |= reader.GetString(1) == index && reader.GetInt64(2) == 0 && reader.GetInt64(4) == 0;
        }

        command.CommandText = $"PRAGMA index_xinfo({index});";
        var columns = new List<(string Name, bool Descending, string Collation)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (reader.GetInt64(5) != 0)
                    columns.Add((reader.IsDBNull(2) ? "" : reader.GetString(2),
                        reader.GetInt64(3) != 0, reader.GetString(4)));
            }
        }

        if (!found || columns.Count != expected.Length || expected.Where((column, position) =>
            !columns[position].Name.Equals(column.Name, StringComparison.OrdinalIgnoreCase)
            || columns[position].Descending != column.Descending
            || !columns[position].Collation.Equals("BINARY", StringComparison.OrdinalIgnoreCase)).Any())
            throw InvalidHistorySchema($"The expected {index} history index is missing or incompatible.");
    }

    private static void ValidateHistoryRelationships(
        SqliteCommand command, string table, (string Column, string Parent)[] expected)
    {
        command.CommandText = $"PRAGMA foreign_key_list({table});";
        using var reader = command.ExecuteReader();
        var found = new List<(string Column, string Parent)>();
        while (reader.Read())
        {
            if (reader.GetInt64(1) != 0 || reader.GetString(4) != "Id" || reader.GetString(6) != "RESTRICT")
                throw InvalidHistorySchema($"The {table} relationships must use stable IDs and restricted deletion.");
            found.Add((reader.GetString(3), reader.GetString(2)));
        }
        if (found.Count != expected.Length || expected.Any(relationship => !found.Contains(relationship)))
            throw InvalidHistorySchema($"The {table} relationships are missing or incompatible.");
    }

    private static InvalidOperationException InvalidHistorySchema(string detail) => new(
        $"The connection history schema is incompatible. {detail} Its data and schema version have not been changed.");

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
            throw InvalidTableSchema(table, $"The {table} table is missing.");

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
            throw InvalidTableSchema(table, $"The {table} columns do not match the supported schema.");
    }

    private static InvalidOperationException InvalidTableSchema(string table, string detail) =>
        table is "ConnectionSessions" or "ConnectionEvents" ? InvalidHistorySchema(detail) : InvalidProfileSchema(detail);

    private static InvalidOperationException InvalidProfileSchema(string detail) => new(
        $"The connection profile schema is incompatible. {detail} Its data and schema version have not been changed.");
}
