var tests = QueryTests.Cases.Concat(ProtocolTests.Cases).Concat(PipeTests.Cases).ToArray();
var failures = 0;
foreach (var (name, run) in tests)
{
    try { await run().WaitAsync(TimeSpan.FromSeconds(30)); Console.WriteLine($"PASS {name}"); }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {exception.GetType().Name}: {exception.Message}");
    }
}
Console.WriteLine($"App Usage 004.3 read-only queries: {tests.Length - failures}/{tests.Length} passed.");
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
    public static async Task ThrowsAsync<T>(Func<Task> run, string context) where T : Exception
    {
        try { await run(); } catch (T) { return; }
        throw new InvalidOperationException($"{context}: expected {typeof(T).Name}.");
    }
    public static void Throws<T>(Action run, string context) where T : Exception
    {
        try { run(); } catch (T) { return; }
        throw new InvalidOperationException($"{context}: expected {typeof(T).Name}.");
    }
    public static Func<Task> Sync(Action run) => () => { run(); return Task.CompletedTask; };
}
