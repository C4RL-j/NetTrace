using System.Globalization;
using System.Net.NetworkInformation;
using Microsoft.Data.Sqlite;
using NetTrace.Models;

namespace NetTrace.Services;

// Stores VPN observations without creating physical sessions, usage or physical
// confirmations. SQLite triggers record interval events atomically. UsageStore migrates first.
public sealed class VpnSessionStore
{
    private const string Select = """
        SELECT Id, ConnectionSessionId, InterfaceIndex, InterfaceGuid, InterfaceName,
            Description, RoutingMode, StartedAtUtc, LastConfirmedAtUtc, EndedAtUtc, EndReason
        FROM VpnSessions
        """;
    private readonly string _connectionString;

    public VpnSessionStore(UsageStore store)
        : this((store ?? throw new ArgumentNullException(nameof(store))).DatabasePath) { }

    public VpnSessionStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, ForeignKeys = true
        }.ToString();
    }

    public VpnSession? GetActiveSession()
    {
        using var connection = Open();
        return Active(connection, null);
    }

    public IReadOnlyList<VpnSession> GetRecentSessions(int limit = 100) => Query(null, limit);
    public IReadOnlyList<VpnSession> GetSessionsByConnectionSession(long connectionSessionId, int limit = 100)
    {
        if (connectionSessionId <= 0) throw new ArgumentOutOfRangeException(nameof(connectionSessionId));
        return Query(connectionSessionId, limit);
    }

    private IReadOnlyList<VpnSession> Query(long? parent, int limit)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + (parent is null ? "" : " WHERE ConnectionSessionId=$parent")
            + " ORDER BY StartedAtUtc DESC, Id DESC LIMIT $limit;";
        if (parent is not null) command.Parameters.AddWithValue("$parent", parent.Value);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var result = new List<VpnSession>();
        while (reader.Read()) result.Add(ReadSession(reader));
        return result.AsReadOnly();
    }

    public VpnSession? Observe(long expectedConnectionSessionId, VpnRoutingState state, DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (expectedConnectionSessionId <= 0) throw new ArgumentOutOfRangeException(nameof(expectedConnectionSessionId));
        var observed = observedAtUtc.ToUniversalTime();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var parent = connection.CreateCommand())
        {
            parent.Transaction = transaction;
            parent.CommandText = "SELECT StartedAtUtc, LastConfirmedAtUtc FROM ConnectionSessions WHERE Id=$id AND EndedAtUtc IS NULL;";
            parent.Parameters.AddWithValue("$id", expectedConnectionSessionId);
            using var reader = parent.ExecuteReader();
            if (!reader.Read() || observed < Parse(reader.GetString(0)) || observed > Parse(reader.GetString(1)))
                throw new InvalidOperationException("The VPN observation requires the same open physical session and a persisted physical confirmation at its timestamp.");
        }
        var current = Active(connection, transaction);
        if (current is not null && current.ConnectionSessionId != expectedConnectionSessionId)
            throw new InvalidOperationException("An unfinished VPN interval belongs to a different physical session.");
        if (current is not null && observed < current.LastConfirmedAtUtc)
        {
            transaction.Commit();
            return current; // A delayed callback cannot reverse a newer observation.
        }
        if (current is null)
        {
            using var previous = connection.CreateCommand();
            previous.Transaction = transaction;
            previous.CommandText = "SELECT max(EndedAtUtc) FROM VpnSessions WHERE ConnectionSessionId=$parent;";
            previous.Parameters.AddWithValue("$parent", expectedConnectionSessionId);
            if (previous.ExecuteScalar() is string ended && observed < Parse(ended))
            {
                transaction.Commit();
                return null; // Closed history also supplies an ordering boundary.
            }
        }

        if (state.Detection.Status == VpnStatus.NoVpn)
        {
            if (current is not null) Close(connection, transaction, current, observed, "VpnDisconnected");
            transaction.Commit();
            return null;
        }
        var candidates = state.Tunnels.DistinctBy(x => x.Tunnel).ToArray();
        if (state.Detection.Status != VpnStatus.TunnelDetected || candidates.Length != 1 ||
            state.Detection.Interfaces.Count != 1 || !state.Detection.Interfaces.Contains(candidates[0].Tunnel))
        {
            if (current is not null) Close(connection, transaction, current, current.LastConfirmedAtUtc,
                state.Detection.Status == VpnStatus.TunnelDetected ? "AmbiguousTunnel" : "DetectionUnavailable");
            transaction.Commit();
            return null;
        }
        var candidate = candidates[0];
        var tunnel = candidate.Tunnel;
        Validate(tunnel, candidate.Mode);
        if (current is not null && !SameTunnel(current, tunnel))
        {
            Close(connection, transaction, current, observed, "TunnelChanged");
            current = null;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$name", tunnel.InterfaceName);
        command.Parameters.AddWithValue("$description", tunnel.Description);
        command.Parameters.AddWithValue("$mode", candidate.Mode.ToString());
        command.Parameters.AddWithValue("$confirmed", Timestamp(observed));
        if (current is null)
        {
            command.CommandText = """
                INSERT INTO VpnSessions(ConnectionSessionId, InterfaceIndex, InterfaceGuid, InterfaceName,
                    Description, RoutingMode, StartedAtUtc, LastConfirmedAtUtc)
                VALUES ($parent, $index, $guid, $name, $description, $mode, $confirmed, $confirmed);
                """;
            command.Parameters.AddWithValue("$parent", expectedConnectionSessionId);
            command.Parameters.AddWithValue("$index", tunnel.InterfaceIndex is { } index ? (object)(long)index : DBNull.Value);
            command.Parameters.AddWithValue("$guid", tunnel.InterfaceGuid is { } guid ? guid.ToString("D") : DBNull.Value);
        }
        else
        {
            command.CommandText = """
                UPDATE VpnSessions SET InterfaceName=$name, Description=$description,
                    RoutingMode=$mode, LastConfirmedAtUtc=$confirmed WHERE Id=$id AND EndedAtUtc IS NULL;
                """;
            command.Parameters.AddWithValue("$id", current.Id);
        }
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("The VPN interval changed during persistence.");
        var updated = Active(connection, transaction);
        transaction.Commit();
        return updated;
    }

    private static bool SameTunnel(VpnSession session, VpnInterfaceInfo tunnel) =>
        session.InterfaceIndex == tunnel.InterfaceIndex && session.InterfaceGuid == tunnel.InterfaceGuid &&
        (session.InterfaceIndex is not null || session.InterfaceGuid is not null ||
            (session.InterfaceName == tunnel.InterfaceName && session.Description == tunnel.Description));

    private static void Validate(VpnInterfaceInfo tunnel, VpnRoutingMode mode)
    {
        if (!Enum.IsDefined(mode) || tunnel.InterfaceIndex == 0 || tunnel.InterfaceGuid == Guid.Empty ||
            ((tunnel.InterfaceIndex is not null || tunnel.InterfaceGuid is not null) &&
                tunnel.OperationalStatus != OperationalStatus.Up) ||
            tunnel.InterfaceName is null || tunnel.Description is null ||
            (tunnel.InterfaceIndex is null && tunnel.InterfaceGuid is null && string.IsNullOrWhiteSpace(tunnel.InterfaceName)))
            throw new ArgumentException("A VPN interval requires valid detected interface evidence and a supported routing mode.");
    }

    private static void Close(SqliteConnection connection, SqliteTransaction transaction,
        VpnSession session, DateTimeOffset endedAtUtc, string reason)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE VpnSessions SET EndedAtUtc=$ended, EndReason=$reason WHERE Id=$id AND EndedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$ended", Timestamp(endedAtUtc < session.LastConfirmedAtUtc ? session.LastConfirmedAtUtc : endedAtUtc));
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$id", session.Id);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("The active VPN interval has already ended.");
    }

    private static VpnSession? Active(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Select + " WHERE EndedAtUtc IS NULL;";
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSession(reader) : null;
    }

    private static VpnSession ReadSession(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : checked((uint)reader.GetInt64(2)),
        reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)), reader.GetString(4), reader.GetString(5),
        Enum.Parse<VpnRoutingMode>(reader.GetString(6)), Parse(reader.GetString(7)), Parse(reader.GetString(8)),
        reader.IsDBNull(9) ? null : Parse(reader.GetString(9)), reader.IsDBNull(10) ? null : reader.GetString(10));
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
    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture);
}
