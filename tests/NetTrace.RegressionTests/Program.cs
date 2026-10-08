using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NetTrace.Models;
using NetTrace.Services;

var tests = new (string Name, Action Run)[]
{
    ("Clean install initializes the NetTrace database and version", CleanInstall),
    ("Legacy version-zero history is copied without modifying the source", LegacyUpgrade),
    ("Existing NetTrace version-zero history is preserved", ExistingUnversionedDatabase),
    ("Repeat launch keeps NetTrace data when both databases exist", RepeatLaunch),
    ("Usage totals and date windows survive reopening", PersistedTotals),
    ("Legacy copy includes uncheckpointed WAL history", WalUpgrade),
    ("Invalid legacy schema fails without publishing a target", InvalidLegacySchema),
    ("Invalid current schema fails without versioning or rebuilding it", InvalidCurrentSchema),
    ("Invalid version-one current schema is still rejected", InvalidVersionedCurrentSchema),
    ("Future current schema version fails without changing it", FutureCurrentVersion),
    ("Future legacy schema version fails without publishing a target", FutureLegacyVersion),
    ("Corrupt legacy data fails without publishing a target", CorruptLegacy),
    ("Destination creation failure preserves the legacy database", DestinationFailure),
    ("Locked legacy database fails with both paths in the diagnostic", LockedLegacy),
    ("Legacy database path that is a directory does not create blank history", LegacyPathDirectory),
    ("Version-one migration preserves usage rows and the original table", VersionOneUpgrade),
    ("Failed version-two migration rolls back new tables and version", FailedVersionTwoMigration),
    ("Version-two schema has profile keys and protected mapping relationships", ProfileSchema),
    ("Profiles persist across database reopening", ProfilePersistence),
    ("SSID mappings resolve after reopening without duplicate profiles", MappingPersistence),
    ("Unknown networks remain unassigned without creating a profile", UnknownNetwork),
    ("Duplicate mapping is idempotent and conflicts do not reassign it", DuplicateMappings),
    ("Profile edits preserve identity and all existing mappings", ProfileEdits),
    ("Profile and mapping validation protects stored associations", ProfileValidation),
    ("SSID identifiers preserve case and whitespace", ExactNetworkIdentifiers),
    ("Native SSID decoding preserves text and arbitrary byte identities", NativeSsidIdentities),
    ("Creating and assigning a profile rolls back on a mapping conflict", AtomicProfileAssignment),
    ("Mapping insertion failure rolls back an already inserted profile", AtomicAssignmentInsertionFailure),
    ("Damaged version-two recognition constraints stop startup safely", InvalidVersionTwoConstraints),
    ("Deleting a mapped profile is restricted by SQLite", RestrictedProfileDeletion),
    ("Connection changes resolve known, unknown and disconnected states", ConnectionTransitions),
    ("Repeated network events do not repeatedly change the current state", RepeatedNetworkEvents),
    ("Explicit refresh recognizes assignments and profile edits immediately", ConnectionProfileRefresh),
    ("Network detection failures and disposal leave monitoring state safe", ConnectionFailureAndDisposal),
    ("Slow native detection does not block current-state reads", NonBlockingCurrentState),
    ("Connection states distinguish recognition, assignment, unavailable identity and disconnection", ConnectionIdentityStates),
    ("Unavailable or disconnected identities never look up stale network mappings", UnavailableIdentityWithoutLookup),
    ("Invalid network identities cannot be resolved or assigned", InvalidNetworkIdentities),
    ("Permission failures preserve history and recover without creating fake mappings", PermissionRecoveryPreservesUsage)
};

var failed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}{Environment.NewLine}{exception}");
    }
}

Console.WriteLine($"{tests.Length - failed}/{tests.Length} regression tests passed.");
return failed == 0 ? 0 : 1;

static void CleanInstall()
{
    using var workspace = new TestWorkspace();
    var store = new UsageStore(workspace.Root);

    Equal(workspace.CurrentPath, store.DatabasePath, "Database path");
    Require(File.Exists(workspace.CurrentPath), "The current database must be created.");
    Require(!Directory.Exists(Path.GetDirectoryName(workspace.LegacyPath)), "A clean install must not create a legacy directory.");
    Equal(2L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Schema version");
    Equal((0L, 0L), store.GetTotals(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)), "Fresh totals");

    using var connection = Open(workspace.CurrentPath);
    using var command = connection.CreateCommand();
    command.CommandText = "PRAGMA table_info(daily_usage);";
    using var reader = command.ExecuteReader();
    var columns = new List<string>();
    while (reader.Read())
        columns.Add(reader.GetString(1));
    Require(columns.SequenceEqual(new[] { "day", "downloaded", "uploaded" }), "The existing three-column usage schema must remain intact.");
}

