using System.Security.Principal;

using (var identity = WindowsIdentity.GetCurrent())
{
    if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
    {
        Console.Error.WriteLine("App Usage 005.1 deterministic tests must run unelevated. These tests use a fake platform and never install a service or change system files.");
        return 2;
    }
}

var failures = 0;
var focusedClear = args.SequenceEqual(["--history-clear-checks"]);
var tests = focusedClear ? HistoryClearTests.Cases : InstallerTests.Cases.Concat(HistoryClearTests.Cases).ToArray();
foreach (var (name, run) in tests)
{
    try
    {
        await run().WaitAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {exception.GetType().Name}: {exception.Message}");
    }
}
Console.WriteLine($"App Usage {(focusedClear ? "005.4 history clear" : "005.1/005.4 installer")}: {tests.Length - failures}/{tests.Length} passed.");
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

    public static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual, string context)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException($"{context}: expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
    }

    public static Func<Task> Sync(Action run) => () => { run(); return Task.CompletedTask; };

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
}
