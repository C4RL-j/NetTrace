using System.Globalization;
using Microsoft.Data.Sqlite;
using NetTrace.Models;
using NetTrace.Presentation;
using NetTrace.Services;
using NetTrace.Utils;

internal static class HistoryOverviewTests
{
    public static (string Name, Action Run)[] Cases =>
    [
        ("Empty history has honest zero summary values and no fake sessions", Empty),
        ("One completed local-day session contributes actual time and bytes", OneCompleted),
        ("Multiple distinct sessions sum connected time and recorded usage", MultipleCompleted),
        ("Older history remains recent without inflating today's values", OlderOnly),
        ("First-day active-only overview includes elapsed time and live counters", ActiveOnly),
        ("Live session replaces its persisted row without duplicate time or bytes", LiveDeduplication),
        ("Mixed completed and active sessions contribute once each", Mixed),
        ("Midnight-crossing completed session clips time and attributes bytes to its start day", CrossMidnight),
        ("An active session started yesterday contributes today's time without guessing today's bytes", ActiveFromYesterday),
        ("Half-open day bounds exclude midnight-ending old sessions and include new zero-length sessions", DayBounds),
        ("Connected time clamps future end times and excludes future starts", FutureBoundaries),
        ("Unfinished persisted history stops at confirmation and never falsely claims active state", PersistedUnfinished),
        ("History durations use compact seconds minutes and padded hours", DurationFormatting),
        ("Summary and recent session usage reuse existing byte formatting and display culture", ByteFormatting),
        ("Deleted profile history retains its network times and recorded usage", DeletedProfile),
        ("Persisted profile rename or deletion overrides a stale live profile display", LiveProfileChanges),
        ("Absent profile display names render Unassigned and preserve stored network identity", Unassigned),
        ("Recent sessions are deduplicated newest-first and bounded to four", RecentOrdering),
        ("The overview reads existing store data without changing rows events usage or schema", StoreReadOnly),
        ("Cached history refreshes live counters and elapsed time without rereading persisted rows", CachedLiveRefresh),
        ("Explicit overview refresh observes new persisted history", ExplicitRefresh),
        ("A changed active session reloads completed history even on a cached refresh", ActiveIdentityRefresh),
        ("Cached history reloads when the local calendar day changes", LocalDayRefresh),
        ("Cached history reloads when the local timezone changes", TimeZoneRefresh),
        ("Local day bounds respect 23-hour and 25-hour daylight-saving days", DaylightSaving),
    ];

    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone(
        "History fixture UTC+8", TimeSpan.FromHours(8), "History fixture UTC+8", "History fixture UTC+8");
    private static readonly DateTimeOffset Now = At(13);
    private static readonly NetworkIdentity Network = new("WiFi", "fixture-network", "Stored network", "fixture-adapter");

    private static DateTimeOffset At(int hour, int minute = 0, int second = 0, int day = 11) =>
        new(2026, 10, day, hour, minute, second, TimeSpan.FromHours(8));

    private static ConnectionSession Session(long id, DateTimeOffset start, DateTimeOffset? end,
        long download = 0, long upload = 0, DateTimeOffset? confirmed = null, string? profile = "DITO — Fixture device") =>
        new(id, profile is null ? null : 7, Network.NetworkType, Network.NetworkIdentifier,
            Network.FriendlyName, Network.AdapterId, start, confirmed ?? end ?? start,
            end, end is null ? null : "ApplicationExit", download, upload, profile);

    private static HistoryOverview Overview(ConnectionSession[] sessions, ConnectionSession? active = null,
        ConnectionSession[]? recent = null, DateTimeOffset? now = null) =>
        HistoryOverview.Create(sessions, recent ?? sessions, active, now ?? Now, Zone);

    private static void Empty()
    {
        var value = Overview([]);
        Equal(0, value.TodaySessionCount, "empty session count");
        Equal(TimeSpan.Zero, value.TodayConnectedTime, "empty connected duration");
        Equal("0s", value.TodayConnectedTimeText, "empty compact time");
        Equal("0 B", value.TodayDownloadText, "empty download");
        Equal("0 B", value.TodayUploadText, "empty upload");
        Equal(0, value.RecentSessions.Count, "no fake recent session");
    }

