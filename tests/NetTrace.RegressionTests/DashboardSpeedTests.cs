using System.Globalization;
using NetTrace.Presentation;
using NetTrace.Utils;

internal static class DashboardSpeedTests
{
    public static (string Name, Action Run)[] Cases =>
    [
        ("Idle live speed renders canonical 0 B/s and 0.00 Mbps", Idle),
        ("Approved live speed conversions use authoritative bytes per second", ApprovedConversions),
        ("Approved readable byte-rate examples keep compact decimal precision", ReadableExamples),
        ("B/s to KB/s switches at exactly 1000 raw bytes per second", KilobyteBoundary),
        ("KB/s to MB/s switches at exactly one million raw bytes per second", MegabyteBoundary),
        ("MB/s to GB/s switches at exactly one billion raw bytes per second", GigabyteBoundary),
        ("Unrounded threshold neighbors keep their own unit even when displayed as 1000", UnroundedUnitSelection),
        ("Live byte-rate units use decimal thresholds rather than binary quantity units", DecimalUnits),
        ("Fractional bytes per second render whole readable B/s", FractionalBytes),
        ("Kilobyte precision uses two one or zero decimals by raw scaled magnitude", KilobytePrecision),
        ("Megabyte precision uses two one or zero decimals by raw scaled magnitude", MegabytePrecision),
        ("Gigabyte precision uses two one or zero decimals by raw scaled magnitude", GigabytePrecision),
        ("Kilobyte precision boundaries do not pre-round the scaled magnitude", KilobytePrecisionBoundaries),
        ("Megabyte precision boundaries do not pre-round the scaled magnitude", MegabytePrecisionBoundaries),
        ("Gigabyte precision boundaries do not pre-round the scaled magnitude", GigabytePrecisionBoundaries),
        ("Tiny positive finite measurements remain readable without scientific notation", TinyFiniteRates),
        ("Negative live measurements are safely displayed as zero in both units", NegativeRates),
        ("NaN and infinite measurements are safely displayed as zero in both units", NonfiniteRates),
        ("IEEE negative zero is normalized to positive idle text in both units", NegativeZero),
        ("Extreme finite live measurements cannot overflow or use scientific notation", ExtremeFiniteRates),
        ("Multi-gigabyte rates retain readable adaptive GB/s precision", LargeGigabyteRates),
        ("English display culture keeps compact byte-rate and Mbps decimal points", EnglishCulture),
        ("French display culture applies the same decimal separator to both live values", FrenchCulture),
        ("Invalid and negative-zero pairs remain canonical idle under French culture", FrenchIdle),
        ("Valid live samples preserve the existing Mbps formatter exactly", ExistingMbpsUnchanged),
        ("Mbps uses the raw sample rather than the rounded primary byte-rate text", SameRawSample),
        ("Independent paired presentations cannot retain a previous sample's speed", IndependentSamples),
        ("Download and upload examples each expose their own matching paired values", DownloadUploadPairs),
        ("Adaptive speed formatting leaves existing cumulative binary quantities unchanged", CumulativeQuantityUnits),
        ("Every supported byte-rate unit uses a compact stable measurement suffix", MeasurementSuffixes),
    ];

    private static void Idle() => English(() => Pair(0, "0 B/s", "0.00 Mbps"));

    private static void ApprovedConversions() => English(() =>
    {
        foreach (var (raw, primary, secondary) in new[]
        {
            (0d, "0 B/s", "0.00 Mbps"), (1d, "1 B/s", "0.00 Mbps"),
            (100d, "100 B/s", "0.00 Mbps"), (1000d, "1.00 KB/s", "0.01 Mbps"),
            (100_000d, "100 KB/s", "0.80 Mbps"), (1_000_000d, "1.00 MB/s", "8.00 Mbps"),
            (5_000_000d, "5.00 MB/s", "40.00 Mbps"), (1_000_000_000d, "1.00 GB/s", "8000.00 Mbps")
        }) Pair(raw, primary, secondary);
    });

