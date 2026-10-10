using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;
using NetTrace.Models;
using NetTrace.Services;

internal static class ProfileManagementTests
{
    public static (string Name, Action Run)[] Cases =>
    [
        ("Profile management creates, trims, edits and reloads existing profile records", ProfileCrud),
        ("Profile management preserves existing required names and duplicate-name rules", ExistingNameRules),
        ("Mapping lookup preserves exact SSID case and surrounding spaces", ExactMappingLookup),
        ("Mapping lookup validates identity without creating missing mappings", LookupValidation),
        ("Moving a mapping preserves its identity, metadata and stable ID", MovePreservesMapping),
        ("Moving a mapping to its existing owner is an idempotent guarded no-op", SameOwnerMove),
        ("Stale mapping moves cannot overwrite another profile's assignment", StaleMoveGuard),
        ("Moving a mapping validates its destination and missing identity safely", InvalidMove),
        ("Concurrent guarded mapping moves have only one winning owner", ConcurrentMoveGuard),
        ("A failed mapping move rolls back without altering its original assignment", MoveRollback),
        ("Removing a selected mapping retains the profile and its other mappings", RemoveSelectedMapping),
        ("Removing a stale mapping cannot delete another owner's mapping", StaleRemovalGuard),
        ("Removing a missing mapping reports a useful failure", MissingRemoval),
        ("A failed mapping removal leaves the persisted mapping unchanged", RemoveRollback),
        ("Deleting a selected profile removes only its owned mappings", DeleteOwnedMappings),
        ("Deleting the final profile leaves a clean persisted empty store", DeleteFinalProfile),
        ("Deleting a missing profile leaves all other saved profiles intact", MissingDeletion),
        ("Profile deletion preserves active and completed history, events and usage", DeletePreservesHistory),
        ("Failures at every profile deletion stage roll back all earlier writes", DeleteRollbackStages),
        ("Mapping removal and movement preserve historical profile associations", MappingChangesPreserveHistory),
        ("Profile management operations retain the existing schema and restrictive foreign keys", ExistingSchema),
        ("Profile management rejects invalid selected IDs without changing saved data", InvalidSelectedIds),
        ("Deleted profiles cannot return through an already resolved queued observation", StaleResolvedObservation),
    ];

    private static void ProfileCrud()
    {
        using var test = new SessionTestContext();
        var created = test.Profiles.CreateProfile("  DITO  ", "  OPPO A3x  ");
        Equal("DITO", created.TelcoName, "trimmed telco");
        Equal("OPPO A3x", created.DeviceName, "trimmed device");
        var edited = test.Profiles.UpdateProfile(created.Id, "  Smart ", " Router ");
        Equal(created.Id, edited.Id, "editing retains the selected profile");
        Equal(created.CreatedAt, edited.CreatedAt, "editing preserves original creation time");
        Equal(1, test.Profiles.GetProfiles().Count, "editing does not create another profile");
        Equal(edited, new ProfileStore(test.DatabasePath).GetProfile(created.Id), "saved changes survive reload");
    }

    private static void ExistingNameRules()
    {
        using var test = new SessionTestContext();
        var first = test.Profiles.CreateProfile("DITO", "OPPO A3x");
        var second = test.Profiles.CreateProfile("DITO", "OPPO A3x");
        Check(first.Id != second.Id, "Existing duplicate display-name rule remains allowed.");
        Expect<ArgumentException>(() => test.Profiles.CreateProfile(" ", "Device"));
        Expect<ArgumentException>(() => test.Profiles.CreateProfile("ISP", " "));
        Expect<ArgumentException>(() => test.Profiles.UpdateProfile(first.Id, "", "Device"));
        Expect<ArgumentException>(() => test.Profiles.UpdateProfile(first.Id, "ISP", ""));
        Equal(2, test.Profiles.GetProfiles().Count, "failed names create no extra records");
        Equal(first, test.Profiles.GetProfile(first.Id), "failed edit preserves existing record");
    }

