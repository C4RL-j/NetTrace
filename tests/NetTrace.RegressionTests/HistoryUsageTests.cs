using System.Globalization;
using Microsoft.Data.Sqlite;
using NetTrace.Models;
using NetTrace.Presentation;
using NetTrace.Services;
using NetTrace.Utils;

internal static class HistoryUsageTests
{
    public static (string Name, Action Run)[] Cases =>
    [
        ("Empty 7D usage exposes seven chronological zero-filled local dates", EmptySevenDays),
        ("Empty 30D usage exposes thirty chronological zero-filled local dates", EmptyThirtyDays),
        ("One session contributes integer download upload and total bytes to its start day", OneSession),
        ("Multiple same-day sessions aggregate raw bytes before formatting", SameDaySessions),
        ("Multi-day usage retains zero-use days between occupied buckets", MultipleDays),
        ("Zero-byte sessions retain their date without manufacturing recorded usage", ZeroByteSession),
        ("Active-only chart includes current tracked bytes without persisted history", ActiveOnly),
        ("Completed and active daily usage sum as distinct session contributions", CompletedAndActive),
        ("Active counters replace the same persisted session exactly once", ActiveDeduplication),
        ("Active session bytes begun yesterday remain in yesterday's daily bucket", ActiveYesterday),
        ("Persisted unfinished session contributes its recorded bytes without a live snapshot", PersistedUnfinished),
        ("Sessions outside the selected 7D period cannot inflate visible totals", OutsideSevenDays),
        ("The first 7D local midnight is included and the prior instant is excluded", SevenDayBoundary),
        ("The first 30D local midnight is included and the prior instant is excluded", ThirtyDayBoundary),
        ("Completed midnight-crossing traffic belongs entirely to its local start date", CrossMidnight),
        ("Seven local dates remain chronological across a month boundary", MonthBoundary),
        ("December 31 crossing to January 1 retains all 100 MB and 20 MB on December 31", YearCrossingAttribution),
        ("Thirty local dates remain chronological across a year boundary", ThirtyDayYearBoundary),
        ("Future session starts are excluded while a start exactly at now is included", FutureStarts),
        ("Repeated persisted session IDs never duplicate daily usage", PersistedDeduplication),
        ("Today's chart bytes agree with History 001 for completed active and crossing sessions", TodayConsistency),
        ("A shared explicit midnight time context keeps Today and chart consistent despite provider changes", SharedTimeContext),
        ("Chart axes reuse existing byte formatting from bytes through multiple gigabytes", AxisFormatting),
        ("7D labels cover every date with Today at the newest bucket", SevenDayLabels),
        ("30D labels remain sparse while covering the first and newest buckets", ThirtyDayLabels),
        ("Daily hover values expose the correct date download upload and total for both ranges", UsageTooltips),
        ("Zero-day hover values clearly state no recorded usage", ZeroTooltip),
        ("The chart scale follows the largest stacked daily total", MaximumScale),
        ("Daily byte sums saturate safely instead of overflowing into negative usage", OverflowSafety),
        ("Usage snapshots are read-only and remain stable when their input list changes", ImmutableSnapshots),
        ("Started-between store queries exclude older overlapping sessions and the upper endpoint", StartedBetweenStore),
        ("Started-between queries normalize UTC offsets and reject invalid windows", StoreWindowValidation),
        ("The bounded start-time query uses the existing session timestamp index", StartQueryIndex),
        ("Both chart ranges and live refreshes leave all existing store rows and schema unchanged", StoreReadOnly),
        ("Cached live refreshes avoid history queries and reuse unchanged visual snapshots", CachedLiveRefresh),
        ("Explicit refresh reads newly persisted daily usage", ExplicitRefresh),
        ("Changing between 7D and 30D invalidates cached persisted input", RangeRefresh),
        ("Changing active identity refreshes predecessor completion and final counters", ActiveIdentityRefresh),
        ("Local date rollover reloads newly relevant persisted daily usage", LocalDateRefresh),
        ("Timezone changes reload sessions newly inside the selected local-day range", TimeZoneRefresh),
        ("23-hour and 25-hour DST days still occupy one daily bucket each", DaylightSaving),
        ("DST range queries use exact local midnight UTC boundaries", DaylightSavingStoreBounds),
        ("Unsupported usage ranges are rejected without creating arbitrary buckets", InvalidRange),
    ];