    private static void ReadableExamples() => English(() =>
    {
        foreach (var (raw, text) in new[]
        {
            (842d, "842 B/s"), (1250d, "1.25 KB/s"), (28_800d, "28.8 KB/s"),
            (940_000d, "940 KB/s"), (1_020_000d, "1.02 MB/s"), (4_920_000d, "4.92 MB/s"),
            (38_400_000d, "38.4 MB/s"), (1_100_000_000d, "1.10 GB/s")
        }) Equal(text, ByteRateFormatter.Format(raw), "approved compact adaptive rate");
    });

    private static void KilobyteBoundary() => English(() =>
    {
        Equal("999 B/s", ByteRateFormatter.Format(999), "just below kilobyte threshold");
        Equal("1.00 KB/s", ByteRateFormatter.Format(1000), "exact kilobyte threshold");
        Equal("1.00 KB/s", ByteRateFormatter.Format(1001), "just above kilobyte threshold");
    });

    private static void MegabyteBoundary() => English(() =>
    {
        Equal("1000 KB/s", ByteRateFormatter.Format(999_999), "just below megabyte threshold retains KB/s");
        Equal("1.00 MB/s", ByteRateFormatter.Format(1_000_000), "exact megabyte threshold");
        Equal("1.00 MB/s", ByteRateFormatter.Format(1_000_001), "just above megabyte threshold");
    });

    private static void GigabyteBoundary() => English(() =>
    {
        Equal("1000 MB/s", ByteRateFormatter.Format(999_999_999), "just below gigabyte threshold retains MB/s");
        Equal("1.00 GB/s", ByteRateFormatter.Format(1_000_000_000), "exact gigabyte threshold");
        Equal("1.00 GB/s", ByteRateFormatter.Format(1_000_000_001), "just above gigabyte threshold");
    });

    private static void UnroundedUnitSelection() => English(() =>
    {
        foreach (var (threshold, lowerText, nextText) in new[]
        {
            (1000d, "1000 B/s", "1.00 KB/s"),
            (1_000_000d, "1000 KB/s", "1.00 MB/s"),
            (1_000_000_000d, "1000 MB/s", "1.00 GB/s")
        })
        {
            Equal(lowerText, ByteRateFormatter.Format(double.BitDecrement(threshold)), "nearest representable value below threshold keeps its unit");
            Equal(nextText, ByteRateFormatter.Format(threshold), "raw threshold promotes exactly once");
            Equal(nextText, ByteRateFormatter.Format(double.BitIncrement(threshold)), "nearest representable value above threshold uses the new unit");
        }
    });

    private static void DecimalUnits() => English(() =>
    {
        Equal("1.02 KB/s", ByteRateFormatter.Format(1024), "1024 bytes per second is more than one decimal KB/s");
        Equal("1.05 MB/s", ByteRateFormatter.Format(1_048_576), "binary megabyte rate uses decimal MB/s");
        Equal("1.07 GB/s", ByteRateFormatter.Format(1_073_741_824), "binary gigabyte rate uses decimal GB/s");
    });

    private static void FractionalBytes() => English(() =>
    {
        foreach (var (raw, text) in new[]
        {
            (0.1d, "0 B/s"), (0.4d, "0 B/s"), (0.6d, "1 B/s"),
            (1.25d, "1 B/s"), (842.4d, "842 B/s"), (842.6d, "843 B/s")
        }) Equal(text, ByteRateFormatter.Format(raw), "fractional B/s has no decimal noise");
    });

    private static void KilobytePrecision() => AssertPrecision(1000, "KB/s");
    private static void MegabytePrecision() => AssertPrecision(1_000_000, "MB/s");
    private static void GigabytePrecision() => AssertPrecision(1_000_000_000, "GB/s");

    private static void AssertPrecision(double divisor, string unit) => English(() =>
    {
        Equal("1.25 " + unit, ByteRateFormatter.Format(1.25 * divisor), "small scaled rate has two decimals");
        Equal("28.8 " + unit, ByteRateFormatter.Format(28.8 * divisor), "middle scaled rate has one decimal");
        Equal("940 " + unit, ByteRateFormatter.Format(940 * divisor), "large scaled rate has whole units");
    });