    private static void OneCompleted()
    {
        var session = Session(1, At(10), At(10, 0, 42), 1_048_576, 1024);
        var value = Overview([session]);
        Equal(1, value.TodaySessionCount, "one session");
        Equal(TimeSpan.FromSeconds(42), value.TodayConnectedTime, "recorded duration");
        Equal("42s", value.TodayConnectedTimeText, "seconds formatting");
        Equal(1_048_576L, value.TodayDownloadBytes, "download bytes");
        Equal(1024L, value.TodayUploadBytes, "upload bytes");
        Equal("10:00 AM – 10:00 AM", value.RecentSessions.Single().TimeRange, "local session times");
        Check(!value.RecentSessions.Single().IsActive, "Completed session cannot be active.");
    }

    private static void MultipleCompleted()
    {
        var first = Session(1, At(8), At(8, 30), 200, 100);
        var second = Session(2, At(9), At(10, 5), 500, 250);
        var value = Overview([first, second, first]);
        Equal(2, value.TodaySessionCount, "distinct session IDs");
        Equal(TimeSpan.FromMinutes(95), value.TodayConnectedTime, "summed connected time");
        Equal("1h 35m", value.TodayConnectedTimeText, "summed duration formatting");
        Equal(700L, value.TodayDownloadBytes, "summed recorded download");
        Equal(350L, value.TodayUploadBytes, "summed recorded upload");
    }

    private static void OlderOnly()
    {
        var value = Overview([Session(1, At(8, day: 10), At(9, day: 10), 900, 800)]);
        Equal(0, value.TodaySessionCount, "old sessions excluded from today");
        Equal(TimeSpan.Zero, value.TodayConnectedTime, "old duration excluded");
        Equal(0L, value.TodayDownloadBytes, "old usage excluded");
        Equal(1, value.RecentSessions.Count, "old session remains visible");
        Equal("Oct 10, 8:00 AM – 9:00 AM", value.RecentSessions[0].TimeRange, "older session date context");
    }

    private static void ActiveOnly()
    {
        var active = Session(1, At(12, 58), null, 1600, 700, At(12, 58, 30));
        var value = Overview([], active);
        Equal(1, value.TodaySessionCount, "live-only session counts once");
        Equal(TimeSpan.FromMinutes(2), value.TodayConnectedTime, "active elapsed time through now");
        Equal(1600L, value.TodayDownloadBytes, "current tracked bytes");
        Equal(700L, value.TodayUploadBytes, "current tracked upload");
        Check(value.RecentSessions.Single().IsActive, "Current session is visibly active.");
        Equal("12:58 PM – Active", value.RecentSessions.Single().TimeRange, "active local range");
    }

    private static void LiveDeduplication()
    {
        var persisted = Session(1, At(12), null, 100, 20, At(12, 10));
        var active = persisted with { DownloadBytes = 900, UploadBytes = 300, ProfileDisplayName = "Updated live profile" };
        var value = Overview([persisted, persisted], active, [persisted, persisted]);
        Equal(1, value.TodaySessionCount, "live row deduplication");
        Equal(TimeSpan.FromHours(1), value.TodayConnectedTime, "duration not counted twice");
        Equal(900L, value.TodayDownloadBytes, "live totals replace persisted totals");
        Equal(300L, value.TodayUploadBytes, "live upload replaces persisted upload");
        Equal(1, value.RecentSessions.Count, "one recent row");
        Equal(persisted.ProfileDisplayName, value.RecentSessions[0].ProfileName, "persisted profile join stays authoritative");
        Equal(ByteFormatter.Format(900), value.RecentSessions[0].DownloadText, "recent live bytes");
    }

    private static void Mixed()
    {
        var completed = Session(1, At(10), At(11), 100, 50);
        var active = Session(2, At(12), null, 200, 75);
        var value = Overview([completed, active], active);
        Equal(2, value.TodaySessionCount, "completed and active sessions");
        Equal(TimeSpan.FromHours(2), value.TodayConnectedTime, "combined observed and live time");
        Equal(300L, value.TodayDownloadBytes, "combined bytes");
        Equal(125L, value.TodayUploadBytes, "combined upload");
    }

    private static void CrossMidnight()
    {
        var crossing = Session(1, At(23, 50, day: 10), At(0, 20), 2_000_000, 400_000);
        var value = Overview([crossing]);
        Equal(1, value.TodaySessionCount, "overlapping session counted");
        Equal(TimeSpan.FromMinutes(20), value.TodayConnectedTime, "only today's connected portion");
        Equal(0L, value.TodayDownloadBytes, "recorded bytes belong to yesterday's start day");
        Equal(0L, value.TodayUploadBytes, "no fabricated per-minute upload split");
        Equal(ByteFormatter.Format(crossing.DownloadBytes), value.RecentSessions[0].DownloadText, "recent row retains complete session bytes");
        Equal("Oct 10, 11:50 PM – Oct 11, 12:20 AM", value.RecentSessions[0].TimeRange, "cross-day dates are explicit");
    }

