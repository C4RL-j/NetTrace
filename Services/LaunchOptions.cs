namespace NetTrace.Services;

public static class LaunchOptions
{
    public static bool IsBackground(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Any(argument =>
            string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase));
    }
}