    private static void KilobytePrecisionBoundaries() => AssertPrecisionBoundaries(1000, "KB/s");
    private static void MegabytePrecisionBoundaries() => AssertPrecisionBoundaries(1_000_000, "MB/s");
    private static void GigabytePrecisionBoundaries() => AssertPrecisionBoundaries(1_000_000_000, "GB/s");

    private static void AssertPrecisionBoundaries(double divisor, string unit) => English(() =>
    {
        Equal("10.00 " + unit, ByteRateFormatter.Format(9.999 * divisor), "below ten uses two decimals despite rounding");
        Equal("10.0 " + unit, ByteRateFormatter.Format(10 * divisor), "exactly ten uses one decimal");
        Equal("100.0 " + unit, ByteRateFormatter.Format(99.999 * divisor), "below one hundred uses one decimal despite rounding");
        Equal("100 " + unit, ByteRateFormatter.Format(100 * divisor), "exactly one hundred uses whole units");
    });

    private static void TinyFiniteRates() => English(() =>
    {
        foreach (var raw in new[] { double.Epsilon, 1e-300, 1e-12, 0.0001 })
            Pair(raw, "0 B/s", "0.00 Mbps");
    });

    private static void NegativeRates() => English(() =>
    {
        foreach (var raw in new[] { -1d, -0.001, -1000d, -1_000_000d, double.MinValue })
            Pair(raw, "0 B/s", "0.00 Mbps");
    });

