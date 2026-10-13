using System.Globalization;
using NetTrace.Presentation;

internal static class DashboardUsageTests
{
    private const long Gigabyte = 1_073_741_824;
    private static readonly (long Downloaded, long Uploaded) Today = (Gigabyte, Gigabyte / 2);
    private static readonly (long Downloaded, long Uploaded) Week = (3 * Gigabyte, Gigabyte);
    private static readonly (long Downloaded, long Uploaded) Month = (10 * Gigabyte, 2 * Gigabyte);

    public static (string Name, Action Run)[] Cases =>
    [
        ("Default Dashboard usage range selects today's total and directions", TodayDefault),
        ("Dashboard seven-day usage uses only the existing seven-day totals", SevenDays),
        ("Dashboard thirty-day usage uses only the existing thirty-day totals", ThirtyDays),
        ("Empty Dashboard usage ranges display zero total and directions", ZeroUsage),
        ("Dashboard usage retains binary quantity units and existing precision", QuantityFormatting),
        ("Dashboard usage honors the current quantity display culture", DisplayCulture),
        ("Refreshed Dashboard usage values do not mutate previous presentations", RefreshedValues),
        ("Unsupported Dashboard usage ranges cannot display a misleading range", UnsupportedRange),
        ("The compact usage selector cycles Today seven days thirty days and back to Today", SelectorCycle),
        ("Each compact selector cycle displays the unchanged selected usage quantities", SelectorValues),
        ("The compact selector rejects unsupported ranges rather than inventing a selection", UnsupportedSelectorRange),
    ];

    private static void TodayDefault() => WithCulture("en-US", () =>
    {
        Equal(DashboardUsageRange.Today, default(DashboardUsageRange), "default range");
        AssertPresentation(Create(default), "TODAY", "1.50 GB", "↓ 1.00 GB   ↑ 512.0 MB");
    });

    private static void SevenDays() => WithCulture("en-US", () =>
        AssertPresentation(Create(DashboardUsageRange.SevenDays),
            "LAST 7 DAYS", "4.00 GB", "↓ 3.00 GB   ↑ 1.00 GB"));

    private static void ThirtyDays() => WithCulture("en-US", () =>
        AssertPresentation(Create(DashboardUsageRange.ThirtyDays),
            "LAST 30 DAYS", "12.00 GB", "↓ 10.00 GB   ↑ 2.00 GB"));

    private static void ZeroUsage() => WithCulture("en-US", () =>
    {
        foreach (var range in Enum.GetValues<DashboardUsageRange>())
        {
            var value = DashboardUsagePresentation.Create(range, (0, 0), (0, 0), (0, 0));
            Equal("0 B", value.TotalText, "empty range total");
            Equal("↓ 0 B   ↑ 0 B", value.BreakdownText, "empty range directions");
        }
    });

    private static void QuantityFormatting() => WithCulture("en-US", () =>
    {
        var bytes = DashboardUsagePresentation.Create(DashboardUsageRange.Today,
            (1536, 512), Week, Month);
        AssertPresentation(bytes, "TODAY", "2.0 KB", "↓ 1.5 KB   ↑ 512 B");

        var megabytes = DashboardUsagePresentation.Create(DashboardUsageRange.SevenDays,
            Today, (1_048_576, 1_048_576), Month);
        AssertPresentation(megabytes, "LAST 7 DAYS", "2.00 MB", "↓ 1.00 MB   ↑ 1.00 MB");
    });

    private static void DisplayCulture() => WithCulture("fr-FR", () =>
    {
        AssertPresentation(Create(DashboardUsageRange.Today),
            "TODAY", "1,50 GB", "↓ 1,00 GB   ↑ 512,0 MB");
        AssertPresentation(Create(DashboardUsageRange.ThirtyDays),
            "LAST 30 DAYS", "12,00 GB", "↓ 10,00 GB   ↑ 2,00 GB");
    });

    private static void RefreshedValues() => WithCulture("en-US", () =>
    {
        var previous = Create(DashboardUsageRange.SevenDays);
        var refreshed = DashboardUsagePresentation.Create(DashboardUsageRange.SevenDays,
            Today, (5 * Gigabyte, 2 * Gigabyte), Month);

        AssertPresentation(refreshed, "LAST 7 DAYS", "7.00 GB", "↓ 5.00 GB   ↑ 2.00 GB");
        AssertPresentation(previous, "LAST 7 DAYS", "4.00 GB", "↓ 3.00 GB   ↑ 1.00 GB");
        AssertPresentation(Create(DashboardUsageRange.Today),
            "TODAY", "1.50 GB", "↓ 1.00 GB   ↑ 512.0 MB");
    });

    private static void UnsupportedRange()
    {
        foreach (var invalid in new[] { -1, 3, int.MaxValue })
        {
            try
            {
                Create((DashboardUsageRange)invalid);
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }

            throw new InvalidOperationException($"Unsupported range {invalid} was accepted.");
        }
    }

    private static void SelectorCycle()
    {
        var selected = DashboardUsageRange.Today;
        for (var cycle = 0; cycle < 3; cycle++)
        {
            selected = DashboardUsagePresentation.NextRange(selected);
            Equal(DashboardUsageRange.SevenDays, selected, "Today advances to Last 7 Days");
            selected = DashboardUsagePresentation.NextRange(selected);
            Equal(DashboardUsageRange.ThirtyDays, selected, "Last 7 Days advances to Last 30 Days");
            selected = DashboardUsagePresentation.NextRange(selected);
            Equal(DashboardUsageRange.Today, selected, "Last 30 Days wraps to Today");
        }
    }

    private static void SelectorValues() => WithCulture("en-US", () =>
    {
        var selected = DashboardUsageRange.Today;
        AssertPresentation(Create(selected), "TODAY", "1.50 GB", "↓ 1.00 GB   ↑ 512.0 MB");
        selected = DashboardUsagePresentation.NextRange(selected);
        AssertPresentation(Create(selected), "LAST 7 DAYS", "4.00 GB", "↓ 3.00 GB   ↑ 1.00 GB");
        selected = DashboardUsagePresentation.NextRange(selected);
        AssertPresentation(Create(selected), "LAST 30 DAYS", "12.00 GB", "↓ 10.00 GB   ↑ 2.00 GB");
        selected = DashboardUsagePresentation.NextRange(selected);
        AssertPresentation(Create(selected), "TODAY", "1.50 GB", "↓ 1.00 GB   ↑ 512.0 MB");
    });

    private static void UnsupportedSelectorRange()
    {
        foreach (var invalid in new[] { -1, 3, int.MaxValue })
        {
            try { DashboardUsagePresentation.NextRange((DashboardUsageRange)invalid); }
            catch (ArgumentOutOfRangeException) { continue; }
            throw new InvalidOperationException($"Unsupported selector range {invalid} was accepted.");
        }
    }

    private static DashboardUsagePresentation Create(DashboardUsageRange range) =>
        DashboardUsagePresentation.Create(range, Today, Week, Month);

    private static void AssertPresentation(DashboardUsagePresentation value,
        string title, string total, string breakdown)
    {
        Equal(title, value.TitleText, "selected range title");
        Equal(total, value.TotalText, "selected range combined total");
        Equal(breakdown, value.BreakdownText, "selected range download/upload quantities");
    }

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
}