    private static void ExactMappingLookup()
    {
        using var test = new SessionTestContext();
        var profile = test.Profiles.CreateProfile("DITO", "Device");
        var mapping = test.Profiles.AssignMapping(profile.Id, "WiFi", " OPPO A3x ", "Friendly");
        Equal(mapping, test.Profiles.GetMapping(" WiFi ", " OPPO A3x "), "network type normalization follows existing semantics");
        Check(test.Profiles.GetMapping("WiFi", "OPPO A3x") is null, "SSID spaces remain meaningful.");
        Check(test.Profiles.GetMapping("WiFi", " oppo a3x ") is null, "SSID case remains meaningful.");
        Check(test.Profiles.GetMapping("wifi", " OPPO A3x ") is null, "Network type matching remains exact.");
        var spaceSsid = test.Profiles.AssignMapping(profile.Id, "WiFi", " ");
        Equal(spaceSsid, test.Profiles.GetMapping("WiFi", " "), "an all-space SSID is still a valid exact identity");
    }

    private static void LookupValidation()
    {
        using var test = new SessionTestContext();
        foreach (var type in new[] { "", " ", null! })
            Expect<ArgumentException>(() => test.Profiles.GetMapping(type, "SSID"));
        foreach (var identifier in new[] { "", null! })
            Expect<ArgumentException>(() => test.Profiles.GetMapping("WiFi", identifier));
        Check(test.Profiles.GetMapping("WiFi", "Missing") is null, "Missing mappings are represented by null.");
        Equal(0, test.Profiles.GetProfiles().Count, "lookup does not create profiles");
    }

    private static void MovePreservesMapping()
    {
        using var test = new SessionTestContext();
        var source = test.Profiles.CreateProfile("DITO", "Source");
        var destination = test.Profiles.CreateProfile("Smart", "Destination");
        var mapping = test.Profiles.AssignMapping(source.Id, "WiFi", " Exact SSID ", "Friendly network");
        var moved = test.Profiles.MoveMapping(mapping.Id, source.Id, destination.Id);
        Equal(mapping with { ProfileId = destination.Id }, moved, "only ownership changes");
        Equal(0, test.Profiles.GetMappings(source.Id).Count, "original owner no longer owns the mapping");
        Equal(moved, test.Profiles.GetMappings(destination.Id).Single(), "only one destination mapping exists");
        Equal(destination.Id, test.Profiles.ResolveProfile(mapping.NetworkType, mapping.NetworkIdentifier)!.Id, "existing resolver sees the new owner");
        Equal(moved, new ProfileStore(test.DatabasePath).GetMapping(mapping.NetworkType, mapping.NetworkIdentifier), "move persists through reload");
        Equal(moved, test.Profiles.AssignMapping(destination.Id, mapping.NetworkType, mapping.NetworkIdentifier), "identical assignment remains a no-op");
        Expect<InvalidOperationException>(() => test.Profiles.AssignMapping(source.Id, mapping.NetworkType, mapping.NetworkIdentifier));
    }

    private static void SameOwnerMove()
    {
        using var test = new SessionTestContext();
        var profile = test.Profiles.CreateProfile("DITO", "Device");
        var mapping = test.Profiles.AssignMapping(profile.Id, "WiFi", "SSID");
        Equal(mapping, test.Profiles.MoveMapping(mapping.Id, profile.Id, profile.Id), "already owned mapping is unchanged");
        Equal(1, test.Profiles.GetMappings(profile.Id).Count, "same-owner operation never duplicates a mapping");
        Expect<InvalidOperationException>(() => test.Profiles.MoveMapping(mapping.Id, profile.Id + 100, profile.Id));
        Equal(mapping, test.Profiles.GetMapping("WiFi", "SSID"), "idempotence still requires the expected owner");
    }

    private static void StaleMoveGuard()
    {
        using var test = new SessionTestContext();
        var source = test.Profiles.CreateProfile("DITO", "Source");
        var destination = test.Profiles.CreateProfile("Smart", "Destination");
        var third = test.Profiles.CreateProfile("Globe", "Third");
        var mapping = test.Profiles.AssignMapping(source.Id, "WiFi", "SSID");
        var moved = new ProfileStore(test.DatabasePath).MoveMapping(mapping.Id, source.Id, destination.Id);
        var error = Expect<InvalidOperationException>(() => test.Profiles.MoveMapping(mapping.Id, source.Id, third.Id));
        Check(error.Message.Contains("Refresh", StringComparison.Ordinal), "Stale ownership explains how to recover.");
        Equal(moved, test.Profiles.GetMapping("WiFi", "SSID"), "stale action cannot overwrite the current owner");
        Equal(0, test.Profiles.GetMappings(third.Id).Count, "stale action creates no destination mapping");
    }