    private static readonly DateOnly Today = new(2026, 10, 12);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone(
        "History usage fixture UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
    private static readonly DateTimeOffset Now = At(Today, 13);
    private static readonly NetworkIdentity Network = new("WiFi", "usage-fixture", "Stored usage fixture", "fixture-adapter");

    private static DateTimeOffset At(DateOnly date, int hour = 10, int minute = 0, int second = 0) =>
        new(date.Year, date.Month, date.Day, hour, minute, second, TimeSpan.FromHours(8));

    private static ConnectionSession Session(long id, DateTimeOffset start, long download = 0, long upload = 0,
        bool active = false, DateTimeOffset? end = null) =>
        new(id, null, Network.NetworkType, Network.NetworkIdentifier, Network.FriendlyName, Network.AdapterId,
            start, active ? start : end ?? start.AddMinutes(15), active ? null : end ?? start.AddMinutes(15),
            active ? null : "Disconnected", download, upload, null);

    private static HistoryUsageChartData Chart(ConnectionSession[] sessions, ConnectionSession? active = null,
        HistoryUsageRange range = HistoryUsageRange.SevenDays, DateTimeOffset? now = null, TimeZoneInfo? zone = null) =>
        HistoryUsageChartData.Create(sessions, active, now ?? Now, zone ?? Zone, range);

    private static DailyUsagePoint Point(HistoryUsageChartData data, DateOnly date) =>
        data.Points.Single(point => point.LocalDate == date);

    private static void AssertDates(HistoryUsageChartData data, DateOnly today)
    {
        Equal((int)data.Range, data.Points.Count, "exact daily bucket count");
        Equal(today.AddDays(1 - (int)data.Range), data.Points[0].LocalDate, "oldest local date first");
        Equal(today, data.Points[^1].LocalDate, "today is rightmost/newest");
        for (var index = 1; index < data.Points.Count; index++)
            Equal(data.Points[index - 1].LocalDate.AddDays(1), data.Points[index].LocalDate, "all local dates remain contiguous");
    }

    private static void EmptySevenDays() => AssertEmpty(HistoryUsageRange.SevenDays);
    private static void EmptyThirtyDays() => AssertEmpty(HistoryUsageRange.ThirtyDays);

    private static void AssertEmpty(HistoryUsageRange range)
    {
        var value = Chart([], range: range);
        AssertDates(value, Today);
        Check(value.Points.All(point => point.TotalBytes == 0), "Every empty date has zero recorded bytes.");
        Check(!value.HasUsage, "Empty period has no recorded usage.");
        Equal(0L, value.MaximumTotalBytes, "zero scale is finite and honest");
        Equal("0 B", value.MaximumText, "zero maximum formatting");
        Equal("0 B", value.MiddleText, "zero middle formatting");
    }

    private static void OneSession()
    {
        var value = Chart([Session(1, At(Today), 1_048_576, 1024)]);
        var point = Point(value, Today);
        Equal(1_048_576L, point.DownloadBytes, "raw download");
        Equal(1024L, point.UploadBytes, "raw upload");
        Equal(1_049_600L, point.TotalBytes, "raw stacked total");
        Equal(1, value.Points.Count(point => point.TotalBytes > 0), "only one occupied date");
        Check(value.HasUsage, "Recorded traffic makes the range nonempty.");
    }

    private static void SameDaySessions()
    {
        var value = Chart([Session(1, At(Today, 8), 1537, 517), Session(2, At(Today, 9), 1537, 517)]);
        var point = Point(value, Today);
        Equal(3074L, point.DownloadBytes, "integer sums retain bytes lost by display rounding");
        Equal(1034L, point.UploadBytes, "raw upload aggregation");
        Equal(4108L, point.TotalBytes, "download and upload stack after aggregation");
    }

    private static void MultipleDays()
    {
        var value = Chart([Session(1, At(Today.AddDays(-6)), 300, 50),
            Session(2, At(Today.AddDays(-3)), 900, 100), Session(3, At(Today), 200, 20)]);
        AssertDates(value, Today);
        Equal(350L, value.Points[0].TotalBytes, "oldest occupied date");
        Equal(0L, value.Points[1].TotalBytes, "missing date is zero-filled");
        Equal(1000L, value.Points[3].TotalBytes, "middle occupied date");
        Equal(220L, value.Points[^1].TotalBytes, "today's occupied date");
        Equal(4, value.Points.Count(point => point.TotalBytes == 0), "all gaps keep their chronological position");
    }

    private static void ZeroByteSession()
    {
        var value = Chart([Session(1, At(Today))]);
        AssertDates(value, Today);
        Check(!value.HasUsage, "Zero-byte session cannot claim measured usage.");
        Equal(0L, Point(value, Today).TotalBytes, "zero session total");
    }

    private static void ActiveOnly()
    {
        var active = Session(1, At(Today, 12), 3210, 1234, active: true);
        var value = Chart([], active);
        Equal(3210L, Point(value, Today).DownloadBytes, "current active counters without a persisted representation");
        Equal(1234L, Point(value, Today).UploadBytes, "active upload");
        Equal(4444L, value.MaximumTotalBytes, "live total sets scale");
    }

    private static void CompletedAndActive()
    {
        var completed = Session(1, At(Today, 8), 700, 80);
        var active = Session(2, At(Today, 12), 1200, 200, active: true);
        var point = Point(Chart([completed], active), Today);
        Equal(1900L, point.DownloadBytes, "completed plus live download");
        Equal(280L, point.UploadBytes, "completed plus live upload");
    }

    private static void ActiveDeduplication()
    {
        var persisted = Session(1, At(Today, 12), 100, 20, active: true);
        var active = persisted with { DownloadBytes = 900, UploadBytes = 300 };
        var point = Point(Chart([persisted, persisted], active), Today);
        Equal(900L, point.DownloadBytes, "exactly one authoritative active contribution");
        Equal(300L, point.UploadBytes, "persisted upload is replaced rather than added");
        Equal(1200L, point.TotalBytes, "no doubled active stacked total");
    }

    private static void ActiveYesterday()
    {
        var active = Session(1, At(Today.AddDays(-1), 23, 55), 5000, 900, active: true);
        var value = Chart([active], active);
        Equal(5900L, Point(value, Today.AddDays(-1)).TotalBytes, "all live bytes remain on local start day");
        Equal(0L, Point(value, Today).TotalBytes, "no invented split into today");
    }

    private static void PersistedUnfinished()
    {
        var persisted = Session(1, At(Today.AddDays(-1)), 600, 200, active: true);
        var value = Chart([persisted]);
        Equal(800L, Point(value, Today.AddDays(-1)).TotalBytes, "stored actual bytes remain recorded usage");
        Equal(0L, Point(value, Today).TotalBytes, "last confirmation cannot manufacture a different byte-attribution date");
    }

    private static void OutsideSevenDays()
    {
        var outside = Session(1, At(Today.AddDays(-7)), 8000, 2000);
        var value = Chart([outside], outside with { EndedAtUtc = null });
        Check(!value.HasUsage, "Both persisted and live starts outside the selected range are excluded.");
        Equal(0L, value.Points.Sum(point => point.TotalBytes), "outside counters cannot inflate visible dates");
    }

    private static void SevenDayBoundary() => AssertBoundary(HistoryUsageRange.SevenDays);
    private static void ThirtyDayBoundary() => AssertBoundary(HistoryUsageRange.ThirtyDays);

    private static void AssertBoundary(HistoryUsageRange range)
    {
        var first = At(Today.AddDays(1 - (int)range), 0);
        var value = Chart([Session(1, first.AddTicks(-1), 1000, 200), Session(2, first, 700, 80)], range: range);
        Equal(780L, value.Points[0].TotalBytes, "inclusive first local midnight");
        Equal(780L, value.Points.Sum(point => point.TotalBytes), "prior instant is excluded from selected dates");
    }

    private static void CrossMidnight()
    {
        var start = At(Today.AddDays(-1), 23, 50);
        var crossing = Session(1, start, 2_000_000, 400_000, end: At(Today, 0, 20));
        var value = Chart([crossing]);
        Equal(2_000_000L, Point(value, Today.AddDays(-1)).DownloadBytes, "full recorded download belongs to the start date");
        Equal(400_000L, Point(value, Today.AddDays(-1)).UploadBytes, "full upload belongs to the start date");
        Equal(0L, Point(value, Today).TotalBytes, "duration across midnight does not distribute traffic");
    }

    private static void MonthBoundary()
    {
        var today = new DateOnly(2026, 10, 1);
        var value = Chart([Session(1, At(new DateOnly(2026, 9, 30), 23), 200, 50)], now: At(today, 13));
        AssertDates(value, today);
        Equal(new DateOnly(2026, 9, 25), value.Points[0].LocalDate, "seven-day calendar subtraction crosses September");
        Equal(250L, Point(value, new DateOnly(2026, 9, 30)).TotalBytes, "September start date remains visible");
    }

    private static void YearCrossingAttribution()
    {
        var today = new DateOnly(2027, 1, 1);
        var previous = new DateOnly(2026, 12, 31);
        const long download = 100 * 1024 * 1024, upload = 20 * 1024 * 1024;
        var crossing = Session(1, At(previous, 23, 55), download, upload, end: At(today, 0, 15));
        foreach (var range in new[] { HistoryUsageRange.SevenDays, HistoryUsageRange.ThirtyDays })
        {
            var value = Chart([crossing], range: range, now: At(today, 13));
            Equal(download, Point(value, previous).DownloadBytes, "December 31 gets all 100 MB");
            Equal(upload, Point(value, previous).UploadBytes, "December 31 gets all 20 MB");
            Equal(0L, Point(value, today).TotalBytes, "January 1 gets no proportional traffic estimate");
        }
    }

    private static void ThirtyDayYearBoundary()
    {
        var today = new DateOnly(2027, 1, 1);
        var value = Chart([], range: HistoryUsageRange.ThirtyDays, now: At(today, 13));
        AssertDates(value, today);
        Equal(new DateOnly(2026, 12, 3), value.Points[0].LocalDate, "thirty-day year boundary");
    }

    private static void FutureStarts()
    {
        var value = Chart([Session(1, Now.AddTicks(1), 1000, 100), Session(2, Now, 700, 80),
            Session(3, At(Today.AddDays(1), 0), 2000, 500)]);
        Equal(780L, Point(value, Today).TotalBytes, "future starts are ignored even within today's date");
        Equal(780L, value.Points.Sum(point => point.TotalBytes), "a start exactly at now is included once");
    }

    private static void PersistedDeduplication()
    {
        var session = Session(1, At(Today), 555, 111);
        var point = Point(Chart([session, session, session]), Today);
        Equal(555L, point.DownloadBytes, "one contribution per stable stored ID");
        Equal(111L, point.UploadBytes, "duplicate representations do not inflate upload");
    }

    private static void TodayConsistency()
    {
        var crossing = Session(1, At(Today.AddDays(-1), 23, 50), 1000, 300, end: At(Today, 0, 20));
        var completed = Session(2, At(Today, 8), 500, 100);
        var active = Session(3, At(Today, 12), 900, 200, active: true);
        var future = Session(4, Now.AddMinutes(1), 6000, 2000);
        foreach (var live in new ConnectionSession?[] { active, crossing with { EndedAtUtc = null } })
        {
            ConnectionSession[] sessions = [crossing, completed, active, future];
            var overview = HistoryOverview.Create(sessions, sessions, live, Now, Zone);
            foreach (var range in new[] { HistoryUsageRange.SevenDays, HistoryUsageRange.ThirtyDays })
            {
                var point = Point(Chart(sessions, live, range), Today);
                Equal(overview.TodayDownloadBytes, point.DownloadBytes, "History 001 and chart share today's download definition");
                Equal(overview.TodayUploadBytes, point.UploadBytes, "History 001 and chart share today's upload definition");
            }
        }
    }

    private static void AxisFormatting()
    {
        var prior = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "en-US", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (var bytes in new long[] { 0, 1024, 761 * 1024, 24 * 1024 * 1024, 1_073_741_824, 20L * 1_073_741_824 })
                {
                    var value = Chart([Session(1, At(Today), bytes)]);
                    Equal(ByteFormatter.Format(bytes), value.MaximumText, "scale uses existing byte units and rounding");
                    Equal(ByteFormatter.Format(bytes / 2), value.MiddleText, "middle scale uses integer bytes");
                    Check(value.MaximumText.Length <= 14, "Representative byte scale remains compact.");
                }
            }
        }
        finally { CultureInfo.CurrentCulture = prior; }
    }

    private static void SharedTimeContext()
    {
        using var fixture = new StoreFixture();
        fixture.Clock.Zone = TimeZoneInfo.CreateCustomTimeZone("History usage provider UTC-8",
            TimeSpan.FromHours(-8), "UTC-8", "UTC-8");
        var sharedNow = At(Today, 0);
        var active = Session(42, sharedNow, 900, 200, active: true);
        var overviewService = new HistoryOverviewService(fixture.History, fixture.Clock);
        var overview = overviewService.GetOverview(active, observedAtUtc: sharedNow, localTimeZone: Zone);
        var usage = fixture.Service.GetChartData(HistoryUsageRange.SevenDays, active,
            observedAtUtc: sharedNow, localTimeZone: Zone);
        Equal(Today, usage.Points[^1].LocalDate, "explicit shared timezone and instant choose the midnight local date");
        Equal(900L, overview.TodayDownloadBytes, "explicit midnight includes the start exactly at now");
        Equal(overview.TodayDownloadBytes, usage.Points[^1].DownloadBytes, "shared context agrees on download");
        Equal(overview.TodayUploadBytes, usage.Points[^1].UploadBytes, "shared context agrees on upload");
        Equal(TimeSpan.Zero, overview.TodayConnectedTime, "exact start at local midnight has zero elapsed duration");
        var defaultOverview = overviewService.GetOverview(active);
        var defaultUsage = fixture.Service.GetChartData(HistoryUsageRange.SevenDays, active);
        Equal(Today.AddDays(-1), defaultUsage.Points[^1].LocalDate, "ordinary API still uses provider timezone and current instant");
        Equal(defaultOverview.TodayDownloadBytes, defaultUsage.Points[^1].DownloadBytes, "default APIs retain History 001 agreement");
    }

    private static void SevenDayLabels()
    {
        var value = Chart([]);
        Equal(7, value.AxisLabels.Count, "one compact label for each 7D bucket");
        Equal("0,1,2,3,4,5,6", string.Join(',', value.AxisLabels.Select(label => label.Index)), "label order follows dates");
        Equal("Today", value.AxisLabels[^1].Text, "rightmost day is clear");
        Equal(value.Points[0].LocalDate.ToString("ddd", CultureInfo.CurrentCulture), value.AxisLabels[0].Text, "short local weekday label");
    }

    private static void ThirtyDayLabels()
    {
        var value = Chart([], range: HistoryUsageRange.ThirtyDays);
        Equal(30, value.Points.Count, "all thirty bars remain represented");
        Check(value.AxisLabels.Count is >= 5 and <= 7, "Thirty-day labels are sparse.");
        Equal(0, value.AxisLabels[0].Index, "first date has a label");
        Equal(29, value.AxisLabels[^1].Index, "newest date has a label");
        Equal("Today", value.AxisLabels[^1].Text, "today remains newest");
        Equal(value.Points[0].LocalDate.ToString("MMM d", CultureInfo.CurrentCulture), value.AxisLabels[0].Text, "first sparse date is explicit");
        Check(value.AxisLabels.Zip(value.AxisLabels.Skip(1)).All(pair => pair.First.Index < pair.Second.Index), "Sparse labels stay ordered.");
    }

    private static void UsageTooltips()
    {
        foreach (var range in new[] { HistoryUsageRange.SevenDays, HistoryUsageRange.ThirtyDays })
        {
            var point = Point(Chart([Session(1, At(Today), 1_048_576, 1024)], range: range), Today);
            Equal("Oct 12, 2026\nDownload: " + ByteFormatter.Format(1_048_576) + "\nUpload: " +
                ByteFormatter.Format(1024) + "\nTotal: " + ByteFormatter.Format(1_049_600), point.ToolTipText,
                "tooltip exposes full date and all actual byte values");
        }
    }

    private static void ZeroTooltip() => Equal("Oct 12, 2026\nNo recorded data usage",
        Point(Chart([]), Today).ToolTipText, "empty-day hover is clear and compact");

    private static void MaximumScale()
    {
        var value = Chart([Session(1, At(Today.AddDays(-1)), 200, 1200),
            Session(2, At(Today), 900, 100), Session(3, At(Today.AddDays(-3)), 300, 100)]);
        Equal(1400L, value.MaximumTotalBytes, "maximum follows download plus upload for the largest day");
        Equal(ByteFormatter.Format(1400), value.MaximumText, "top stacked scale label");
        Equal(ByteFormatter.Format(700), value.MiddleText, "middle scale label");
    }

    private static void OverflowSafety()
    {
        var point = Point(Chart([Session(1, At(Today, 8), long.MaxValue - 100, long.MaxValue - 50),
            Session(2, At(Today, 9), 500, 100)]), Today);
        Equal(long.MaxValue, point.DownloadBytes, "download saturation");
        Equal(long.MaxValue, point.UploadBytes, "upload saturation");
        Equal(long.MaxValue, point.TotalBytes, "stacked saturation");
        Check(point.TotalBytes > 0, "Large totals cannot wrap negative.");
    }

    private static void ImmutableSnapshots()
    {
        var sessions = new List<ConnectionSession> { Session(1, At(Today), 100, 20) };
        var value = HistoryUsageChartData.Create(sessions, null, Now, Zone, HistoryUsageRange.SevenDays);
        sessions.Clear();
        Equal(120L, value.Points[^1].TotalBytes, "input mutation does not change chart snapshot");
        var points = (IList<DailyUsagePoint>)value.Points;
        Check(points.IsReadOnly, "Daily points are read-only.");
        Throws<NotSupportedException>(() => points[0] = new(Today, 1, 2), "UI consumers cannot replace daily points");
        Check(((IList<HistoryUsageAxisLabel>)value.AxisLabels).IsReadOnly, "Axis labels are read-only.");
    }

    private static void StartedBetweenStore()
    {
        using var fixture = new StoreFixture();
        var first = At(Today.AddDays(-6), 0);
        fixture.AddCompleted(first.AddMinutes(-10), first.AddMinutes(10), 1000, 200);
        var included = fixture.AddCompleted(first, first.AddMinutes(10), 500, 100);
        var nextDay = At(Today.AddDays(1), 0);
        fixture.AddCompleted(nextDay, nextDay.AddMinutes(10), 900, 300);
        var rows = fixture.History.GetSessionsStartedBetween(first, nextDay);
        Equal(1, rows.Count, "only starts inside the half-open window are loaded");
        Equal(included.Id, rows[0].Id, "exact lower boundary is included");
        var value = fixture.Service.GetChartData(HistoryUsageRange.SevenDays, null);
        Equal(600L, value.Points[0].TotalBytes, "service uses start-day retrieval rather than interval overlap");
    }

    private static void StoreWindowValidation()
    {
        using var fixture = new StoreFixture();
        var first = At(Today.AddDays(-6), 0);
        var included = fixture.AddCompleted(first, first.AddMinutes(10), 500, 100);
        var rows = fixture.History.GetSessionsStartedBetween(first.ToOffset(TimeSpan.FromHours(-3)), Now.ToUniversalTime());
        Equal(included.Id, rows.Single().Id, "query timestamps normalize equivalent UTC offsets");
        Throws<ArgumentException>(() => fixture.History.GetSessionsStartedBetween(Now, Now), "empty window rejected");
        Throws<ArgumentException>(() => fixture.History.GetSessionsStartedBetween(Now, Now.AddSeconds(-1)), "reversed window rejected");
    }

    private static void StartQueryIndex()
    {
        using var fixture = new StoreFixture();
        using var connection = Open(fixture.Usage.DatabasePath, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = """
            EXPLAIN QUERY PLAN SELECT Id FROM ConnectionSessions
            WHERE StartedAtUtc >= $from AND StartedAtUtc < $to
            ORDER BY StartedAtUtc DESC, Id DESC;
            """;
        command.Parameters.AddWithValue("$from", At(Today.AddDays(-29), 0).ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$to", At(Today.AddDays(1), 0).ToUniversalTime().ToString("O"));
        using var reader = command.ExecuteReader();
        var plan = new List<string>();
        while (reader.Read()) plan.Add(reader.GetString(3));
        Check(plan.Any(line => line.Contains("IX_ConnectionSessions_StartedAtUtc", StringComparison.Ordinal)), "Bounded query uses the existing timestamp index.");
        Check(plan.All(line => !line.Contains("TEMP B-TREE", StringComparison.Ordinal)), "Existing index also supplies newest-first ordering.");
    }

    private static void StoreReadOnly()
    {
        using var fixture = new StoreFixture();
        fixture.AddCompleted(At(Today.AddDays(-20)), At(Today.AddDays(-20), 11), 2000, 300);
        fixture.AddCompleted(At(Today), At(Today, 11), 700, 100);
        var active = fixture.Begin(At(Today, 12));
        fixture.History.Confirm(active.Id, At(Today, 12, 10), 50, 10);
        active = active with { DownloadBytes = 900, UploadBytes = 200 };
        fixture.Usage.AddUsage(Today, 3000, 1000);
        fixture.Usage.SaveTrafficPeaks(Today, 12000, 3000);
        var before = Snapshot(fixture.Usage.DatabasePath);
        var seven = fixture.Service.GetChartData(HistoryUsageRange.SevenDays, active);
        Equal(1900L, Point(seven, Today).TotalBytes, "7D includes completed and live actual totals");
        fixture.Service.GetChartData(HistoryUsageRange.ThirtyDays, active);
        fixture.Service.GetChartData(HistoryUsageRange.ThirtyDays, active with { DownloadBytes = 1200 }, refresh: false);
        fixture.Service.GetChartData(HistoryUsageRange.SevenDays, null);
        Equal(before, Snapshot(fixture.Usage.DatabasePath), "range selection and chart display mutate no history usage events or schema");
    }

    private static void CachedLiveRefresh()
    {
        using var fixture = new StoreFixture();
        var active = fixture.Begin(At(Today, 12)) with { DownloadBytes = 900, UploadBytes = 200 };
        var initial = fixture.Service.GetChartData(HistoryUsageRange.SevenDays, active);
        Check(ReferenceEquals(initial, fixture.Service.GetChartData(HistoryUsageRange.SevenDays, active)), "Unchanged explicit persisted refresh reuses chart snapshot.");
        fixture.Clock.Now = Now.AddMinutes(1);
        HideSessionTable(fixture.Usage.DatabasePath, hide: true);
        try
        {
            var unchanged = fixture.Service.GetChartData(HistoryUsageRange.SevenDays, active, refresh: false);
            Check(ReferenceEquals(initial, unchanged), "Elapsed time alone neither queries nor redraws byte chart.");
            var changed = fixture.Service.GetChartData(HistoryUsageRange.SevenDays,
                active with { DownloadBytes = 1900 }, refresh: false);
            Check(!ReferenceEquals(initial, changed), "Changed tracked usage creates a new visual snapshot.");
            Equal(2100L, Point(changed, Today).TotalBytes, "SQL-free cached refresh applies latest live bytes");
            Equal(1100L, Point(initial, Today).TotalBytes, "older visual snapshot remains immutable");
        }
        finally { HideSessionTable(fixture.Usage.DatabasePath, hide: false); }
    }

    private static void ExplicitRefresh()
    {
        using var fixture = new StoreFixture();
        var initial = fixture.Service.GetChartData(HistoryUsageRange.SevenDays, null);
        fixture.AddCompleted(At(Today), At(Today, 11), 700, 100);
        Check(ReferenceEquals(initial, fixture.Service.GetChartData(HistoryUsageRange.SevenDays, null, refresh: false)), "Same-day cached tick retains prior persisted input.");
        var value = fixture.Service.GetChartData(HistoryUsageRange.SevenDays, null);
        Equal(800L, Point(value, Today).TotalBytes, "explicit refresh observes newly completed bytes");
        Check(!ReferenceEquals(initial, value), "New daily usage creates a changed chart snapshot.");
    }

    private static void RangeRefresh()
    {
        using var fixture = new StoreFixture();
        fixture.AddCompleted(At(Today.AddDays(-20)), At(Today.AddDays(-20), 11), 700, 100);
        var seven = fixture.Service.GetChartData(HistoryUsageRange.SevenDays, null);
        Check(!seven.HasUsage, "Twenty-day-old start is outside 7D.");
        var thirty = fixture.Service.GetChartData(HistoryUsageRange.ThirtyDays, null, refresh: false);
        Equal(800L, Point(thirty, Today.AddDays(-20)).TotalBytes, "range change reloads older starts");
        AssertDates(thirty, Today);
        Check(!ReferenceEquals(seven, thirty), "Changed range changes the visual snapshot.");
        seven = fixture.Service.GetChartData(HistoryUsageRange.SevenDays, null, refresh: false);
        AssertDates(seven, Today);
        Check(!seven.HasUsage, "Returning to 7D applies its own bounded data.");
    }

    private static void ActiveIdentityRefresh()
    {
        using var fixture = new StoreFixture();
        var first = fixture.Begin(At(Today, 8));
        fixture.Service.GetChartData(HistoryUsageRange.SevenDays, first);
        fixture.End(first, At(Today, 9), 333, 100);
        var second = fixture.Begin(At(Today, 12)) with { DownloadBytes = 500, UploadBytes = 150 };
        var value = fixture.Service.GetChartData(HistoryUsageRange.SevenDays, second, refresh: false);
        Equal(833L, Point(value, Today).DownloadBytes, "new active ID reloads completed predecessor counters");
        Equal(250L, Point(value, Today).UploadBytes, "successor upload plus final predecessor upload");
        fixture.End(second, Now, 600, 200);
        value = fixture.Service.GetChartData(HistoryUsageRange.SevenDays, null, refresh: false);
        Equal(933L, Point(value, Today).DownloadBytes, "active-to-none reloads final persisted bytes");
        Equal(300L, Point(value, Today).UploadBytes, "completed successor upload after live state clears");
    }

    private static void LocalDateRefresh()
    {
        using var fixture = new StoreFixture();
        fixture.Service.GetChartData(HistoryUsageRange.SevenDays, null);
        var tomorrow = Today.AddDays(1);
        fixture.AddCompleted(At(tomorrow, 0), At(tomorrow, 0, 10), 555, 100);
        fixture.Clock.Now = At(tomorrow, 0, 15);
        var value = fixture.Service.GetChartData(HistoryUsageRange.SevenDays, null, refresh: false);
        AssertDates(value, tomorrow);
        Equal(655L, Point(value, tomorrow).TotalBytes, "new local date reloads future-day rows absent from original query");
    }

    private static void TimeZoneRefresh()
    {
        using var fixture = new StoreFixture();
        var old = Today.AddDays(-7);
        fixture.AddCompleted(At(old, 21), At(old, 22), 555, 100);
        Check(!fixture.Service.GetChartData(HistoryUsageRange.SevenDays, null).HasUsage, "Initial UTC+8 range excludes its previous date.");
        fixture.Clock.Zone = TimeZoneInfo.CreateCustomTimeZone("History usage fixture UTC-8",
            TimeSpan.FromHours(-8), "UTC-8", "UTC-8");
        var value = fixture.Service.GetChartData(HistoryUsageRange.SevenDays, null, refresh: false);
        var changedToday = Today.AddDays(-1);
        AssertDates(value, changedToday);
        Equal(655L, Point(value, old).TotalBytes, "timezone change reloads starts absent from the prior date window");
    }

    private static TimeZoneInfo DstZone()
    {
        var spring = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var autumn = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2020, 1, 1),
            new DateTime(2030, 12, 31), TimeSpan.FromHours(1), spring, autumn);
        return TimeZoneInfo.CreateCustomTimeZone("History usage fixture DST", TimeSpan.FromHours(-5),
            "History usage fixture DST", "Standard", "Daylight", [rule]);
    }

    private static void DaylightSaving()
    {
        var zone = DstZone();
        foreach (var (date, hours) in new[] { (new DateOnly(2026, 3, 8), 23), (new DateOnly(2026, 11, 1), 25) })
        {
            var bounds = HistoryOverview.GetLocalDayBounds(date, zone);
            Equal(TimeSpan.FromHours(hours), bounds.EndUtc - bounds.StartUtc, "shared local-midnight DST boundaries");
            var now = bounds.EndUtc.AddSeconds(-1);
            var first = Session(1, bounds.StartUtc.AddMinutes(30), 700, 100);
            var second = Session(2, bounds.EndUtc.AddHours(-1), 900, 200);
            var crossing = Session(3, bounds.StartUtc.AddMinutes(-5), 555, 111, end: bounds.StartUtc.AddMinutes(5));
            var value = Chart([first, second, crossing], now: now, zone: zone);
            AssertDates(value, date);
            Equal(1600L, Point(value, date).DownloadBytes, "both ends of DST date aggregate in one bucket");
            Equal(300L, Point(value, date).UploadBytes, "DST date upload sum");
            Equal(666L, Point(value, date.AddDays(-1)).TotalBytes, "DST midnight crossing remains on its start date");
        }
    }

    private static void DaylightSavingStoreBounds()
    {
        var zone = DstZone();
        using var fixture = new StoreFixture();
        var today = new DateOnly(2026, 3, 9);
        fixture.Clock.Zone = zone;
        fixture.Clock.Now = new DateTimeOffset(2026, 3, 9, 16, 0, 0, TimeSpan.Zero);
        var first = HistoryOverview.GetLocalDayBounds(today.AddDays(-6), zone).StartUtc;
        fixture.AddCompleted(first.AddMinutes(-5), first.AddMinutes(5), 1000, 200);
        fixture.AddCompleted(first, first.AddMinutes(10), 700, 100);
        var value = fixture.Service.GetChartData(HistoryUsageRange.SevenDays, null);
        AssertDates(value, today);
        Equal(800L, value.Points[0].TotalBytes, "query begins at local midnight before the intervening spring offset change");
        Equal(800L, value.Points.Sum(point => point.TotalBytes), "bounded start query excludes the preceding UTC instant");
    }

    private static void InvalidRange()
    {
        Throws<ArgumentOutOfRangeException>(() => Chart([], range: (HistoryUsageRange)8), "pure model rejects unsupported range");
        using var fixture = new StoreFixture();
        Throws<ArgumentOutOfRangeException>(() => fixture.Service.GetChartData((HistoryUsageRange)0, null), "service rejects unsupported range");
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode }.ToString());
        connection.Open();
        return connection;
    }

    private static string Snapshot(string path)
    {
        using var connection = Open(path, SqliteOpenMode.ReadOnly);
        var lines = new List<string>();
        foreach (var table in new[] { "sqlite_schema", "daily_usage", "ConnectionProfiles", "NetworkMappings", "ConnectionSessions", "ConnectionEvents", "VpnSessions", "daily_traffic_peaks" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table} ORDER BY rowid;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) lines.Add(table + ":" + string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(index => reader.GetValue(index).ToString())));
        }
        return string.Join('\n', lines);
    }

    private static void HideSessionTable(string path, bool hide)
    {
        using var connection = Open(path, SqliteOpenMode.ReadWrite);
        using var command = connection.CreateCommand();
        command.CommandText = hide ? "ALTER TABLE ConnectionSessions RENAME TO CachedUsageSessionFixture;" :
            "ALTER TABLE CachedUsageSessionFixture RENAME TO ConnectionSessions;";
        command.ExecuteNonQuery();
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

    private static void Throws<TException>(Action action, string description) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException(description + ": expected " + typeof(TException).Name + ".");
    }

    private sealed class UsageClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = HistoryUsageTests.Now;
        public TimeZoneInfo Zone { get; set; } = HistoryUsageTests.Zone;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => Zone;
    }

    private sealed class StoreFixture : IDisposable
    {
        private readonly TestWorkspace _workspace = new();
        public UsageStore Usage { get; }
        public ConnectionHistoryStore History { get; }
        public UsageClock Clock { get; } = new();
        public HistoryUsageService Service { get; }

        public StoreFixture()
        {
            Usage = new UsageStore(_workspace.Root);
            History = new ConnectionHistoryStore(Usage);
            Service = new HistoryUsageService(History, Clock);
        }

        public ConnectionSession Begin(DateTimeOffset started) => History.Transition(null, started,
            null, 0, 0, Network, null, started, "Connected", "Usage fixture connected.")!;

        public void End(ConnectionSession session, DateTimeOffset ended, long download, long upload) =>
            History.Transition(session.Id, ended, "Disconnected", download, upload,
                null, null, ended, "Disconnected", "Usage fixture disconnected.");

        public ConnectionSession AddCompleted(DateTimeOffset started, DateTimeOffset ended, long download, long upload)
        {
            var session = Begin(started);
            End(session, ended, download, upload);
            return History.GetRecentSessions().Single(row => row.Id == session.Id);
        }

        public void Dispose() => _workspace.Dispose();
    }
}