    private static void ActiveFromYesterday()
    {
        var active = Session(1, At(23, 50, day: 10), null, 8_000_000, 2_000_000, At(12, 59));
        var value = Overview([active], active);
        Equal(1, value.TodaySessionCount, "active midnight-crossing session counts once");
        Equal(TimeSpan.FromHours(13), value.TodayConnectedTime, "active duration starts at local midnight");
        Equal(0L, value.TodayDownloadBytes, "live usage follows the same start-day attribution rule");
        Equal(0L, value.TodayUploadBytes, "yesterday's aggregate upload is not attributed to today");
        Equal(ByteFormatter.Format(active.DownloadBytes), value.RecentSessions[0].DownloadText, "actual full live session total remains visible");
        Check(value.RecentSessions[0].IsActive, "Crossing active session remains active.");
    }

    private static void DayBounds()
    {
        var old = Session(1, At(23, day: 10), At(0), 500, 200);
        var zero = Session(2, At(0), At(0), 10, 5);
        var nextDay = Session(3, At(0, day: 12), At(0, day: 12), 900, 900);
        var value = Overview([old, zero, nextDay]);
        Equal(1, value.TodaySessionCount, "half-open local day with zero-length new session");
        Equal(TimeSpan.Zero, value.TodayConnectedTime, "midnight endpoint does not add duration");
        Equal(10L, value.TodayDownloadBytes, "only local-start-day bytes");
        var bounds = HistoryOverview.GetLocalDayBounds(Now, Zone);
        Equal(new DateTimeOffset(2026, 10, 10, 16, 0, 0, TimeSpan.Zero), bounds.StartUtc, "local midnight converted to UTC");
        Equal(new DateTimeOffset(2026, 10, 11, 16, 0, 0, TimeSpan.Zero), bounds.EndUtc, "following local midnight converted to UTC");
    }

    private static void FutureBoundaries()
    {
        var value = Overview([Session(1, At(12), At(15)), Session(2, At(14), At(15))]);
        Equal(1, value.TodaySessionCount, "future session starts cannot inflate today");
        Equal(TimeSpan.FromHours(1), value.TodayConnectedTime, "confirmed future end clamps to now");
        var rollback = Overview([Session(1, At(12), At(11))]);
        Equal(TimeSpan.Zero, rollback.TodayConnectedTime, "bad or rolled-back clock cannot produce negative connected time");
    }

    private static void PersistedUnfinished()
    {
        var stale = Session(1, At(11), null, 300, 40, At(11, 20));
        var value = Overview([stale]);
        Equal(TimeSpan.FromMinutes(20), value.TodayConnectedTime, "no inferred connection after last confirmation");
        Equal(300L, value.TodayDownloadBytes, "stored actual bytes remain usable");
        Check(!value.RecentSessions[0].IsActive, "A stored unfinished row alone cannot prove active state.");
        Equal("11:00 AM – Last confirmed 11:20 AM", value.RecentSessions[0].TimeRange, "safe unfinished range");
    }

    private static void DurationFormatting()
    {
        foreach (var (duration, expected) in new[]
        {
            (TimeSpan.Zero, "0s"), (TimeSpan.FromMilliseconds(900), "0s"),
            (TimeSpan.FromSeconds(42), "42s"), (TimeSpan.FromSeconds(495), "8m 15s"),
            (TimeSpan.FromMinutes(8), "8m"), (TimeSpan.FromMinutes(65), "1h 05m"),
            (TimeSpan.FromMinutes(222), "3h 42m")
        })
            Equal(expected, Overview([Session(1, Now - duration, Now)]).TodayConnectedTimeText, "compact duration");
    }

