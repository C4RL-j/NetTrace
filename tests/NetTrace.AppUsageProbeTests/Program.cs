using AppUsageEtwProbe;

var tests = ProbeModelTests.Cases
    .Concat(ProbeNormalizationTests.Cases)
    .Concat(ProbeParsingTests.Cases)
    .Concat(ProbeAggregationTests.Cases)
    .Concat(ProbeAdapterComparisonTests.Cases)
    .Concat(ProbeLifecycleTests.Cases)
    .Concat(ProbeOfflineTrafficTests.Cases)
    .ToArray();

var failed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

Console.WriteLine($"App Usage 000: {tests.Length - failed}/{tests.Length} passed.");
return failed == 0 ? 0 : 1;

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

    public static void Throws<T>(Action action, string context) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"{context}: expected {typeof(T).Name}.");
    }
}
