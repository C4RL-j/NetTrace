using AppUsageStorage;
using Microsoft.Data.Sqlite;

var tests = StorageTests.Cases.Concat(SchemaFailureTests.Cases).ToArray();
var failures = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}"); }
}
Console.WriteLine($"App Usage 004.1 storage: {tests.Length - failures}/{tests.Length} passed.");
return failures == 0 ? 0 : 1;

internal static class Check
{
    public static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}.");
    }

    public static void True(bool value, string context)
    {
        if (!value) throw new InvalidOperationException(context);
    }

    public static T Throws<T>(Action run, string context) where T : Exception
    {
        try { run(); }
        catch (T ex) { return ex; }
        throw new InvalidOperationException($"{context}: expected {typeof(T).Name}.");
    }

    public static AppUsageStorageException Failure(Action run, AppUsageStorageFailure failure, string databasePath)
    {
        var exception = Throws<AppUsageStorageException>(run, $"typed {failure} failure");
        Equal(failure, exception.Failure, "failure classification");
        Equal(databasePath, exception.DatabasePath, "failure identifies database");
        True(!string.IsNullOrWhiteSpace(exception.Message), "failure contains diagnostic message");
        return exception;
    }

    public static Func<Task> Sync(Action run) => () => { run(); return Task.CompletedTask; };
}

internal sealed class TestStorage : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "NetTrace.AppUsageStorageTests", Guid.NewGuid().ToString("N"));
    public string DatabasePath => DailyAppUsageStore.GetDatabasePath(Root);

    public TestStorage() => Directory.CreateDirectory(Root);

    public DailyAppUsageStore Open() => new(Root);

    public SqliteConnection Raw()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    public void Execute(string sql)
    {
        using var connection = Raw();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public long Scalar(string sql)
    {
        using var connection = Raw();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        // The unique directory is the only recursive deletion target; production storage is never used.
        var resolvedRoot = Path.GetFullPath(Root);
        var testParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NetTrace.AppUsageStorageTests")) + Path.DirectorySeparatorChar;
        if (!resolvedRoot.StartsWith(testParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup escaped isolated storage.");
        Directory.Delete(resolvedRoot, recursive: true);
    }
}