    private static void InvalidMove()
    {
        using var test = new SessionTestContext();
        var profile = test.Profiles.CreateProfile("DITO", "Device");
        var mapping = test.Profiles.AssignMapping(profile.Id, "WiFi", "SSID");
        Expect<InvalidOperationException>(() => test.Profiles.MoveMapping(mapping.Id, profile.Id, 99999));
        Expect<InvalidOperationException>(() => test.Profiles.MoveMapping(99999, profile.Id, profile.Id));
        Equal(mapping, test.Profiles.GetMapping("WiFi", "SSID"), "invalid move leaves its original owner intact");
    }

    private static void ConcurrentMoveGuard()
    {
        using var test = new SessionTestContext();
        var source = test.Profiles.CreateProfile("DITO", "Source");
        var destinations = new[] { test.Profiles.CreateProfile("Smart", "Destination"), test.Profiles.CreateProfile("Globe", "Destination") };
        var mapping = test.Profiles.AssignMapping(source.Id, "WiFi", "SSID");
        using var start = new ManualResetEventSlim();
        var tasks = destinations.Select(destination => Task.Run(() =>
        {
            start.Wait();
            try
            {
                new ProfileStore(test.DatabasePath).MoveMapping(mapping.Id, source.Id, destination.Id);
                return true;
            }
            catch (InvalidOperationException) { return false; }
        })).ToArray();
        start.Set();
        var results = Task.WhenAll(tasks).GetAwaiter().GetResult();
        Equal(1, results.Count(result => result), "exactly one expected-owner move succeeds");
        var current = test.Profiles.GetMapping("WiFi", "SSID")!;
        Check(destinations.Any(destination => destination.Id == current.ProfileId), "One selected destination owns the original mapping.");
        Equal(mapping.Id, current.Id, "concurrent moves preserve stable mapping ID");
        Equal(1L, Integer(test.DatabasePath, "SELECT count(*) FROM NetworkMappings;"), "concurrent moves cannot duplicate identity");
    }

    private static void MoveRollback()
    {
        using var test = new SessionTestContext();
        var source = test.Profiles.CreateProfile("DITO", "Source");
        var destination = test.Profiles.CreateProfile("Smart", "Destination");
        var mapping = test.Profiles.AssignMapping(source.Id, "WiFi", "SSID");
        Execute(test.DatabasePath, "CREATE TRIGGER FailMove BEFORE UPDATE OF ProfileId ON NetworkMappings BEGIN SELECT RAISE(ABORT,'fixture move failure'); END;");
        Expect<SqliteException>(() => test.Profiles.MoveMapping(mapping.Id, source.Id, destination.Id));
        Equal(mapping, test.Profiles.GetMapping("WiFi", "SSID"), "failed update rolls back ownership");
    }

    private static void RemoveSelectedMapping()
    {
        using var test = new SessionTestContext();
        var profile = test.Profiles.CreateProfile("DITO", "Device");
        var selected = test.Profiles.AssignMapping(profile.Id, "WiFi", "Selected");
        var retained = test.Profiles.AssignMapping(profile.Id, "WiFi", "Retained");
        test.Profiles.RemoveMapping(selected.Id, profile.Id);
        Equal(retained, test.Profiles.GetMappings(profile.Id).Single(), "only the selected mapping is removed");
        Equal(profile, test.Profiles.GetProfile(profile.Id), "profile remains saved");
        Check(test.Profiles.ResolveProfile("WiFi", "Selected") is null, "removed identity becomes unassigned");
        Check(new ProfileStore(test.DatabasePath).GetMapping("WiFi", "Selected") is null, "removal survives reload");
    }

    private static void StaleRemovalGuard()
    {
        using var test = new SessionTestContext();
        var source = test.Profiles.CreateProfile("DITO", "Source");
        var destination = test.Profiles.CreateProfile("Smart", "Destination");
        var mapping = test.Profiles.AssignMapping(source.Id, "WiFi", "SSID");
        var moved = test.Profiles.MoveMapping(mapping.Id, source.Id, destination.Id);
        Expect<InvalidOperationException>(() => test.Profiles.RemoveMapping(mapping.Id, source.Id));
        Equal(moved, test.Profiles.GetMapping("WiFi", "SSID"), "stale selection cannot remove another owner's mapping");
    }