    private static void NonfiniteRates() => English(() =>
    {
        foreach (var raw in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Pair(raw, "0 B/s", "0.00 Mbps");
    });

    private static void NegativeZero() => English(() =>
    {
        var negativeZero = BitConverter.Int64BitsToDouble(long.MinValue);
        Check(BitConverter.DoubleToInt64Bits(negativeZero) < 0, "Fixture is IEEE negative zero.");
        Pair(negativeZero, "0 B/s", "0.00 Mbps");
        Equal(LiveSpeedPresentation.Create(0), LiveSpeedPresentation.Create(negativeZero), "positive and negative zero have identical paired presentation");
    });

    private static void ExtremeFiniteRates() => English(() =>
    {
        foreach (var raw in new[] { 1e30, 1e100, double.MaxValue })
        {
            var value = LiveSpeedPresentation.Create(raw);
            Check(value.ByteRateText.EndsWith(" GB/s", StringComparison.Ordinal), "Large finite raw rate uses supported GB/s.");
            Check(value.MbpsText.EndsWith(" Mbps", StringComparison.Ordinal), "Large finite secondary retains Mbps.");
            foreach (var text in new[] { value.ByteRateText, value.MbpsText })
            {
                Check(!text.Contains("NaN", StringComparison.Ordinal) && !text.Contains("Infinity", StringComparison.Ordinal), "Finite sample never becomes nonfinite text.");
                var number = text[..text.LastIndexOf(' ')];
                Check(!number.Any(char.IsLetter), "Finite numeric text uses fixed notation.");
            }
            var primaryNumber = value.ByteRateText[..value.ByteRateText.LastIndexOf(' ')];
            Check(!primaryNumber.Contains('.'), "Extreme scaled rate has no excessive fractional precision.");
        }
    });

    private static void LargeGigabyteRates() => English(() =>
    {
        Pair(2_630_000_000, "2.63 GB/s", "21040.00 Mbps");
        Pair(50_123_000_000, "50.1 GB/s", "400984.00 Mbps");
        Pair(100_700_000_000, "101 GB/s", "805600.00 Mbps");
    });

    private static void EnglishCulture() => English(() =>
    {
        Pair(1250, "1.25 KB/s", "0.01 Mbps");
        Pair(28_800, "28.8 KB/s", "0.23 Mbps");
        Pair(1_020_000, "1.02 MB/s", "8.16 Mbps");
    });

    private static void FrenchCulture() => WithCulture("fr-FR", () =>
    {
        Pair(1250, "1,25 KB/s", "0,01 Mbps");
        Pair(28_800, "28,8 KB/s", "0,23 Mbps");
        Pair(1_020_000, "1,02 MB/s", "8,16 Mbps");
    });

    private static void FrenchIdle() => WithCulture("fr-FR", () =>
    {
        foreach (var raw in new[] { 0d, BitConverter.Int64BitsToDouble(long.MinValue), -1d, double.NaN, double.PositiveInfinity })
            Pair(raw, "0 B/s", "0,00 Mbps");
    });

    private static void ExistingMbpsUnchanged()
    {
        foreach (var culture in new[] { "en-US", "fr-FR" }) WithCulture(culture, () =>
        {
            foreach (var raw in new[] { 0d, 1d, 612.5, 750d, 36_700d, 36.7 * 1024, 100_000d,
                425_000d, 3_100_000d, 1_100_000_000d, double.Epsilon, double.MaxValue })
                Equal(ByteFormatter.FormatRate(raw), LiveSpeedPresentation.Create(raw).MbpsText,
                    "valid paired sample preserves existing Mbps conversion and rounding");
        });
    }

    private static void SameRawSample() => English(() =>
    {
        // Reconstructing raw bytes from either rounded primary would produce a
        // different Mbps value for these samples, exposing a lossy round-trip.
        Pair(100_600, "101 KB/s", "0.80 Mbps");
        Pair(4_924_999, "4.92 MB/s", "39.40 Mbps");
        Pair(28_812.735, "28.8 KB/s", "0.23 Mbps");
    });

    private static void IndependentSamples() => English(() =>
    {
        var previous = LiveSpeedPresentation.Create(5_000_000);
        var current = LiveSpeedPresentation.Create(1000);
        Equal("5.00 MB/s", previous.ByteRateText, "previous immutable primary");
        Equal("40.00 Mbps", previous.MbpsText, "previous immutable secondary");
        Equal("1.00 KB/s", current.ByteRateText, "current primary belongs to new sample");
        Equal("0.01 Mbps", current.MbpsText, "current secondary belongs to the same new sample");
    });

    private static void DownloadUploadPairs() => English(() =>
    {
        Pair(4_920_000, "4.92 MB/s", "39.36 Mbps");
        Pair(1_420_000, "1.42 MB/s", "11.36 Mbps");
    });

    private static void CumulativeQuantityUnits() => English(() =>
    {
        Equal("1.0 KB", ByteFormatter.Format(1024), "existing cumulative kilobyte quantity");
        Equal("1.00 MB", ByteFormatter.Format(1_048_576), "existing cumulative megabyte quantity");
        Equal("1.00 GB", ByteFormatter.Format(1_073_741_824), "existing cumulative gigabyte quantity");
        Equal("1.02 KB/s", ByteRateFormatter.Format(1024), "live rate retains distinct decimal units and per-second suffix");
    });

    private static void MeasurementSuffixes() => English(() =>
    {
        foreach (var (raw, suffix) in new[] { (842d, " B/s"), (1250d, " KB/s"), (1_020_000d, " MB/s"), (1_100_000_000d, " GB/s") })
        {
            var text = ByteRateFormatter.Format(raw);
            Check(text.EndsWith(suffix, StringComparison.Ordinal), "Adaptive rate names its supported byte unit.");
            Check(text.Count(character => character == ' ') == 1, "Measurement has one separating space.");
            Check(text.Length <= 14, "Representative measurements remain compact.");
        }
    });

    private static void Pair(double raw, string primary, string secondary)
    {
        var value = LiveSpeedPresentation.Create(raw);
        Equal(primary, value.ByteRateText, "paired byte-rate primary");
        Equal(secondary, value.MbpsText, "paired Mbps secondary");
        Equal(primary, ByteRateFormatter.Format(raw), "standalone and paired byte-rate formatting agree");
    }

    private static void English(Action action) => WithCulture("en-US", action);

    private static void WithCulture(string name, Action action)
    {
        var prior = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name); action(); }
        finally { CultureInfo.CurrentCulture = prior; }
    }

    private static void Equal<T>(T expected, T actual, string description)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{description}: expected {expected}, got {actual}.");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }
}
