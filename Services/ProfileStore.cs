using System.Globalization;
using Microsoft.Data.Sqlite;
using NetTrace.Models;

namespace NetTrace.Services;

public sealed class ProfileStore
{
    private readonly string _connectionString;

    public ProfileStore(UsageStore usageStore)
        : this((usageStore ?? throw new ArgumentNullException(nameof(usageStore))).DatabasePath)
    {
    }

    // UsageStore owns database initialization and schema migration before this store is used.
    public ProfileStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true
        }.ToString();
    }

    public IReadOnlyList<ConnectionProfile> GetProfiles()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, TelcoName, DeviceName, CreatedAt, UpdatedAt
            FROM ConnectionProfiles
            ORDER BY TelcoName COLLATE NOCASE, DeviceName COLLATE NOCASE, Id;
            """;
        using var reader = command.ExecuteReader();
        var profiles = new List<ConnectionProfile>();
        while (reader.Read())
            profiles.Add(ReadProfile(reader));
        return profiles;
    }

    public ConnectionProfile? GetProfile(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, TelcoName, DeviceName, CreatedAt, UpdatedAt FROM ConnectionProfiles WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadProfile(reader) : null;
    }

    public IReadOnlyList<NetworkMapping> GetMappings(long profileId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ProfileId, NetworkType, NetworkIdentifier, FriendlyName, CreatedAt
            FROM NetworkMappings
            WHERE ProfileId=$profileId
            ORDER BY NetworkType, NetworkIdentifier, Id;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        using var reader = command.ExecuteReader();
        var mappings = new List<NetworkMapping>();
        while (reader.Read())
            mappings.Add(ReadMapping(reader));
        return mappings;
    }

    public ConnectionProfile CreateProfile(string telcoName, string deviceName)
    {
        var names = ValidateNames(telcoName, deviceName);
        using var connection = Open();
        return InsertProfile(connection, null, names.Telco, names.Device);
    }

    public ConnectionProfile UpdateProfile(long id, string telcoName, string deviceName)
    {
        var names = ValidateNames(telcoName, deviceName);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ConnectionProfiles SET TelcoName=$telco, DeviceName=$device, UpdatedAt=$updatedAt
            WHERE Id=$id
            RETURNING Id, TelcoName, DeviceName, CreatedAt, UpdatedAt;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$telco", names.Telco);
        command.Parameters.AddWithValue("$device", names.Device);
        command.Parameters.AddWithValue("$updatedAt", Timestamp());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException("The connection profile no longer exists. Refresh the profile list and try again.");
        return ReadProfile(reader);
    }

    public ConnectionProfile? ResolveProfile(string networkType, string networkIdentifier)
    {
        var type = ValidateIdentity(networkType, networkIdentifier);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.Id, p.TelcoName, p.DeviceName, p.CreatedAt, p.UpdatedAt
            FROM NetworkMappings m
            JOIN ConnectionProfiles p ON p.Id=m.ProfileId
            WHERE m.NetworkType=$type COLLATE BINARY AND m.NetworkIdentifier=$identifier COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$identifier", networkIdentifier);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadProfile(reader) : null;
    }

    public NetworkMapping AssignMapping(
        long profileId, string networkType, string networkIdentifier, string? friendlyName = null)
    {
        var type = ValidateIdentity(networkType, networkIdentifier);
        using var connection = Open();
        // The write transaction makes concurrent duplicate assignments deterministic.
        using var transaction = connection.BeginTransaction();
        var existing = FindMapping(connection, transaction, type, networkIdentifier);
        if (existing is not null)
        {
            if (existing.ProfileId != profileId)
                throw MappingConflict();
            transaction.Commit();
            return existing;
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT count(*) FROM ConnectionProfiles WHERE Id=$id;";
            command.Parameters.AddWithValue("$id", profileId);
            if (Convert.ToInt64(command.ExecuteScalar()) == 0)
                throw new InvalidOperationException("The selected connection profile no longer exists. Refresh the profile list and try again.");
        }

        var mapping = InsertMapping(connection, transaction, profileId, type, networkIdentifier, friendlyName);
        transaction.Commit();
        return mapping;
    }

    public ConnectionProfile CreateAndAssignProfile(
        string telcoName, string deviceName, string networkType, string networkIdentifier, string? friendlyName = null)
    {
        var names = ValidateNames(telcoName, deviceName);
        var type = ValidateIdentity(networkType, networkIdentifier);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (FindMapping(connection, transaction, type, networkIdentifier) is not null)
            throw MappingConflict();

        var profile = InsertProfile(connection, transaction, names.Telco, names.Device);
        InsertMapping(connection, transaction, profile.Id, type, networkIdentifier, friendlyName);
        transaction.Commit();
        return profile;
    }

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

    private static ConnectionProfile InsertProfile(
        SqliteConnection connection, SqliteTransaction? transaction, string telco, string device)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ConnectionProfiles(TelcoName, DeviceName, CreatedAt, UpdatedAt)
            VALUES ($telco, $device, $timestamp, $timestamp)
            RETURNING Id, TelcoName, DeviceName, CreatedAt, UpdatedAt;
            """;
        command.Parameters.AddWithValue("$telco", telco);
        command.Parameters.AddWithValue("$device", device);
        command.Parameters.AddWithValue("$timestamp", Timestamp());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException("The connection profile could not be saved.");
        return ReadProfile(reader);
    }

    private static NetworkMapping InsertMapping(
        SqliteConnection connection, SqliteTransaction transaction, long profileId,
        string networkType, string networkIdentifier, string? friendlyName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO NetworkMappings(ProfileId, NetworkType, NetworkIdentifier, FriendlyName, CreatedAt)
            VALUES ($profileId, $type, $identifier, $friendlyName, $timestamp)
            RETURNING Id, ProfileId, NetworkType, NetworkIdentifier, FriendlyName, CreatedAt;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$type", networkType);
        command.Parameters.AddWithValue("$identifier", networkIdentifier);
        command.Parameters.AddWithValue("$friendlyName", string.IsNullOrWhiteSpace(friendlyName) ? DBNull.Value : friendlyName.Trim());
        command.Parameters.AddWithValue("$timestamp", Timestamp());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException("The network mapping could not be saved.");
        return ReadMapping(reader);
    }

    private static NetworkMapping? FindMapping(
        SqliteConnection connection, SqliteTransaction transaction, string networkType, string networkIdentifier)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT Id, ProfileId, NetworkType, NetworkIdentifier, FriendlyName, CreatedAt
            FROM NetworkMappings
            WHERE NetworkType=$type COLLATE BINARY AND NetworkIdentifier=$identifier COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$type", networkType);
        command.Parameters.AddWithValue("$identifier", networkIdentifier);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadMapping(reader) : null;
    }

    private static (string Telco, string Device) ValidateNames(string telcoName, string deviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(telcoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);
        return (telcoName.Trim(), deviceName.Trim());
    }

    private static string ValidateIdentity(string networkType, string networkIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(networkType);
        ArgumentException.ThrowIfNullOrEmpty(networkIdentifier);
        // SSIDs are case-sensitive and can contain leading/trailing spaces; never trim them.
        return networkType.Trim();
    }

    private static string Timestamp() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ReadTimestamp(string value) =>
        DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static ConnectionProfile ReadProfile(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
        ReadTimestamp(reader.GetString(3)), ReadTimestamp(reader.GetString(4)));

    private static NetworkMapping ReadMapping(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4), ReadTimestamp(reader.GetString(5)));

    private static InvalidOperationException MappingConflict() => new(
        "This network is already assigned to another connection profile. Refresh the connection and use its existing assignment.");
}