    private static void MissingRemoval()
    {
        using var test = new SessionTestContext();
        var profile = test.Profiles.CreateProfile("DITO", "Device");
        var mapping = test.Profiles.AssignMapping(profile.Id, "WiFi", "SSID");
        test.Profiles.RemoveMapping(mapping.Id, profile.Id);
        var error = Expect<InvalidOperationException>(() => test.Profiles.RemoveMapping(mapping.Id, profile.Id));
        Check(error.Message.Contains("Refresh", StringComparison.Ordinal), "Missing mapping removal has a recovery message.");
        Equal(profile, test.Profiles.GetProfile(profile.Id), "missing removal leaves the profile intact");
    }

    private static void RemoveRollback()
    {
        using var test = new SessionTestContext();
        var profile = test.Profiles.CreateProfile("DITO", "Device");
        var mapping = test.Profiles.AssignMapping(profile.Id, "WiFi", "SSID");
        Execute(test.DatabasePath, "CREATE TRIGGER FailRemove BEFORE DELETE ON NetworkMappings BEGIN SELECT RAISE(ABORT,'fixture removal failure'); END;");
        Expect<SqliteException>(() => test.Profiles.RemoveMapping(mapping.Id, profile.Id));
        Equal(mapping, test.Profiles.GetMapping("WiFi", "SSID"), "failed removal leaves persisted mapping unchanged");
    }

    private static void DeleteOwnedMappings()
    {
        using var test = new SessionTestContext();
        var selected = test.Profiles.CreateProfile("DITO", "Selected");
        var retained = test.Profiles.CreateProfile("Smart", "Retained");
        test.Profiles.AssignMapping(selected.Id, "WiFi", "First");
        test.Profiles.AssignMapping(selected.Id, "Ethernet", "Second");
        var retainedMapping = test.Profiles.AssignMapping(retained.Id, "WiFi", "Retained");
        test.Profiles.DeleteProfile(selected.Id);
        Check(test.Profiles.GetProfile(selected.Id) is null, "Selected profile is removed.");
        Equal(0, test.Profiles.GetMappings(selected.Id).Count, "all owned mappings are removed");
        Equal(retained, test.Profiles.GetProfiles().Single(), "other saved profile retained");
        Equal(retainedMapping, test.Profiles.GetMappings(retained.Id).Single(), "other profile mapping retained");
        var reopened = new ProfileStore(test.DatabasePath);
        Check(reopened.ResolveProfile("WiFi", "First") is null, "deleted profile identity stays unassigned after reload");
        Equal(retained, reopened.GetProfile(retained.Id), "retained profile persists");
    }

    private static void DeleteFinalProfile()
    {
        using var test = new SessionTestContext();
        var profile = test.Profiles.CreateProfile("DITO", "Final");
        test.Profiles.AssignMapping(profile.Id, "WiFi", "SSID");
        test.Profiles.DeleteProfile(profile.Id);
        var reopened = new ProfileStore(test.DatabasePath);
        Equal(0, reopened.GetProfiles().Count, "final profile deletion leaves an empty store");
        Check(reopened.GetMapping("WiFi", "SSID") is null, "final profile's mapping is removed");
        var next = reopened.CreateProfile("Smart", "New");
        Equal(next, reopened.GetProfiles().Single(), "empty store accepts a later explicit profile save");
    }

    private static void MissingDeletion()
    {
        using var test = new SessionTestContext();
        var profile = test.Profiles.CreateProfile("DITO", "Retained");
        var error = Expect<InvalidOperationException>(() => test.Profiles.DeleteProfile(99999));
        Check(error.Message.Contains("no longer exists", StringComparison.Ordinal), "Missing deletion explains stale selection.");
        Equal(profile, test.Profiles.GetProfiles().Single(), "missing deletion cannot remove another profile");
    }

