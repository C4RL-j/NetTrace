namespace NetTrace.Utils;

public static class ByteRateFormatter
{
    public static string Format(double bytesPerSecond) => FormatNormalized(Normalize(bytesPerSecond));

    internal static double Normalize(double bytesPerSecond) =>
        double.IsFinite(bytesPerSecond) && bytesPerSecond > 0 ? bytesPerSecond : 0d;

    internal static string FormatNormalized(double bytesPerSecond)
    {
        // Live networking rates use decimal units. Select from the unrounded
        // measurement so display rounding cannot promote it into the next unit.
        var (divisor, unit) = bytesPerSecond switch
        {
            >= 1_000_000_000 => (1_000_000_000d, "GB/s"),
            >= 1_000_000 => (1_000_000d, "MB/s"),
            >= 1_000 => (1_000d, "KB/s"),
            _ => (1d, "B/s")
        };
        var value = bytesPerSecond / divisor;
        var decimals = divisor == 1 ? 0 : value < 10 ? 2 : value < 100 ? 1 : 0;
        return $"{value.ToString($"F{decimals}")} {unit}";
    }
}
