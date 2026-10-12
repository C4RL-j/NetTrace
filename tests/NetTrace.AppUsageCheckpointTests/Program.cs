var tests = EventPersistenceTests.Cases.Concat(CheckpointTests.Cases).ToArray();
var failures = 0;
foreach (var (name, run) in tests)
{
    try { await run().WaitAsync(TimeSpan.FromSeconds(12)); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}"); }
}
Console.WriteLine($"App Usage 004.2 checkpoints: {tests.Length - failures}/{tests.Length} passed.");
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
    public static void Throws<T>(Action run, string context) where T : Exception
    {
        try { run(); } catch (T) { return; }
        throw new InvalidOperationException($"{context}: expected {typeof(T).Name}.");
    }
    public static async Task ThrowsAsync<T>(Func<Task> run, string context) where T : Exception
    {
        try { await run(); } catch (T) { return; }
        throw new InvalidOperationException($"{context}: expected {typeof(T).Name}.");
    }
    public static async Task Eventually(Func<bool> condition, string context)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
        {
            if (deadline.IsCancellationRequested) throw new InvalidOperationException(context);
            await Task.Delay(1);
        }
    }
    public static Func<Task> Sync(Action run) => () => { run(); return Task.CompletedTask; };
}
