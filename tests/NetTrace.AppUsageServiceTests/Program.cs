using AppUsageEtwProbe;

if (EtwCapture.IsElevated)
{
    Console.Error.WriteLine("App Usage 003 deterministic tests must run with a normal token; service installation and actual ETW capture are separate integration checks.");
    return 2;
}

var focusedClear = args.SequenceEqual(["--history-management-checks"]);
var tests = focusedClear ? HistoryManagementTests.Cases : AggregationLifecycleTests.Cases.Concat(ServiceLifecycleTests.Cases)
    .Concat(ProcessCacheTests.Cases).Concat(SessionOwnershipTests.Cases).Concat(ServiceIpcTests.Cases)
    .Concat(HistoryManagementTests.Cases).ToArray();
var failures = 0;
foreach (var (name, run) in tests)
{
    try { await run().WaitAsync(TimeSpan.FromSeconds(12)); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}"); }
}
Console.WriteLine($"App Usage {(focusedClear ? "005.4 management" : "003/005.4 service")}: {tests.Length - failures}/{tests.Length} passed.");
return failures == 0 ? 0 : 1;

internal static class Check
{
    public static void True(bool value, string context)
    {
        if (!value) throw new InvalidOperationException(context);
    }
    public static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}.");
    }
    public static Func<Task> Sync(Action run) => () => { run(); return Task.CompletedTask; };
    public static async Task ThrowsAsync<T>(Func<Task> run, string context) where T : Exception
    {
        try { await run(); } catch (T) { return; }
        throw new InvalidOperationException($"{context}: expected {typeof(T).Name}.");
    }
}