    private static void ByteFormatting()
    {
        var prior = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "en-US", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (var bytes in new long[] { 0, 1023, 1024, 761 * 1024, 1_048_576, 2_824_365_998 })
                {
                    var value = Overview([Session(1, At(12), Now, bytes, bytes)]);
                    Equal(ByteFormatter.Format(bytes), value.TodayDownloadText, "summary uses existing units and rounding");
                    Equal(ByteFormatter.Format(bytes), value.TodayUploadText, "upload uses existing formatter");
                    Equal(ByteFormatter.Format(bytes), value.RecentSessions[0].DownloadText, "recent download uses existing formatter");
                    Equal(ByteFormatter.Format(bytes), value.RecentSessions[0].UploadText, "recent upload uses existing formatter");
                }
            }
        }
        finally { CultureInfo.CurrentCulture = prior; }
    }

    private static void DeletedProfile()
    {
        using var fixture = new StoreFixture();
        var profiles = new ProfileStore(fixture.Usage);
        var profile = profiles.CreateProfile("DITO", "Deleted fixture device");
        var session = fixture.AddCompleted(At(10), At(11), 700, 150, profile.Id);
        profiles.DeleteProfile(profile.Id);
        var value = fixture.Service.GetOverview(null);
        Equal(1, value.TodaySessionCount, "deleted profile does not hide history");
        Equal(session.Id, value.RecentSessions[0].Id, "original session remains");
        Equal("Unassigned", value.RecentSessions[0].ProfileName, "deleted profile fallback");
        Equal(Network.DisplayName, value.RecentSessions[0].NetworkName, "stored network identity survives profile deletion");
        Equal(TimeSpan.FromHours(1), value.TodayConnectedTime, "historical interval survives");
        Equal(700L, value.TodayDownloadBytes, "historical bytes survive");
    }

    private static void Unassigned()
    {
        foreach (var profile in new string?[] { null, "", "  " })
        {
            var value = Overview([Session(1, At(12), Now, profile: profile)]);
            Equal("Unassigned", value.RecentSessions[0].ProfileName, "missing valid profile display name");
            Equal(Network.DisplayName, value.RecentSessions[0].NetworkName, "stored network identity remains");
        }
    }

    private static void LiveProfileChanges()
    {
        using var fixture = new StoreFixture();
        var profiles = new ProfileStore(fixture.Usage);
        var original = profiles.CreateProfile("DITO", "Original fixture name");
        var active = fixture.Begin(At(12), original.Id) with { DownloadBytes = 1000, UploadBytes = 200 };
        Equal(original.DisplayName, fixture.Service.GetOverview(active).RecentSessions[0].ProfileName, "initial active profile name");
        var renamed = profiles.UpdateProfile(original.Id, "DITO", "Renamed fixture name");
        var value = fixture.Service.GetOverview(active);
        Equal(renamed.DisplayName, value.RecentSessions[0].ProfileName, "persisted rename wins stale live profile");
        Equal(1000L, value.TodayDownloadBytes, "rename preserves current live byte counters");
        profiles.DeleteProfile(original.Id);
        value = fixture.Service.GetOverview(active);
        Equal("Unassigned", value.RecentSessions[0].ProfileName, "deleted association cannot be resurrected by stale live state");
        Equal(Network.DisplayName, value.RecentSessions[0].NetworkName, "network identity remains after deletion");
        Equal(1000L, value.TodayDownloadBytes, "deletion preserves current live byte counters");
        Check(value.RecentSessions[0].IsActive, "Profile deletion does not end its current physical session.");
    }

    private static void RecentOrdering()
    {
        var sessions = Enumerable.Range(1, 8).Select(id => Session(id, At(id), At(id, 10))).ToArray();
        var sameStart = Session(99, At(8), At(8, 10));
        var value = Overview([], recent: [sessions[2], sessions[7], sameStart, sessions[0], sessions[7], .. sessions]);
        Equal(4, value.RecentSessions.Count, "bounded recent rows");
        Equal("99,8,7,6", string.Join(',', value.RecentSessions.Select(row => row.Id)), "latest start then descending stable ID");
        var active = Session(100, At(12), null, 456, 789);
        value = Overview([], active, sessions);
        Equal("100,8,7,6", string.Join(',', value.RecentSessions.Select(row => row.Id)), "new live snapshot enters bounded recent rows");
        Check(value.RecentSessions[0].IsActive, "Newest active session is represented.");
    }

    private static void StoreReadOnly()
    {
        using var fixture = new StoreFixture();
        fixture.AddCompleted(At(23, 50, day: 10), At(0, 20), 900, 300);
        fixture.AddCompleted(At(10), At(11), 700, 150);
        var persisted = fixture.Begin(At(12));
        fixture.History.Confirm(persisted.Id, At(12, 10), 50, 10);
        var active = persisted with { LastConfirmedAtUtc = At(12, 59), DownloadBytes = 500, UploadBytes = 100 };
        fixture.Usage.AddUsage(new DateOnly(2026, 10, 11), 3000, 1000);
        var before = Snapshot(fixture.Usage.DatabasePath);
        var value = fixture.Service.GetOverview(active);
        Equal(3, value.TodaySessionCount, "existing overlap query plus live snapshot");
        Equal(TimeSpan.FromMinutes(140), value.TodayConnectedTime, "real store local-day clipping");
        Equal(1200L, value.TodayDownloadBytes, "real store attribution with live replacement");
        Equal(250L, value.TodayUploadBytes, "real store upload attribution");
        fixture.Service.GetOverview(active with { DownloadBytes = 600 }, refresh: false);
        fixture.Service.GetOverview(null);
        Equal(before, Snapshot(fixture.Usage.DatabasePath), "overview performs no persistence or schema mutation");
    }

    private static void CachedLiveRefresh()
    {
        using var fixture = new StoreFixture();
        var active = fixture.Begin(At(12));
        var first = fixture.Service.GetOverview(active);
        fixture.Clock.Now = Now.AddMinutes(3);
        active = active with { DownloadBytes = 2048, UploadBytes = 1024 };
        var value = fixture.Service.GetOverview(active, refresh: false);
        Equal(first.TodayConnectedTime + TimeSpan.FromMinutes(3), value.TodayConnectedTime, "cached refresh updates live elapsed time");
        Equal(2048L, value.TodayDownloadBytes, "cached refresh updates live counters");
        // If a live tick issued a database query, this intentionally unavailable
        // fixture table would fail. Restore it before inspecting persisted history.
        RenameSessionTable(fixture.Usage.DatabasePath, true);
        try
        {
            value = fixture.Service.GetOverview(active with { DownloadBytes = 4096 }, refresh: false);
            Equal(4096L, value.TodayDownloadBytes, "cached live ticks do not query history");
        }
        finally { RenameSessionTable(fixture.Usage.DatabasePath, false); }
    }

    private static void ExplicitRefresh()
    {
        using var fixture = new StoreFixture();
        fixture.Service.GetOverview(null);
        fixture.AddCompleted(At(10), At(11), 777, 111);
        Equal(0, fixture.Service.GetOverview(null, refresh: false).TodaySessionCount, "cached same-day persisted input");
        var value = fixture.Service.GetOverview(null);
        Equal(1, value.TodaySessionCount, "explicit page refresh reads latest persisted sessions");
        Equal(777L, value.TodayDownloadBytes, "explicit refresh sees persisted totals");
    }

    private static void ActiveIdentityRefresh()
    {
        using var fixture = new StoreFixture();
        var first = fixture.Begin(At(10));
        fixture.Service.GetOverview(first);
        fixture.End(first, At(11), 333, 100);
        var second = fixture.Begin(At(12));
        var value = fixture.Service.GetOverview(second, refresh: false);
        Equal(2, value.TodaySessionCount, "session ID change reloads predecessor completion");
        Equal(TimeSpan.FromHours(2), value.TodayConnectedTime, "cached old live session no longer extends through now");
        Equal(333L, value.TodayDownloadBytes, "completed predecessor totals are refreshed");
        Check(value.RecentSessions.Single(row => row.Id == first.Id).IsActive == false, "Predecessor is completed.");
        fixture.End(second, Now, 444, 200);
        value = fixture.Service.GetOverview(null, refresh: false);
        Equal(777L, value.TodayDownloadBytes, "active-to-none reloads newly completed counters");
    }

    private static void LocalDayRefresh()
    {
        using var fixture = new StoreFixture();
        var active = fixture.Begin(At(12));
        active = active with { DownloadBytes = 999, UploadBytes = 100 };
        Equal(999L, fixture.Service.GetOverview(active).TodayDownloadBytes, "original local start-day attribution");
        fixture.Clock.Now = At(0, 15, day: 12);
        var value = fixture.Service.GetOverview(active, refresh: false);
        Equal(1, value.TodaySessionCount, "ongoing session overlaps new local day");
        Equal(TimeSpan.FromMinutes(15), value.TodayConnectedTime, "new day's active duration is clipped");
        Equal(0L, value.TodayDownloadBytes, "new day's totals do not inherit unsplittable bytes");
        Equal(ByteFormatter.Format(999), value.RecentSessions[0].DownloadText, "recent aggregate remains accurate");

        using var completedFixture = new StoreFixture();
        completedFixture.Service.GetOverview(null);
        completedFixture.AddCompleted(At(0, day: 12), At(0, 10, day: 12), 555, 100);
        completedFixture.Clock.Now = At(0, 15, day: 12);
        value = completedFixture.Service.GetOverview(null, refresh: false);
        Equal(1, value.TodaySessionCount, "day rollover reloads new persisted data with unchanged null active identity");
        Equal(555L, value.TodayDownloadBytes, "new day query supplies newly persisted bytes");
    }

    private static void TimeZoneRefresh()
    {
        using var fixture = new StoreFixture();
        fixture.AddCompleted(At(21, day: 10), At(22, day: 10), 555, 100);
        Equal(0, fixture.Service.GetOverview(null).TodaySessionCount, "session outside UTC+8 current local day");
        fixture.Clock.Zone = TimeZoneInfo.CreateCustomTimeZone("History fixture UTC-8",
            TimeSpan.FromHours(-8), "History fixture UTC-8", "History fixture UTC-8");
        var value = fixture.Service.GetOverview(null, refresh: false);
        Equal(1, value.TodaySessionCount, "timezone change reloads a session absent from cached today inputs");
        Equal(555L, value.TodayDownloadBytes, "changed local start date is now included accurately");
        Equal(1, value.RecentSessions.Count, "timezone change does not hide old history");
    }

    private static void DaylightSaving()
    {
        var spring = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var autumn = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2020, 1, 1),
            new DateTime(2030, 12, 31), TimeSpan.FromHours(1), spring, autumn);
        var zone = TimeZoneInfo.CreateCustomTimeZone("History fixture DST", TimeSpan.FromHours(-5),
            "History fixture DST", "Standard", "Daylight", [rule]);
        foreach (var (month, day, hours) in new[] { (3, 8, 23), (11, 1, 25) })
        {
            var noonUtc = new DateTimeOffset(2026, month, day, 17, 0, 0, TimeSpan.Zero);
            var bounds = HistoryOverview.GetLocalDayBounds(noonUtc, zone);
            Equal(TimeSpan.FromHours(hours), bounds.EndUtc - bounds.StartUtc, "DST local-day real duration");
            var now = bounds.EndUtc.AddSeconds(-1);
            var active = Session(1, bounds.StartUtc.AddMinutes(-10), null, 100, 50);
            var value = HistoryOverview.Create([], [], active, now, zone);
            Equal(now - bounds.StartUtc, value.TodayConnectedTime, "DST active session clips at correct UTC local midnight");
            Equal(0L, value.TodayDownloadBytes, "DST midnight crossing follows local start-day attribution");
        }
    }

    private static string Snapshot(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        var lines = new List<string>();
        foreach (var table in new[] { "sqlite_schema", "daily_usage", "ConnectionProfiles", "NetworkMappings", "ConnectionSessions", "ConnectionEvents", "VpnSessions", "daily_traffic_peaks" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table} ORDER BY rowid;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                lines.Add(table + ":" + string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(index => reader.GetValue(index).ToString())));
        }
        return string.Join('\n', lines);
    }

    private static void RenameSessionTable(string path, bool hide)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadWrite }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = hide ? "ALTER TABLE ConnectionSessions RENAME TO CachedSessionFixture;" :
            "ALTER TABLE CachedSessionFixture RENAME TO ConnectionSessions;";
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

    private sealed class OverviewClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = HistoryOverviewTests.Now;
        public TimeZoneInfo Zone { get; set; } = HistoryOverviewTests.Zone;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => Zone;
    }

    private sealed class StoreFixture : IDisposable
    {
        private readonly TestWorkspace _workspace = new();
        public UsageStore Usage { get; }
        public ConnectionHistoryStore History { get; }
        public OverviewClock Clock { get; } = new();
        public HistoryOverviewService Service { get; }

        public StoreFixture()
        {
            Usage = new UsageStore(_workspace.Root);
            History = new ConnectionHistoryStore(Usage);
            Service = new HistoryOverviewService(History, Clock);
        }

        public ConnectionSession Begin(DateTimeOffset started, long? profileId = null) =>
            History.Transition(null, started, null, 0, 0, Network, profileId,
                started, "Connected", "Fixture session connected.")!;

        public void End(ConnectionSession session, DateTimeOffset ended, long download, long upload) =>
            History.Transition(session.Id, ended, "Disconnected", download, upload,
                null, null, ended, "Disconnected", "Fixture session disconnected.");

        public ConnectionSession AddCompleted(DateTimeOffset started, DateTimeOffset ended,
            long download, long upload, long? profileId = null)
        {
            var session = Begin(started, profileId);
            End(session, ended, download, upload);
            return History.GetRecentSessions().Single(row => row.Id == session.Id);
        }

        public void Dispose() => _workspace.Dispose();
    }
}