static void LegacyUpgrade()
{
    using var workspace = new TestWorkspace();
    CreateFixture(workspace.LegacyPath);
    var original = Hash(workspace.LegacyPath);

    var store = new UsageStore(workspace.Root);

    Equal(workspace.CurrentPath, store.DatabasePath, "Migrated database path");
    AssertFixtureTotals(store);
    Equal(2L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Migrated schema version");
    Equal(0L, ReadInteger(workspace.LegacyPath, "PRAGMA user_version;"), "Legacy schema version");
    Equal(1L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM legacy_metadata WHERE value = 'keep this table';"), "Other legacy tables");
    Equal(original, Hash(workspace.LegacyPath), "Legacy database bytes");
}

static void ExistingUnversionedDatabase()
{
    using var workspace = new TestWorkspace();
    CreateFixture(workspace.CurrentPath);

    var store = new UsageStore(workspace.Root);

    AssertFixtureTotals(store);
    Equal(2L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Existing database schema version");
    Equal(1L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM legacy_metadata;"), "Existing unrelated table");
    Require(!File.Exists(workspace.LegacyPath), "Existing NetTrace startup must not create a legacy database.");
}

static void RepeatLaunch()
{
    using var workspace = new TestWorkspace();
    CreateFixture(workspace.LegacyPath);
    var sourceHash = Hash(workspace.LegacyPath);
    var first = new UsageStore(workspace.Root);
    var day = new DateOnly(2026, 1, 15);
    first.AddUsage(day, 500, 100);

    var second = new UsageStore(workspace.Root);

    Equal((123_956L, 7_990L), second.GetTotals(day, day), "Current data takes precedence over legacy data");
    Equal(sourceHash, Hash(workspace.LegacyPath), "Legacy database after repeated launch");
    Equal(0L, ReadInteger(workspace.LegacyPath, "PRAGMA user_version;"), "Legacy version after repeated launch");
}

static void PersistedTotals()
{
    using var workspace = new TestWorkspace();
    var today = new DateOnly(2026, 1, 15);
    var store = new UsageStore(workspace.Root);
    store.AddUsage(today, 1_000, 200);
    store.AddUsage(today, 500, 100);
    store.AddUsage(today.AddDays(-6), 3_000, 400);
    store.AddUsage(today.AddDays(-29), 5_000, 600);
    store.AddUsage(today.AddDays(-30), 9_000, 800);
    SqliteConnection.ClearAllPools();

    var reopened = new UsageStore(workspace.Root);

    Equal((1_500L, 300L), reopened.GetTotals(today, today), "Today totals");
    Equal((4_500L, 700L), reopened.GetTotals(today.AddDays(-6), today), "Seven-day inclusive totals");
    Equal((9_500L, 1_300L), reopened.GetTotals(today.AddDays(-29), today), "Thirty-day inclusive totals");
    Equal(2L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Version after reopening");
}

static void WalUpgrade()
{
    using var workspace = new TestWorkspace();
    CreateFixture(workspace.LegacyPath);
    // Keep the writer open so SQLite cannot checkpoint the WAL on last close.
    using var writer = Open(workspace.LegacyPath);
    Execute(writer, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
    Execute(writer, "INSERT INTO daily_usage VALUES ('2026-01-16', 987654, 321);");
    var walPath = workspace.LegacyPath + "-wal";
    Require(File.Exists(walPath) && new FileInfo(walPath).Length > 0, "The fixture must contain an uncheckpointed WAL.");
    var databaseHash = Hash(workspace.LegacyPath);
    var walHash = Hash(walPath);

    var store = new UsageStore(workspace.Root);

    Equal((987_654L, 321L), store.GetTotals(new DateOnly(2026, 1, 16), new DateOnly(2026, 1, 16)), "WAL-only history");
    AssertFixtureTotals(store);
    Equal(2L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "WAL copy schema version");
    Equal(databaseHash, Hash(workspace.LegacyPath), "Legacy main file during WAL migration");
    Equal(walHash, Hash(walPath), "Legacy WAL during migration");
    Equal(0L, ReadInteger(workspace.LegacyPath, "PRAGMA user_version;"), "Legacy WAL schema version");
}

static void InvalidLegacySchema()
{
    using var workspace = new TestWorkspace();
    CreateFixture(workspace.LegacyPath, schema: "CREATE TABLE daily_usage (day TEXT PRIMARY KEY, downloaded INTEGER NOT NULL DEFAULT 0);");
    var original = Hash(workspace.LegacyPath);

    ExpectStartupFailure(workspace.Root);

    Require(!File.Exists(workspace.CurrentPath), "An invalid legacy schema must not become the current database.");
    Equal(original, Hash(workspace.LegacyPath), "Invalid legacy database bytes");
    Equal(0L, ReadInteger(workspace.LegacyPath, "PRAGMA user_version;"), "Invalid legacy schema version");
}

static void InvalidCurrentSchema()
{
    using var workspace = new TestWorkspace();
    CreateFixture(workspace.CurrentPath, schema: "CREATE TABLE daily_usage (day TEXT PRIMARY KEY, downloaded INTEGER NOT NULL DEFAULT 0);");
    var original = Hash(workspace.CurrentPath);

    ExpectStartupFailure(workspace.Root);

    Equal(original, Hash(workspace.CurrentPath), "Invalid current database bytes");
    Equal(0L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Invalid current schema version");
}

static void FutureCurrentVersion()
{
    using var workspace = new TestWorkspace();
    CreateFixture(workspace.CurrentPath, version: 3);
    var original = Hash(workspace.CurrentPath);

    ExpectStartupFailure(workspace.Root);

    Equal(original, Hash(workspace.CurrentPath), "Future current database bytes");
    Equal(3L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Future current version");
}

static void InvalidVersionedCurrentSchema()
{
    using var workspace = new TestWorkspace();
    CreateFixture(workspace.CurrentPath, version: 1, schema: "CREATE TABLE daily_usage (day TEXT PRIMARY KEY, downloaded INTEGER NOT NULL DEFAULT 0);");
    var original = Hash(workspace.CurrentPath);

    ExpectStartupFailure(workspace.Root);

    Equal(original, Hash(workspace.CurrentPath), "Invalid version-one current database bytes");
    Equal(1L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Rejected current schema version");
}

static void FutureLegacyVersion()
{
    using var workspace = new TestWorkspace();
    CreateFixture(workspace.LegacyPath, version: 3);
    var original = Hash(workspace.LegacyPath);

    ExpectStartupFailure(workspace.Root);

    Require(!File.Exists(workspace.CurrentPath), "A future legacy schema must not be published as a usable current database.");
    Equal(original, Hash(workspace.LegacyPath), "Future legacy database bytes");
    Equal(3L, ReadInteger(workspace.LegacyPath, "PRAGMA user_version;"), "Future legacy version");
}

static void CorruptLegacy()
{
    using var workspace = new TestWorkspace();
    Directory.CreateDirectory(Path.GetDirectoryName(workspace.LegacyPath)!);
    File.WriteAllText(workspace.LegacyPath, "Deliberately invalid SQLite fixture.");
    var original = Hash(workspace.LegacyPath);

    ExpectStartupFailure(workspace.Root);

    Require(!File.Exists(workspace.CurrentPath), "A corrupt legacy file must not leave a new database that hides the failed migration.");
    Equal(original, Hash(workspace.LegacyPath), "Corrupt legacy bytes");
}

static void DestinationFailure()
{
    using var workspace = new TestWorkspace();
    CreateFixture(workspace.LegacyPath);
    var original = Hash(workspace.LegacyPath);
    var blockedDirectory = Path.GetDirectoryName(workspace.CurrentPath)!;
    File.WriteAllText(blockedDirectory, "A file obstructs creation of the destination directory.");

    ExpectStartupFailure(workspace.Root);

    Equal(original, Hash(workspace.LegacyPath), "Legacy database after destination failure");
    Require(!File.Exists(workspace.CurrentPath), "A destination failure must not report a published target.");
    Require(File.Exists(blockedDirectory), "An obstructing existing file must remain untouched.");
}

static void LockedLegacy()
{
    using var workspace = new TestWorkspace();
    CreateFixture(workspace.LegacyPath);
    var original = Hash(workspace.LegacyPath);
    Exception failure;

    using (File.Open(workspace.LegacyPath, FileMode.Open, FileAccess.Read, FileShare.None))
        failure = ExpectStartupFailure(workspace.Root);

    Require(failure.Message.Contains(workspace.LegacyPath, StringComparison.Ordinal), "The copy failure must identify its legacy source.");
    Require(failure.Message.Contains(workspace.CurrentPath, StringComparison.Ordinal), "The copy failure must identify its NetTrace destination.");
    Require(!File.Exists(workspace.CurrentPath), "A locked legacy source must not publish a current database.");
    Equal(original, Hash(workspace.LegacyPath), "Locked legacy database bytes after release");
    Equal(0L, ReadInteger(workspace.LegacyPath, "PRAGMA user_version;"), "Locked legacy schema version after release");
}

static void LegacyPathDirectory()
{
    using var workspace = new TestWorkspace();
    Directory.CreateDirectory(workspace.LegacyPath);
    var sentinel = Path.Combine(workspace.LegacyPath, "preserve.txt");
    File.WriteAllText(sentinel, "Preserve this existing directory and its contents.");
    var original = Hash(sentinel);

    var failure = ExpectStartupFailure(workspace.Root);

    Require(failure.Message.Contains(workspace.LegacyPath, StringComparison.Ordinal), "The diagnostic must identify the invalid legacy database path.");
    Require(!File.Exists(workspace.CurrentPath), "An invalid legacy path must not be treated as absent and replaced by blank current history.");
    Require(Directory.Exists(workspace.LegacyPath), "The existing legacy path directory must remain.");
    Equal(original, Hash(sentinel), "Legacy path directory contents");
}

static void VersionOneUpgrade()
{
    using var workspace = new TestWorkspace();
    CreateFixture(workspace.CurrentPath, version: 1);
    var originalSchema = ReadText(workspace.CurrentPath, "SELECT sql FROM sqlite_schema WHERE name = 'daily_usage';");
    var originalRootPage = ReadInteger(workspace.CurrentPath, "SELECT rootpage FROM sqlite_schema WHERE name = 'daily_usage';");

    var store = new UsageStore(workspace.Root);

    AssertFixtureTotals(store);
    Equal(2L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Version-one upgrade version");
    Equal(originalSchema, ReadText(workspace.CurrentPath, "SELECT sql FROM sqlite_schema WHERE name = 'daily_usage';"), "Usage table definition after upgrade");
    Equal(originalRootPage, ReadInteger(workspace.CurrentPath, "SELECT rootpage FROM sqlite_schema WHERE name = 'daily_usage';"), "Usage table remains in its original B-tree");
    Equal(3L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM daily_usage;"), "Original daily rows");
    Equal(1L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM legacy_metadata;"), "Unrelated legacy rows");
    Equal(0L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM ConnectionProfiles;"), "No profiles are invented during migration");
    Equal(0L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM NetworkMappings;"), "No mappings are invented during migration");
}

static void FailedVersionTwoMigration()
{
    using var workspace = new TestWorkspace();
    CreateFixture(workspace.CurrentPath, version: 1);
    using (var connection = Open(workspace.CurrentPath))
        Execute(connection, "CREATE TABLE NetworkMappings (sentinel TEXT); INSERT INTO NetworkMappings VALUES ('preserve existing conflicting table');");

    ExpectStartupFailure(workspace.Root);

    Equal(1L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Failed upgrade retains version one");
    Equal(0L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM sqlite_schema WHERE name = 'ConnectionProfiles';"), "Partially created profiles table must roll back");
    Equal("preserve existing conflicting table", ReadText(workspace.CurrentPath, "SELECT sentinel FROM NetworkMappings;"), "Existing conflicting schema/data must remain intact");
    Equal(126_456L, ReadInteger(workspace.CurrentPath, "SELECT sum(downloaded) FROM daily_usage;"), "Failed upgrade preserves usage totals");
    Equal(3L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM daily_usage;"), "Failed upgrade preserves usage rows");
}

static void ProfileSchema()
{
    using var workspace = new TestWorkspace();
    var store = new UsageStore(workspace.Root);
    var profiles = new ProfileStore(store);
    var profile = profiles.CreateProfile("DITO", "D2 Router");
    profiles.AssignMapping(profile.Id, "WiFi", "DITO_HOME");

    using var connection = Open(workspace.CurrentPath);
    using var command = connection.CreateCommand();
    command.CommandText = "PRAGMA foreign_key_list(NetworkMappings);";
    using (var reader = command.ExecuteReader())
    {
        Require(reader.Read(), "NetworkMappings must declare a foreign key.");
        Equal("ConnectionProfiles", reader.GetString(2), "Mapping foreign-key table");
        Equal("ProfileId", reader.GetString(3), "Mapping foreign-key column");
        Equal("Id", reader.GetString(4), "Profile key column");
        Equal("RESTRICT", reader.GetString(6).ToUpperInvariant(), "Mapped profiles cannot be cascade deleted");
    }

    Execute(connection, "PRAGMA foreign_keys=ON;");
    ExpectFailure(() => Execute(connection, "INSERT INTO NetworkMappings(ProfileId, NetworkType, NetworkIdentifier, CreatedAt) VALUES (999999, 'WiFi', 'orphan', '2026-01-15T00:00:00Z');"), "Orphan mapping insertion");
    ExpectFailure(() => Execute(connection, $"INSERT INTO NetworkMappings(ProfileId, NetworkType, NetworkIdentifier, CreatedAt) VALUES ({profile.Id}, 'WiFi', 'DITO_HOME', '2026-01-15T00:00:00Z');"), "Duplicate mapping database constraint");
    Equal(1L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM NetworkMappings;"), "Rejected inserts leave mappings unchanged");
}

static void ProfilePersistence()
{
    using var workspace = new TestWorkspace();
    var profiles = new ProfileStore(new UsageStore(workspace.Root));
    var beforeCreation = DateTimeOffset.UtcNow;
    var created = profiles.CreateProfile(" DITO ", " D2 Router ");
    Require(created.Id > 0, "Saved profiles have a persistent identifier.");
    Equal("DITO — D2 Router", created.DisplayName, "Profile display name");
    Require(created.CreatedAt >= beforeCreation && created.CreatedAt <= DateTimeOffset.UtcNow, "Profile creation time records this operation.");
    Require(created.UpdatedAt >= created.CreatedAt, "Initial update time must not precede creation time.");
    SqliteConnection.ClearAllPools();

    var reopened = new ProfileStore(new UsageStore(workspace.Root));
    var persisted = reopened.GetProfile(created.Id);

    Require(persisted is not null, "Saved profile survives reopening.");
    Equal(created, persisted, "All profile fields survive reopening");
    Equal(1, reopened.GetProfiles().Count, "Reopened profile list");
    Require(reopened.GetProfile(long.MaxValue) is null, "A nonexistent profile is not synthesized.");
}

static void MappingPersistence()
{
    using var workspace = new TestWorkspace();
    var profiles = new ProfileStore(new UsageStore(workspace.Root));
    var profile = profiles.CreateProfile("DITO", "D2 Router");
    var first = profiles.AssignMapping(profile.Id, "WiFi", "SSID A", "Router 2.4 GHz");
    profiles.AssignMapping(profile.Id, "WiFi", "SSID B", "Router 5 GHz");
    SqliteConnection.ClearAllPools();

    var reopened = new ProfileStore(new UsageStore(workspace.Root));

    Equal(profile, reopened.ResolveProfile("WiFi", "SSID A"), "First reopened SSID recognition");
    Equal(profile, reopened.ResolveProfile("WiFi", "SSID B"), "Second reopened SSID recognition");
    Equal(1, reopened.GetProfiles().Count, "Multiple SSIDs share one profile");
    Equal(2, reopened.GetMappings(profile.Id).Count, "Both mappings survive reopening");
    var persistedFirst = reopened.GetMappings(profile.Id).Single(mapping => mapping.NetworkIdentifier == "SSID A");
    Equal(first, persistedFirst, "Mapping identity, metadata and creation time survive reopening");
}

static void UnknownNetwork()
{
    using var workspace = new TestWorkspace();
    var store = new UsageStore(workspace.Root);
    var profiles = new ProfileStore(store);
    var existing = profiles.CreateProfile("DITO", "Vivo Phone");
    profiles.AssignMapping(existing.Id, "WiFi", "MyHotspot123");
    var resolver = new ProfileResolver(profiles);

    Equal(existing, resolver.Resolve(new NetworkIdentity("WiFi", "MyHotspot123", "MyHotspot123", "physical-adapter")), "Known network resolver result");
    Require(resolver.Resolve(new NetworkIdentity("WiFi", "MyHotspot987", "MyHotspot987", "physical-adapter")) is null, "Unknown SSID remains unassigned.");
    Require(resolver.Resolve(null) is null, "Disconnected state has no resolved profile.");
    Equal(1, profiles.GetProfiles().Count, "Resolving unknown networks does not create profiles");
    Equal(1, profiles.GetMappings(existing.Id).Count, "Resolving unknown networks does not create recognition rules");
    Equal(0L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM NetworkMappings WHERE NetworkIdentifier = 'MyHotspot987';"), "Unknown network is not silently mapped");
}

static void DuplicateMappings()
{
    using var workspace = new TestWorkspace();
    var profiles = new ProfileStore(new UsageStore(workspace.Root));
    var firstProfile = profiles.CreateProfile("DITO", "D2 Router");
    var secondProfile = profiles.CreateProfile("TNT", "Vivo Phone");
    var first = profiles.AssignMapping(firstProfile.Id, "WiFi", "Same SSID");
    var repeated = profiles.AssignMapping(firstProfile.Id, "WiFi", "Same SSID");

    Equal(first.Id, repeated.Id, "Idempotent mapping retains its row identity");
    Equal(1, profiles.GetMappings(firstProfile.Id).Count, "Idempotent mapping does not duplicate rows");
    ExpectFailure(() => profiles.AssignMapping(secondProfile.Id, "WiFi", "Same SSID"), "Conflicting mapping assignment");
    Equal(firstProfile, profiles.ResolveProfile("WiFi", "Same SSID"), "Conflict preserves the original profile assignment");
    Equal(0, profiles.GetMappings(secondProfile.Id).Count, "Conflict does not assign a second profile");
}

static void ProfileEdits()
{
    using var workspace = new TestWorkspace();
    var profiles = new ProfileStore(new UsageStore(workspace.Root));
    var original = profiles.CreateProfile("DITO", "D2 Router");
    var first = profiles.AssignMapping(original.Id, "WiFi", "DITO_HOME");
    var second = profiles.AssignMapping(original.Id, "WiFi", "DITO_HOME_5G");

    profiles.UpdateProfile(original.Id, " Home Fiber ", " Main Router ");
    var reopened = new ProfileStore(new UsageStore(workspace.Root));
    var edited = reopened.GetProfile(original.Id)!;

    Equal(original.Id, edited.Id, "Editing preserves the profile identity");
    Equal(original.CreatedAt, edited.CreatedAt, "Editing preserves creation time");
    Require(edited.UpdatedAt >= original.UpdatedAt, "Editing advances or preserves the update timestamp.");
    Equal("Home Fiber — Main Router", edited.DisplayName, "Edited display name");
    Equal(edited, reopened.ResolveProfile("WiFi", "DITO_HOME"), "First mapping resolves updated profile");
    Equal(edited, reopened.ResolveProfile("WiFi", "DITO_HOME_5G"), "Second mapping resolves updated profile");
    var mappings = reopened.GetMappings(original.Id);
    Require(mappings.Any(mapping => mapping == first) && mappings.Any(mapping => mapping == second), "Editing names does not rewrite network mappings.");
}

static void ProfileValidation()
{
    using var workspace = new TestWorkspace();
    var profiles = new ProfileStore(new UsageStore(workspace.Root));
    ExpectFailure(() => profiles.CreateProfile(" ", "Router"), "Empty telco name");
    ExpectFailure(() => profiles.CreateProfile("DITO", " \t"), "Empty device name");
    Equal(0, profiles.GetProfiles().Count, "Invalid profile names are not persisted");

    var profile = profiles.CreateProfile("DITO", "D2 Router");
    ExpectFailure(() => profiles.UpdateProfile(profile.Id, "", "Phone"), "Invalid profile edit");
    ExpectFailure(() => profiles.UpdateProfile(long.MaxValue, "TNT", "Phone"), "Missing profile edit");
    ExpectFailure(() => profiles.AssignMapping(long.MaxValue, "WiFi", "Orphan"), "Assignment to missing profile");
    ExpectFailure(() => profiles.AssignMapping(profile.Id, "", "SSID"), "Empty network type");
    ExpectFailure(() => profiles.AssignMapping(profile.Id, "WiFi", ""), "Empty network identifier");
    Equal(profile, profiles.GetProfile(profile.Id), "Invalid edits preserve stored profile");
    Equal(0, profiles.GetMappings(profile.Id).Count, "Invalid assignments create no mappings");

    var quoted = profiles.CreateProfile("ISP's network", "Router'); DROP TABLE daily_usage; --");
    profiles.AssignMapping(quoted.Id, "WiFi", "SSID'; DROP TABLE ConnectionProfiles; --");
    Equal(quoted, profiles.ResolveProfile("WiFi", "SSID'; DROP TABLE ConnectionProfiles; --"), "User text is stored as parameters");
    Equal(0L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM daily_usage;"), "Profile text cannot modify the usage table");
}

static void ExactNetworkIdentifiers()
{
    using var workspace = new TestWorkspace();
    var profiles = new ProfileStore(new UsageStore(workspace.Root));
    var first = profiles.CreateProfile("DITO", "D2 Router");
    var second = profiles.CreateProfile("TNT", "Vivo Phone");
    profiles.AssignMapping(first.Id, "WiFi", "Hotspot");
    profiles.AssignMapping(second.Id, "WiFi", "hotspot");
    profiles.AssignMapping(first.Id, "WiFi", " Hotspot ");
    profiles.AssignMapping(second.Id, "Ethernet", "Hotspot");

    Equal(first, profiles.ResolveProfile("WiFi", "Hotspot"), "Uppercase SSID");
    Equal(second, profiles.ResolveProfile("WiFi", "hotspot"), "Lowercase SSID is a different network identity");
    Equal(first, profiles.ResolveProfile("WiFi", " Hotspot "), "SSID whitespace is preserved");
    Require(profiles.ResolveProfile("WiFi", "Hotspot ") is null, "Unmapped whitespace variation stays unassigned.");
    Equal(second, profiles.ResolveProfile("Ethernet", "Hotspot"), "Network type is part of mapping identity");
    Equal(2, profiles.GetMappings(first.Id).Count, "Distinct same-profile SSIDs remain distinct");
}

static void AtomicProfileAssignment()
{
    using var workspace = new TestWorkspace();
    var profiles = new ProfileStore(new UsageStore(workspace.Root));
    profiles.CreateAndAssignProfile("DITO", "D2 Router", "WiFi", "Already assigned");
    var original = profiles.ResolveProfile("WiFi", "Already assigned");
    Require(original is not null, "Create-and-assign saves both the profile and its mapping.");
    Equal(1, profiles.GetProfiles().Count, "Create-and-assign profile count");

    ExpectFailure(() => profiles.CreateAndAssignProfile("TNT", "Vivo Phone", "WiFi", "Already assigned"), "Atomic conflicting create-and-assign");

    Equal(1, profiles.GetProfiles().Count, "Failed create-and-assign does not leave an orphan new profile");
    Equal(original, profiles.ResolveProfile("WiFi", "Already assigned"), "Atomic conflict preserves original mapping");
    Equal(1, profiles.GetMappings(original!.Id).Count, "Atomic conflict does not duplicate mapping rows");
}

static void NativeSsidIdentities()
{
    // Exercise native byte decoding without requiring a connected Wi-Fi adapter.
    var method = typeof(NetworkIdentityService).GetMethod("CreateWifiIdentity", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("The native SSID identity factory was not found.");
    var decode = method.CreateDelegate<WifiIdentityFactory>();
    var adapterId = "physical-wifi";
    var text = " DITO_家庭 ";
    var normal = decode(System.Text.Encoding.UTF8.GetBytes(text), adapterId);
    Equal("WiFi", normal.NetworkType, "UTF-8 SSID network type");
    Equal(text, normal.NetworkIdentifier, "UTF-8 SSID bytes preserve case, Unicode and whitespace");
    Equal(adapterId, normal.AdapterId, "Native adapter identity is retained");

    var firstInvalid = decode(new byte[] { 0xFF }, adapterId);
    var secondInvalid = decode(new byte[] { 0xFE }, adapterId);
    var literalHex = decode(System.Text.Encoding.UTF8.GetBytes("FF"), adapterId);
    var control = decode(new byte[] { 0x41, 0x00, 0x42 }, adapterId);
    Equal("WiFiBytes", firstInvalid.NetworkType, "Invalid UTF-8 uses a byte mapping type");
    Equal("FF", firstInvalid.NetworkIdentifier, "Invalid UTF-8 bytes are preserved as hex");
    Require(firstInvalid.NetworkIdentifier != secondInvalid.NetworkIdentifier, "Different invalid UTF-8 byte sequences do not collapse into one identity.");
    Equal("WiFi", literalHex.NetworkType, "Literal hex-like SSID remains a text identity");
    Require(firstInvalid.NetworkType != literalHex.NetworkType, "Byte identity cannot collide with a literal hex-like SSID.");
    Equal("WiFiBytes", control.NetworkType, "Control bytes use a display-safe byte identity");
    Equal("410042", control.NetworkIdentifier, "Embedded control bytes are not discarded");
    Equal(new string('A', 32), decode(System.Text.Encoding.UTF8.GetBytes(new string('A', 32)), adapterId).NetworkIdentifier, "Maximum-length SSID");
    ExpectFailure(() => decode(Array.Empty<byte>(), adapterId), "Empty native SSID");
    ExpectFailure(() => decode(new byte[33], adapterId), "Oversized native SSID");
}

static void RestrictedProfileDeletion()
{
    using var workspace = new TestWorkspace();
    var profiles = new ProfileStore(new UsageStore(workspace.Root));
    var profile = profiles.CreateProfile("DITO", "D2 Router");
    profiles.AssignMapping(profile.Id, "WiFi", "DITO_HOME");
    using var connection = Open(workspace.CurrentPath);
    Execute(connection, "PRAGMA foreign_keys=ON;");

    ExpectFailure(() => Execute(connection, $"DELETE FROM ConnectionProfiles WHERE Id = {profile.Id};"), "Mapped profile deletion");

    Equal(profile, profiles.ResolveProfile("WiFi", "DITO_HOME"), "Rejected deletion preserves recognition");
    Equal(1L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM ConnectionProfiles;"), "Mapped profile remains present");
    Equal(1L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM NetworkMappings;"), "Mapping remains present");
}

static void AtomicAssignmentInsertionFailure()
{
    using var workspace = new TestWorkspace();
    var store = new UsageStore(workspace.Root);
    store.AddUsage(new DateOnly(2026, 1, 15), 1_000, 100);
    var profiles = new ProfileStore(store);
    using (var connection = Open(workspace.CurrentPath))
        Execute(connection, "CREATE TRIGGER FailMappingInsert BEFORE INSERT ON NetworkMappings BEGIN SELECT RAISE(ABORT, 'Simulated mapping insertion failure'); END;");

    ExpectFailure(() => profiles.CreateAndAssignProfile("DITO", "D2 Router", "WiFi", "New hotspot"), "Mapping insertion failure after profile insertion");

    Equal(0, profiles.GetProfiles().Count, "Mapping failure rolls back the new profile");
    Equal(0L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM NetworkMappings;"), "Mapping failure creates no recognition rule");
    Equal((1_000L, 100L), store.GetTotals(new DateOnly(2026, 1, 15), new DateOnly(2026, 1, 15)), "Mapping transaction failure preserves usage history");
}

static void InvalidVersionTwoConstraints()
{
    using var workspace = new TestWorkspace();
    var store = new UsageStore(workspace.Root);
    store.AddUsage(new DateOnly(2026, 1, 15), 1_000, 100);
    var profiles = new ProfileStore(store);
    var profile = profiles.CreateProfile("DITO", "D2 Router");
    profiles.AssignMapping(profile.Id, "WiFi", "DITO_HOME");
    using (var connection = Open(workspace.CurrentPath))
        Execute(connection, "DROP INDEX IX_NetworkMappings_ProfileId;");

    ExpectStartupFailure(workspace.Root);

    Equal(2L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Invalid version-two schema version remains unchanged");
    Equal(1L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM ConnectionProfiles;"), "Invalid version-two schema preserves profiles");
    Equal(1L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM NetworkMappings;"), "Invalid version-two schema preserves mappings");
    Equal(1_000L, ReadInteger(workspace.CurrentPath, "SELECT sum(downloaded) FROM daily_usage;"), "Invalid version-two schema preserves usage");
    Equal(0L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM sqlite_schema WHERE name='IX_NetworkMappings_ProfileId';"), "Invalid schema is rejected without unsolicited repair");
}

static void ConnectionTransitions()
{
    using var workspace = new TestWorkspace();
    var profiles = new ProfileStore(new UsageStore(workspace.Root));
    var first = profiles.CreateProfile("DITO", "D2 Router");
    var second = profiles.CreateProfile("Home Fiber", "Main Router");
    profiles.AssignMapping(first.Id, "WiFi", "DITO_HOME");
    profiles.AssignMapping(second.Id, "Ethernet", "physical-ethernet");
    var wifi = new NetworkIdentity("WiFi", "DITO_HOME", "DITO_HOME", "physical-wifi");
    var unknown = new NetworkIdentity("WiFi", "Phone hotspot", "Phone hotspot", "physical-wifi");
    var ethernet = new NetworkIdentity("Ethernet", "physical-ethernet", "Ethernet", "physical-ethernet");
    var identity = new FakeNetworkIdentityService { Network = wifi };
    using var monitor = new ConnectionProfileMonitor(identity, new ProfileResolver(profiles));
    monitor.Start();
    WaitUntil(() => monitor.Current.Profile == first, "Initial known Wi-Fi recognition");
    Equal(wifi, monitor.Current.Network, "Initial physical identity");
    Equal(ConnectionStatus.ConnectedRecognized, monitor.Current.Status, "Known Wi-Fi connection state");
    Require(!monitor.Current.CanAssign, "A recognized connection does not offer assignment.");

    identity.Network = unknown;
    identity.RaiseNetworkChanged();
    WaitUntil(() => monitor.Current.Network == unknown, "Changed unknown SSID recognition");
    Require(monitor.Current.Profile is null, "An unknown physical connection remains unassigned.");
    Equal(ConnectionStatus.ConnectedUnassigned, monitor.Current.Status, "Unknown valid Wi-Fi state");
    Require(monitor.Current.CanAssign, "An unknown valid Wi-Fi identity can be assigned.");
    Equal(2, profiles.GetProfiles().Count, "Unknown-network transition does not add profiles");

    identity.Network = null;
    identity.RaiseNetworkChanged();
    WaitUntil(() => monitor.Current.Network is null && monitor.Current.Profile is null, "Disconnected recognition");
    Equal(ConnectionStatus.Disconnected, monitor.Current.Status, "No connected physical network state");
    Require(!monitor.Current.CanAssign, "A disconnected connection cannot be assigned.");

    identity.Network = ethernet;
    identity.RaiseNetworkChanged();
    WaitUntil(() => monitor.Current.Profile == second, "Ethernet profile recognition after reconnecting");
    Equal(ethernet, monitor.Current.Network, "Ethernet physical identity");

    identity.Network = wifi;
    identity.RaiseNetworkChanged();
    WaitUntil(() => monitor.Current.Profile == first, "Returning to the original Wi-Fi profile");
}

static void RepeatedNetworkEvents()
{
    using var workspace = new TestWorkspace();
    var profiles = new ProfileStore(new UsageStore(workspace.Root));
    var unknown = new NetworkIdentity("WiFi", "Unassigned hotspot", "Unassigned hotspot", "physical-wifi");
    var identity = new FakeNetworkIdentityService { Network = unknown };
    using var monitor = new ConnectionProfileMonitor(identity, new ProfileResolver(profiles));
    var notifications = 0;
    monitor.StateChanged += (_, _) => Interlocked.Increment(ref notifications);
    monitor.Start();
    WaitUntil(() => monitor.Current.Network == unknown && Volatile.Read(ref notifications) > 0, "Initial unknown-network presentation");
    var initialNotifications = Volatile.Read(ref notifications);
    var initialDetections = identity.Detections;

    for (var i = 0; i < 8; i++)
        identity.RaiseNetworkChanged();
    WaitUntil(() => identity.Detections > initialDetections, "Debounced repeated network detection");
    Thread.Sleep(300);

    Equal(initialNotifications, Volatile.Read(ref notifications), "Repeated identical network events do not repeat UI notifications");
    Equal(0, profiles.GetProfiles().Count, "Repeated unknown networks do not create profiles");
    Require(monitor.Current.Profile is null, "Repeated unknown network remains unassigned.");
}

static void ConnectionProfileRefresh()
{
    using var workspace = new TestWorkspace();
    var profiles = new ProfileStore(new UsageStore(workspace.Root));
    var identity = new FakeNetworkIdentityService
    {
        Network = new NetworkIdentity("WiFi", "Renamed hotspot", "Renamed hotspot", "physical-wifi")
    };
    using var monitor = new ConnectionProfileMonitor(identity, new ProfileResolver(profiles));
    monitor.Start();
    WaitUntil(() => monitor.Current.Network == identity.Network, "Initial unassigned network");
    Require(monitor.Current.Profile is null, "Initial network is unassigned.");

    var created = profiles.CreateProfile("DITO", "Vivo Phone");
    profiles.AssignMapping(created.Id, "WiFi", "Renamed hotspot");
    monitor.Refresh();
    WaitUntil(() => monitor.Current.Profile == created, "Assignment recognized without reconnecting");

    profiles.UpdateProfile(created.Id, "TNT", "Vivo Phone");
    monitor.Refresh();
    WaitUntil(() => monitor.Current.Profile?.DisplayName == "TNT — Vivo Phone", "Edited profile recognized without reconnecting");
    Equal(created.Id, monitor.Current.Profile!.Id, "Refresh preserves the profile identity");
}

static void ConnectionFailureAndDisposal()
{
    using var workspace = new TestWorkspace();
    var store = new UsageStore(workspace.Root);
    var profiles = new ProfileStore(store);
    var identity = new FakeNetworkIdentityService
    {
        Network = new NetworkIdentity("WiFi", "Hotspot", "Hotspot", "physical-wifi"),
        Failure = new InvalidOperationException("Simulated Windows WLAN access failure")
    };
    using var monitor = new ConnectionProfileMonitor(identity, new ProfileResolver(profiles));
    var notifications = 0;
    monitor.StateChanged += (_, _) => Interlocked.Increment(ref notifications);
    monitor.Start();
    WaitUntil(() => monitor.Current.Message?.Contains("Simulated Windows WLAN access failure", StringComparison.Ordinal) == true && Volatile.Read(ref notifications) > 0, "Detection failure diagnostic");
    Require(monitor.Current.Network is null && monitor.Current.Profile is null, "Detection failure does not invent a physical profile.");
    Equal(ConnectionStatus.ConnectedIdentityUnavailable, monitor.Current.Status, "API exception identity state");
    Require(!monitor.Current.CanAssign, "An API failure cannot offer assignment.");
    store.AddUsage(new DateOnly(2026, 1, 15), 4_096, 512);
    Equal((4_096L, 512L), store.GetTotals(new DateOnly(2026, 1, 15), new DateOnly(2026, 1, 15)), "Usage persistence remains independent of connection detection failure");

    identity.Failure = null;
    identity.Network = null;
    identity.Availability = NetworkIdentityAvailability.Unavailable;
    identity.DetectionMessage = "Wi-Fi SSID unavailable. Check Windows location permissions.";
    identity.RaiseNetworkChanged();
    WaitUntil(() => monitor.Current.Message == identity.DetectionMessage, "Native unavailability diagnostic without an exception");
    Require(monitor.Current.Network is null && monitor.Current.Profile is null, "An unavailable SSID does not expose a recognition identity.");
    Equal(ConnectionStatus.ConnectedIdentityUnavailable, monitor.Current.Status, "Native SSID unavailability state");
    Require(!monitor.Current.CanAssign, "An unavailable SSID does not offer assignment.");

    identity.Network = new NetworkIdentity("WiFi", "Recovered hotspot", "Recovered hotspot", "physical-wifi");
    identity.Availability = null;
    identity.DetectionMessage = null;
    identity.RaiseNetworkChanged();
    WaitUntil(() => monitor.Current.Network == identity.Network && monitor.Current.Message is null && Volatile.Read(ref notifications) >= 3, "Recovery after native detection becomes available");

    monitor.Dispose();
    Require(identity.Disposed, "Disposing connection monitoring disposes its native identity source.");
    var finalState = monitor.Current;
    var finalNotifications = Volatile.Read(ref notifications);
    identity.Network = null;
    identity.RaiseNetworkChanged();
    Thread.Sleep(300);
    Equal(finalState, monitor.Current, "Disposed connection monitoring ignores later network events");
    Equal(finalNotifications, Volatile.Read(ref notifications), "Disposed connection monitoring emits no later state notifications");
    monitor.Dispose();
}

static void ConnectionIdentityStates()
{
    using var workspace = new TestWorkspace();
    var profiles = new ProfileStore(new UsageStore(workspace.Root));
    var profile = profiles.CreateAndAssignProfile("DITO", "D2 Router", "WiFi", "DITO_HOME");
    var known = new NetworkIdentity("WiFi", "DITO_HOME", "DITO_HOME", "physical-wifi");
    var unknown = new NetworkIdentity("WiFi", "Renamed hotspot", "Renamed hotspot", "physical-wifi");
    var identity = new FakeNetworkIdentityService { Network = known };
    using var monitor = new ConnectionProfileMonitor(identity, new ProfileResolver(profiles));
    Equal(ConnectionStatus.Detecting, monitor.Current.Status, "Initial detection state");
    Require(!monitor.Current.CanAssign, "Detection in progress cannot offer assignment.");
    monitor.Start();
    WaitUntil(() => monitor.Current.Status == ConnectionStatus.ConnectedRecognized, "Recognized connection state");
    Equal(profile, monitor.Current.Profile, "Recognized profile");
    Require(!monitor.Current.CanAssign, "Recognized identity does not need assignment.");

    identity.Network = unknown;
    identity.RaiseNetworkChanged();
    WaitUntil(() => monitor.Current.Status == ConnectionStatus.ConnectedUnassigned, "Valid unassigned connection state");
    Equal(unknown, monitor.Current.Network, "Unassigned connection retains its actual identity");
    Require(monitor.Current.Profile is null && monitor.Current.CanAssign, "Only a valid unassigned identity offers assignment.");

    // Even if an identity source retains an old SSID, an unavailable result cannot use it.
    identity.Network = known;
    identity.Availability = NetworkIdentityAvailability.Unavailable;
    identity.DetectionMessage = "Windows could not provide the connected Wi-Fi identity.";
    identity.RaiseNetworkChanged();
    WaitUntil(() => monitor.Current.Status == ConnectionStatus.ConnectedIdentityUnavailable, "Connected identity unavailable state");
    Require(monitor.Current.Network is null && monitor.Current.Profile is null && !monitor.Current.CanAssign,
        "Unavailable identity cannot reuse a known mapping or offer assignment.");
    Equal(identity.DetectionMessage, monitor.Current.Message, "Identity availability diagnostic");

    identity.Availability = NetworkIdentityAvailability.Disconnected;
    identity.DetectionMessage = "No active physical connection.";
    identity.RaiseNetworkChanged();
    WaitUntil(() => monitor.Current.Status == ConnectionStatus.Disconnected, "Disconnected state differs from unavailable identity");
    Require(monitor.Current.Network is null && monitor.Current.Profile is null && !monitor.Current.CanAssign,
        "A disconnected result cannot reuse a stale network identity.");
    Equal(1, profiles.GetProfiles().Count, "State changes do not invent profiles");
    Equal(1, profiles.GetMappings(profile.Id).Count, "State changes do not invent mappings");
}

static void UnavailableIdentityWithoutLookup()
{
    using var workspace = new TestWorkspace();
    var missingDatabase = Path.Combine(workspace.Root, "must-not-open.db");
    var identity = new FakeNetworkIdentityService
    {
        Network = new NetworkIdentity("WiFi", "Stale known SSID", "Stale known SSID", "physical-wifi"),
        Availability = NetworkIdentityAvailability.Unavailable,
        DetectionMessage = "Windows denied access to the connected SSID. Check location permission."
    };
    // A lookup against this store would fail because no database exists.
    using var monitor = new ConnectionProfileMonitor(identity, new ProfileResolver(new ProfileStore(missingDatabase)));
    monitor.Start();
    WaitUntil(() => monitor.Current.Status == ConnectionStatus.ConnectedIdentityUnavailable, "Unavailable identity without database lookup");
    Equal(identity.DetectionMessage, monitor.Current.Message, "Permission diagnostic survives without a database lookup failure");
    Require(!monitor.Current.CanAssign && monitor.Current.Network is null && monitor.Current.Profile is null,
        "Unavailable identity is not a valid assignment target.");

    identity.Availability = NetworkIdentityAvailability.Disconnected;
    identity.DetectionMessage = "No active physical connection.";
    identity.RaiseNetworkChanged();
    WaitUntil(() => monitor.Current.Status == ConnectionStatus.Disconnected, "Disconnected identity without database lookup");
    Equal(identity.DetectionMessage, monitor.Current.Message, "Disconnected diagnostic survives without lookup");
    Require(!File.Exists(missingDatabase), "Unavailable and disconnected states cannot create a recognition database.");
}

static void InvalidNetworkIdentities()
{
    using var workspace = new TestWorkspace();
    var missingDatabase = Path.Combine(workspace.Root, "invalid-identities-must-not-open.db");
    var resolver = new ProfileResolver(new ProfileStore(missingDatabase));
    var invalid = new[]
    {
        new NetworkIdentity("WiFi", "", "Missing SSID", "physical-wifi"),
        new NetworkIdentity("", "SSID", "SSID", "physical-wifi"),
        new NetworkIdentity(" \t", "SSID", "SSID", "physical-wifi"),
        new NetworkIdentity("WiFi", null!, "Missing SSID", "physical-wifi")
    };
    var identity = new FakeNetworkIdentityService { Availability = NetworkIdentityAvailability.Available };
    using var monitor = new ConnectionProfileMonitor(identity, resolver);
    monitor.Start();
    WaitUntil(() => monitor.Current.Status == ConnectionStatus.ConnectedIdentityUnavailable, "Available result missing an identity");

    foreach (var network in invalid)
    {
        Require(!network.HasValidIdentifier, "Malformed network identity must fail validation.");
        Require(resolver.Resolve(network) is null, "Malformed identity is rejected before consulting the database.");
        var fabricated = new CurrentConnectionState(ConnectionStatus.ConnectedUnassigned, network, null, null);
        Require(!fabricated.CanAssign, "A malformed identity cannot become assignable through its status alone.");
        identity.Network = network;
        var previousDetections = identity.Detections;
        identity.RaiseNetworkChanged();
        WaitUntil(() => identity.Detections > previousDetections, "Malformed network identity detection");
        Thread.Sleep(50);
        Equal(ConnectionStatus.ConnectedIdentityUnavailable, monitor.Current.Status, "Malformed identity state");
        Require(monitor.Current.Network is null && monitor.Current.Profile is null && !monitor.Current.CanAssign,
            "Malformed identity exposes no mapping or assignment target.");
        Require(!string.IsNullOrWhiteSpace(monitor.Current.Message), "Malformed identity provides a useful diagnostic.");
    }

    identity.Availability = (NetworkIdentityAvailability)int.MaxValue;
    identity.Network = new NetworkIdentity("WiFi", "Valid but unsupported result", "SSID", "physical-wifi");
    identity.DetectionMessage = "Unsupported network availability result.";
    identity.RaiseNetworkChanged();
    WaitUntil(() => monitor.Current.Message == identity.DetectionMessage, "Unsupported availability result");
    Require(monitor.Current.Network is null && !monitor.Current.CanAssign, "An unsupported result cannot offer assignment.");
    Require(!File.Exists(missingDatabase), "Malformed identities cannot create a recognition database.");

    var profiles = new ProfileStore(new UsageStore(workspace.Root));
    var whitespace = new NetworkIdentity("WiFi", "   ", "   ", "physical-wifi");
    var profile = profiles.CreateAndAssignProfile("DITO", "D2 Router", whitespace.NetworkType, whitespace.NetworkIdentifier);
    Require(whitespace.HasValidIdentifier, "A nonempty whitespace SSID remains a real exact identity.");
    Equal(profile, new ProfileResolver(profiles).Resolve(whitespace), "Whitespace-only SSID mapping resolves without normalization");
}

static void PermissionRecoveryPreservesUsage()
{
    using var workspace = new TestWorkspace();
    var day = new DateOnly(2026, 1, 15);
    var store = new UsageStore(workspace.Root);
    store.AddUsage(day, 1_000, 100);
    var profiles = new ProfileStore(store);
    var profile = profiles.CreateAndAssignProfile("DITO", "Vivo Phone", "WiFi", "Original hotspot");
    var identity = new FakeNetworkIdentityService
    {
        Availability = NetworkIdentityAvailability.Unavailable,
        DetectionMessage = "Wi-Fi is connected, but Windows denied access to its SSID. Check Windows location permission."
    };
    using var monitor = new ConnectionProfileMonitor(identity, new ProfileResolver(profiles));
    var notifications = 0;
    monitor.StateChanged += (_, _) => Interlocked.Increment(ref notifications);
    monitor.Start();
    WaitUntil(() => monitor.Current.Status == ConnectionStatus.ConnectedIdentityUnavailable && Volatile.Read(ref notifications) > 0,
        "Permission failure presented separately from an unassigned network");
    Require(!monitor.Current.CanAssign, "Permission failure cannot start profile assignment.");
    var initialNotifications = Volatile.Read(ref notifications);
    var initialDetections = identity.Detections;
    for (var i = 0; i < 8; i++) identity.RaiseNetworkChanged();
    WaitUntil(() => identity.Detections > initialDetections, "Repeated permission-failure event detection");
    Thread.Sleep(300);
    Equal(initialNotifications, Volatile.Read(ref notifications), "Repeated permission failures do not repeatedly notify the UI");

    store.AddUsage(day, 4_096, 512);
    var reopened = new UsageStore(workspace.Root);
    Equal((5_096L, 612L), reopened.GetTotals(day, day), "Usage continues to persist during identity unavailability");
    Equal(1, profiles.GetProfiles().Count, "Permission failure creates no profile");
    Equal(1, profiles.GetMappings(profile.Id).Count, "Permission failure creates no fake or empty mapping");

    identity.Network = new NetworkIdentity("WiFi", "Renamed hotspot", "Renamed hotspot", "physical-wifi");
    identity.Availability = NetworkIdentityAvailability.Available;
    identity.DetectionMessage = null;
    identity.RaiseNetworkChanged();
    WaitUntil(() => monitor.Current.Status == ConnectionStatus.ConnectedUnassigned, "Valid unknown identity after permission recovery");
    Require(monitor.Current.CanAssign && monitor.Current.Profile is null, "Assignment becomes available only after a real identity is recovered.");
    profiles.AssignMapping(profile.Id, "WiFi", "Renamed hotspot");
    monitor.Refresh();
    WaitUntil(() => monitor.Current.Status == ConnectionStatus.ConnectedRecognized, "Recovered identity resolves its assigned profile");
    Equal(profile, monitor.Current.Profile, "Recovery reuses the existing profile");
    Equal(1, profiles.GetProfiles().Count, "Recovery does not duplicate the profile");
    Equal(2, profiles.GetMappings(profile.Id).Count, "Only the original and user-assigned identities are persisted");
    Equal((5_096L, 612L), new UsageStore(workspace.Root).GetTotals(day, day), "Recognition and recovery preserve recorded totals after reopening");
}

static void WaitUntil(Func<bool> condition, string description)
{
    var timer = System.Diagnostics.Stopwatch.StartNew();
    while (!condition())
    {
        if (timer.Elapsed > TimeSpan.FromSeconds(5))
            throw new InvalidOperationException($"Timed out waiting for {description}.");
        Thread.Sleep(10);
    }
}

static void NonBlockingCurrentState()
{
    using var workspace = new TestWorkspace();
    using var detectionStarted = new ManualResetEventSlim();
    using var releaseDetection = new ManualResetEventSlim();
    var identity = new FakeNetworkIdentityService
    {
        Network = new NetworkIdentity("WiFi", "Slow hotspot", "Slow hotspot", "physical-wifi"),
        DetectionStarted = detectionStarted,
        ReleaseDetection = releaseDetection
    };
    var profiles = new ProfileStore(new UsageStore(workspace.Root));
    using var monitor = new ConnectionProfileMonitor(identity, new ProfileResolver(profiles));
    monitor.Start();
    var quicklyReadable = false;
    Task<CurrentConnectionState>? read = null;
    try
    {
        Require(detectionStarted.Wait(TimeSpan.FromSeconds(5)), "The fake native query must start.");
        read = Task.Run(() => monitor.Current);
        quicklyReadable = read.Wait(TimeSpan.FromMilliseconds(500));
    }
    finally
    {
        // Always release the worker before monitor/event disposal, including assertion failures.
        releaseDetection.Set();
    }

    if (read is not null)
        Require(read.Wait(TimeSpan.FromSeconds(5)), "The state read must complete after releasing native detection.");
    Require(quicklyReadable, "Reading the current connection must not wait for a slow native network query.");
    WaitUntil(() => monitor.Current.Network == identity.Network, "Slow native query completes after release");
}

static void CreateFixture(string path, int version = 0, string? schema = null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var connection = Open(path);
    Execute(connection, schema ?? """
        CREATE TABLE daily_usage (
            day TEXT PRIMARY KEY,
            downloaded INTEGER NOT NULL DEFAULT 0,
            uploaded INTEGER NOT NULL DEFAULT 0
        );
        INSERT INTO daily_usage VALUES ('2026-01-15', 123456, 7890);
        INSERT INTO daily_usage VALUES ('2026-01-10', 1000, 100);
        INSERT INTO daily_usage VALUES ('2025-12-20', 2000, 200);
        CREATE TABLE legacy_metadata (value TEXT NOT NULL);
        INSERT INTO legacy_metadata VALUES ('keep this table');
        """);
    Execute(connection, $"PRAGMA user_version={version};");
}

static void AssertFixtureTotals(UsageStore store)
{
    Equal((123_456L, 7_890L), store.GetTotals(new DateOnly(2026, 1, 15), new DateOnly(2026, 1, 15)), "Migrated daily totals");
    Equal((124_456L, 7_990L), store.GetTotals(new DateOnly(2026, 1, 9), new DateOnly(2026, 1, 15)), "Migrated seven-day totals");
    Equal((126_456L, 8_190L), store.GetTotals(new DateOnly(2025, 12, 17), new DateOnly(2026, 1, 15)), "Migrated thirty-day totals");
}

static Exception ExpectStartupFailure(string root)
{
    try
    {
        _ = new UsageStore(root);
    }
    catch (Exception exception)
    {
        Require(!string.IsNullOrWhiteSpace(exception.Message), "Startup failures must provide a diagnostic message.");
        return exception;
    }

    throw new InvalidOperationException("Startup unexpectedly accepted an unusable database or destination.");
}

static Exception ExpectFailure(Action operation, string description)
{
    try
    {
        operation();
    }
    catch (Exception exception)
    {
        Require(!string.IsNullOrWhiteSpace(exception.Message), $"{description} must provide a diagnostic.");
        return exception;
    }

    throw new InvalidOperationException($"{description}: the invalid operation unexpectedly succeeded.");
}

static SqliteConnection Open(string path)
{
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Pooling = false,
        DefaultTimeout = 1
    }.ToString());
    connection.Open();
    return connection;
}

static long ReadInteger(string path, string sql)
{
    using var connection = Open(path);
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    return Convert.ToInt64(command.ExecuteScalar());
}

static string ReadText(string path, string sql)
{
    using var connection = Open(path);
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    return command.ExecuteScalar()?.ToString() ?? throw new InvalidOperationException("Expected a text result.");
}

static void Execute(SqliteConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.ExecuteNonQuery();
}

static string Hash(string path)
{
    using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    return Convert.ToHexString(SHA256.HashData(stream));
}

static void Equal<T>(T expected, T actual, string description)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{description}: expected {expected}, got {actual}.");
}

static void Require(bool condition, string description)
{
    if (!condition)
        throw new InvalidOperationException(description);
}

sealed class TestWorkspace : IDisposable
{
    private readonly string _parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NetTrace.RegressionTests"));
    public string Root { get; }
    public string CurrentPath => Path.Combine(Root, "NetTrace", "usage.db");
    public string LegacyPath => Path.Combine(Root, "DataUsageMonitor", "usage.db");

    public TestWorkspace()
    {
        Root = Path.Combine(_parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        var resolved = Path.GetFullPath(Root);
        if (!resolved.StartsWith(_parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to remove a test directory outside the temporary test parent.");
        Directory.Delete(resolved, recursive: true);
    }
}

sealed class FakeNetworkIdentityService : INetworkIdentityService
{
    private NetworkIdentity? _network;
    private Exception? _failure;
    private int _detections;

    public NetworkIdentity? Network
    {
        get => Volatile.Read(ref _network);
        set => Volatile.Write(ref _network, value);
    }

    public Exception? Failure
    {
        get => Volatile.Read(ref _failure);
        set => Volatile.Write(ref _failure, value);
    }

    public int Detections => Volatile.Read(ref _detections);
    public bool Disposed { get; private set; }
    public string? DetectionMessage { get; set; }
    public NetworkIdentityAvailability? Availability { get; set; }
    public ManualResetEventSlim? DetectionStarted { get; set; }
    public ManualResetEventSlim? ReleaseDetection { get; set; }
    public event EventHandler? NetworkChanged;

    public NetworkIdentityResult GetCurrentNetwork()
    {
        Interlocked.Increment(ref _detections);
        DetectionStarted?.Set();
        ReleaseDetection?.Wait();
        if (Failure is { } failure)
            throw failure;
        var network = Network;
        return new(Availability ?? (network is null ? NetworkIdentityAvailability.Disconnected : NetworkIdentityAvailability.Available),
            network, DetectionMessage);
    }

    public void RaiseNetworkChanged() => NetworkChanged?.Invoke(this, EventArgs.Empty);
    public void Dispose() => Disposed = true;
}

delegate NetworkIdentity WifiIdentityFactory(ReadOnlySpan<byte> ssidBytes, string adapterId);