    private static void DeletePreservesHistory()
    {
        using var test = new SessionTestContext();
        var selected = test.Profiles.CreateProfile("DITO", "Deleted");
        var other = test.Profiles.CreateProfile("Smart", "Retained");
        PopulateHistory(test, selected, other);
        var sessions = test.History.GetRecentSessions();
        var events = test.History.GetRecentEvents();
        var active = test.History.GetActiveSession()!;
        var day = new DateOnly(2026, 1, 15);
        test.Usage.AddUsage(day, 123456, 7890);
        test.Usage.SaveTrafficPeaks(day, 1500, 200);
        var daily = Rows(test.DatabasePath, "SELECT * FROM daily_usage ORDER BY rowid;");
        var peaks = Rows(test.DatabasePath, "SELECT * FROM daily_traffic_peaks ORDER BY rowid;");
        test.Profiles.DeleteProfile(selected.Id);
        var afterSessions = test.History.GetRecentSessions();
        Equal(sessions.Count, afterSessions.Count, "all physical history rows remain");
        for (var index = 0; index < sessions.Count; index++)
            Equal(sessions[index].ProfileId == selected.Id ? sessions[index] with { ProfileId = null, ProfileDisplayName = null } : sessions[index],
                afterSessions[index], "history preserves identity, timestamps, duration and counters");
        var afterEvents = test.History.GetRecentEvents();
        Equal(events.Count, afterEvents.Count, "all event rows remain");
        for (var index = 0; index < events.Count; index++)
            Equal(events[index].ProfileId == selected.Id ? events[index] with { ProfileId = null } : events[index],
                afterEvents[index], "events preserve identity, order, timestamp and messages");
        Equal(active.Id, test.History.GetActiveSession()!.Id, "deletion does not close or replace the active physical session");
        Check(test.History.GetActiveSession()!.ProfileId is null, "Active persisted session safely detaches deleted profile.");
        Equal(daily, Rows(test.DatabasePath, "SELECT * FROM daily_usage ORDER BY rowid;"), "daily usage is unchanged");
        Equal(peaks, Rows(test.DatabasePath, "SELECT * FROM daily_traffic_peaks ORDER BY rowid;"), "passive traffic peaks are unchanged");
        Equal(0L, Integer(test.DatabasePath, "SELECT count(*) FROM pragma_foreign_key_check;"), "deletion leaves valid foreign keys");
    }

    private static void DeleteRollbackStages()
    {
        var triggerBodies = new[]
        {
            "BEFORE UPDATE OF ProfileId ON ConnectionSessions",
            "BEFORE UPDATE OF ProfileId ON ConnectionEvents",
            "BEFORE DELETE ON NetworkMappings",
            "BEFORE DELETE ON ConnectionProfiles"
        };
        foreach (var trigger in triggerBodies)
        {
            using var test = new SessionTestContext();
            var selected = test.Profiles.CreateProfile("DITO", "Deleted");
            var other = test.Profiles.CreateProfile("Smart", "Retained");
            PopulateHistory(test, selected, other);
            var tables = new[] { "ConnectionProfiles", "NetworkMappings", "ConnectionSessions", "ConnectionEvents" };
            var before = tables.ToDictionary(table => table, table => Rows(test.DatabasePath, $"SELECT * FROM {table} ORDER BY rowid;"));
            Execute(test.DatabasePath, $"CREATE TRIGGER FailDelete {trigger} BEGIN SELECT RAISE(ABORT,'fixture deletion failure'); END;");
            Expect<SqliteException>(() => test.Profiles.DeleteProfile(selected.Id));
            foreach (var table in tables)
                Equal(before[table], Rows(test.DatabasePath, $"SELECT * FROM {table} ORDER BY rowid;"), "failed deletion rolls back " + table + " at " + trigger);
            Execute(test.DatabasePath, "DROP TRIGGER FailDelete;");
            test.Profiles.DeleteProfile(selected.Id);
            Check(test.Profiles.GetProfile(selected.Id) is null, "A later valid deletion succeeds after rollback.");
        }
    }

    private static void MappingChangesPreserveHistory()
    {
        using var test = new SessionTestContext();
        var selected = test.Profiles.CreateProfile("DITO", "Selected");
        var other = test.Profiles.CreateProfile("Smart", "Retained");
        PopulateHistory(test, selected, other);
        var sessions = Rows(test.DatabasePath, "SELECT * FROM ConnectionSessions ORDER BY rowid;");
        var events = Rows(test.DatabasePath, "SELECT * FROM ConnectionEvents ORDER BY rowid;");
        var mapping = test.Profiles.GetMapping("WiFi", "Selected active")!;
        var moved = test.Profiles.MoveMapping(mapping.Id, selected.Id, other.Id);
        test.Profiles.RemoveMapping(moved.Id, other.Id);
        Equal(sessions, Rows(test.DatabasePath, "SELECT * FROM ConnectionSessions ORDER BY rowid;"), "mapping actions do not rewrite history");
        Equal(events, Rows(test.DatabasePath, "SELECT * FROM ConnectionEvents ORDER BY rowid;"), "mapping actions do not rewrite events");
    }

