namespace NetTrace.Utils;

public static class ByteFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long bytes)
    {
        if (bytes < 0)
            bytes = 0;

        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var decimals = unit switch
        {
            0 => 0,
            1 => value >= 100 ? 0 : 1,
            _ => value >= 100 ? 1 : 2
        };

        return $"{value.ToString($"F{decimals}")} {Units[unit]}";
    }

    public static string FormatRate(double bytesPerSecond)
    {
        if (!double.IsFinite(bytesPerSecond) || bytesPerSecond < 0)
            bytesPerSecond = 0;

        // Network rates use decimal megabits; data totals retain their byte units.
        return $"{bytesPerSecond / 125_000:F2} Mbps";
    }
}
