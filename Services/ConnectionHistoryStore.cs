using System.Globalization;
using Microsoft.Data.Sqlite;
using NetTrace.Models;

namespace NetTrace.Services;

public sealed class ConnectionHistoryStore
{
    private const string SessionSelect = """
        SELECT s.Id, s.ProfileId, s.NetworkType, s.NetworkIdentifier, s.NetworkFriendlyName,
               s.AdapterId, s.StartedAtUtc, s.LastConfirmedAtUtc, s.EndedAtUtc, s.EndReason,
               s.DownloadBytes, s.UploadBytes,
               CASE WHEN p.Id IS NULL THEN NULL ELSE p.TelcoName || ' — ' || p.DeviceName END
        FROM ConnectionSessions s LEFT JOIN ConnectionProfiles p ON p.Id=s.ProfileId
        """;

    private readonly string _connectionString;

    public ConnectionHistoryStore(UsageStore usageStore)
        : this((usageStore ?? throw new ArgumentNullException(nameof(usageStore))).DatabasePath)
    {
    }

    // UsageStore initializes/migrates the database before history operations begin.
    public ConnectionHistoryStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true
        }.ToString();
    }

    public ConnectionSession? GetActiveSession()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = SessionSelect + " WHERE s.EndedAtUtc IS NULL;";
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSession(reader) : null;
    }

    public IReadOnlyList<ConnectionSession> GetRecentSessions(int limit = 100)
    {
        ValidateLimit(limit);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = SessionSelect + " ORDER BY s.StartedAtUtc DESC, s.Id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        return ReadSessions(command);
    }

    // Half-open UTC window; include sessions whose last observed interval overlaps the range.
    public IReadOnlyList<ConnectionSession> GetSessions(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        if (toUtc <= fromUtc)
            throw new ArgumentException("The session date range must end after it starts.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = SessionSelect + """
             WHERE s.StartedAtUtc < $to AND COALESCE(s.EndedAtUtc,s.LastConfirmedAtUtc) >= $from
             ORDER BY s.StartedAtUtc DESC, s.Id DESC;
            """;
        command.Parameters.AddWithValue("$from", Timestamp(fromUtc));
        command.Parameters.AddWithValue("$to", Timestamp(toUtc));
        return ReadSessions(command);
    }

    public IReadOnlyList<ConnectionSession> GetSessionsByProfile(long profileId, int limit = 100)
    {
        ValidateLimit(limit);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = SessionSelect + """
             WHERE s.ProfileId=$profileId ORDER BY s.StartedAtUtc DESC, s.Id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$limit", limit);
        return ReadSessions(command);
    }

    public IReadOnlyList<ConnectionEvent> GetRecentEvents(int limit = 100)
    {
        ValidateLimit(limit);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, OccurredAtUtc, EventType, SessionId, ProfileId, NetworkType, NetworkIdentifier, Message
            FROM ConnectionEvents ORDER BY OccurredAtUtc DESC, Id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var events = new List<ConnectionEvent>();
        while (reader.Read())
            events.Add(new ConnectionEvent(reader.GetInt64(0), ReadTimestamp(reader.GetString(1)),
                reader.GetString(2), NullableInteger(reader, 3), NullableInteger(reader, 4),
                NullableText(reader, 5), NullableText(reader, 6), reader.GetString(7)));
        return events;
    }

    public IReadOnlyList<ConnectionSession> RecoverUnfinishedSessions(DateTimeOffset recoveredAtUtc)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SessionSelect + " WHERE s.EndedAtUtc IS NULL ORDER BY s.Id;";
        var stale = ReadSessions(command);
        var recovered = new List<ConnectionSession>();
        foreach (var session in stale)
        {
            // A crash supplies no evidence of connectivity after the last persisted observation.
            CloseSession(connection, transaction, session, session.LastConfirmedAtUtc,
                "UnexpectedExit", session.DownloadBytes, session.UploadBytes);
            InsertEvent(connection, transaction, recoveredAtUtc, "SessionRecoveredAfterUnexpectedExit",
                session.Id, session.ProfileId, NetworkOf(session),
                "Previous session ended at its last confirmed observation after an unexpected exit.");
            recovered.Add(session with { EndedAtUtc = session.LastConfirmedAtUtc, EndReason = "UnexpectedExit" });
        }
        transaction.Commit();
        return recovered;
    }

    // Closing the old session, opening its successor and recording a transition are one commit.
    public ConnectionSession? Transition(
        long? previousSessionId,
        DateTimeOffset endedAtUtc,
        string? endReason,
        long downloadTotal,
        long uploadTotal,
        NetworkIdentity? newNetwork,
        long? newProfileId,
        DateTimeOffset occurredAtUtc,
        string? eventType,
        string? eventMessage)
    {
        ValidateTotals(downloadTotal, uploadTotal);
        ValidateNetwork(newNetwork);
        if (newNetwork is null && newProfileId is not null)
            throw new ArgumentException("A session profile requires a valid physical network identity.");
        if (previousSessionId is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(endReason);
        if (eventType is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        ConnectionSession? previous = null;
        if (previousSessionId is not null)
        {
            previous = RequireActiveSession(connection, transaction, previousSessionId.Value);
            CloseSession(connection, transaction, previous, endedAtUtc, endReason!, downloadTotal, uploadTotal);
        }

        var next = newNetwork is null ? null : InsertSession(connection, transaction,
            newNetwork, newProfileId, occurredAtUtc);
        if (eventType is not null)
            InsertEvent(connection, transaction, occurredAtUtc, eventType,
                next?.Id ?? previous?.Id,
                next is not null ? next.ProfileId : previous?.ProfileId,
                newNetwork ?? (previous is null ? null : NetworkOf(previous)), eventMessage ?? "");
        transaction.Commit();
        return next;
    }

    // Absolute totals make a retry safe: persisting the same progress twice never adds bytes twice.
    public void Confirm(long sessionId, DateTimeOffset confirmedAtUtc, long downloadTotal, long uploadTotal)
    {
        ValidateTotals(downloadTotal, uploadTotal);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var session = RequireActiveSession(connection, transaction, sessionId);
        ValidateProgress(session, downloadTotal, uploadTotal);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE ConnectionSessions SET LastConfirmedAtUtc=$confirmed,
                DownloadBytes=$download, UploadBytes=$upload WHERE Id=$id AND EndedAtUtc IS NULL;
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$confirmed", Timestamp(Later(session.LastConfirmedAtUtc, confirmedAtUtc)));
        command.Parameters.AddWithValue("$download", downloadTotal);
        command.Parameters.AddWithValue("$upload", uploadTotal);
        if (command.ExecuteNonQuery() != 1)
            throw StaleSession();
        transaction.Commit();
    }

    public void UpdateActiveProfile(
        long sessionId, long? profileId, DateTimeOffset occurredAtUtc, string eventType, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentNullException.ThrowIfNull(message);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var session = RequireActiveSession(connection, transaction, sessionId);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Only the active session is associated; completed historical sessions are never rewritten.
        command.CommandText = "UPDATE ConnectionSessions SET ProfileId=$profileId WHERE Id=$id AND EndedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$profileId", DbValue(profileId));
        command.Parameters.AddWithValue("$id", sessionId);
        if (command.ExecuteNonQuery() != 1)
            throw StaleSession();
        InsertEvent(connection, transaction, occurredAtUtc, eventType,
            sessionId, profileId, NetworkOf(session), message);
        transaction.Commit();
    }

    public void RecordEvent(
        DateTimeOffset occurredAtUtc, string eventType, long? sessionId,
        long? profileId, NetworkIdentity? network, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentNullException.ThrowIfNull(message);
        ValidateNetwork(network);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        InsertEvent(connection, transaction, occurredAtUtc, eventType, sessionId, profileId, network, message);
        transaction.Commit();
    }

    private static ConnectionSession InsertSession(
        SqliteConnection connection, SqliteTransaction transaction,
        NetworkIdentity network, long? profileId, DateTimeOffset startedAtUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ConnectionSessions(ProfileId, NetworkType, NetworkIdentifier, NetworkFriendlyName,
                AdapterId, StartedAtUtc, LastConfirmedAtUtc)
            VALUES ($profileId, $type, $identifier, $friendlyName, $adapterId, $started, $started)
            RETURNING Id;
            """;
        command.Parameters.AddWithValue("$profileId", DbValue(profileId));
        command.Parameters.AddWithValue("$type", network.NetworkType);
        command.Parameters.AddWithValue("$identifier", network.NetworkIdentifier);
        command.Parameters.AddWithValue("$friendlyName", network.FriendlyName ?? network.NetworkIdentifier);
        command.Parameters.AddWithValue("$adapterId", network.AdapterId ?? "");
        command.Parameters.AddWithValue("$started", Timestamp(startedAtUtc));
        var id = Convert.ToInt64(command.ExecuteScalar());
        return ReadSession(connection, transaction, id)
            ?? throw new InvalidOperationException("The connection session could not be saved.");
    }

    private static void CloseSession(
        SqliteConnection connection, SqliteTransaction transaction, ConnectionSession session,
        DateTimeOffset endedAtUtc, string endReason, long downloadTotal, long uploadTotal)
    {
        ValidateProgress(session, downloadTotal, uploadTotal);
        var end = Later(session.LastConfirmedAtUtc, endedAtUtc);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE ConnectionSessions SET LastConfirmedAtUtc=$ended, EndedAtUtc=$ended, EndReason=$reason,
                DownloadBytes=$download, UploadBytes=$upload WHERE Id=$id AND EndedAtUtc IS NULL;
            """;
        command.Parameters.AddWithValue("$id", session.Id);
        command.Parameters.AddWithValue("$ended", Timestamp(end));
        command.Parameters.AddWithValue("$reason", endReason);
        command.Parameters.AddWithValue("$download", downloadTotal);
        command.Parameters.AddWithValue("$upload", uploadTotal);
        if (command.ExecuteNonQuery() != 1)
            throw StaleSession();
    }

    private static void InsertEvent(
        SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset occurredAtUtc,
        string eventType, long? sessionId, long? profileId, NetworkIdentity? network, string message)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ConnectionEvents(OccurredAtUtc, EventType, SessionId, ProfileId, NetworkType, NetworkIdentifier, Message)
            VALUES ($occurred, $type, $sessionId, $profileId, $networkType, $identifier, $message);
            """;
        command.Parameters.AddWithValue("$occurred", Timestamp(occurredAtUtc));
        command.Parameters.AddWithValue("$type", eventType);
        command.Parameters.AddWithValue("$sessionId", DbValue(sessionId));
        command.Parameters.AddWithValue("$profileId", DbValue(profileId));
        command.Parameters.AddWithValue("$networkType", DbValue(network?.NetworkType));
        command.Parameters.AddWithValue("$identifier", DbValue(network?.NetworkIdentifier));
        command.Parameters.AddWithValue("$message", message);
        command.ExecuteNonQuery();
    }

    private static ConnectionSession RequireActiveSession(
        SqliteConnection connection, SqliteTransaction transaction, long id)
    {
        var session = ReadSession(connection, transaction, id);
        return session is { EndedAtUtc: null } ? session : throw StaleSession();
    }

    private static ConnectionSession? ReadSession(SqliteConnection connection, SqliteTransaction transaction, long id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SessionSelect + " WHERE s.Id=$id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSession(reader) : null;
    }

    private static IReadOnlyList<ConnectionSession> ReadSessions(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var sessions = new List<ConnectionSession>();
        while (reader.Read())
            sessions.Add(ReadSession(reader));
        return sessions;
    }

    private static ConnectionSession ReadSession(SqliteDataReader reader) => new(
        reader.GetInt64(0), NullableInteger(reader, 1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), ReadTimestamp(reader.GetString(6)),
        ReadTimestamp(reader.GetString(7)), reader.IsDBNull(8) ? null : ReadTimestamp(reader.GetString(8)),
        NullableText(reader, 9), reader.GetInt64(10), reader.GetInt64(11), NullableText(reader, 12));

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void ValidateTotals(long downloadTotal, long uploadTotal)
    {
        if (downloadTotal < 0 || uploadTotal < 0)
            throw new ArgumentOutOfRangeException(nameof(downloadTotal), "Session byte totals cannot be negative.");
    }

    private static void ValidateProgress(ConnectionSession session, long downloadTotal, long uploadTotal)
    {
        if (downloadTotal < session.DownloadBytes || uploadTotal < session.UploadBytes)
            throw new InvalidOperationException("Connection session byte totals cannot decrease.");
    }

    private static void ValidateNetwork(NetworkIdentity? network)
    {
        if (network is not null && !network.HasValidIdentifier)
            throw new ArgumentException("A connection session requires a valid physical network identity.", nameof(network));
    }

    private static void ValidateLimit(int limit)
    {
        if (limit < 1 || limit > 10_000)
            throw new ArgumentOutOfRangeException(nameof(limit), "History query limits must be between 1 and 10000.");
    }

    private static NetworkIdentity NetworkOf(ConnectionSession session) => new(
        session.NetworkType, session.NetworkIdentifier, session.NetworkFriendlyName, session.AdapterId);

    private static object DbValue(object? value) => value ?? DBNull.Value;
    private static long? NullableInteger(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt64(index);
    private static string? NullableText(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static DateTimeOffset Later(DateTimeOffset first, DateTimeOffset second) => first >= second ? first : second;
    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ReadTimestamp(string value) =>
        DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None).ToUniversalTime();
    private static InvalidOperationException StaleSession() => new("The connection session is no longer active. Refresh its state before saving.");
}