    private static void ExistingSchema()
    {
        using var test = new SessionTestContext();
        var schema = Rows(test.DatabasePath, "SELECT type,name,tbl_name,sql FROM sqlite_schema ORDER BY name;");
        var version = Integer(test.DatabasePath, "PRAGMA user_version;");
        var selected = test.Profiles.CreateProfile("DITO", "Selected");
        var other = test.Profiles.CreateProfile("Smart", "Retained");
        var mapping = test.Profiles.AssignMapping(selected.Id, "WiFi", "SSID");
        test.Profiles.MoveMapping(mapping.Id, selected.Id, other.Id);
        test.Profiles.RemoveMapping(mapping.Id, other.Id);
        test.Profiles.DeleteProfile(selected.Id);
        Equal(schema, Rows(test.DatabasePath, "SELECT type,name,tbl_name,sql FROM sqlite_schema ORDER BY name;"), "profile management has no schema migration");
        Equal(version, Integer(test.DatabasePath, "PRAGMA user_version;"), "schema version remains unchanged");
        test.Profiles.AssignMapping(other.Id, "WiFi", "Retained");
        Expect<SqliteException>(() => Execute(test.DatabasePath, "DELETE FROM ConnectionProfiles;"));
        Equal(other, test.Profiles.GetProfile(other.Id), "raw deletion remains restricted by existing foreign keys");
    }

    private static void InvalidSelectedIds()
    {
        using var test = new SessionTestContext();
        var profile = test.Profiles.CreateProfile("DITO", "Device");
        var mapping = test.Profiles.AssignMapping(profile.Id, "WiFi", "SSID");
        foreach (var id in new[] { 0L, -1L })
        {
            Expect<ArgumentOutOfRangeException>(() => test.Profiles.MoveMapping(id, profile.Id, profile.Id));
            Expect<ArgumentOutOfRangeException>(() => test.Profiles.MoveMapping(mapping.Id, id, profile.Id));
            Expect<ArgumentOutOfRangeException>(() => test.Profiles.MoveMapping(mapping.Id, profile.Id, id));
            Expect<ArgumentOutOfRangeException>(() => test.Profiles.RemoveMapping(id, profile.Id));
            Expect<ArgumentOutOfRangeException>(() => test.Profiles.RemoveMapping(mapping.Id, id));
            Expect<ArgumentOutOfRangeException>(() => test.Profiles.DeleteProfile(id));
        }
        Equal(profile, test.Profiles.GetProfile(profile.Id), "invalid selected IDs leave profile unchanged");
        Equal(mapping, test.Profiles.GetMapping("WiFi", "SSID"), "invalid selected IDs leave mapping unchanged");
    }

