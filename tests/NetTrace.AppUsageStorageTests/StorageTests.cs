using AppUsageStorage;
using Microsoft.Data.Sqlite;

internal static class StorageTests
{
    private static readonly DateOnly Today = new(2026, 10, 13);
    private static readonly DateTimeOffset Seen = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);
    private static ApplicationIdentity Brave(string? name = "Brave") => ApplicationIdentity.FromExecutablePath(@"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe", name);
    private static ApplicationIdentity Editor() => ApplicationIdentity.FromExecutablePath(@"C:\Apps\Editor\editor.exe", "Editor");

    public static IEnumerable<(string Name, Func<Task> Run)> Cases => new (string, Func<Task>)[]
    {
        ("Storage uses machine data NetTrace subdirectory", Check.Sync(LocationStrategy)),
        ("Fresh database creates and opens again", Check.Sync(CreateAndReopen)),
        ("Initialize is idempotent and preserves usage", Check.Sync(InitializeIdempotent)),
        ("Executable identity normalizes full path case and separators", Check.Sync(NormalizedIdentity)),
        ("Identity stores display name and executable metadata", Check.Sync(IdentityMetadata)),
        ("Fallback display name derives from executable filename", Check.Sync(DefaultDisplayName)),
        ("System and Unknown have separate stable identities", Check.Sync(FallbackIdentities)),
        ("One application day persists exact bytes and metadata", Check.Sync(InsertOne)),
        ("Small deltas accumulate into one application day", Check.Sync(Accumulate)),
        ("Download-only delta retains zero upload", Check.Sync(DownloadOnly)),
        ("Upload-only delta retains zero download", Check.Sync(UploadOnly)),
        ("Multiple apps on one day remain distinct", Check.Sync(MultipleApps)),
        ("Same app across days remains separate", Check.Sync(MultipleDays)),
        ("Multiple process observations use one executable identity", Check.Sync(MultipleProcessObservations)),
        ("System usage is preserved", Check.Sync(SystemBucket)),
        ("Unknown usage is preserved", Check.Sync(UnknownBucket)),
        ("Zero-byte observation supports metadata", Check.Sync(ZeroDelta)),
        ("No rows outside empty queried day", Check.Sync(EmptyDay)),
        ("Inclusive generic range filters rows", Check.Sync(GenericRange)),
        ("Today equivalent range returns one local day", Check.Sync(TodayRange)),
        ("Seven-day range includes today and previous six days", Check.Sync(SevenDayRange)),
        ("Thirty-day range includes today and previous twenty-nine days", Check.Sync(ThirtyDayRange)),
        ("Both range endpoints are inclusive", Check.Sync(InclusiveBoundaries)),
        ("Range results order dates then stable identity", Check.Sync(RangeOrdering)),
        ("Reversed range is rejected", Check.Sync(ReversedRange)),
        ("Local midnight creates separate date buckets", Check.Sync(MidnightSeparation)),
        ("Local date uses observation timezone rather than UTC", Check.Sync(LocalDateConversion)),
        ("DST fallback observations remain in local calendar date", Check.Sync(DstCalendarDate)),
        ("Negative download is rejected without mutation", Check.Sync(NegativeDownload)),
        ("Negative upload is rejected without mutation", Check.Sync(NegativeUpload)),
        ("Invalid batch member leaves all rows unchanged", Check.Sync(InvalidBatch)),
        ("LastSeen is normalized to UTC and newest timestamp wins", Check.Sync(LastSeenUtc)),
        ("Older observations cannot overwrite newer display metadata", Check.Sync(StaleMetadata)),
        ("Newest observation updates historical display metadata", Check.Sync(NewerMetadata)),
        ("Historical metadata survives executable deletion", Check.Sync(DeletedExecutable)),
        ("Ninety-day retention cutoff includes today", Check.Sync(RetentionCutoff)),
        ("Retention deletes strictly older dates and keeps cutoff", Check.Sync(RetentionBoundary)),
        ("Retention supports empty database", Check.Sync(EmptyRetention)),
        ("Daily counters support signed 64-bit maximum", Check.Sync(MaximumCounter)),
        ("Counter overflow rolls back entire batch", Check.Sync(OverflowRollback)),
        ("Synthetic many-app many-day aggregates remain small", Check.Sync(SyntheticSize)),
        ("Concurrent store instances accumulate without lost updates", ConcurrentWriters),
    };

    private static void LocationStrategy()
    {
        var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        Check.Equal(Path.Combine(commonData, "NetTrace", "app-usage.db"), DailyAppUsageStore.GetDatabasePath(commonData), "ProgramData location strategy");
        using var scope = new TestStorage();
        Check.Equal(Path.Combine(scope.Root, "NetTrace", "app-usage.db"), scope.DatabasePath, "isolated override preserves product subdirectory");
        Check.Equal(scope.DatabasePath, scope.Open().DatabasePath, "reported database path");
    }

    private static void CreateAndReopen()
    {
        using var scope = new TestStorage();
        Check.True(!File.Exists(scope.DatabasePath), "fresh database absent");
        var store = scope.Open();
        Check.True(File.Exists(scope.DatabasePath), "database created");
        store.AddUsageDelta(Today, Brave(), 41, 7, Seen);
        var row = scope.Open().GetUsageForDate(Today).Single();
        Check.Equal(41L, row.DownloadBytes, "reopened download");
        Check.Equal(7L, row.UploadBytes, "reopened upload");
    }

    private static void InitializeIdempotent()
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        store.AddUsageDelta(Today, Brave(), 19, 3, Seen);
        store.Initialize();
        store.Initialize();
        Check.Equal(19L, store.GetUsageForDate(Today).Single().DownloadBytes, "repeat initialize retains data");
    }

    private static void NormalizedIdentity()
    {
        var first = Brave();
        var equivalent = ApplicationIdentity.FromExecutablePath("c:/PROGRAM FILES/BraveSoftware/Brave-Browser/Application/./brave.exe");
        Check.Equal(first.Key, equivalent.Key, "case, separator and dot segments normalize");
        Check.Equal("path:" + Path.GetFullPath(first.ExecutablePath!).ToUpperInvariant(), first.Key, "path-based key");
        Check.True(!first.Key.Contains("pid", StringComparison.OrdinalIgnoreCase), "identity has no process component");
    }

    private static void IdentityMetadata()
    {
        var app = Brave("Historical browser");
        Check.Equal("Historical browser", app.DisplayName, "provided display name");
        Check.Equal("brave.exe", app.ExecutableName, "executable filename");
        Check.Equal(Path.GetFullPath(@"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe"), app.ExecutablePath, "full executable path");
    }

    private static void DefaultDisplayName()
    {
        var app = ApplicationIdentity.FromExecutablePath(@"C:\Apps\Editor\editor.exe");
        Check.Equal("editor", app.DisplayName, "default display name");
    }

    private static void FallbackIdentities()
    {
        Check.Equal("system", ApplicationIdentity.System.Key, "stable System key");
        Check.Equal("unknown", ApplicationIdentity.Unknown.Key, "stable Unknown key");
        Check.True(ApplicationIdentity.System.ExecutablePath is null, "System has no invented path");
        Check.True(ApplicationIdentity.Unknown.ExecutableName is null, "Unknown has no invented executable name");
    }

    private static void InsertOne()
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        var app = Brave();
        store.AddUsageDelta(Today, app, 1_024, 2_048, Seen);
        var row = store.GetUsageForDate(Today).Single();
        Check.Equal(Today, row.LocalDate, "date");
        Check.Equal(app.Key, row.ApplicationIdentity, "identity");
        Check.Equal(app.DisplayName, row.DisplayName, "display name");
        Check.Equal(app.ExecutableName, row.ExecutableName, "executable name");
        Check.Equal(app.ExecutablePath, row.ExecutablePath, "executable path");
        Check.Equal(1_024L, row.DownloadBytes, "download");
        Check.Equal(2_048L, row.UploadBytes, "upload");
        Check.Equal(Seen, row.LastSeen, "last seen");
    }

    private static void Accumulate()
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        for (var i = 0; i < 25; i++) store.AddUsageDelta(Today, Brave(), 11, 3, Seen.AddSeconds(i));
        var row = store.GetUsageForDate(Today).Single();
        Check.Equal(275L, row.DownloadBytes, "accumulated download");
        Check.Equal(75L, row.UploadBytes, "accumulated upload");
        Check.Equal(Seen.AddSeconds(24), row.LastSeen, "latest observation");
    }

    private static void DownloadOnly() => OneDirection(8_765, 0);
    private static void UploadOnly() => OneDirection(0, 4_321);

    private static void OneDirection(long download, long upload)
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        store.AddUsageDelta(Today, Brave(), download, upload, Seen);
        var row = store.GetUsageForDate(Today).Single();
        Check.Equal(download, row.DownloadBytes, "directional download");
        Check.Equal(upload, row.UploadBytes, "directional upload");
    }

    private static void MultipleApps()
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        store.AddUsageDelta(Today, Brave(), 10, 1, Seen);
        store.AddUsageDelta(Today, Editor(), 20, 2, Seen);
        var rows = store.GetUsageForDate(Today);
        Check.Equal(2, rows.Count, "two app rows");
        Check.Equal(10L, rows.Single(row => row.ApplicationIdentity == Brave().Key).DownloadBytes, "Brave counter");
        Check.Equal(20L, rows.Single(row => row.ApplicationIdentity == Editor().Key).DownloadBytes, "Editor counter");
    }

    private static void MultipleDays()
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        store.AddUsageDelta(Today.AddDays(-1), Brave(), 10, 1, Seen.AddDays(-1));
        store.AddUsageDelta(Today, Brave(), 20, 2, Seen);
        Check.Equal(10L, store.GetUsageForDate(Today.AddDays(-1)).Single().DownloadBytes, "previous day");
        Check.Equal(20L, store.GetUsageForDate(Today).Single().DownloadBytes, "today");
    }

    private static void MultipleProcessObservations()
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        // Process IDs remain upstream: all three process observations resolve to the same executable.
        var observations = new[] { (Pid: 1201, Download: 10L), (Pid: 1202, Download: 20L), (Pid: 1203, Download: 30L) };
        foreach (var observation in observations)
            store.AddUsageDelta(Today, Brave(), observation.Download, 1, Seen);
        Check.Equal(60L, store.GetUsageForDate(Today).Single().DownloadBytes, "same executable aggregates across processes");
        Check.Equal(1L, scope.Scalar("SELECT COUNT(*) FROM daily_app_usage"), "only one persisted row");
    }

    private static void SystemBucket() => FallbackBucket(ApplicationIdentity.System);
    private static void UnknownBucket() => FallbackBucket(ApplicationIdentity.Unknown);

    private static void FallbackBucket(ApplicationIdentity app)
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        store.AddUsageDelta(Today, app, 99, 44, Seen);
        var row = store.GetUsageForDate(Today).Single();
        Check.Equal(app.Key, row.ApplicationIdentity, "fallback identity persisted");
        Check.Equal(app.DisplayName, row.DisplayName, "fallback display persisted");
        Check.Equal(99L, row.DownloadBytes, "fallback traffic retained");
        Check.True(row.ExecutablePath is null, "fallback path remains absent");
    }

    private static void ZeroDelta()
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        store.AddUsageDelta(Today, Brave(), 0, 0, Seen);
        var row = store.GetUsageForDate(Today).Single();
        Check.Equal(0L, row.DownloadBytes, "zero download");
        Check.Equal(0L, row.UploadBytes, "zero upload");
        Check.Equal("Brave", row.DisplayName, "zero-byte metadata");
    }

    private static void EmptyDay()
    {
        using var scope = new TestStorage();
        Check.Equal(0, scope.Open().GetUsageForDate(Today).Count, "empty day result");
    }

    private static void PopulateDays(DailyAppUsageStore store)
    {
        var deltas = Enumerable.Range(0, 40).Select(i => new UsageDelta(Today.AddDays(-i), Brave(), i + 1, i + 2, Seen.AddDays(-i))).ToArray();
        store.AddUsageDeltas(deltas);
    }

    private static void GenericRange() => CheckRange(10, 4);
    private static void TodayRange() => CheckRange(0, 0);
    private static void SevenDayRange() => CheckRange(6, 0);
    private static void ThirtyDayRange() => CheckRange(29, 0);

    private static void CheckRange(int firstDaysAgo, int lastDaysAgo)
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        PopulateDays(store);
        var start = Today.AddDays(-firstDaysAgo);
        var end = Today.AddDays(-lastDaysAgo);
        var rows = store.GetUsageForRange(start, end);
        Check.Equal(firstDaysAgo - lastDaysAgo + 1, rows.Count, "inclusive range count");
        Check.Equal(start, rows.First().LocalDate, "range starts at requested day");
        Check.Equal(end, rows.Last().LocalDate, "range ends at requested day");
        Check.Equal(Enumerable.Range(lastDaysAgo, firstDaysAgo - lastDaysAgo + 1).Sum(i => (long)i + 1), rows.Sum(row => row.DownloadBytes), "exact range bytes");
    }

    private static void InclusiveBoundaries()
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        var start = Today.AddDays(-6);
        foreach (var date in new[] { start.AddDays(-1), start, Today, Today.AddDays(1) })
            store.AddUsageDelta(date, Brave(), 1, 1, Seen);
        var rows = store.GetUsageForRange(start, Today);
        Check.Equal(2, rows.Count, "outside rows excluded");
        Check.Equal(start, rows[0].LocalDate, "start included");
        Check.Equal(Today, rows[1].LocalDate, "end included");
    }

    private static void RangeOrdering()
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        foreach (var date in new[] { Today, Today.AddDays(-1) })
            foreach (var app in new[] { ApplicationIdentity.Unknown, ApplicationIdentity.System, Brave(), Editor() })
                store.AddUsageDelta(date, app, 1, 1, Seen);
        var rows = store.GetUsageForRange(Today.AddDays(-1), Today);
        var expected = rows.OrderBy(row => row.LocalDate).ThenBy(row => row.ApplicationIdentity, StringComparer.Ordinal).ToArray();
        Check.True(rows.SequenceEqual(expected), "date then identity order");
    }

    private static void ReversedRange()
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        Check.Throws<ArgumentException>(() => store.GetUsageForRange(Today, Today.AddDays(-1)), "reversed range validation");
    }

    private static TimeZoneInfo FixedLocalZone() => TimeZoneInfo.CreateCustomTimeZone("TestUtcPlus8", TimeSpan.FromHours(8), "Test local", "Test local");

    private static void MidnightSeparation()
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        var first = UsageDelta.FromObservation(new DateTimeOffset(2026, 10, 12, 23, 59, 0, TimeSpan.FromHours(8)), FixedLocalZone(), Brave(), 10, 1);
        var second = UsageDelta.FromObservation(new DateTimeOffset(2026, 10, 13, 0, 1, 0, TimeSpan.FromHours(8)), FixedLocalZone(), Brave(), 20, 2);
        store.AddUsageDeltas(new[] { first, second });
        Check.Equal(new DateOnly(2026, 10, 12), first.LocalDate, "23:59 local date");
        Check.Equal(Today, second.LocalDate, "00:01 local date");
        Check.Equal(10L, store.GetUsageForDate(first.LocalDate).Single().DownloadBytes, "previous midnight bucket");
        Check.Equal(20L, store.GetUsageForDate(second.LocalDate).Single().DownloadBytes, "next midnight bucket");
    }

    private static void LocalDateConversion()
    {
        var observedAt = new DateTimeOffset(2026, 10, 12, 16, 1, 0, TimeSpan.Zero);
        var delta = UsageDelta.FromObservation(observedAt, FixedLocalZone(), Brave(), 1, 2);
        Check.Equal(Today, delta.LocalDate, "local date differs from UTC date");
        Check.Equal(observedAt, delta.LastSeen, "observation timestamp preserved");
    }

    private static void DstCalendarDate()
    {
        var daylightStart = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var daylightEnd = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1), daylightStart, daylightEnd);
        var zone = TimeZoneInfo.CreateCustomTimeZone("TestEastern2026", TimeSpan.FromHours(-5), "Test Eastern", "Test standard", "Test daylight", new[] { rule });
        var first = UsageDelta.FromObservation(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), zone, Brave(), 1, 1);
        var repeatedHour = UsageDelta.FromObservation(new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero), zone, Brave(), 1, 1);
        Check.Equal(new DateOnly(2026, 11, 1), first.LocalDate, "first local 01:30");
        Check.Equal(first.LocalDate, repeatedHour.LocalDate, "second local 01:30 same calendar date");
    }

    private static void NegativeDownload() => RejectNegative(-1, 0);
    private static void NegativeUpload() => RejectNegative(0, -1);

    private static void RejectNegative(long download, long upload)
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        store.AddUsageDelta(Today, Brave(), 3, 4, Seen);
        Check.Throws<ArgumentOutOfRangeException>(() => store.AddUsageDelta(Today, Brave(), download, upload, Seen), "negative delta validation");
        var row = store.GetUsageForDate(Today).Single();
        Check.Equal(3L, row.DownloadBytes, "download unchanged");
        Check.Equal(4L, row.UploadBytes, "upload unchanged");
    }

    private static void InvalidBatch()
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        var deltas = new[] { new UsageDelta(Today, Brave(), 20, 4, Seen), new UsageDelta(Today, Editor(), -1, 0, Seen) };
        Check.Throws<ArgumentOutOfRangeException>(() => store.AddUsageDeltas(deltas), "batch input validation");
        Check.Equal(0, store.GetUsageForDate(Today).Count, "invalid batch is atomic");
    }

    private static void LastSeenUtc()
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        var newest = Seen.ToOffset(TimeSpan.FromHours(8));
        store.AddUsageDelta(Today, Brave(), 1, 1, newest);
        store.AddUsageDelta(Today, Brave(), 1, 1, Seen.AddMinutes(-1));
        var lastSeen = store.GetUsageForDate(Today).Single().LastSeen;
        Check.Equal(Seen, lastSeen, "newest timestamp retained");
        Check.Equal(TimeSpan.Zero, lastSeen.Offset, "timestamp read back UTC");
        using var connection = scope.Raw();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_seen_utc FROM daily_app_usage";
        Check.Equal(Seen.ToString("O", System.Globalization.CultureInfo.InvariantCulture), (string)command.ExecuteScalar()!, "UTC round-trip serialization");
    }

    private static void StaleMetadata()
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        store.AddUsageDelta(Today, Brave("New name"), 10, 1, Seen);
        store.AddUsageDelta(Today, Brave("Old name"), 20, 2, Seen.AddMinutes(-1));
        var row = store.GetUsageForDate(Today).Single();
        Check.Equal("New name", row.DisplayName, "stale metadata ignored");
        Check.Equal(30L, row.DownloadBytes, "stale observation still contributes bytes");
    }

    private static void NewerMetadata()
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        store.AddUsageDelta(Today, Brave("Old name"), 10, 1, Seen);
        store.AddUsageDelta(Today, Brave("New name"), 0, 0, Seen.AddMinutes(1));
        var row = store.GetUsageForDate(Today).Single();
        Check.Equal("New name", row.DisplayName, "new metadata retained");
        Check.Equal(10L, row.DownloadBytes, "metadata refresh retains usage");
    }

    private static void DeletedExecutable()
    {
        using var scope = new TestStorage();
        var executablePath = Path.Combine(scope.Root, "removed-app.exe");
        File.WriteAllText(executablePath, "test fixture; never executed");
        var app = ApplicationIdentity.FromExecutablePath(executablePath, "Removed application");
        scope.Open().AddUsageDelta(Today, app, 321, 123, Seen);
        File.Delete(executablePath);
        var row = scope.Open().GetUsageForDate(Today).Single();
        Check.Equal("Removed application", row.DisplayName, "historical display survives disappearance");
        Check.Equal("removed-app.exe", row.ExecutableName, "historical executable name survives");
        Check.Equal(executablePath, row.ExecutablePath, "historical path survives");
        Check.Equal(321L, row.DownloadBytes, "historical bytes survive");
    }

    private static void RetentionCutoff()
    {
        Check.Equal(90, DailyAppUsageStore.RetentionDays, "bounded retention policy");
        var cutoff = DailyAppUsageStore.GetRetentionCutoff(Today);
        Check.Equal(Today.AddDays(-89), cutoff, "today plus previous 89 dates");
        Check.Equal(90, Today.DayNumber - cutoff.DayNumber + 1, "ninety inclusive dates");
    }

    private static void RetentionBoundary()
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        var cutoff = DailyAppUsageStore.GetRetentionCutoff(Today);
        foreach (var date in new[] { cutoff.AddDays(-1), cutoff, Today })
            foreach (var app in new[] { Brave(), ApplicationIdentity.System })
                store.AddUsageDelta(date, app, 1, 1, Seen);
        Check.Equal(2, store.RetentionCleanup(cutoff), "only old rows deleted");
        Check.Equal(0, store.GetUsageForDate(cutoff.AddDays(-1)).Count, "older date cleared");
        Check.Equal(2, store.GetUsageForDate(cutoff).Count, "cutoff retained");
        Check.Equal(2, store.GetUsageForDate(Today).Count, "today retained");
        Check.Equal(0, store.RetentionCleanup(cutoff), "cleanup idempotent");
    }

    private static void EmptyRetention()
    {
        using var scope = new TestStorage();
        Check.Equal(0, scope.Open().RetentionCleanup(Today), "empty cleanup returns zero");
    }

    private static void MaximumCounter()
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        store.AddUsageDelta(Today, Brave(), long.MaxValue, long.MaxValue, Seen);
        var row = store.GetUsageForDate(Today).Single();
        Check.Equal(long.MaxValue, row.DownloadBytes, "maximum download exact");
        Check.Equal(long.MaxValue, row.UploadBytes, "maximum upload exact");
    }

    private static void OverflowRollback()
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        store.AddUsageDelta(Today, Brave(), long.MaxValue, 1, Seen);
        var batch = new[] { new UsageDelta(Today, Editor(), 50, 5, Seen), new UsageDelta(Today, Brave(), 1, 0, Seen.AddMinutes(1)) };
        Check.Failure(() => store.AddUsageDeltas(batch), AppUsageStorageFailure.TransactionFailed, scope.DatabasePath);
        var row = store.GetUsageForDate(Today).Single();
        Check.Equal(Brave().Key, row.ApplicationIdentity, "prior batch insertion rolled back");
        Check.Equal(long.MaxValue, row.DownloadBytes, "overflow retains exact old download");
        Check.Equal(Seen, row.LastSeen, "failed write retains old timestamp");
    }

    private static void SyntheticSize()
    {
        using var scope = new TestStorage();
        var store = scope.Open();
        const int apps = 100;
        const int days = 120;
        var identities = Enumerable.Range(0, apps).Select(i => ApplicationIdentity.FromExecutablePath($@"C:\Synthetic\Application-{i:000}\app-{i:000}.exe", $"Synthetic app {i}")).ToArray();
        var deltas = new List<UsageDelta>(apps * days * 2);
        for (var day = 0; day < days; day++)
            foreach (var app in identities)
            {
                deltas.Add(new UsageDelta(Today.AddDays(-day), app, 1_000, 100, Seen.AddDays(-day)));
                deltas.Add(new UsageDelta(Today.AddDays(-day), app, 500, 50, Seen.AddDays(-day).AddMinutes(1)));
            }
        store.AddUsageDeltas(deltas);
        Check.Equal((long)apps * days, scope.Scalar("SELECT COUNT(*) FROM daily_app_usage"), "daily aggregate row count, no raw events");
        var rows = store.GetUsageForRange(Today.AddDays(-(days - 1)), Today);
        Check.Equal(apps * days, rows.Count, "synthetic range row count");
        Check.Equal((long)apps * days * 1_500, rows.Sum(row => row.DownloadBytes), "synthetic exact totals");
        Check.Equal(1L, scope.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"), "only aggregate table");
        var size = new FileInfo(scope.DatabasePath).Length;
        Check.True(size < 8L * 1024 * 1024, $"12,000 aggregate rows exceed 8 MiB: {size} bytes");
        Check.Equal(apps * (days - DailyAppUsageStore.RetentionDays), store.RetentionCleanup(DailyAppUsageStore.GetRetentionCutoff(Today)), "synthetic retention deletes older thirty days");
        Check.Equal((long)apps * DailyAppUsageStore.RetentionDays, scope.Scalar("SELECT COUNT(*) FROM daily_app_usage"), "synthetic bounded ninety days");
        Console.WriteLine($"Synthetic storage: {apps * days:N0} app/day rows, {deltas.Count:N0} input deltas, {size:N0} bytes.");
    }

    private static async Task ConcurrentWriters()
    {
        using var scope = new TestStorage();
        var first = scope.Open();
        var second = scope.Open();
        using var ready = new Barrier(2);
        await Task.WhenAll(new[] { first, second }.Select(store => Task.Run(() =>
        {
            if (!ready.SignalAndWait(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("Concurrent writers did not start.");
            for (var i = 0; i < 20; i++) store.AddUsageDelta(Today, Brave(), 7, 3, Seen.AddSeconds(i));
        })));
        var row = first.GetUsageForDate(Today).Single();
        Check.Equal(280L, row.DownloadBytes, "concurrent download no lost update");
        Check.Equal(120L, row.UploadBytes, "concurrent upload no lost update");
    }
}
