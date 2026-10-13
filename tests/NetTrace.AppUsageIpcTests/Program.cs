var focusedLifecycle = args.SequenceEqual(["--pipe-lifecycle-checks"]);
var tests = focusedLifecycle ? PipeLifecycleTests.Cases : AggregationTests.Cases.Concat(ProtocolTests.Cases).Concat(PipeTests.Cases)
    .Concat(CaptureStatusTests.Cases).Concat(PipeLifecycleTests.Cases).ToArray();
var failures = 0;
foreach (var (name, run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
    }
}
Console.WriteLine($"App Usage {(focusedLifecycle ? "005.6 pipe lifecycle" : "002/005.6 IPC")}: {tests.Length - failures}/{tests.Length} passed.");
return failures == 0 ? 0 : 1;

internal static class Check
{
    public static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}.");
    }
    public static void True(bool condition, string context)
    {
        if (!condition) throw new InvalidOperationException(context);
    }
    public static Func<Task> Sync(Action test) => () =>
    {
        test();
        return Task.CompletedTask;
    };
    public static async Task ThrowsAsync<T>(Func<Task> run, string context) where T : Exception
    {
        try { await run(); }
        catch (T) { return; }
        throw new InvalidOperationException($"{context}: expected {typeof(T).Name}.");
    }
}