    private static void StaleResolvedObservation()
    {
        using var workspace = new TestWorkspace();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var clock = new HeadlessTimeProvider();
        var network = new NetworkIdentity("WiFi", "Race fixture", "Race fixture", "fixture-wifi");
        var identity = new FakeNetworkIdentityService { Network = network };
        var bandwidth = new FakeBandwidthMonitor
        {
            Adapters = [new AdapterInfo(network.AdapterId, "Fixture Wi-Fi", "Owned fixture", "Wireless80211", 86_000_000, "192.0.2.2", true)]
        };
        bandwidth.SetSnapshot(network.AdapterId, new NetworkSnapshot(10_000, 1000, clock.GetUtcNow()));
        var verifier = new VpnRouteVerifier(new VpnDetectionService(new FakeVpnInterfaceSource()), new FakeVpnRouteSource());
        using var service = MonitoringService.CreateForDataRoot(workspace.Root, identity, verifier, bandwidth, clock,
            new IcmpMonitor(new NoInternetPingSource(), timeProvider: clock),
            new InternetReachabilityMonitor(new NoInternetReachabilitySource(), timeProvider: clock));
        var selected = service.Profiles.CreateProfile("DITO", "Deleted during resolution");
        service.Profiles.AssignMapping(selected.Id, network.NetworkType, network.NetworkIdentifier);
        var profileMonitor = PrivateField<ConnectionProfileMonitor>(service, "_profileMonitor");
        var blockNext = 0;
        profileMonitor.ObservationReceived += (_, observation) =>
        {
            if (Interlocked.Exchange(ref blockNext, 0) != 0)
            {
                Equal(selected.Id, observation.State.Profile!.Id, "blocked observation already resolved the pre-edit profile");
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Fixture stale observation release timed out.");
            }
        };
        var publishedProfiles = new ConcurrentQueue<long?>();
        var recordChanges = 0;
        service.Changed += (_, _) =>
        {
            if (Volatile.Read(ref recordChanges) != 0) publishedProfiles.Enqueue(service.GetSnapshot().Connection.Profile?.Id);
        };
        try
        {
            service.Start();
            PrivateField<Timer>(service, "_timer").Change(Timeout.Infinite, Timeout.Infinite);
            WaitUntil(() => service.GetSnapshot().Session?.ProfileId == selected.Id, "fixture accepts its initial mapped connection");
            service.SampleNow();
            var original = service.GetSnapshot();
            Volatile.Write(ref blockNext, 1);
            identity.RaiseNetworkChanged();
            Check(entered.Wait(TimeSpan.FromSeconds(5)), "Resolved pre-edit observation reaches the deterministic barrier.");
            service.Profiles.DeleteProfile(selected.Id);
            service.RefreshRecognition();
            Volatile.Write(ref recordChanges, 1);
            release.Set();
            WaitUntil(() => service.GetSnapshot().Connection.Status == ConnectionStatus.ConnectedUnassigned, "fresh observation resolves the deleted profile as unassigned");
            var current = service.GetSnapshot();
            Equal(original.Session!.Id, current.Session!.Id, "profile deletion and invalidation preserve the physical session");
            Check(current.Session.ProfileId is null, "Active session refresh removes the deleted profile association.");
            Check(current.PersistenceMessage is null, "Discarded stale metadata cannot cause a foreign-key persistence failure.");
            Check(publishedProfiles.All(id => id != selected.Id), "Already published pre-edit callback cannot restore the deleted profile.");
            clock.Advance(TimeSpan.FromSeconds(1));
            bandwidth.SetSnapshot(network.AdapterId, new NetworkSnapshot(10_100, 1010, clock.GetUtcNow()));
            service.SampleNow();
            Equal(100L, service.GetSnapshot().SessionDownloaded, "profile-only invalidation preserves the bandwidth baseline");
        }
        finally { release.Set(); }
    }

    private static T PrivateField<T>(object instance, string name) =>
        (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
            ?? throw new InvalidOperationException("Missing fixture field: " + name));

    private static void PopulateHistory(SessionTestContext test, ConnectionProfile selected, ConnectionProfile other)
    {
        var first = new NetworkIdentity("WiFi", "Selected completed", "Selected completed", "fixture-wifi");
        var second = new NetworkIdentity("WiFi", "Other completed", "Other completed", "fixture-wifi");
        var active = new NetworkIdentity("WiFi", "Selected active", "Selected active", "fixture-wifi");
        test.Profiles.AssignMapping(selected.Id, first.NetworkType, first.NetworkIdentifier);
        test.Profiles.AssignMapping(other.Id, second.NetworkType, second.NetworkIdentifier);
        test.Profiles.AssignMapping(selected.Id, active.NetworkType, active.NetworkIdentifier);
        foreach (var (network, profile, bytes) in new[] { (first, selected, 8192L), (second, other, 42L), (active, selected, 100L) })
        {
            test.Clock.Advance(TimeSpan.FromSeconds(10));
            test.Tracker.ObserveConnection(new CurrentConnectionState(ConnectionStatus.ConnectedRecognized, network, profile, null));
            test.Clock.Advance(TimeSpan.FromSeconds(1));
            Check(test.Tracker.RecordSample(test.Tracker.Current!.Id, network.AdapterId, 1, test.Clock.GetUtcNow(), bytes, bytes / 4), "Fixture sample is attributed to its existing physical session.");
            test.Tracker.Flush();
        }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Integer(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string Rows(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(index => reader.IsDBNull(index) ? "<null>" : reader.GetValue(index).ToString())));
        return string.Join("\n", rows);
    }

    private static T Expect<T>(Action operation) where T : Exception
    {
        try { operation(); }
        catch (T exception)
        {
            Check(!string.IsNullOrWhiteSpace(exception.Message), "Persistence errors include a useful message.");
            return exception;
        }
        throw new InvalidOperationException("Expected operation failure: " + typeof(T).Name);
    }

    private static void WaitUntil(Func<bool> condition, string description)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(description);
            Thread.Sleep(20);
        }
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
