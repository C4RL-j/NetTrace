using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using NetTrace.Models;
using NetTrace.Services;
using NetTrace.Utils;

if (args.Length > 0 && args[0] == "--lease-test-child")
    return RunLeaseChild(args);

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
    ("Permission failures preserve history and recover without creating fake mappings", PermissionRecoveryPreservesUsage),
    ("Version-two migration preserves usage, profile and mapping rows and B-trees", VersionTwoUpgrade),
    ("Legacy version-two migration preserves its original database unchanged", LegacyVersionTwoUpgrade),
    ("Failed version-three migration rolls back all new schema changes", FailedVersionThreeMigration),
    ("One valid connection creates one session and repeated observations are idempotent", SessionConnectionDeduplication),
    ("Physical SSID changes split sessions even when the profile and adapter are unchanged", SessionNetworkTransitions),
    ("Disconnect and reconnect close and reopen sessions with meaningful events", SessionDisconnectReconnect),
    ("Sleep and resume split the same physical network into new sessions", SessionSleepResume),
    ("Unexpected exit recovery ends at the last confirmed observation", SessionUnexpectedExitRecovery),
    ("Active unassigned session assignment leaves completed historical sessions unchanged", SessionActiveProfileAssignment),
    ("Valid sampler deltas accumulate once and batched persistence is retry safe", SessionUsageAccumulation),
    ("Wrong adapter, stale session and invalid samples cannot bleed into a new session", SessionInvalidSamples),
    ("Identity retrieval failure closes confirmed history without fabricating a new connection", SessionIdentityUnavailable),
    ("Graceful exit flushes usage and closes the session at UTC exit time", SessionGracefulExit),
    ("Profile renaming preserves historical identity and session foreign keys", SessionProfileRename),
    ("Recent, profile and UTC-overlap history queries return deterministic results", SessionHistoryQueries),
    ("SQLite constraints enforce one active session and protected historical profile references", SessionSchemaConstraints),
    ("Persisted future event names do not require a schema change", ExtensibleEventHistory),
    ("Counter resets and baseline changes cannot manufacture bandwidth", BandwidthDeltaSafety),
    ("Captured network observations preserve rapid A to B to A history", CapturedConnectionObservations),
    ("Explicit freshness refresh discards a blocked pre-resume native query", InvalidatedNativeObservation),
    ("Failed physical transition rolls back history and pauses byte attribution", FailedSessionTransition),
    ("Repeated and out-of-order sample timestamps cannot double count session usage", SessionSampleTimestamps),
    ("Wall-clock rollback cannot create negative session durations", SessionClockRollback),
    ("A missing single-active-session constraint stops startup without changing history", InvalidVersionThreeConstraints),
    ("A database run lease prevents another instance from recovering a live session", DatabaseLeaseProtection),
    ("Failed sleep persistence retries the original boundary without spanning suspension", FailedSleepPersistence),
    ("Zero-byte idle samples persist liveness without inventing usage", IdleSessionConfirmation),
    ("Same-thread duplicate database leases are rejected and repeated disposal is safe", SameThreadDatabaseLease),
    ("First startup acquires ownership before creating or opening SQLite", DatabaseLeaseBeforeInitialization),
    ("Independent processes cannot recover a live session and release permits the next startup", CrossProcessDatabaseLease),
    ("Hard-killed lease owners release OS ownership and stale sessions recover at confirmation", CrashedProcessDatabaseLease),
    ("A stale lock file cannot permanently prevent future startup", StaleDatabaseLeaseFile),
    ("Lease release is thread independent and disposal is idempotent", CrossThreadDatabaseLeaseDisposal),
    ("Wrong-path and disposed leases cannot authorize database initialization", DatabaseLeaseOwnershipValidation),
    ("Failed database initialization releases its scoped ownership for retry", FailedInitializationReleasesLease),
    ("History query and foreign-key paths have useful SQLite indices", HistoryQueryIndices),
    ("Existing version-three databases gain event relationship indices without rewriting history", ExistingVersionThreeIndices),
    ("Malformed history index definitions fail without publishing schema repairs", MalformedHistoryIndices),
    ("An empty native interface inventory reports no VPN", VpnNoAdapters),
    ("Dormant and disconnected VPN adapters are not active tunnels", VpnDormantAdapter),
    ("Connected native tunnel candidates are detected conservatively", VpnActiveTunnel),
    ("Hyper-V, VMware, VirtualBox and loopback interfaces are not VPNs", VpnOrdinaryVirtualAdapters),
    ("Exact native VPN driver identities recognize renamed active adapters", VpnDriverCandidates),
    ("Friendly VPN names and descriptions alone do not prove a tunnel", VpnNameOnlyHints),
    ("Hardware interfaces cannot become VPN candidates from driver or name hints", VpnHardwareNotCandidate),
    ("IPv6 transition mechanisms do not masquerade as VPN tunnels", VpnTransitionTunnels),
    ("An opaque active tunnel is indeterminate rather than guessed", VpnOpaqueTunnel),
    ("A connected native RAS VPN is recognized without invented interface identifiers", VpnRasConnected),
    ("Dormant native RAS VPN connections are not connected tunnels", VpnRasDormant),
    ("PPP and dial-up connections are not assumed to be VPNs", VpnPppNotAssumed),
    ("Incomplete native inventory reports uncertainty instead of a false no-VPN result", VpnIncompleteInventory),
    ("Native API failures produce a safe indeterminate state", VpnApiFailure),
    ("Each VPN state query reads current native connection status", VpnStateRefresh),
    ("Unknown operational or media status cannot prove an active tunnel", VpnUnconfirmedStatus),
    ("TAP always-connected media configuration cannot prove an active VPN", VpnTapAlwaysConnected),
    ("RAS dormant, reconnecting and unknown substates cannot prove a live VPN", VpnRasConnectionSubstates),
    ("Detected tunnels without route evidence remain unverified", VpnRoutesMissing),
    ("Effective VPN defaults verify full routing independently for IPv4 and IPv6", VpnRoutesFullDefaults),
    ("Specific VPN prefixes with a physical default verify split routing", VpnRoutesSplitPrefixes),
    ("Route plus interface metrics determine the effective default", VpnRoutesCombinedMetrics),
    ("Competing paired half-defaults use longest-prefix matching", VpnRoutesHalfDefaults),
    ("IPv4 full routing and IPv6 split routing retain independent results", VpnRoutesIndependentFamilies),
    ("Metric ties affecting VPN routes stay unverified", VpnRoutesMetricTies),
    ("Repeated and reordered route observations produce deterministic evidence", VpnRoutesDeterminism),
    ("Incomplete and failed native route reads preserve uncertainty", VpnRoutesIncomplete),
    ("Down, expired, loopback and disabled default routes cannot prove a tunnel", VpnRoutesUnusable),
    ("Missing route metadata or mismatched interface GUIDs cannot prove ownership", VpnRoutesOwnership),
    ("RAS-only VPN detection cannot invent a route interface relationship", VpnRoutesRasWithoutInterface),
    ("Interface and route metric addition cannot overflow into a winning route", VpnRoutesMetricOverflow),
    ("More-specific public bypasses are uncertainty rather than a guessed full tunnel", VpnRoutesPublicBypass),
    ("Private LAN and local routes do not defeat verified general VPN routing", VpnRoutesLocalExceptions),
    ("A partial general-route pair cannot manufacture full tunnel coverage", VpnRoutesPartialCoverage),
    ("Malformed or wrong-family route evidence is rejected conservatively", VpnRoutesMalformed),
    ("Verification of non-detected or uncertain VPN state makes no routing claims", VpnRoutesNoDetectedTunnel),
    ("Fresh native route reads replace previous verification without stale results", VpnRoutesRefresh),
    ("Verified IPv4 full routing remains full when IPv6 is provably unavailable", VpnFamilyIpv4OnlyFull),
    ("Verified IPv4 split routing remains split when IPv6 is provably unavailable", VpnFamilyIpv4OnlySplit),
    ("Usable unverified IPv6 prevents an aggregate full VPN claim", VpnFamilyUsableWithoutVpnPaths),
    ("Verified IPv6 full routing remains full when IPv4 is provably unavailable", VpnFamilyIpv6OnlyFull),
    ("No usable IP family cannot manufacture a full or split tunnel", VpnFamilyNeitherUsable),
    ("Unknown native family observations cannot be ignored during aggregation", VpnFamilyUnknownNotIgnored),
    ("Completed loopback and inactive family snapshots prove unavailability", VpnFamilyUnavailableEvidence),
    ("Missing or contradictory family metadata cannot claim unavailability", VpnFamilyMetadataSafety),
    ("Version-three migration preserves usage, profiles, mappings and physical history", VpnVersionThreeUpgrade),
    ("Fresh version-four schema stores protected VPN intervals without byte columns", VpnFreshSchema),
    ("A failed version-four migration rolls back schema and retains prior data", VpnFailedMigration),
    ("Legacy version-three copy migrates without changing its rollback source", VpnLegacyVersionThreeUpgrade),
    ("VPN off to on opens one child interval without changing its physical session", VpnSessionStarts),
    ("Repeated VPN observations confirm the same interval after reopening", VpnSessionRepeated),
    ("VPN on to off ends only the VPN interval", VpnSessionDisconnects),
    ("Physical session boundaries atomically close their active VPN interval", VpnSessionParentCloses),
    ("Routing mode changes update one VPN interval deterministically", VpnSessionRouteChanges),
    ("SQLite enforces at most one active VPN interval", VpnSessionSingleActive),
    ("VPN child relationships protect historical physical session references", VpnSessionRelationships),
    ("A failed VPN tunnel transition rolls back the previous interval closure", VpnSessionAtomicTransition),
    ("Unavailable or ambiguous VPN detection does not invent a child interval", VpnSessionUncertainDetection),
    ("Changing native tunnel identity ends and replaces the VPN interval", VpnSessionTunnelChanges),
    ("RAS-only VPN identities persist honestly without synthetic interface identifiers", VpnSessionRasIdentity),
    ("Stale and out-of-order VPN observations cannot rewrite confirmed intervals", VpnSessionObservationSafety),
    ("VPN persistence leaves bandwidth totals and physical event history unchanged", VpnSessionPreservesPhysicalAccounting),
    ("Closed or unconfirmed physical parents cannot accept VPN observations", VpnSessionParentValidation),
    ("VPN history queries return deterministic parent-scoped intervals", VpnSessionHistoryQueries),
    ("VPN monitoring persists native observations without physical or event duplication", VpnSessionMonitorObservations),
    ("A slow VPN query cannot attach an old observation to a successor physical session", VpnSessionMonitorStaleParent),
    ("VPN activation records one connected event with its physical context", VpnEventsConnected),
    ("Repeated and reopened VPN observations do not duplicate connection events", VpnEventsRepeated),
    ("Actual routing mode changes record one event without restarting intervals", VpnEventsRouteChanged),
    ("VPN off records one disconnected event while the physical connection remains active", VpnEventsDisconnected),
    ("VPN event context follows the current physical profile without rewriting history", VpnEventsPhysicalContext),
    ("Physical session boundaries record exactly one VPN disconnect on the original parent", VpnEventsParentClosure),
    ("Failed VPN event inserts roll back VPN and physical lifecycle changes atomically", VpnEventsAtomicFailure),
    ("Existing version-four databases gain VPN event triggers without backfilling history", VpnEventsExistingVersionFour),
    ("Native tunnel replacement records a disconnect and connect in deterministic order", VpnEventsTunnelReplacement),
    ("Sleep closes VPN before its physical parent at one explicit boundary", VpnLifecycleSleep),
    ("Resume with a fresh active VPN creates a new child on a new physical session", VpnLifecycleResumeActive),
    ("Resume with VPN off cannot resurrect the pre-sleep interval", VpnLifecycleResumeOff),
    ("Crash recovery ends stale VPN at its own persisted confirmation", VpnLifecycleCrashBoundary),
    ("Restart observes an active VPN freshly after physical recovery", VpnLifecycleStartupActive),
    ("Normal exit flushes physical usage and closes VPN cleanly", VpnLifecycleNormalExit),
    ("Physical closure never leaves or permits an orphan active VPN interval", VpnLifecycleParentSafety),
    ("Repeated recovery cannot duplicate VPN disconnect or physical recovery events", VpnLifecycleRecoveryDedup),
    ("Failed lifecycle events roll back VPN and physical boundaries for safe retry", VpnLifecycleAtomicFailure),
    ("Resume drops a blocked pre-sleep VPN query and promptly obtains fresh evidence", VpnLifecycleStaleSleepQuery),
    ("A hard-killed owned process recovers VPN at its separate persisted boundary", VpnLifecycleKilledOwner),
    ("The headless core samples and persists without a WPF window or dispatcher", BackgroundAutomaticMonitoring),
    ("Background monitoring retains the existing thirty-second persistence cadence", BackgroundBatchedPersistence),
    ("Core start and stop are idempotent and stop joins every owned monitor", BackgroundStartStop),
    ("Selecting the current adapter preserves its bandwidth baseline", BackgroundSameAdapterBaseline),
    ("Physical identity changes on one adapter discard the old counter baseline", BackgroundPhysicalTransitionBaseline),
    ("Headless counter resets cannot create a false bandwidth spike", BackgroundCounterReset),
    ("Profile assignment and editing resolve immediately without a dashboard", BackgroundProfileRecognition),
    ("Headless sleep and resume retain fresh physical and VPN interval boundaries", BackgroundSleepResume),
    ("Core shutdown flushes usage, closes VPN and physical history and releases its lease", BackgroundCleanExit),
    ("Core shutdown joins blocked VPN work before releasing database ownership", BackgroundShutdownJoin),
    ("Live traffic uses decimal megabits with a consistent Mbps unit", LiveTrafficMegabits),
    ("Live traffic rounds actual sampled bytes without an intermediate byte-unit conversion", LiveTrafficPrecision),
    ("Invalid live traffic cannot display negative or nonfinite rates", LiveTrafficInvalidRates),
    ("Data quantities retain their existing byte-unit formatting", DataQuantityFormatting),
    ("Fresh schema five stores daily passive traffic peaks independently of usage", TrafficPeaksFreshSchema),
    ("Version-four migration preserves every existing table and all connection history", TrafficPeaksVersionFourUpgrade),
    ("A conflicting peak table rolls back version-five migration and preserves old history", TrafficPeaksFailedMigration),
    ("Fractional daily peaks survive reopening and repeated maxima upserts", TrafficPeaksPersistence),
    ("Peak repositories and SQLite reject invalid rates and dates", TrafficPeaksValidation),
    ("Damaged current peak schemas and externally invalid peak rows fail startup safely", TrafficPeaksInvalidCurrentSchema),
    ("Passive peaks reuse the existing live sample without counting it twice", TrafficPeaksCoreAccumulation),
    ("Peak persistence stays batched and process restart retains today's peak", TrafficPeaksCoreBatchAndRestart),
    ("Local-day rollover while offline or sleeping never displays yesterday's peak", TrafficPeaksCoreRollover),
    ("Failed peak persistence retains independently dirty days through rollover", TrafficPeaksCoreMultiDayRetry),
    ("A failed peak write retries without losing or double-counting usage", TrafficPeaksCoreWriteRetry),
    ("Failed daily usage cannot prevent peak flush or clean physical and VPN closure", TrafficPeaksCoreUsageFailure),
    ("Physical identity transitions and counter resets cannot manufacture a passive peak", TrafficPeaksCoreBaselineSafety),
    ("Missing per-user startup registration is disabled without creating a registry key", StartupRegistrationAbsent),
    ("Windows startup writes one correctly quoted background command for paths with spaces", StartupRegistrationCommand),
    ("Enabling startup twice is idempotent and preserves other startup entries", StartupRegistrationIdempotent),
    ("Disabling startup removes only NetTrace's named registration", StartupRegistrationDisableScope),
    ("Startup enable and disable immediately reflect persisted current-user state", StartupRegistrationToggle),
    ("External startup command changes are observed without rewriting the registration", StartupRegistrationExternalChanges),
    ("Nonstring or expandable startup values cannot masquerade as NetTrace registration", StartupRegistrationValueKinds),
    ("Invalid or missing startup executable paths cannot create a registration", StartupRegistrationInvalidPaths),
    ("Unicode and apostrophes in an executable path remain one quoted startup argument", StartupRegistrationUnicodePath),
    ("Equivalent absolute executable paths produce the same canonical registration", StartupRegistrationCanonicalPath)
}.Concat(IcmpHealthTests.Cases).Concat(IcmpRollingTests.Cases)
    .Concat(IcmpStabilityChartTests.Cases).Concat(IcmpPacketLossChartTests.Cases)
    .Concat(IcmpQualityTests.Cases).ToArray();

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
    Equal(5L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Schema version");
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
    Equal(5L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Migrated schema version");
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
    Equal(5L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Existing database schema version");
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
    Equal(5L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Version after reopening");
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
    Equal(5L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "WAL copy schema version");
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
    CreateFixture(workspace.CurrentPath, version: 6);
    var original = Hash(workspace.CurrentPath);

    ExpectStartupFailure(workspace.Root);

    Equal(original, Hash(workspace.CurrentPath), "Future current database bytes");
    Equal(6L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Future current version");
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
    CreateFixture(workspace.LegacyPath, version: 6);
    var original = Hash(workspace.LegacyPath);

    ExpectStartupFailure(workspace.Root);

    Require(!File.Exists(workspace.CurrentPath), "A future legacy schema must not be published as a usable current database.");
    Equal(original, Hash(workspace.LegacyPath), "Future legacy database bytes");
    Equal(6L, ReadInteger(workspace.LegacyPath, "PRAGMA user_version;"), "Future legacy version");
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
    Equal(5L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Version-one upgrade version");
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
    CreateVersionTwoFixture(workspace.CurrentPath);
    using (var connection = Open(workspace.CurrentPath))
        Execute(connection, "DELETE FROM daily_usage; INSERT INTO daily_usage VALUES ('2026-01-15', 1000, 100); DELETE FROM NetworkMappings WHERE Id=52; DROP INDEX IX_NetworkMappings_ProfileId;");

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

static void CreateVersionTwoFixture(string path)
{
    CreateFixture(path, version: 2);
    using var connection = Open(path);
    Execute(connection, """
        CREATE TABLE ConnectionProfiles (
            Id INTEGER PRIMARY KEY,
            TelcoName TEXT NOT NULL,
            DeviceName TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );
        CREATE TABLE NetworkMappings (
            Id INTEGER PRIMARY KEY,
            ProfileId INTEGER NOT NULL REFERENCES ConnectionProfiles(Id) ON DELETE RESTRICT,
            NetworkType TEXT NOT NULL,
            NetworkIdentifier TEXT NOT NULL COLLATE BINARY,
            FriendlyName TEXT,
            CreatedAt TEXT NOT NULL,
            UNIQUE (NetworkType, NetworkIdentifier)
        );
        CREATE INDEX IX_NetworkMappings_ProfileId ON NetworkMappings(ProfileId);
        INSERT INTO ConnectionProfiles VALUES (41, 'DITO', 'D2 Router', '2026-01-15T01:02:03.0000000+00:00', '2026-01-15T04:05:06.0000000+00:00');
        INSERT INTO NetworkMappings VALUES (51, 41, 'WiFi', 'DITO_HOME', 'Main Wi-Fi', '2026-01-15T01:02:03.0000000+00:00');
        INSERT INTO NetworkMappings VALUES (52, 41, 'WiFi', 'DITO_HOME_5G', NULL, '2026-01-15T01:02:03.0000000+00:00');
        """);
}

static void VersionTwoUpgrade()
{
    using var workspace = new TestWorkspace();
    CreateVersionTwoFixture(workspace.CurrentPath);
    var originalTables = new[] { "daily_usage", "ConnectionProfiles", "NetworkMappings" }
        .ToDictionary(table => table, table => (
            Sql: ReadText(workspace.CurrentPath, $"SELECT sql FROM sqlite_schema WHERE name='{table}';"),
            Root: ReadInteger(workspace.CurrentPath, $"SELECT rootpage FROM sqlite_schema WHERE name='{table}';")));
    var originalProfile = ReadText(workspace.CurrentPath, "SELECT Id || '|' || TelcoName || '|' || DeviceName || '|' || CreatedAt || '|' || UpdatedAt FROM ConnectionProfiles;");
    var originalMappings = ReadText(workspace.CurrentPath, "SELECT group_concat(row, ';') FROM (SELECT Id || '|' || ProfileId || '|' || NetworkType || '|' || NetworkIdentifier || '|' || coalesce(FriendlyName, '<null>') || '|' || CreatedAt AS row FROM NetworkMappings ORDER BY Id);");

    var store = new UsageStore(workspace.Root);

    Equal(5L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Version-two upgrade version");
    AssertFixtureTotals(store);
    foreach (var (table, original) in originalTables)
    {
        Equal(original.Sql, ReadText(workspace.CurrentPath, $"SELECT sql FROM sqlite_schema WHERE name='{table}';"), $"{table} schema remains unchanged");
        Equal(original.Root, ReadInteger(workspace.CurrentPath, $"SELECT rootpage FROM sqlite_schema WHERE name='{table}';"), $"{table} B-tree remains unchanged");
    }
    Equal(originalProfile, ReadText(workspace.CurrentPath, "SELECT Id || '|' || TelcoName || '|' || DeviceName || '|' || CreatedAt || '|' || UpdatedAt FROM ConnectionProfiles;"), "All profile fields preserved");
    Equal(originalMappings, ReadText(workspace.CurrentPath, "SELECT group_concat(row, ';') FROM (SELECT Id || '|' || ProfileId || '|' || NetworkType || '|' || NetworkIdentifier || '|' || coalesce(FriendlyName, '<null>') || '|' || CreatedAt AS row FROM NetworkMappings ORDER BY Id);"), "All mapping fields preserved");
    Equal(0L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM ConnectionSessions;"), "Migration invents no historical sessions");
    Equal(0L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM ConnectionEvents;"), "Migration invents no connection events");
    Equal(41L, new ProfileStore(store).ResolveProfile("WiFi", "DITO_HOME_5G")!.Id, "Migrated recognition still works");
}

// An independent pre-VPN fixture: never derive historical schema by downgrading the current database.
static void CreateVersionThreeFixture(string path)
{
    CreateVersionTwoFixture(path);
    using var connection = Open(path);
    Execute(connection, """
        CREATE TABLE ConnectionSessions (
            Id INTEGER PRIMARY KEY,
            ProfileId INTEGER REFERENCES ConnectionProfiles(Id) ON DELETE RESTRICT,
            NetworkType TEXT NOT NULL CHECK(length(trim(NetworkType)) > 0),
            NetworkIdentifier TEXT NOT NULL COLLATE BINARY CHECK(length(NetworkIdentifier) > 0),
            NetworkFriendlyName TEXT NOT NULL,
            AdapterId TEXT NOT NULL,
            StartedAtUtc TEXT NOT NULL,
            LastConfirmedAtUtc TEXT NOT NULL,
            EndedAtUtc TEXT,
            EndReason TEXT,
            DownloadBytes INTEGER NOT NULL DEFAULT 0 CHECK(DownloadBytes >= 0),
            UploadBytes INTEGER NOT NULL DEFAULT 0 CHECK(UploadBytes >= 0),
            CHECK(LastConfirmedAtUtc >= StartedAtUtc),
            CHECK(EndedAtUtc IS NULL OR EndedAtUtc >= LastConfirmedAtUtc),
            CHECK((EndedAtUtc IS NULL AND EndReason IS NULL) OR
                  (EndedAtUtc IS NOT NULL AND EndReason IS NOT NULL))
        );
        CREATE UNIQUE INDEX UX_ConnectionSessions_Active ON ConnectionSessions((1)) WHERE EndedAtUtc IS NULL;
        CREATE INDEX IX_ConnectionSessions_StartedAtUtc ON ConnectionSessions(StartedAtUtc DESC, Id DESC);
        CREATE INDEX IX_ConnectionSessions_ProfileId ON ConnectionSessions(ProfileId, StartedAtUtc DESC);
        CREATE TABLE ConnectionEvents (
            Id INTEGER PRIMARY KEY,
            OccurredAtUtc TEXT NOT NULL,
            EventType TEXT NOT NULL CHECK(length(trim(EventType)) > 0),
            SessionId INTEGER REFERENCES ConnectionSessions(Id) ON DELETE RESTRICT,
            ProfileId INTEGER REFERENCES ConnectionProfiles(Id) ON DELETE RESTRICT,
            NetworkType TEXT,
            NetworkIdentifier TEXT COLLATE BINARY,
            Message TEXT NOT NULL
        );
        CREATE INDEX IX_ConnectionEvents_OccurredAtUtc ON ConnectionEvents(OccurredAtUtc DESC, Id DESC);
        CREATE INDEX IX_ConnectionEvents_SessionId ON ConnectionEvents(SessionId);
        CREATE INDEX IX_ConnectionEvents_ProfileId ON ConnectionEvents(ProfileId);
        INSERT INTO ConnectionSessions VALUES (61, NULL, 'WiFi', 'OLD_HOME', 'Original old SSID', 'physical-wifi',
            '2026-01-15T01:00:00.0000000+00:00', '2026-01-15T02:00:00.0000000+00:00',
            '2026-01-15T02:00:00.0000000+00:00', 'NetworkChanged', 4096, 512);
        INSERT INTO ConnectionSessions VALUES (62, 41, 'WiFi', 'DITO_HOME', 'Current Wi-Fi', 'physical-wifi',
            '2026-01-15T03:00:00.0000000+00:00', '2026-01-15T03:15:00.0000000+00:00', NULL, NULL, 8192, 1024);
        INSERT INTO ConnectionEvents VALUES (71, '2026-01-15T01:00:00.0000000+00:00', 'Connected', 61, NULL,
            'WiFi', 'OLD_HOME', 'Original event');
        INSERT INTO ConnectionEvents VALUES (72, '2026-01-15T03:00:00.0000000+00:00', 'NetworkChanged', 62, 41,
            'WiFi', 'DITO_HOME', 'Current event');
        PRAGMA user_version=3;
        """);
}

static void LegacyVersionTwoUpgrade()
{
    using var workspace = new TestWorkspace();
    CreateVersionTwoFixture(workspace.LegacyPath);
    var original = Hash(workspace.LegacyPath);

    var store = new UsageStore(workspace.Root);

    AssertFixtureTotals(store);
    Equal(5L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Copied version-two database reaches current version five");
    Equal(2L, ReadInteger(workspace.LegacyPath, "PRAGMA user_version;"), "Original version-two database is not migrated in place");
    Equal(original, Hash(workspace.LegacyPath), "Legacy version-two database bytes remain unchanged");
    Equal(41L, new ProfileStore(store).ResolveProfile("WiFi", "DITO_HOME")!.Id, "Legacy profile recognition preserved");
    Equal(2L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM NetworkMappings;"), "Legacy recognition rules preserved");
}

static void FailedVersionThreeMigration()
{
    using var workspace = new TestWorkspace();
    CreateVersionTwoFixture(workspace.CurrentPath);
    using (var connection = Open(workspace.CurrentPath))
        Execute(connection, "CREATE TABLE ConnectionEvents(sentinel TEXT); INSERT INTO ConnectionEvents VALUES ('keep conflicting event data');");

    ExpectStartupFailure(workspace.Root);

    Equal(2L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Failed migration keeps version two");
    Equal(0L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM sqlite_schema WHERE name='ConnectionSessions';"), "Partially created sessions roll back");
    Equal("keep conflicting event data", ReadText(workspace.CurrentPath, "SELECT sentinel FROM ConnectionEvents;"), "Conflicting pre-existing data is not replaced");
    Equal(126_456L, ReadInteger(workspace.CurrentPath, "SELECT sum(downloaded) FROM daily_usage;"), "Usage totals survive migration failure");
    Equal(1L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM ConnectionProfiles;"), "Profiles survive migration failure");
    Equal(2L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM NetworkMappings;"), "Mappings survive migration failure");
}

static CurrentConnectionState Connected(NetworkIdentity network, ConnectionProfile? profile = null) =>
    new(profile is null ? ConnectionStatus.ConnectedUnassigned : ConnectionStatus.ConnectedRecognized,
        network, profile, null);

static CurrentConnectionState Disconnected() => new(ConnectionStatus.Disconnected, null, null, "Disconnected");

static NetworkIdentity Wifi(string ssid = "DITO_HOME", string adapterId = "physical-wifi") =>
    new("WiFi", ssid, ssid, adapterId);

static void SessionConnectionDeduplication()
{
    using var test = new SessionTestContext();
    var network = Wifi();
    test.Tracker.ObserveConnection(Connected(network));
    var id = test.Tracker.Current!.Id;
    for (var index = 0; index < 10; index++)
    {
        test.Clock.Advance(TimeSpan.FromSeconds(1));
        test.Tracker.ObserveConnection(Connected(network));
    }
    test.Tracker.Flush();

    Equal(id, test.Tracker.Current!.Id, "Repeated observations preserve the active session ID");
    Equal(1, test.History.GetRecentSessions().Count, "Repeated callbacks do not create sessions");
    Equal(1, test.History.GetRecentEvents().Count, "Repeated callbacks do not create events");
    Equal("Connected", test.History.GetRecentEvents()[0].EventType, "First connected event");
    Equal(test.Clock.GetUtcNow(), test.History.GetActiveSession()!.LastConfirmedAtUtc, "Identical observations confirm liveness");
}

static void SessionNetworkTransitions()
{
    using var test = new SessionTestContext();
    var profile = test.Profiles.CreateProfile("DITO", "D2 Router");
    var first = Wifi("SSID A");
    var second = Wifi("SSID B");
    test.Profiles.AssignMapping(profile.Id, first.NetworkType, first.NetworkIdentifier);
    test.Profiles.AssignMapping(profile.Id, second.NetworkType, second.NetworkIdentifier);
    test.Tracker.ObserveConnection(Connected(first, profile));
    var firstId = test.Tracker.Current!.Id;
    test.Clock.Advance(TimeSpan.FromSeconds(1));
    Require(test.Tracker.RecordSample(firstId, first.AdapterId, 1, test.Clock.GetUtcNow(), 4_096, 256), "First session sample accepted");
    test.Clock.Advance(TimeSpan.FromMinutes(1));
    test.Tracker.ObserveConnection(Connected(second, profile));

    var rows = test.History.GetRecentSessions();
    Equal(2, rows.Count, "Different identities on one profile create different sessions");
    var old = rows.Single(session => session.Id == firstId);
    Equal("SSID A", old.NetworkIdentifier, "Closed session stores its original SSID");
    Equal("NetworkChanged", old.EndReason, "Physical switch closes old session with reason");
    Equal(4_096L, old.DownloadBytes, "Transition flushes old download usage");
    Equal(256L, old.UploadBytes, "Transition flushes old upload usage");
    Require(old.EndedAtUtc is not null, "Old session is closed");
    Equal("SSID B", test.Tracker.Current!.NetworkIdentifier, "New session stores new SSID");
    Equal(profile.Id, test.Tracker.Current.ProfileId!.Value, "Both sessions may use the same profile");
    Equal(0L, test.Tracker.Current.DownloadBytes, "Old counters do not bleed into new session");
    Equal(1, test.History.GetRecentEvents().Count(item => item.EventType == "NetworkChanged"), "One meaningful network changed event");
    Equal(1L, ReadInteger(test.DatabasePath, "SELECT count(*) FROM ConnectionSessions WHERE EndedAtUtc IS NULL;"), "Only one new active session");
}

static void SessionDisconnectReconnect()
{
    using var test = new SessionTestContext();
    test.Tracker.ObserveConnection(Disconnected());
    test.Tracker.ObserveConnection(Disconnected());
    Equal(0, test.History.GetRecentSessions().Count, "Startup disconnection creates no session");
    Equal(0, test.History.GetRecentEvents().Count, "Repeated initial disconnection is not a transition");
    test.Tracker.ObserveConnection(Connected(Wifi()));
    var firstId = test.Tracker.Current!.Id;
    test.Clock.Advance(TimeSpan.FromSeconds(10));
    test.Tracker.ObserveConnection(Disconnected());
    test.Tracker.ObserveConnection(Disconnected());
    Require(test.Tracker.Current is null, "Disconnection has no active session");
    Require(test.History.GetActiveSession() is null, "Disconnection closes persisted active session");
    Equal("Disconnected", test.History.GetRecentSessions().Single().EndReason, "Explicit disconnection reason");
    test.Clock.Advance(TimeSpan.FromSeconds(10));
    test.Tracker.ObserveConnection(Connected(Wifi()));
    Require(test.Tracker.Current!.Id != firstId, "Reconnect creates a new session");
    Equal(2, test.History.GetRecentSessions().Count, "Two connected periods produce two sessions");
    Equal(2, test.History.GetRecentEvents().Count(item => item.EventType == "Connected"), "Each actual reconnect records connected once");
    Equal(1, test.History.GetRecentEvents().Count(item => item.EventType == "Disconnected"), "Repeated disconnected observation is deduplicated");
}

static void SessionSleepResume()
{
    using var test = new SessionTestContext();
    test.Tracker.ObserveConnection(Connected(Wifi()));
    var firstId = test.Tracker.Current!.Id;
    test.Clock.Advance(TimeSpan.FromMinutes(3));
    var sleepAt = test.Clock.GetUtcNow();
    test.Tracker.Sleep();
    test.Tracker.Sleep();
    Require(test.Tracker.Current is null, "Sleep closes current session");
    var old = test.History.GetRecentSessions().Single();
    Equal("Sleep", old.EndReason, "Sleep end reason");
    Equal(sleepAt, old.EndedAtUtc!.Value, "Sleep is an explicit end observation");
    test.Clock.Advance(TimeSpan.FromHours(2));
    test.Tracker.ObserveConnection(Connected(Wifi()));
    Require(test.Tracker.Current is null, "Queued network observations cannot start a session during sleep");
    test.Tracker.Resume();
    test.Tracker.Resume();
    Require(test.Tracker.Current is null, "Resume waits for fresh detection");
    test.Tracker.ObserveConnection(Connected(Wifi()));
    Require(test.Tracker.Current!.Id != firstId, "Same SSID after sleep creates a new session");
    Equal(2, test.History.GetRecentSessions().Count, "Sleep interval is not bridged");
    Equal(1, test.History.GetRecentEvents().Count(item => item.EventType == "Sleep"), "Duplicate sleep notification is ignored");
    Equal(1, test.History.GetRecentEvents().Count(item => item.EventType == "Resume"), "Duplicate resume notification is ignored");
}

static void SessionUnexpectedExitRecovery()
{
    using var test = new SessionTestContext();
    var firstNetwork = Wifi();
    test.Tracker.ObserveConnection(Connected(firstNetwork));
    var firstId = test.Tracker.Current!.Id;
    test.Clock.Advance(TimeSpan.FromSeconds(30));
    var confirmed = test.Clock.GetUtcNow();
    Require(test.Tracker.RecordSample(firstId, firstNetwork.AdapterId, 1, confirmed, 8_192, 512), "Sample accepted before unexpected exit");
    test.Tracker.Flush();
    test.Clock.Advance(TimeSpan.FromHours(8));
    var startup = test.Clock.GetUtcNow();
    var restarted = new ConnectionSessionTracker(new ConnectionHistoryStore(new UsageStore(test.Root)), test.Clock);

    Require(restarted.Current is null, "Recovery does not carry the previous run's active session");
    var recovered = test.History.GetRecentSessions().Single();
    Equal(confirmed, recovered.EndedAtUtc!.Value, "Crash recovery stops at last persisted observation");
    Equal(confirmed, recovered.LastConfirmedAtUtc, "Recovery does not move confirmation into the startup gap");
    Equal("UnexpectedExit", recovered.EndReason, "Recovered session reason is explicit");
    Equal(8_192L, recovered.DownloadBytes, "Persisted crash usage survives recovery");
    var recoveryEvent = test.History.GetRecentEvents().Single(item => item.EventType == "SessionRecoveredAfterUnexpectedExit");
    Equal(firstId, recoveryEvent.SessionId!.Value, "Recovery event links the old session");
    Equal(startup, recoveryEvent.OccurredAtUtc, "Recovery itself is recorded at new startup");
    restarted.ObserveConnection(Connected(firstNetwork));
    Require(restarted.Current!.Id != firstId, "Restart creates a new session even on the same SSID");
    Equal(startup, restarted.Current.StartedAtUtc, "New session begins at current detection");
    Equal(1L, ReadInteger(test.DatabasePath, "SELECT count(*) FROM ConnectionSessions WHERE EndedAtUtc IS NULL;"), "Recovery leaves only the new session active");
}

static void SessionActiveProfileAssignment()
{
    using var test = new SessionTestContext();
    var network = Wifi("NewHotspot");
    test.Tracker.ObserveConnection(Connected(network));
    var historicalId = test.Tracker.Current!.Id;
    Require(test.Tracker.Current.ProfileId is null, "Unknown valid network starts unassigned");
    test.Clock.Advance(TimeSpan.FromMinutes(1));
    test.Tracker.ObserveConnection(Disconnected());
    test.Clock.Advance(TimeSpan.FromMinutes(1));
    test.Tracker.ObserveConnection(Connected(network));
    var activeId = test.Tracker.Current!.Id;
    var profile = test.Profiles.CreateProfile("DITO", "Vivo Phone");
    test.Profiles.AssignMapping(profile.Id, network.NetworkType, network.NetworkIdentifier);
    test.Clock.Advance(TimeSpan.FromSeconds(1));
    test.Tracker.ObserveConnection(Connected(network, profile));
    test.Tracker.ObserveConnection(Connected(network, profile));

    Equal(activeId, test.Tracker.Current!.Id, "Assignment associates the existing active session");
    Equal(profile.Id, test.History.GetActiveSession()!.ProfileId!.Value, "Active association is persisted immediately");
    Require(test.History.GetRecentSessions().Single(session => session.Id == historicalId).ProfileId is null, "Completed unassigned session is not rewritten");
    Equal(2, test.History.GetRecentSessions().Count, "Assignment does not split the physical session");
    Equal(1, test.History.GetRecentEvents().Count(item => item.EventType == "ProfileAssigned"), "Assignment is recorded once");
}

static void SessionUsageAccumulation()
{
    using var test = new SessionTestContext();
    var network = Wifi();
    test.Tracker.ObserveConnection(Connected(network));
    var id = test.Tracker.Current!.Id;
    test.Clock.Advance(TimeSpan.FromSeconds(1));
    var sampledAt = test.Clock.GetUtcNow();
    Require(test.Tracker.RecordSample(id, network.AdapterId, 1, sampledAt, 1_000, 200), "Valid first delta accepted");
    Require(!test.Tracker.RecordSample(id, network.AdapterId, 1, sampledAt, 1_000, 200), "Duplicate sequence is rejected");
    test.Clock.Advance(TimeSpan.FromSeconds(1));
    Require(test.Tracker.RecordSample(id, network.AdapterId, 2, test.Clock.GetUtcNow(), 500, 50), "Second distinct delta accepted");
    Equal(1_500L, test.Tracker.Current!.DownloadBytes, "Session download counts each sample once");
    Equal(250L, test.Tracker.Current.UploadBytes, "Session upload counts each sample once");
    Equal(0L, test.History.GetActiveSession()!.DownloadBytes, "Samples are batched instead of writing SQLite every second");
    test.Tracker.Flush();
    test.Tracker.Flush();
    var persisted = new ConnectionHistoryStore(test.DatabasePath).GetActiveSession()!;
    Equal(1_500L, persisted.DownloadBytes, "Repeated flush cannot double count persisted downloads");
    Equal(250L, persisted.UploadBytes, "Repeated flush cannot double count persisted uploads");
    Equal(test.Clock.GetUtcNow(), persisted.LastConfirmedAtUtc, "Periodic flush stores last confirmation");
}

static void SessionInvalidSamples()
{
    using var test = new SessionTestContext();
    var first = Wifi("A");
    test.Tracker.ObserveConnection(Connected(first));
    var oldId = test.Tracker.Current!.Id;
    test.Clock.Advance(TimeSpan.FromSeconds(1));
    Require(!test.Tracker.RecordSample(oldId, "virtual-vpn", 1, test.Clock.GetUtcNow(), 999_999, 999_999), "Virtual or unrelated adapter cannot contribute");
    Require(!test.Tracker.RecordSample(oldId, first.AdapterId, 1, test.Clock.GetUtcNow(), -1, 10), "Negative delta cannot contribute");
    test.Tracker.ObserveConnection(Connected(Wifi("B", "physical-other")));
    var newId = test.Tracker.Current!.Id;
    test.Clock.Advance(TimeSpan.FromSeconds(1));
    Require(!test.Tracker.RecordSample(oldId, first.AdapterId, 2, test.Clock.GetUtcNow(), 1_000, 100), "Old session's in-flight sample cannot enter the new session");
    Require(!test.Tracker.RecordSample(newId, first.AdapterId, 2, test.Clock.GetUtcNow(), 1_000, 100), "Old adapter sample cannot enter the new session");
    Require(test.Tracker.RecordSample(newId, "physical-other", 3, test.Clock.GetUtcNow(), 100, 10), "Matching physical adapter sample accepted");
    test.Tracker.Flush();
    Equal(100L, test.History.GetActiveSession()!.DownloadBytes, "Only correct adapter bytes are persisted");
    Equal(10L, test.History.GetActiveSession()!.UploadBytes, "Only correct adapter uploads are persisted");
}

static void SessionIdentityUnavailable()
{
    using var test = new SessionTestContext();
    var network = Wifi();
    test.Tracker.ObserveConnection(Connected(network));
    var id = test.Tracker.Current!.Id;
    test.Clock.Advance(TimeSpan.FromSeconds(1));
    var confirmed = test.Clock.GetUtcNow();
    Require(test.Tracker.RecordSample(id, network.AdapterId, 1, confirmed, 100, 10), "Valid observation before permission failure");
    test.Clock.Advance(TimeSpan.FromMinutes(1));
    var unavailable = new CurrentConnectionState(ConnectionStatus.ConnectedIdentityUnavailable, null, null,
        "Windows could not provide the network identity because location access was denied.");
    test.Tracker.ObserveConnection(unavailable);
    test.Tracker.ObserveConnection(unavailable);

    Require(test.Tracker.Current is null, "Unavailable identity does not create fake sessions");
    var old = test.History.GetRecentSessions().Single();
    Equal("IdentityUnavailable", old.EndReason, "Failure has its own reason rather than pretending disconnected");
    Equal(confirmed, old.EndedAtUtc!.Value, "Failure closes at last known identity confirmation");
    Equal(100L, old.DownloadBytes, "Known usage is retained");
    Equal(0L, ReadInteger(test.DatabasePath, "SELECT count(*) FROM NetworkMappings;"), "Permission failure creates no fake recognition rule");
    Equal(0, test.History.GetRecentEvents().Count(item => item.EventType == "Disconnected"), "Identity failure is not represented as a confirmed disconnection");
    test.Tracker.ObserveConnection(Connected(network));
    Require(test.Tracker.Current!.Id != id, "Fresh identity after an unknown interval creates a fresh session");
}

static void SessionGracefulExit()
{
    using var test = new SessionTestContext();
    var network = Wifi();
    test.Tracker.ObserveConnection(Connected(network));
    var id = test.Tracker.Current!.Id;
    test.Clock.Advance(TimeSpan.FromSeconds(1));
    Require(test.Tracker.RecordSample(id, network.AdapterId, 1, test.Clock.GetUtcNow(), 2_048, 128), "Pending sample accepted before exit");
    test.Clock.Advance(TimeSpan.FromSeconds(5));
    var exitedAt = test.Clock.GetUtcNow();
    test.Tracker.Close();
    test.Tracker.Close();

    Require(test.History.GetActiveSession() is null, "Graceful application exit closes the database session");
    var ended = test.History.GetRecentSessions().Single();
    Equal("ApplicationExit", ended.EndReason, "Normal exit reason is explicit");
    Equal(exitedAt, ended.EndedAtUtc!.Value, "Normal close records exit UTC time");
    Equal(exitedAt, ended.LastConfirmedAtUtc, "Normal close confirms the final observation");
    Equal(2_048L, ended.DownloadBytes, "Normal close flushes pending download bytes");
    Equal(128L, ended.UploadBytes, "Normal close flushes pending upload bytes");
    var restarted = new ConnectionSessionTracker(new ConnectionHistoryStore(test.DatabasePath), test.Clock);
    Equal(0, test.History.GetRecentEvents().Count(item => item.EventType == "SessionRecoveredAfterUnexpectedExit"), "Clean exit needs no recovery event");
}

static void SessionProfileRename()
{
    using var test = new SessionTestContext();
    var network = Wifi();
    var profile = test.Profiles.CreateProfile("DITO", "D2 Router");
    test.Tracker.ObserveConnection(Connected(network, profile));
    var id = test.Tracker.Current!.Id;
    test.Clock.Advance(TimeSpan.FromMinutes(1));
    var renamed = test.Profiles.UpdateProfile(profile.Id, "DITO", "Home Router");
    test.Tracker.ObserveConnection(Connected(network, renamed));
    test.Tracker.ObserveConnection(Connected(network, renamed));

    Equal(id, test.Tracker.Current!.Id, "Renaming a profile does not split the session");
    var persisted = test.History.GetActiveSession()!;
    Equal(profile.Id, persisted.ProfileId!.Value, "Session foreign key retains the stable profile ID");
    Equal("DITO — Home Router", persisted.ProfileDisplayName, "Historical projection uses the current profile name");
    Equal("DITO_HOME", persisted.NetworkIdentifier, "Stored historical SSID remains unchanged");
    Equal(1, test.History.GetRecentEvents().Count(item => item.EventType == "ProfileChanged"), "Rename is represented once");
    test.Tracker.ObserveConnection(Disconnected());
    Equal(1, test.History.GetSessionsByProfile(profile.Id).Count, "Renamed profile still finds its completed history");
}

static void SessionHistoryQueries()
{
    using var test = new SessionTestContext();
    var profile = test.Profiles.CreateProfile("Home Fiber", "Main Router");
    var firstStart = test.Clock.GetUtcNow();
    test.Tracker.ObserveConnection(Connected(Wifi("A"), profile));
    var firstId = test.Tracker.Current!.Id;
    test.Clock.Advance(TimeSpan.FromMinutes(10));
    test.Tracker.ObserveConnection(Disconnected());
    test.Clock.Advance(TimeSpan.FromMinutes(10));
    test.Tracker.ObserveConnection(Connected(Wifi("B")));
    var secondId = test.Tracker.Current!.Id;
    test.Clock.Advance(TimeSpan.FromMinutes(10));
    test.Tracker.Flush();

    Equal(secondId, test.History.GetRecentSessions(1).Single().Id, "Recent sessions honor descending order and limit");
    Equal(firstId, test.History.GetSessionsByProfile(profile.Id).Single().Id, "Profile query filters sessions");
    var overlapStart = firstStart.AddMinutes(5).ToOffset(TimeSpan.FromHours(8));
    var overlapEnd = firstStart.AddMinutes(15).ToOffset(TimeSpan.FromHours(-5));
    Equal(firstId, test.History.GetSessions(overlapStart, overlapEnd).Single().Id, "Date range finds overlapping sessions and normalizes timezones");
    Equal(2, test.History.GetSessions(firstStart, firstStart.AddHours(1)).Count, "Date range includes completed and current observations");
    Require(test.History.GetRecentSessions().All(session => session.StartedAtUtc.Offset == TimeSpan.Zero && session.LastConfirmedAtUtc.Offset == TimeSpan.Zero && (session.EndedAtUtc is null || session.EndedAtUtc.Value.Offset == TimeSpan.Zero)), "Session query timestamps are UTC");
    Require(test.History.GetRecentEvents().All(item => item.OccurredAtUtc.Offset == TimeSpan.Zero), "Event query timestamps are UTC");
    Equal(1, test.History.GetRecentEvents(1).Count, "Recent events honor the requested limit");
    ExpectFailure(() => test.History.GetSessions(overlapEnd, overlapStart), "Reversed range validation");
}

static void SessionSchemaConstraints()
{
    using var test = new SessionTestContext();
    var network = Wifi();
    var profile = test.Profiles.CreateProfile("DITO", "D2 Router");
    test.Tracker.ObserveConnection(Connected(network, profile));
    using var connection = Open(test.DatabasePath);
    Execute(connection, "PRAGMA foreign_keys=ON;");
    using var tableCommand = connection.CreateCommand();
    tableCommand.CommandText = "PRAGMA table_info(ConnectionSessions);";
    using (var reader = tableCommand.ExecuteReader())
    {
        var profileIdNullable = false;
        while (reader.Read())
            if (reader.GetString(1) == "ProfileId") profileIdNullable = reader.GetInt64(3) == 0;
        Require(profileIdNullable, "Session schema permits legitimate unassigned history");
    }
    ExpectFailure(() => Execute(connection, $"DELETE FROM ConnectionProfiles WHERE Id={profile.Id};"), "Session-only profile references cannot be cascade deleted");
    ExpectFailure(() => Execute(connection, "INSERT INTO ConnectionSessions(ProfileId, NetworkType, NetworkIdentifier, NetworkFriendlyName, AdapterId, StartedAtUtc, LastConfirmedAtUtc, DownloadBytes, UploadBytes) SELECT ProfileId, NetworkType, NetworkIdentifier, NetworkFriendlyName, AdapterId, StartedAtUtc, LastConfirmedAtUtc, DownloadBytes, UploadBytes FROM ConnectionSessions WHERE EndedAtUtc IS NULL;"), "A second active session is forbidden by SQLite");
    Equal(1L, ReadInteger(test.DatabasePath, "SELECT count(*) FROM ConnectionSessions WHERE EndedAtUtc IS NULL;"), "Constraint failure preserves the existing active session");
}

static void ExtensibleEventHistory()
{
    using var test = new SessionTestContext();
    var instantWithOffset = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.FromHours(8));
    test.History.RecordEvent(instantWithOffset, "CustomFutureEvent", null, null, null, "Stored generic event without enabling a future feature.");
    var item = test.History.GetRecentEvents().Single();
    Equal("CustomFutureEvent", item.EventType, "New event names fit the existing schema");
    Equal(instantWithOffset.ToUniversalTime(), item.OccurredAtUtc, "Event API stores UTC even for offset input");
    Equal(TimeSpan.Zero, item.OccurredAtUtc.Offset, "Event API reads canonical UTC");
}

static void BandwidthDeltaSafety()
{
    var started = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
    var first = new NetworkSnapshot(20_000, 5_000, started);
    Require(BandwidthDeltaCalculator.Calculate(null, first) is null, "First observation establishes a baseline");
    var second = new NetworkSnapshot(21_000, 5_200, started.AddSeconds(1));
    var valid = BandwidthDeltaCalculator.Calculate(first, second)!;
    Equal(1_000L, valid.DownloadBytes, "Shared dashboard delta downloads");
    Equal(200L, valid.UploadBytes, "Shared dashboard delta uploads");
    Equal(1d, valid.ElapsedSeconds, "Shared elapsed sampling duration");
    Require(BandwidthDeltaCalculator.Calculate(second, second) is null, "Duplicate observation cannot manufacture traffic");
    Require(BandwidthDeltaCalculator.Calculate(second, first) is null, "Out-of-order sample cannot manufacture traffic");
    var reset = new NetworkSnapshot(10, 2, started.AddSeconds(2));
    var resetDelta = BandwidthDeltaCalculator.Calculate(second, reset)!;
    Equal(0L, resetDelta.DownloadBytes, "Reset download counter produces no spike");
    Equal(0L, resetDelta.UploadBytes, "Reset upload counter produces no spike");
    var afterReset = new NetworkSnapshot(110, 22, started.AddSeconds(3));
    var recovered = BandwidthDeltaCalculator.Calculate(reset, afterReset)!;
    Equal(100L, recovered.DownloadBytes, "Sampling resumes from reset baseline");
    Equal(20L, recovered.UploadBytes, "Uploads resume from reset baseline");
    Require(BandwidthDeltaCalculator.Calculate(null, afterReset) is null, "Physical adapter change establishes a fresh baseline");
}

static void CapturedConnectionObservations()
{
    using var test = new SessionTestContext();
    var identity = new FakeNetworkIdentityService { Network = Wifi("A") };
    using var monitor = new ConnectionProfileMonitor(identity, new ProfileResolver(test.Profiles));
    var observations = new System.Collections.Concurrent.ConcurrentQueue<ConnectionObservationEventArgs>();
    var stateChanges = 0;
    monitor.ObservationReceived += (_, observation) => observations.Enqueue(observation);
    monitor.StateChanged += (_, _) => Interlocked.Increment(ref stateChanges);
    monitor.Start();
    WaitUntil(() => observations.Count == 1, "First captured connection observation");
    identity.Network = Wifi("B");
    identity.RaiseNetworkChanged();
    WaitUntil(() => observations.Count == 2, "Second captured connection observation");
    identity.Network = Wifi("A");
    identity.RaiseNetworkChanged();
    WaitUntil(() => observations.Count == 3, "Third captured connection observation");
    identity.RaiseNetworkChanged();
    WaitUntil(() => observations.Count == 4, "Repeated connection confirms observation without a state change");

    var captured = observations.ToArray();
    Require(captured.Select(item => item.State.Network!.NetworkIdentifier).SequenceEqual(new[] { "A", "B", "A", "A" }), "Callbacks carry their own captured state instead of rereading only the latest A");
    Require(captured.Select(item => item.Sequence).SequenceEqual(new long[] { 1, 2, 3, 4 }), "Observation sequences preserve order");
    Equal(3, Volatile.Read(ref stateChanges), "Repeated observation keeps old StateChanged notification deduplicated");
    foreach (var observation in captured)
        test.Tracker.ObserveConnection(observation.State, observation.ObservedAtUtc);
    Equal(3, test.History.GetRecentSessions().Count, "Delayed delivery preserves all three continuous physical periods");
    Equal(2, test.History.GetRecentEvents().Count(item => item.EventType == "NetworkChanged"), "Only the actual A-to-B and B-to-A transitions create events");
}

static void InvalidatedNativeObservation()
{
    using var workspace = new TestWorkspace();
    var profiles = new ProfileStore(new UsageStore(workspace.Root));
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var identity = new FakeNetworkIdentityService
    {
        Network = Wifi("BeforeSleep"),
        CaptureBeforeBlocking = true,
        DetectionStarted = entered,
        ReleaseDetection = release
    };
    using var monitor = new ConnectionProfileMonitor(identity, new ProfileResolver(profiles));
    var observations = new System.Collections.Concurrent.ConcurrentQueue<ConnectionObservationEventArgs>();
    monitor.ObservationReceived += (_, observation) => observations.Enqueue(observation);
    monitor.Start();
    Require(entered.Wait(TimeSpan.FromSeconds(5)), "Pre-resume native query started");
    try
    {
        identity.Network = Wifi("AfterResume");
        monitor.Refresh(invalidatePending: true);
    }
    finally
    {
        release.Set();
    }
    WaitUntil(() => observations.Count > 0, "Fresh post-resume query completed");
    Require(observations.All(item => item.State.Network!.NetworkIdentifier == "AfterResume"), "Invalidated pre-resume detection never reaches session history");
    Equal("AfterResume", monitor.Current.Network!.NetworkIdentifier, "Current state uses a fresh native query");
}

static void FailedSessionTransition()
{
    using var test = new SessionTestContext();
    var network = Wifi("A");
    test.Tracker.ObserveConnection(Connected(network));
    var id = test.Tracker.Current!.Id;
    var missingProfile = new ConnectionProfile(999_999, "Missing ISP", "Missing device", test.Clock.GetUtcNow(), test.Clock.GetUtcNow());
    test.Clock.Advance(TimeSpan.FromSeconds(1));
    ExpectFailure(() => test.Tracker.ObserveConnection(Connected(Wifi("B"), missingProfile)), "Unknown profile rejects the atomic network switch");
    Equal(id, test.History.GetActiveSession()!.Id, "Failed switch rolls back closing the old session");
    Equal(1, test.History.GetRecentSessions().Count, "Failed switch creates no partial new session");
    Equal(1, test.History.GetRecentEvents().Count, "Failed switch creates no false event");
    Require(!test.Tracker.RecordSample(id, network.AdapterId, 1, test.Clock.GetUtcNow(), 1_000, 100), "Uncertain identity after failure pauses attribution");
    test.Tracker.ObserveConnection(Connected(network));
    test.Clock.Advance(TimeSpan.FromSeconds(1));
    Require(test.Tracker.RecordSample(id, network.AdapterId, 2, test.Clock.GetUtcNow(), 10, 1), "Successful physical observation restores attribution");
    ExpectFailure(() => test.Tracker.ObserveConnection(Connected(network, missingProfile)), "Unknown profile rejects active assignment");
    Require(test.History.GetActiveSession()!.ProfileId is null, "Failed assignment leaves the old profile association intact");
    test.Clock.Advance(TimeSpan.FromSeconds(1));
    Require(!test.Tracker.RecordSample(id, network.AdapterId, 3, test.Clock.GetUtcNow(), 1_000, 100), "Failed profile mutation also pauses uncertain attribution");
    test.Tracker.ObserveConnection(Connected(network));
    test.Tracker.Flush();
    Equal(10L, test.History.GetActiveSession()!.DownloadBytes, "No bytes are misattributed after failed mutations");
}

static void SessionSampleTimestamps()
{
    using var test = new SessionTestContext();
    var network = Wifi();
    test.Tracker.ObserveConnection(Connected(network));
    var id = test.Tracker.Current!.Id;
    var started = test.Clock.GetUtcNow();
    Require(!test.Tracker.RecordSample(id, network.AdapterId, 1, started.AddSeconds(-1), 1_000, 100), "Sample older than the session is rejected");
    test.Clock.Advance(TimeSpan.FromSeconds(1));
    var firstSample = test.Clock.GetUtcNow();
    Require(test.Tracker.RecordSample(id, network.AdapterId, 2, firstSample, 100, 10), "First current timestamp accepted");
    Require(!test.Tracker.RecordSample(id, network.AdapterId, 3, firstSample, 1_000, 100), "Duplicate timestamp with a new sequence is still rejected");
    Require(!test.Tracker.RecordSample(id, network.AdapterId, 4, started, 1_000, 100), "Out-of-order sample with a new sequence is rejected");
    test.Clock.Advance(TimeSpan.FromSeconds(1));
    Require(test.Tracker.RecordSample(id, network.AdapterId, 5, test.Clock.GetUtcNow(), 50, 5), "Fresh timestamp continues sampling");
    test.Tracker.Flush();
    Equal(150L, test.History.GetActiveSession()!.DownloadBytes, "Only distinct current samples contribute downloads");
    Equal(15L, test.History.GetActiveSession()!.UploadBytes, "Only distinct current samples contribute uploads");
}

static void SessionClockRollback()
{
    using var test = new SessionTestContext();
    var network = Wifi();
    test.Tracker.ObserveConnection(Connected(network));
    var id = test.Tracker.Current!.Id;
    test.Clock.Advance(TimeSpan.FromSeconds(5));
    var lastConfirmed = test.Clock.GetUtcNow();
    Require(test.Tracker.RecordSample(id, network.AdapterId, 1, lastConfirmed, 10, 1), "Confirmed sample before a system clock adjustment");
    test.Clock.Advance(TimeSpan.FromHours(-1));
    test.Tracker.ObserveConnection(Connected(network));
    test.Tracker.Close();
    var ended = test.History.GetRecentSessions().Single();
    Equal(lastConfirmed, ended.LastConfirmedAtUtc, "Wall-clock correction cannot move confirmation backwards");
    Equal(lastConfirmed, ended.EndedAtUtc!.Value, "Wall-clock correction cannot create an end before confirmed usage");
    Require(ended.EndedAtUtc >= ended.StartedAtUtc, "Session duration stays nonnegative");
}

static void InvalidVersionThreeConstraints()
{
    using var test = new SessionTestContext();
    var profile = test.Profiles.CreateProfile("DITO", "D2 Router");
    var network = Wifi();
    test.Profiles.AssignMapping(profile.Id, network.NetworkType, network.NetworkIdentifier);
    test.Tracker.ObserveConnection(Connected(network, profile));
    test.Usage.AddUsage(new DateOnly(2026, 1, 15), 2_048, 256);
    using (var connection = Open(test.DatabasePath))
        Execute(connection, "DROP INDEX UX_ConnectionSessions_Active;");

    ExpectStartupFailure(test.Root);

    Equal(5L, ReadInteger(test.DatabasePath, "PRAGMA user_version;"), "Damaged history schema is not reversioned");
    Equal(1L, ReadInteger(test.DatabasePath, "SELECT count(*) FROM ConnectionSessions;"), "Startup rejection preserves session rows");
    Equal(1L, ReadInteger(test.DatabasePath, "SELECT count(*) FROM ConnectionEvents;"), "Startup rejection preserves events");
    Equal(1L, ReadInteger(test.DatabasePath, "SELECT count(*) FROM ConnectionProfiles;"), "Startup rejection preserves profiles");
    Equal(1L, ReadInteger(test.DatabasePath, "SELECT count(*) FROM NetworkMappings;"), "Startup rejection preserves mappings");
    Equal(2_048L, ReadInteger(test.DatabasePath, "SELECT sum(downloaded) FROM daily_usage;"), "Startup rejection preserves existing daily usage");
    Equal(0L, ReadInteger(test.DatabasePath, "SELECT count(*) FROM sqlite_schema WHERE name='UX_ConnectionSessions_Active';"), "Damaged schema is rejected without unsolicited rebuilding");
}

static void DatabaseLeaseProtection()
{
    using var workspace = new TestWorkspace();
    _ = new UsageStore(workspace.Root);
    using (var activeRun = new DatabaseRunLease(workspace.CurrentPath))
    {
        var diagnostic = Task.Run(() =>
        {
            try
            {
                using var competingRun = new DatabaseRunLease(workspace.CurrentPath);
                return "";
            }
            catch (InvalidOperationException exception)
            {
                return exception.Message;
            }
        }).GetAwaiter().GetResult();
        Require(diagnostic.Contains("already monitoring", StringComparison.OrdinalIgnoreCase), "A competing run cannot recover the live run's active session");
    }
    using var subsequentRun = new DatabaseRunLease(workspace.CurrentPath);
}

static void FailedSleepPersistence()
{
    using var test = new SessionTestContext();
    var network = Wifi();
    test.Tracker.ObserveConnection(Connected(network));
    var id = test.Tracker.Current!.Id;
    using (var connection = Open(test.DatabasePath))
        Execute(connection, "CREATE TRIGGER fail_sleep BEFORE UPDATE OF EndedAtUtc ON ConnectionSessions WHEN NEW.EndReason='Sleep' BEGIN SELECT RAISE(ABORT, 'simulated sleep persistence failure'); END;");
    test.Clock.Advance(TimeSpan.FromMinutes(1));
    var sleepAt = test.Clock.GetUtcNow();
    ExpectFailure(test.Tracker.Sleep, "Simulated session closure write failure");
    Require(test.Tracker.IsSleeping, "Failed persistence still blocks samples during physical suspension");
    test.Clock.Advance(TimeSpan.FromHours(2));
    Require(!test.Tracker.RecordSample(id, network.AdapterId, 1, test.Clock.GetUtcNow(), 1_000, 100), "No suspended interval bytes enter the old session");
    test.Tracker.ObserveConnection(Connected(Wifi("QueuedNetwork")));
    Equal(id, test.Tracker.Current!.Id, "Queued observation cannot switch a suspended session");
    ExpectFailure(test.Tracker.Resume, "Resume retries the unresolved sleep boundary before accepting networks");
    Require(test.Tracker.IsSleeping, "Repeated write failure remains safely suspended");
    using (var connection = Open(test.DatabasePath))
        Execute(connection, "DROP TRIGGER fail_sleep;");
    test.Tracker.Resume();
    Require(test.Tracker.Current is null, "Successful retry closes the old sleep session before fresh detection");
    var ended = test.History.GetRecentSessions().Single();
    Equal(sleepAt, ended.EndedAtUtc!.Value, "Retry records original sleep time instead of resume time");
    Equal("Sleep", ended.EndReason, "Retry preserves the original sleep cause");
    test.Tracker.ObserveConnection(Connected(network));
    Require(test.Tracker.Current!.Id != id, "Fresh resumed network starts a different session");
    Equal(1, test.History.GetRecentEvents().Count(item => item.EventType == "Sleep"), "Failed transactions do not duplicate Sleep events");
    Equal(1, test.History.GetRecentEvents().Count(item => item.EventType == "Resume"), "Successful resume produces one event");
}

static void IdleSessionConfirmation()
{
    using var test = new SessionTestContext();
    var network = Wifi();
    test.Tracker.ObserveConnection(Connected(network));
    var id = test.Tracker.Current!.Id;
    test.Clock.Advance(TimeSpan.FromSeconds(30));
    var confirmed = test.Clock.GetUtcNow();
    Require(test.Tracker.RecordSample(id, network.AdapterId, 1, confirmed, 0, 0), "An idle sample still confirms the physical observation");
    test.Tracker.Flush();
    Equal(confirmed, test.History.GetActiveSession()!.LastConfirmedAtUtc, "Idle liveness persists periodically");
    Equal(0L, test.History.GetActiveSession()!.DownloadBytes, "Idle sample invents no downloads");
    Equal(0L, test.History.GetActiveSession()!.UploadBytes, "Idle sample invents no uploads");
    test.Clock.Advance(TimeSpan.FromHours(1));
    _ = new ConnectionSessionTracker(new ConnectionHistoryStore(test.DatabasePath), test.Clock);
    Equal(confirmed, test.History.GetRecentSessions().Single().EndedAtUtc!.Value, "Idle unexpected exit still uses the last confirmed observation");
}

static void SameThreadDatabaseLease()
{
    using var workspace = new TestWorkspace();
    _ = new UsageStore(workspace.Root);
    var lease = new DatabaseRunLease(workspace.CurrentPath);
    try
    {
        var failure = ExpectFailure(() =>
        {
            using var duplicate = new DatabaseRunLease(workspace.CurrentPath.ToUpperInvariant());
        }, "Same-thread duplicate lease must not bypass the process guard through mutex reentrancy");
        Require(failure.Message.Contains("already monitoring", StringComparison.OrdinalIgnoreCase), "Duplicate lease provides a useful already-running diagnostic");
        using var independent = new DatabaseRunLease(workspace.LegacyPath);
    }
    finally
    {
        lease.Dispose();
        lease.Dispose();
    }
    using var reopened = new DatabaseRunLease(workspace.CurrentPath);
}

static void DatabaseLeaseBeforeInitialization()
{
    using var workspace = new TestWorkspace();
    Require(!File.Exists(workspace.CurrentPath), "Fixture must begin without a SQLite database");
    using var owner = new DatabaseRunLease(workspace.CurrentPath);
    Require(owner.IsHeld, "The first startup owns its lease before database initialization");
    var failure = ExpectFailure(() => _ = new UsageStore(workspace.Root), "Unowned startup must fail before SQLite creation");
    Require(failure.Message.Contains("already monitoring", StringComparison.OrdinalIgnoreCase), "Competing startup provides a useful ownership diagnostic");
    Require(!File.Exists(workspace.CurrentPath), "Rejected startup must not create, migrate or open the database");
    var usage = new UsageStore(workspace.Root, owner);
    Equal(workspace.CurrentPath, usage.DatabasePath, "Held initialization ownership points to the same database");
    Equal(5L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Owned startup initializes schema v5");
    Require(owner.IsHeld, "Initialization does not release the caller's run ownership");
}

static void CrossProcessDatabaseLease()
{
    using var workspace = new TestWorkspace();
    using var owner = new LeaseChildProcess(workspace.Root, "hold");
    Equal("READY", owner.ReadLine(), "The first independent process acquired and persisted its active session");
    var history = new ConnectionHistoryStore(workspace.CurrentPath);
    var first = history.GetActiveSession()!;
    Require(first is not null, "Owner's active session exists");
    SqliteConnection.ClearAllPools();
    var originalHash = Hash(workspace.CurrentPath);

    using (var competing = new LeaseChildProcess(workspace.Root, "start"))
    {
        Equal("BLOCKED", competing.ReadLine(), "Second independent process is rejected before initialization/recovery");
        competing.ExpectExit(0);
    }
    Equal(originalHash, Hash(workspace.CurrentPath), "Rejected second startup leaves the entire database unchanged");
    Equal(first, history.GetActiveSession(), "The second process cannot modify the first process's active session");
    Equal(0, history.GetRecentEvents().Count(item => item.EventType == "SessionRecoveredAfterUnexpectedExit"), "Live ownership never generates a recovery event");

    owner.Release();
    Equal("RELEASED", owner.ReadLine(), "Normal shutdown closes the session and releases ownership");
    owner.ExpectExit(0);
    Equal("ApplicationExit", history.GetRecentSessions().Single().EndReason, "Normal owner shutdown closes cleanly");
    using var next = new LeaseChildProcess(workspace.Root, "start");
    Equal("ACQUIRED", next.ReadLine(), "The next independent process can acquire immediately after release");
    next.ExpectExit(0);
    Equal(2, history.GetRecentSessions().Count, "The next run creates its own session");
    Require(history.GetActiveSession() is null, "Both graceful child runs close their own sessions");
}

static void CrashedProcessDatabaseLease()
{
    using var workspace = new TestWorkspace();
    using var owner = new LeaseChildProcess(workspace.Root, "hold");
    Equal("READY", owner.ReadLine(), "Crash fixture owns a persisted session");
    var history = new ConnectionHistoryStore(workspace.CurrentPath);
    var stale = history.GetActiveSession()!;
    owner.KillOwnedProcess();
    Require(File.Exists(workspace.CurrentPath + ".nettrace.lock"), "Crash leaves a harmless lock-file entry on disk");
    Equal(stale, history.GetActiveSession(), "Killing an owner bypasses graceful session cleanup");

    using (var next = new LeaseChildProcess(workspace.Root, "start"))
    {
        Equal("ACQUIRED", next.ReadLine(), "The OS releases exclusive ownership when the owner process is killed");
        next.ExpectExit(0);
    }
    var recovered = history.GetRecentSessions().Single(item => item.Id == stale.Id);
    Equal(stale.LastConfirmedAtUtc, recovered.EndedAtUtc!.Value, "Recovery closes at the last persisted confirmation");
    Equal("UnexpectedExit", recovered.EndReason, "Hard-killed session has an explicit recovery reason");
    Equal(stale.DownloadBytes, recovered.DownloadBytes, "Persisted session downloads survive process death");
    Equal(stale.UploadBytes, recovered.UploadBytes, "Persisted session uploads survive process death");
    Equal(1, history.GetRecentEvents().Count(item => item.EventType == "SessionRecoveredAfterUnexpectedExit"), "Recovery is recorded exactly once");
    Require(history.GetActiveSession() is null, "A subsequent graceful exit leaves no active session");
    using var final = new DatabaseRunLease(workspace.CurrentPath);
    Require(final.IsHeld, "Neither the killed process nor its stale sidecar can permanently lock out startup");
}

static void StaleDatabaseLeaseFile()
{
    using var workspace = new TestWorkspace();
    Directory.CreateDirectory(Path.GetDirectoryName(workspace.CurrentPath)!);
    var lockPath = workspace.CurrentPath + ".nettrace.lock";
    File.WriteAllText(lockPath, "stale owner metadata must never grant or block ownership");
    using (var lease = new DatabaseRunLease(workspace.CurrentPath))
    {
        Require(lease.IsHeld, "An unheld preexisting lock file is reusable");
        var store = new UsageStore(workspace.Root, lease);
        Equal(5L, ReadInteger(store.DatabasePath, "PRAGMA user_version;"), "Stale sidecar does not block fresh database startup");
    }
    Require(File.Exists(lockPath), "Shutdown does not delete and recreate the shared lock-file name");
    using var next = new DatabaseRunLease(workspace.CurrentPath);
    Require(next.IsHeld, "A retained lock file can be acquired again");
}

static void CrossThreadDatabaseLeaseDisposal()
{
    using var workspace = new TestWorkspace();
    var lease = new DatabaseRunLease(workspace.CurrentPath);
    try
    {
        Task.Run(lease.Dispose).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        Require(!lease.IsHeld, "A different thread can release process-lifetime ownership");
        lease.Dispose();
        ExpectFailure(() => lease.EnsureOwns(workspace.CurrentPath), "Released lease cannot authorize initialization");
        using var replacement = new DatabaseRunLease(workspace.CurrentPath);
        Require(replacement.IsHeld, "Cross-thread release permits immediate reacquisition");
    }
    finally { lease.Dispose(); }
}

static void DatabaseLeaseOwnershipValidation()
{
    using var first = new TestWorkspace();
    using var second = new TestWorkspace();
    using var owner = new DatabaseRunLease(first.CurrentPath);
    ExpectFailure(() => _ = new UsageStore(second.Root, owner), "A different database's lease must be rejected");
    Require(!File.Exists(second.CurrentPath), "Wrong-path ownership must not create or migrate SQLite");
    Require(owner.IsHeld, "Rejected wrong-path initialization does not dispose the rightful owner's lease");
    owner.Dispose();
    ExpectFailure(() => _ = new UsageStore(first.Root, owner), "Disposed ownership must be rejected");
    Require(!File.Exists(first.CurrentPath), "Disposed ownership must not create or migrate SQLite");
    using var replacement = new DatabaseRunLease(first.CurrentPath);
    Require(replacement.IsHeld, "Ownership verification failures do not leave process-local poison state");
}

static void FailedInitializationReleasesLease()
{
    using var workspace = new TestWorkspace();
    Directory.CreateDirectory(Path.GetDirectoryName(workspace.CurrentPath)!);
    using (var connection = Open(workspace.CurrentPath))
        Execute(connection, "CREATE TABLE unrelated(value TEXT); INSERT INTO unrelated VALUES('preserve'); PRAGMA user_version=1;");
    var originalHash = Hash(workspace.CurrentPath);
    ExpectStartupFailure(workspace.Root);
    Equal(originalHash, Hash(workspace.CurrentPath), "Failed initialization preserves the incompatible database");
    using var retry = new DatabaseRunLease(workspace.CurrentPath);
    Require(retry.IsHeld, "A failed initializer releases ownership without waiting for process termination");
}

static void HistoryQueryIndices()
{
    using var test = new SessionTestContext();
    test.Tracker.ObserveConnection(Connected(Wifi()));
    using var connection = Open(test.DatabasePath);
    var cases = new (string Sql, string Index)[]
    {
        ("SELECT Id FROM ConnectionSessions WHERE EndedAtUtc IS NULL", "UX_ConnectionSessions_Active"),
        ("SELECT Id FROM ConnectionSessions ORDER BY StartedAtUtc DESC,Id DESC LIMIT 100", "IX_ConnectionSessions_StartedAtUtc"),
        ("SELECT Id FROM ConnectionSessions WHERE ProfileId=1 ORDER BY StartedAtUtc DESC,Id DESC LIMIT 100", "IX_ConnectionSessions_ProfileId"),
        ("SELECT Id FROM ConnectionSessions WHERE StartedAtUtc < '2027-01-01' AND COALESCE(EndedAtUtc,LastConfirmedAtUtc)>='2026-01-01' ORDER BY StartedAtUtc DESC,Id DESC", "IX_ConnectionSessions_StartedAtUtc"),
        ("SELECT Id FROM ConnectionEvents ORDER BY OccurredAtUtc DESC,Id DESC LIMIT 100", "IX_ConnectionEvents_OccurredAtUtc"),
        ("SELECT Id FROM ConnectionEvents WHERE SessionId=1", "IX_ConnectionEvents_SessionId"),
        ("SELECT Id FROM ConnectionEvents WHERE ProfileId=1", "IX_ConnectionEvents_ProfileId")
    };
    foreach (var (sql, index) in cases)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        using var reader = command.ExecuteReader();
        var details = new List<string>();
        while (reader.Read()) details.Add(reader.GetString(3));
        Require(details.Any(detail => detail.Contains(index, StringComparison.Ordinal)), $"SQLite uses {index} for its common history access path: {string.Join("; ", details)}");
    }
}

static void ExistingVersionThreeIndices()
{
    using var workspace = new TestWorkspace();
    CreateVersionThreeFixture(workspace.CurrentPath);
    var history = new ConnectionHistoryStore(workspace.CurrentPath);
    var originalSession = history.GetActiveSession();
    var originalEvents = history.GetRecentEvents().ToArray();
    using (var connection = Open(workspace.CurrentPath))
        Execute(connection, "DROP INDEX IX_ConnectionEvents_SessionId; DROP INDEX IX_ConnectionEvents_ProfileId;");
    _ = new UsageStore(workspace.Root);
    Equal(5L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Historical v3 compatibility maintenance reaches current schema v5");
    Equal(originalSession, history.GetActiveSession(), "Index maintenance does not recover or rewrite active session rows");
    Require(originalEvents.SequenceEqual(history.GetRecentEvents()), "Index maintenance does not rewrite event history");
    Equal(2L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM sqlite_schema WHERE name IN ('IX_ConnectionEvents_SessionId','IX_ConnectionEvents_ProfileId') AND type='index';"), "Both missing event relationship indices are restored");
}

static void MalformedHistoryIndices()
{
    foreach (var replacement in new[]
    {
        "DROP INDEX IX_ConnectionEvents_SessionId; CREATE INDEX IX_ConnectionEvents_SessionId ON ConnectionEvents(ProfileId);",
        "DROP INDEX IX_ConnectionSessions_StartedAtUtc; CREATE INDEX IX_ConnectionSessions_StartedAtUtc ON ConnectionSessions(StartedAtUtc ASC,Id DESC);",
        "DROP INDEX IX_ConnectionSessions_ProfileId; CREATE INDEX IX_ConnectionSessions_ProfileId ON ConnectionSessions(ProfileId,Id DESC);"
    })
    {
        using var test = new SessionTestContext();
        test.Tracker.ObserveConnection(Connected(Wifi()));
        var active = test.History.GetActiveSession();
        using (var connection = Open(test.DatabasePath))
            Execute(connection, "DROP INDEX IX_ConnectionEvents_ProfileId;" + replacement);
        var originalHash = Hash(test.DatabasePath);
        ExpectStartupFailure(test.Root);
        Equal(originalHash, Hash(test.DatabasePath), "Rejected index definitions roll back all attempted index creation");
        Equal(0L, ReadInteger(test.DatabasePath, "SELECT count(*) FROM sqlite_schema WHERE name='IX_ConnectionEvents_ProfileId';"), "Failed validation does not publish an unrelated index repair");
        Equal(active, test.History.GetActiveSession(), "Rejected index definitions preserve the active session");
        Equal(5L, ReadInteger(test.DatabasePath, "PRAGMA user_version;"), "Rejected index definitions preserve schema version");
    }
}

static VpnInterfaceEvidence VpnInterface() => new()
{
    InterfaceIndex = 42,
    InterfaceGuid = new Guid("825018c6-06a8-4721-9b04-9aedfbf70d23"),
    InterfaceName = "Renamed work connection",
    Description = "Native tunnel device",
    InterfaceType = 131,
    TunnelType = 1,
    OperationalStatus = OperationalStatus.Up,
    MediaConnectState = 1,
    StatusFlags = 0
};

static VpnInterfaceInventory VpnInventory(params VpnInterfaceEvidence[] interfaces) =>
    new(interfaces, Array.Empty<VpnRasConnectionEvidence>());

static void VpnNoAdapters()
{
    var state = VpnDetectionService.Classify(VpnInventory());
    Equal(VpnStatus.NoVpn, state.Status, "No native interfaces means no detected tunnel");
    Equal(0, state.Interfaces.Count, "Empty inventory produces no fabricated interface");
}

static void VpnDormantAdapter()
{
    foreach (var status in new[] { OperationalStatus.Down, OperationalStatus.Dormant,
        OperationalStatus.LowerLayerDown, OperationalStatus.NotPresent })
    {
        var evidence = VpnInterface() with { InterfaceType = 6, DriverComponentId = "wintun", OperationalStatus = status };
        var state = VpnDetectionService.Classify(VpnInventory(evidence));
        Equal(VpnStatus.NoVpn, state.Status, $"A {status} VPN adapter is not connected");
        Equal(0, state.Interfaces.Count, "Dormant adapter does not appear among active candidates");
    }

    var disconnected = VpnInterface() with { MediaConnectState = 2 };
    Equal(VpnStatus.NoVpn, VpnDetectionService.Classify(VpnInventory(disconnected)).Status,
        "Disconnected media overrides operational Up");
    foreach (byte flags in new byte[] { 2, 16, 32, 64, 128 })
    {
        var excluded = VpnInterface() with { StatusFlags = flags };
        Equal(VpnStatus.NoVpn, VpnDetectionService.Classify(VpnInventory(excluded)).Status,
            $"Filter, disconnected-media and endpoint flags cannot create a tunnel: {flags}");
    }
}

static void VpnActiveTunnel()
{
    foreach (uint tunnelType in new uint[] { 1, 2, 15 })
    {
        var evidence = VpnInterface() with { TunnelType = tunnelType };
        var state = VpnDetectionService.Classify(VpnInventory(evidence));
        Equal(VpnStatus.TunnelDetected, state.Status, $"Connected native tunnel type {tunnelType}");
        Equal(1, state.Interfaces.Count, "Exactly one active native candidate");
        var candidate = state.Interfaces[0];
        Equal<uint?>(evidence.InterfaceIndex, candidate.InterfaceIndex, "Native interface index is preserved");
        Equal<Guid?>(evidence.InterfaceGuid, candidate.InterfaceGuid, "Native interface GUID is preserved");
        Equal(evidence.InterfaceName, candidate.InterfaceName, "Native interface alias is preserved");
        Equal(evidence.Description, candidate.Description, "Native interface description is preserved");
        Equal<uint?>(evidence.InterfaceType, candidate.InterfaceType, "Native interface type is preserved");
        Equal<uint?>(evidence.TunnelType, candidate.TunnelType, "Native tunnel type is preserved");
        Equal<OperationalStatus?>(evidence.OperationalStatus, candidate.OperationalStatus, "Native operational status is preserved");
        Require(!string.IsNullOrWhiteSpace(candidate.Evidence), "Candidate retains its recognition evidence.");
    }
}

static void VpnOrdinaryVirtualAdapters()
{
    var inventory = VpnInventory(
        VpnInterface() with { InterfaceType = 6, Description = "Hyper-V Virtual Ethernet Adapter", DriverComponentId = "vms_mp", DriverService = "VMSMP" },
        VpnInterface() with { InterfaceIndex = 43, InterfaceType = 6, Description = "VMware Virtual Ethernet Adapter", DriverComponentId = "vmware_virtual_ethernet", DriverService = "VMnetAdapter" },
        VpnInterface() with { InterfaceIndex = 44, InterfaceType = 6, Description = "VirtualBox Host-Only Ethernet Adapter", DriverComponentId = "sun_vboxnetadp", DriverService = "VBoxNetAdp" },
        VpnInterface() with { InterfaceIndex = 45, InterfaceType = 6, Description = "Ordinary virtual switch", DriverComponentId = "ordinary_virtual_adapter" },
        VpnInterface() with { InterfaceIndex = 46, InterfaceType = 24, Description = "Software Loopback Interface", DriverComponentId = "wintun" });
    var state = VpnDetectionService.Classify(inventory);
    Equal(VpnStatus.NoVpn, state.Status, "Common virtualization and loopback devices are not tunnels");
    Equal(0, state.Interfaces.Count, "Ordinary virtual devices do not create VPN candidates");
}

static void VpnDriverCandidates()
{
    foreach (var component in new[] { "wintun", "wireguard", "tap0901", "ovpn-dco", "WiNtUn" })
    {
        var evidence = VpnInterface() with { InterfaceType = 6, TunnelType = 0,
            InterfaceName = "User renamed this adapter", Description = "User-facing generic description",
            DriverComponentId = component };
        var state = VpnDetectionService.Classify(VpnInventory(evidence));
        Equal(VpnStatus.TunnelDetected, state.Status, $"Exact {component} native component identity");
        Equal(1, state.Interfaces.Count, "Driver evidence yields one candidate");
    }

    var serviceEvidence = VpnInterface() with { InterfaceType = 6, DriverService = "WireGuard" };
    Equal(VpnStatus.TunnelDetected, VpnDetectionService.Classify(VpnInventory(serviceEvidence)).Status,
        "Exact native driver service recognizes an adapter without a component ID");
    var nearMatch = VpnInterface() with { InterfaceType = 6, DriverComponentId = "not-wintun", DriverService = "wireguard-helper" };
    Equal(VpnStatus.NoVpn, VpnDetectionService.Classify(VpnInventory(nearMatch)).Status,
        "A substring or similarly named service does not prove a VPN driver");
}

static void VpnNameOnlyHints()
{
    foreach (var hint in new[] { "VPN", "WireGuard", "OpenVPN", "TAP-Windows Adapter V9", "My corporate tunnel" })
    {
        var evidence = VpnInterface() with { InterfaceType = 6, InterfaceName = hint,
            Description = hint, DriverComponentId = "ordinary_virtual_adapter" };
        Equal(VpnStatus.NoVpn, VpnDetectionService.Classify(VpnInventory(evidence)).Status,
            $"Friendly alias and description '{hint}' are not authoritative VPN evidence");
    }
}

static void VpnHardwareNotCandidate()
{
    foreach (uint type in new uint[] { 6, 71 })
    {
        var evidence = VpnInterface() with { InterfaceType = type, StatusFlags = 1,
            InterfaceName = "VPN", Description = "WireGuard", DriverComponentId = "wireguard" };
        Equal(VpnStatus.NoVpn, VpnDetectionService.Classify(VpnInventory(evidence)).Status,
            "Physical hardware identity overrides misleading VPN metadata");
    }
}

static void VpnTransitionTunnels()
{
    foreach (uint tunnelType in new uint[] { 11, 13, 14 })
    {
        var evidence = VpnInterface() with { TunnelType = tunnelType, InterfaceName = "VPN", Description = "VPN" };
        Equal(VpnStatus.NoVpn, VpnDetectionService.Classify(VpnInventory(evidence)).Status,
            $"6to4, ISATAP and Teredo are IPv6 transition mechanisms: {tunnelType}");
    }
}

static void VpnOpaqueTunnel()
{
    var state = VpnDetectionService.Classify(VpnInventory(VpnInterface() with { TunnelType = 0 }));
    Equal(VpnStatus.Indeterminate, state.Status, "An active unknown native tunnel is not guessed to be a VPN");
    Require(!string.IsNullOrWhiteSpace(state.Message), "An uncertain tunnel has a useful diagnostic.");
}

static void VpnRasConnected()
{
    var inventory = new VpnInterfaceInventory(Array.Empty<VpnInterfaceEvidence>(),
        new[] { new VpnRasConnectionEvidence("Work VPN", "WAN Miniport (IKEv2)", "vpn", 0x2000) });
    var state = VpnDetectionService.Classify(inventory);
    Equal(VpnStatus.TunnelDetected, state.Status, "RAS Connected plus VPN device type is positive native evidence");
    Equal(1, state.Interfaces.Count, "One connected RAS VPN candidate");
    var candidate = state.Interfaces[0];
    Equal("Work VPN", candidate.InterfaceName, "RAS connection name is preserved");
    Equal("WAN Miniport (IKEv2)", candidate.Description, "RAS native device name is preserved");
    Require(candidate.InterfaceIndex is null && candidate.InterfaceGuid is null && candidate.InterfaceType is null &&
        candidate.TunnelType is null && candidate.OperationalStatus is null,
        "RAS entry GUID, logon LUID and connection state cannot masquerade as native interface fields.");

    var failedConnection = new VpnInterfaceInventory(Array.Empty<VpnInterfaceEvidence>(),
        new[] { new VpnRasConnectionEvidence("Work VPN", "WAN Miniport (IKEv2)", "vpn", 0x2000, 691) });
    Equal(VpnStatus.Indeterminate, VpnDetectionService.Classify(failedConnection).Status,
        "RAS connection errors prevent an apparently Connected state from proving a live VPN");
}

static void VpnRasDormant()
{
    foreach (uint connectionState in new uint[] { 0, 0x1000, 0x2001 })
    {
        var inventory = new VpnInterfaceInventory(Array.Empty<VpnInterfaceEvidence>(),
            new[] { new VpnRasConnectionEvidence("Installed VPN", "WAN Miniport (IKEv2)", "vpn", connectionState) });
        Equal(VpnStatus.NoVpn, VpnDetectionService.Classify(inventory).Status,
            $"Only RAS Connected, not an existing or transitioning entry, creates a tunnel: {connectionState}");
    }
}

static void VpnPppNotAssumed()
{
    var inventory = new VpnInterfaceInventory(new[] { VpnInterface() with { InterfaceType = 23, TunnelType = 0 } },
        new[] { new VpnRasConnectionEvidence("VPN named dial-up", "Phone modem", "modem", 0x2000),
            new VpnRasConnectionEvidence("VPN named direct link", "Serial cable", "direct", 0x2000) });
    var state = VpnDetectionService.Classify(inventory);
    Require(state.Status != VpnStatus.TunnelDetected, "PPP and non-VPN RAS connections cannot prove a VPN.");
    Equal(0, state.Interfaces.Count, "PPP/dial-up without positive VPN evidence produces no candidate");
}

static void VpnIncompleteInventory()
{
    var inventory = new VpnInterfaceInventory(Array.Empty<VpnInterfaceEvidence>(),
        Array.Empty<VpnRasConnectionEvidence>(), false, "Windows denied native interface metadata access.");
    var state = VpnDetectionService.Classify(inventory);
    Equal(VpnStatus.Indeterminate, state.Status, "Incomplete native observations cannot confidently rule out VPNs");
    Require(state.Message?.Contains("denied", StringComparison.OrdinalIgnoreCase) == true,
        "The original native diagnostic remains available.");
    Equal(0, state.Interfaces.Count, "Failure does not fabricate an interface");

    var partiallyObservedTunnel = inventory with { Interfaces = new[] { VpnInterface() } };
    Equal(VpnStatus.TunnelDetected, VpnDetectionService.Classify(partiallyObservedTunnel).Status,
        "An independent positive native observation remains usable despite another inventory failure");
}

static void VpnApiFailure()
{
    var source = new FakeVpnInterfaceSource { Failure = new System.ComponentModel.Win32Exception(5, "Native inventory denied") };
    var service = new VpnDetectionService(source);
    var state = service.GetCurrentState();
    Equal(VpnStatus.Indeterminate, state.Status, "A native API failure cannot report NoVpn");
    Equal(0, state.Interfaces.Count, "Failed API does not invent VPN candidates");
    Require(!string.IsNullOrWhiteSpace(state.Message), "Native API failure has a useful diagnostic.");
    Equal(1, source.Reads, "A state query reads the native source once");
}

static void VpnStateRefresh()
{
    var source = new FakeVpnInterfaceSource { Inventory = VpnInventory(VpnInterface()) };
    var service = new VpnDetectionService(source);
    Equal(VpnStatus.TunnelDetected, service.GetCurrentState().Status, "Initially connected native tunnel");
    source.Inventory = VpnInventory(VpnInterface() with { OperationalStatus = OperationalStatus.Down });
    Equal(VpnStatus.NoVpn, service.GetCurrentState().Status, "A subsequent query observes the disconnected interface");
    source.Failure = new System.ComponentModel.Win32Exception(5);
    Equal(VpnStatus.Indeterminate, service.GetCurrentState().Status, "Later native failure cannot reuse stale detection");
    source.Failure = null;
    source.Inventory = VpnInventory();
    Equal(VpnStatus.NoVpn, service.GetCurrentState().Status, "Detection recovers after a transient native failure");
    Equal(4, source.Reads, "Each query reads fresh evidence independently");
}

static void VpnUnconfirmedStatus()
{
    var unknownOperation = VpnInterface() with { InterfaceType = 6, DriverComponentId = "wintun", OperationalStatus = OperationalStatus.Unknown };
    Equal(VpnStatus.Indeterminate, VpnDetectionService.Classify(VpnInventory(unknownOperation)).Status,
        "Unknown operational status on a known VPN device cannot prove connection or disconnection");
    var unknownMedia = VpnInterface() with { MediaConnectState = 0 };
    Equal(VpnStatus.Indeterminate, VpnDetectionService.Classify(VpnInventory(unknownMedia)).Status,
        "An opaque media state on a native tunnel cannot prove that the tunnel is connected");
    var unauthenticated = VpnInterface() with { StatusFlags = 8 };
    Equal(VpnStatus.Indeterminate, VpnDetectionService.Classify(VpnInventory(unauthenticated)).Status,
        "An unauthenticated native tunnel cannot prove a live connection");
    var malformedMedia = VpnInterface() with { InterfaceType = 6, DriverComponentId = "wintun", MediaConnectState = 99 };
    Equal(VpnStatus.Indeterminate, VpnDetectionService.Classify(VpnInventory(malformedMedia)).Status,
        "A known tunnel driver cannot make an invalid native media state trustworthy");
}

static void VpnTapAlwaysConnected()
{
    var evidence = VpnInterface() with { InterfaceType = 6, TunnelType = 0, DriverComponentId = "tap0901", TapMediaAlwaysConnected = true };
    var state = VpnDetectionService.Classify(VpnInventory(evidence));
    Equal(VpnStatus.Indeterminate, state.Status,
        "An always-connected TAP setting cannot distinguish a dormant adapter from an active VPN");
    Equal(1, state.Interfaces.Count, "Indeterminate state retains the unconfirmed native TAP metadata");
    Require(state.Interfaces[0].Evidence.Contains("Always Connected", StringComparison.OrdinalIgnoreCase),
        "TAP ambiguity is explained without pretending an active VPN was found.");
}

static void VpnRasConnectionSubstates()
{
    foreach (uint substate in new uint[] { 1, 2, 99 })
    {
        var inventory = new VpnInterfaceInventory(Array.Empty<VpnInterfaceEvidence>(),
            new[] { new VpnRasConnectionEvidence("Work VPN", "WAN Miniport (IKEv2)", "vpn", 0x2000,
                ConnectionSubstate: substate) });
        var state = VpnDetectionService.Classify(inventory);
        Equal(VpnStatus.Indeterminate, state.Status,
            $"RAS Connected with dormant, reconnecting or unknown substate is not a live VPN claim: {substate}");
    }

    foreach (uint substate in new uint[] { 0, 0x2000 })
    {
        var inventory = new VpnInterfaceInventory(Array.Empty<VpnInterfaceEvidence>(),
            new[] { new VpnRasConnectionEvidence("Work VPN", "WAN Miniport (IKEv2)", "vpn", 0x2000,
                ConnectionSubstate: substate) });
        Equal(VpnStatus.TunnelDetected, VpnDetectionService.Classify(inventory).Status,
            $"RAS Connected with no substate or Reconnected confirms a local tunnel: {substate}");
    }
}

static VpnRouteEvidence VpnRoute(string prefix, byte length, uint index = 42,
    uint routeMetric = 1, uint? interfaceMetric = 0)
{
    var address = IPAddress.Parse(prefix);
    return new(index, index == 42 ? VpnInterface().InterfaceGuid
            : new Guid("4b62f267-c1ad-4fdc-bc2a-b09daff48690"), address, length,
        address.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any,
        routeMetric, interfaceMetric, true);
}

static VpnState DetectedVpn() => VpnDetectionService.Classify(VpnInventory(VpnInterface()));

static VpnTunnelRoutingResult VerifyVpnRoutes(IReadOnlyList<VpnRouteEvidence> ipv4,
    IReadOnlyList<VpnRouteEvidence>? ipv6 = null)
{
    var state = VpnRouteVerifier.Verify(DetectedVpn(),
        new(AddressFamily.InterNetwork, ipv4),
        new(AddressFamily.InterNetworkV6, ipv6 ?? new[] { VpnRoute("::", 0) }));
    Equal(1, state.Tunnels.Count, "One detected interface retains one verification result");
    return state.Tunnels[0];
}

static void VpnRoutesMissing()
{
    var result = VerifyVpnRoutes(Array.Empty<VpnRouteEvidence>(), Array.Empty<VpnRouteEvidence>());
    Equal(VpnRoutingMode.Unverified, result.IPv4.Mode, "No IPv4 route proof");
    Equal(VpnRoutingMode.Unverified, result.IPv6.Mode, "No IPv6 route proof");
    Equal(VpnRoutingMode.Unverified, result.Mode, "Detection alone cannot establish routing mode");
    Equal(IpFamilyAvailability.Unavailable, result.IPv4.Availability, "A complete empty IPv4 inventory proves absence");
    Equal(IpFamilyAvailability.Unavailable, result.IPv6.Availability, "A complete empty IPv6 inventory proves absence");
    Require(!string.IsNullOrWhiteSpace(result.IPv4.Reason), "Missing-route result explains uncertainty.");
}

static void VpnRoutesFullDefaults()
{
    var result = VerifyVpnRoutes(new[] { VpnRoute("0.0.0.0", 0), VpnRoute("0.0.0.0", 0, 7, 100) },
        new[] { VpnRoute("::", 0), VpnRoute("::", 0, 7, 100) });
    Equal(VpnRoutingMode.FullTunnel, result.IPv4.Mode, "The effective IPv4 default uses the detected interface");
    Equal(VpnRoutingMode.FullTunnel, result.IPv6.Mode, "The effective IPv6 default uses the detected interface");
    Equal(VpnRoutingMode.FullTunnel, result.Mode, "Both families must be verified full for aggregate FullTunnel");
    Require(result.IPv4.Routes.Any(route => route.InterfaceIndex == 42 && route.PrefixLength == 0),
        "Verification retains the native IPv4 evidence.");
    Equal(DetectedVpn().Interfaces[0], result.Tunnel, "Detection identity remains unchanged by route verification");
}

static void VpnRoutesSplitPrefixes()
{
    var result = VerifyVpnRoutes(new[] { VpnRoute("0.0.0.0", 0, 7), VpnRoute("10.0.0.0", 8) });
    Equal(VpnRoutingMode.SplitTunnel, result.IPv4.Mode,
        "A corporate VPN prefix and general physical default prove split IPv4 routing");
    Equal(VpnRoutingMode.SplitTunnel, result.Mode, "One split family with another verified full family remains split");
}

static void VpnRoutesCombinedMetrics()
{
    var vpn = VpnRoute("0.0.0.0", 0, routeMetric: 1, interfaceMetric: 100);
    var physical = VpnRoute("0.0.0.0", 0, 7, 10, 1);
    Equal<ulong?>(101, vpn.EffectiveMetric, "Route cost includes the VPN interface metric");
    Equal<ulong?>(11, physical.EffectiveMetric, "Route cost includes the competing physical interface metric");
    Equal(VpnRoutingMode.Unverified, VerifyVpnRoutes(new[] { vpn, physical }).IPv4.Mode,
        "A losing VPN default alone does not prove VPN traffic");
    var result = VerifyVpnRoutes(new[] { vpn with { InterfaceMetric = 1 }, physical });
    Equal(VpnRoutingMode.FullTunnel, result.IPv4.Mode, "Changing interface cost changes the effective default");
}

static void VpnRoutesHalfDefaults()
{
    var result = VerifyVpnRoutes(new[] { VpnRoute("0.0.0.0", 1, routeMetric: 100),
        VpnRoute("128.0.0.0", 1, routeMetric: 100), VpnRoute("0.0.0.0", 0, 7, 1) },
        new[] { VpnRoute("::", 1, routeMetric: 100), VpnRoute("8000::", 1, routeMetric: 100), VpnRoute("::", 0, 7, 1) });
    Equal(VpnRoutingMode.FullTunnel, result.IPv4.Mode, "Paired IPv4 /1 routes outrank a cheaper physical /0");
    Equal(VpnRoutingMode.FullTunnel, result.IPv6.Mode, "Paired IPv6 /1 routes outrank a cheaper physical /0");
    Equal(VpnRoutingMode.FullTunnel, result.Mode, "Equivalent paired defaults cover both families");
}

static void VpnRoutesIndependentFamilies()
{
    var result = VerifyVpnRoutes(new[] { VpnRoute("0.0.0.0", 0) },
        new[] { VpnRoute("::", 0, 7), VpnRoute("2606:4700::", 32) });
    Equal(VpnRoutingMode.FullTunnel, result.IPv4.Mode, "IPv4 route analysis is independent");
    Equal(VpnRoutingMode.SplitTunnel, result.IPv6.Mode, "IPv6 specific prefixes do not borrow IPv4 defaults");
    Equal(VpnRoutingMode.SplitTunnel, result.Mode, "Different verified family modes produce aggregate split routing");
    var missingV6 = VerifyVpnRoutes(new[] { VpnRoute("0.0.0.0", 0) }, Array.Empty<VpnRouteEvidence>());
    Equal(VpnRoutingMode.FullTunnel, missingV6.IPv4.Mode, "Missing IPv6 proof does not erase IPv4 evidence");
    Equal(IpFamilyAvailability.Unavailable, missingV6.IPv6.Availability, "Complete empty IPv6 evidence proves no usable family");
    Equal(VpnRoutingMode.FullTunnel, missingV6.Mode, "A proven unavailable family does not defeat verified IPv4 full routing");
}

static void VpnRoutesMetricTies()
{
    var result = VerifyVpnRoutes(new[] { VpnRoute("0.0.0.0", 0), VpnRoute("0.0.0.0", 0, 7) });
    Equal(VpnRoutingMode.Unverified, result.IPv4.Mode, "Equal-cost VPN and physical defaults are ambiguous");
    Equal(VpnRoutingMode.Unverified, result.Mode, "An ambiguous family prevents a confident aggregate claim");
    var specificTie = VerifyVpnRoutes(new[] { VpnRoute("0.0.0.0", 0, 7),
        VpnRoute("8.8.8.0", 24), VpnRoute("8.8.8.0", 24, 7) });
    Equal(VpnRoutingMode.Unverified, specificTie.IPv4.Mode, "Equal-cost specific VPN and physical paths are ambiguous");
}

static void VpnRoutesDeterminism()
{
    var routes = new[] { VpnRoute("0.0.0.0", 0, 7, 100), VpnRoute("0.0.0.0", 0),
        VpnRoute("192.168.0.0", 16, 7), VpnRoute("10.20.0.0", 16) };
    var first = VerifyVpnRoutes(routes);
    var repeated = VerifyVpnRoutes(routes);
    var reordered = VerifyVpnRoutes(routes.Reverse().ToArray());
    Equal(first.Mode, repeated.Mode, "Repeated identical observations do not change routing mode");
    Equal(first.IPv4.Mode, reordered.IPv4.Mode, "Enumeration order does not choose a route winner");
    Equal(first.IPv4.Reason, reordered.IPv4.Reason, "Routing explanations remain deterministic");
    Require(first.IPv4.Routes.SequenceEqual(reordered.IPv4.Routes), "Evidence is sorted independently of enumeration order.");
    var duplicate = VerifyVpnRoutes(routes.Concat(new[] { routes[1] }).ToArray());
    Equal(first.IPv4.Mode, duplicate.IPv4.Mode, "Repeated identical rows do not manufacture an equal-cost competitor");
}

static void VpnRoutesIncomplete()
{
    var state = VpnRouteVerifier.Verify(DetectedVpn(),
        new(AddressFamily.InterNetwork, new[] { VpnRoute("0.0.0.0", 0) }, false, "Windows denied IPv4 route metadata."),
        new(AddressFamily.InterNetworkV6, new[] { VpnRoute("::", 0) }));
    Equal(VpnRoutingMode.Unverified, state.Tunnels[0].IPv4.Mode, "Incomplete IPv4 inventory cannot prove effective routing");
    Equal(VpnRoutingMode.FullTunnel, state.Tunnels[0].IPv6.Mode, "An independent complete family can still be verified");
    Equal(VpnRoutingMode.Unverified, state.Tunnels[0].Mode, "Partial observation cannot produce aggregate full routing");

    var source = new FakeVpnRouteSource { FailureFamily = AddressFamily.InterNetwork,
        Failure = new System.ComponentModel.Win32Exception(5, "Native IPv4 route table denied") };
    var service = new VpnRouteVerifier(new VpnDetectionService(new FakeVpnInterfaceSource
        { Inventory = VpnInventory(VpnInterface()) }), source);
    var failed = service.GetCurrentState();
    Equal(VpnRoutingMode.Unverified, failed.Tunnels[0].IPv4.Mode, "A native route API failure remains uncertainty");
    Equal(VpnRoutingMode.FullTunnel, failed.Tunnels[0].IPv6.Mode, "One failed family does not suppress another native read");
}

static void VpnRoutesUnusable()
{
    foreach (var candidate in new[] { VpnRoute("0.0.0.0", 0) with { InterfaceUsable = false },
        VpnRoute("0.0.0.0", 0) with { ValidLifetime = 0 },
        VpnRoute("0.0.0.0", 0) with { IsLoopback = true },
        VpnRoute("0.0.0.0", 0) with { DefaultRoutesEnabled = false } })
    {
        Equal(VpnRoutingMode.Unverified, VerifyVpnRoutes(new[] { candidate, VpnRoute("0.0.0.0", 0, 7, 100) }).IPv4.Mode,
            "Unusable, expired, loopback or disabled-default VPN rows cannot establish traffic routing");
    }
}

static void VpnRoutesOwnership()
{
    foreach (var candidate in new[] { VpnRoute("0.0.0.0", 0) with { InterfaceGuid = new Guid("4b62f267-c1ad-4fdc-bc2a-b09daff48690") },
        VpnRoute("0.0.0.0", 0) with { InterfaceGuid = null },
        VpnRoute("0.0.0.0", 0) with { InterfaceIndex = 43 },
        VpnRoute("0.0.0.0", 0) with { InterfaceMetric = null },
        VpnRoute("0.0.0.0", 0) with { InterfaceUsable = null },
        VpnRoute("0.0.0.0", 0) with { DefaultRoutesEnabled = null } })
    {
        Equal(VpnRoutingMode.Unverified, VerifyVpnRoutes(new[] { candidate }).IPv4.Mode,
            "Interface index reuse, missing ownership or missing eligibility metadata cannot prove a VPN path");
    }

    var detection = DetectedVpn();
    var offline = detection with { Interfaces = new[] { detection.Interfaces[0] with { OperationalStatus = OperationalStatus.Down } } };
    var offlineResult = VpnRouteVerifier.Verify(offline,
        new(AddressFamily.InterNetwork, new[] { VpnRoute("0.0.0.0", 0) }),
        new(AddressFamily.InterNetworkV6, new[] { VpnRoute("::", 0) }));
    Equal(VpnRoutingMode.Unverified, offlineResult.Tunnels[0].Mode,
        "Routes cannot override an unconfirmed candidate operational state");
}

static void VpnRoutesRasWithoutInterface()
{
    var detection = VpnDetectionService.Classify(new(Array.Empty<VpnInterfaceEvidence>(),
        new[] { new VpnRasConnectionEvidence("Work VPN", "WAN Miniport (IKEv2)", "vpn", 0x2000) }));
    var state = VpnRouteVerifier.Verify(detection,
        new(AddressFamily.InterNetwork, new[] { VpnRoute("0.0.0.0", 0) }),
        new(AddressFamily.InterNetworkV6, new[] { VpnRoute("::", 0) }));
    Equal(VpnRoutingMode.Unverified, state.Tunnels[0].Mode, "RAS status without a native interface relationship cannot verify routes");
    Require(state.Tunnels[0].Tunnel.InterfaceIndex is null && state.Tunnels[0].Tunnel.InterfaceGuid is null,
        "Route verification does not fabricate a RAS interface index or GUID.");
}

static void VpnRoutesMetricOverflow()
{
    var expensive = VpnRoute("0.0.0.0", 0, routeMetric: uint.MaxValue - 1, interfaceMetric: 100);
    Equal<ulong?>((ulong)uint.MaxValue - 1 + 100, expensive.EffectiveMetric, "Combined native costs use a wide integer");
    Equal(VpnRoutingMode.Unverified, VerifyVpnRoutes(new[] { expensive, VpnRoute("0.0.0.0", 0, 7, 100) }).IPv4.Mode,
        "A wrapped uint sum cannot make the VPN default falsely win");
    var unspecified = VpnRoute("0.0.0.0", 0, routeMetric: uint.MaxValue);
    Equal<ulong?>(null, unspecified.EffectiveMetric, "Windows route metric -1 is unspecified rather than a known cost");
    Equal(VpnRoutingMode.Unverified, VerifyVpnRoutes(new[] { unspecified }).IPv4.Mode,
        "An unspecified route metric cannot establish an effective default");
}

static void VpnRoutesPublicBypass()
{
    var result = VerifyVpnRoutes(new[] { VpnRoute("0.0.0.0", 0), VpnRoute("8.8.8.8", 32, 7, 100) });
    Equal(VpnRoutingMode.Unverified, result.IPv4.Mode,
        "A more-specific physical public route may be a VPN endpoint bypass or a real exception");
    var losingVpnSpecific = VerifyVpnRoutes(new[] { VpnRoute("0.0.0.0", 0, 7),
        VpnRoute("8.8.8.0", 24, routeMetric: 100), VpnRoute("8.8.8.0", 24, 7, 1) });
    Equal(VpnRoutingMode.Unverified, losingVpnSpecific.IPv4.Mode,
        "The presence of a losing VPN prefix does not prove effective split routing");
}

static void VpnRoutesLocalExceptions()
{
    var result = VerifyVpnRoutes(new[] { VpnRoute("0.0.0.0", 0), VpnRoute("192.168.0.0", 16, 7),
        VpnRoute("127.0.0.0", 8, 7), VpnRoute("224.0.0.0", 4, 7) },
        new[] { VpnRoute("::", 0), VpnRoute("fe80::", 10, 7), VpnRoute("fc00::", 7, 7), VpnRoute("ff00::", 8, 7) });
    Equal(VpnRoutingMode.FullTunnel, result.IPv4.Mode, "IPv4 LAN, loopback and multicast exceptions are local context");
    Equal(VpnRoutingMode.FullTunnel, result.IPv6.Mode, "IPv6 local and multicast exceptions are local context");
}

static void VpnRoutesPartialCoverage()
{
    var result = VerifyVpnRoutes(new[] { VpnRoute("0.0.0.0", 1) });
    Equal(VpnRoutingMode.Unverified, result.IPv4.Mode, "One half-default alone does not describe general network routing");
    var mixedHalves = VerifyVpnRoutes(new[] { VpnRoute("0.0.0.0", 1), VpnRoute("128.0.0.0", 1, 7) });
    Equal(VpnRoutingMode.SplitTunnel, mixedHalves.IPv4.Mode, "One VPN half and one physical half verify split routing");
}

static void VpnRoutesMalformed()
{
    foreach (var malformed in new[] { VpnRoute("0.0.0.0", 33),
        VpnRoute("0.0.0.0", 0) with { NextHop = IPAddress.IPv6Any },
        VpnRoute("::", 0) })
    {
        Equal(VpnRoutingMode.Unverified, VerifyVpnRoutes(new[] { malformed }).IPv4.Mode,
            "Invalid prefix lengths and address-family mismatches cannot establish IPv4 routing");
    }
}

static void VpnRoutesNoDetectedTunnel()
{
    foreach (var status in new[] { VpnStatus.NoVpn, VpnStatus.Indeterminate })
    {
        var detection = new VpnState(status, Array.Empty<VpnInterfaceInfo>(), "No confirmed candidate");
        var state = VpnRouteVerifier.Verify(detection,
            new(AddressFamily.InterNetwork, new[] { VpnRoute("0.0.0.0", 0) }),
            new(AddressFamily.InterNetworkV6, new[] { VpnRoute("::", 0) }));
        Equal(detection, state.Detection, "Route verification preserves the original detector state");
        Equal(0, state.Tunnels.Count, "A route row cannot promote an unconfirmed interface into a VPN candidate");
    }

    foreach (var nativeSource in new[] { new FakeVpnInterfaceSource(),
        new FakeVpnInterfaceSource { Failure = new System.ComponentModel.Win32Exception(5) } })
    {
        var routes = new FakeVpnRouteSource();
        var service = new VpnRouteVerifier(new VpnDetectionService(nativeSource), routes);
        Equal(0, service.GetCurrentState().Tunnels.Count, "No confirmed candidate means no routing claims");
        Equal(0, routes.Reads.Count, "NoVPN or indeterminate detection does not trigger native route reads");
    }
}

static void VpnRoutesRefresh()
{
    var source = new FakeVpnRouteSource();
    var service = new VpnRouteVerifier(new VpnDetectionService(new FakeVpnInterfaceSource
        { Inventory = VpnInventory(VpnInterface()) }), source);
    Equal(VpnRoutingMode.FullTunnel, service.GetCurrentState().Tunnels[0].Mode, "Initially effective VPN defaults");
    source.IPv4 = new(AddressFamily.InterNetwork, new[] { VpnRoute("0.0.0.0", 0, 7), VpnRoute("10.0.0.0", 8) });
    Equal(VpnRoutingMode.SplitTunnel, service.GetCurrentState().Tunnels[0].Mode, "A later query observes new split routes");
    source.IPv4 = new(AddressFamily.InterNetwork, new[] { VpnRoute("0.0.0.0", 0, 7) });
    Equal(VpnRoutingMode.Unverified, service.GetCurrentState().Tunnels[0].Mode, "Removed routes do not reuse stale full-tunnel proof");
    Equal(3, source.Reads.Count(family => family == AddressFamily.InterNetwork), "Every state query rereads IPv4 native routes");
    Equal(3, source.Reads.Count(family => family == AddressFamily.InterNetworkV6), "Every state query rereads IPv6 native routes");
}

static VpnTunnelRoutingResult VerifyVpnFamilies(VpnRouteInventory ipv4, VpnRouteInventory ipv6) =>
    VpnRouteVerifier.Verify(DetectedVpn(), ipv4, ipv6).Tunnels.Single();

static void VpnFamilyIpv4OnlyFull()
{
    var result = VerifyVpnFamilies(new(AddressFamily.InterNetwork, new[] { VpnRoute("0.0.0.0", 0) }),
        new(AddressFamily.InterNetworkV6, Array.Empty<VpnRouteEvidence>()));
    Equal(IpFamilyAvailability.Usable, result.IPv4.Availability, "Active IPv4 forwarding establishes family usability");
    Equal(VpnRoutingMode.FullTunnel, result.IPv4.Mode, "IPv4 is individually verified full");
    Equal(IpFamilyAvailability.Unavailable, result.IPv6.Availability, "Complete empty IPv6 snapshot proves unavailability");
    Equal(VpnRoutingMode.Unverified, result.IPv6.Mode, "Unavailable IPv6 is not falsely marked full");
    Equal(VpnRoutingMode.FullTunnel, result.Mode, "Only applicable IPv4 contributes to aggregate routing");
}

static void VpnFamilyIpv4OnlySplit()
{
    var result = VerifyVpnFamilies(new(AddressFamily.InterNetwork,
            new[] { VpnRoute("0.0.0.0", 0, 7), VpnRoute("10.0.0.0", 8) }),
        new(AddressFamily.InterNetworkV6, Array.Empty<VpnRouteEvidence>()));
    Equal(IpFamilyAvailability.Usable, result.IPv4.Availability, "Known IPv4 paths establish applicability");
    Equal(VpnRoutingMode.SplitTunnel, result.IPv4.Mode, "Corporate prefix and physical default verify split routing");
    Equal(IpFamilyAvailability.Unavailable, result.IPv6.Availability, "Absent IPv6 is independently unavailable");
    Equal(VpnRoutingMode.SplitTunnel, result.Mode, "Unavailable IPv6 does not hide verified IPv4 split routing");
}

static void VpnFamilyUsableWithoutVpnPaths()
{
    foreach (var ipv6Routes in new[] { new[] { VpnRoute("::", 0, 7) },
        new[] { VpnRoute("fe80::", 10, 7) },
        new[] { VpnRoute("::", 0), VpnRoute("::", 0, 7) } })
    {
        var result = VerifyVpnFamilies(new(AddressFamily.InterNetwork, new[] { VpnRoute("0.0.0.0", 0) }),
            new(AddressFamily.InterNetworkV6, ipv6Routes));
        Equal(IpFamilyAvailability.Usable, result.IPv6.Availability,
            "A confirmed live physical interface proves IPv6 exists even without VPN routes");
        Equal(VpnRoutingMode.Unverified, result.IPv6.Mode, "Physical-only or ambiguous usable IPv6 remains unverified");
        Equal(VpnRoutingMode.Unverified, result.Mode,
            "A usable unverified family cannot be ignored to claim overall full tunneling");
    }
}

static void VpnFamilyIpv6OnlyFull()
{
    var result = VerifyVpnFamilies(new(AddressFamily.InterNetwork, Array.Empty<VpnRouteEvidence>()),
        new(AddressFamily.InterNetworkV6, new[] { VpnRoute("::", 0) }));
    Equal(IpFamilyAvailability.Unavailable, result.IPv4.Availability, "Complete empty IPv4 snapshot proves unavailability");
    Equal(IpFamilyAvailability.Usable, result.IPv6.Availability, "Active IPv6 forwarding establishes applicability");
    Equal(VpnRoutingMode.FullTunnel, result.IPv6.Mode, "IPv6 is individually verified full");
    Equal(VpnRoutingMode.FullTunnel, result.Mode, "Aggregation is symmetric for IPv6-only routing");
}

static void VpnFamilyNeitherUsable()
{
    var result = VerifyVpnFamilies(new(AddressFamily.InterNetwork, Array.Empty<VpnRouteEvidence>()),
        new(AddressFamily.InterNetworkV6, Array.Empty<VpnRouteEvidence>()));
    Equal(IpFamilyAvailability.Unavailable, result.IPv4.Availability, "No applicable IPv4");
    Equal(IpFamilyAvailability.Unavailable, result.IPv6.Availability, "No applicable IPv6");
    Equal(VpnRoutingMode.Unverified, result.Mode, "An empty applicable family set cannot prove routing");
}

static void VpnFamilyUnknownNotIgnored()
{
    foreach (var ipv6 in new[]
    {
        new VpnRouteInventory(AddressFamily.InterNetworkV6, Array.Empty<VpnRouteEvidence>(), false, "Native IPv6 query denied"),
        new VpnRouteInventory(AddressFamily.InterNetworkV6, Array.Empty<VpnRouteEvidence>(), false,
            "Incomplete native inventory", IpFamilyAvailability.Unavailable),
        new VpnRouteInventory(AddressFamily.InterNetwork, Array.Empty<VpnRouteEvidence>())
    })
    {
        var result = VerifyVpnFamilies(new(AddressFamily.InterNetwork, new[] { VpnRoute("0.0.0.0", 0) }), ipv6);
        Equal(IpFamilyAvailability.Unknown, result.IPv6.Availability, "API failure, partial or wrong-family observations are uncertainty");
        Equal(VpnRoutingMode.Unverified, result.IPv6.Mode, "Unknown applicability provides no route proof");
        Equal(VpnRoutingMode.Unverified, result.Mode, "Unknown family state cannot be ignored during aggregation");
    }

    var source = new FakeVpnRouteSource { FailureFamily = AddressFamily.InterNetworkV6,
        Failure = new System.ComponentModel.Win32Exception(5, "Native IPv6 query denied") };
    var service = new VpnRouteVerifier(new VpnDetectionService(new FakeVpnInterfaceSource
        { Inventory = VpnInventory(VpnInterface()) }), source);
    var nativeFailure = service.GetCurrentState().Tunnels.Single();
    Equal(IpFamilyAvailability.Unknown, nativeFailure.IPv6.Availability, "Thrown native query does not become a successful empty snapshot");
    Equal(VpnRoutingMode.Unverified, nativeFailure.Mode, "Native query failure cannot falsely promote full routing");
}

static void VpnFamilyUnavailableEvidence()
{
    foreach (var ipv6Routes in new[]
    {
        new[] { VpnRoute("::1", 128, 7) with { IsLoopback = true } },
        new[] { VpnRoute("::", 0, 7) with { InterfaceUsable = false } },
        new[] { VpnRoute("::1", 128, 7) with { IsLoopback = true },
            VpnRoute("::", 0, 7) with { InterfaceUsable = false } }
    })
    {
        var result = VerifyVpnFamilies(new(AddressFamily.InterNetwork, new[] { VpnRoute("0.0.0.0", 0) }),
            new(AddressFamily.InterNetworkV6, ipv6Routes));
        Equal(IpFamilyAvailability.Unavailable, result.IPv6.Availability,
            "Completed loopback-only or confirmed inactive interface snapshots prove no usable IPv6");
        Equal(VpnRoutingMode.FullTunnel, result.Mode, "Known unavailable IPv6 does not weaken verified IPv4 routing");
    }

    foreach (var liveInterface in new[] { VpnRoute("::", 0, 7) with { ValidLifetime = 0 },
        VpnRoute("::", 0, 7) with { DefaultRoutesEnabled = false } })
    {
        var result = VerifyVpnFamilies(new(AddressFamily.InterNetwork, new[] { VpnRoute("0.0.0.0", 0) }),
            new(AddressFamily.InterNetworkV6, new[] { liveInterface }));
        Equal(IpFamilyAvailability.Usable, result.IPv6.Availability,
            "An expired or disabled route does not erase evidence of a live IPv6 interface");
        Equal(VpnRoutingMode.Unverified, result.Mode, "A usable family without route proof still prevents aggregate full routing");
    }
}

static void VpnFamilyMetadataSafety()
{
    foreach (var ipv6 in new[]
    {
        new VpnRouteInventory(AddressFamily.InterNetworkV6,
            new[] { VpnRoute("::", 0, 7) with { InterfaceUsable = null } }),
        new VpnRouteInventory(AddressFamily.InterNetworkV6,
            new[] { VpnRoute("::", 0, 7) with { InterfaceUsable = null } },
            Availability: IpFamilyAvailability.Unavailable),
        new VpnRouteInventory(AddressFamily.InterNetworkV6,
            new[] { VpnRoute("::", 0, 7) }, Availability: IpFamilyAvailability.Unavailable),
        new VpnRouteInventory(AddressFamily.InterNetworkV6,
            new[] { VpnRoute("::", 0) }, Availability: IpFamilyAvailability.Unavailable)
    })
    {
        var result = VerifyVpnFamilies(new(AddressFamily.InterNetwork, new[] { VpnRoute("0.0.0.0", 0) }), ipv6);
        Equal(IpFamilyAvailability.Unknown, result.IPv6.Availability,
            "Missing interface state or contradictory unavailable declarations remain uncertain");
        Equal(VpnRoutingMode.Unverified, result.Mode, "Contradictory metadata cannot hide a potentially usable family");
    }

    var explicitlyUsable = VerifyVpnFamilies(new(AddressFamily.InterNetwork, new[] { VpnRoute("0.0.0.0", 0) }),
        new(AddressFamily.InterNetworkV6, Array.Empty<VpnRouteEvidence>(), Availability: IpFamilyAvailability.Usable));
    Equal(IpFamilyAvailability.Usable, explicitlyUsable.IPv6.Availability, "Positive family support remains applicable even with no routes");
    Equal(VpnRoutingMode.Unverified, explicitlyUsable.Mode, "Known applicability without route proof cannot be ignored");

    var knownAndUnknown = VerifyVpnFamilies(new(AddressFamily.InterNetwork, new[] { VpnRoute("0.0.0.0", 0) }),
        new(AddressFamily.InterNetworkV6, new[] { VpnRoute("::", 0),
            VpnRoute("2606:4700::", 32, 7) with { InterfaceUsable = null } }));
    Equal(IpFamilyAvailability.Usable, knownAndUnknown.IPv6.Availability,
        "A confirmed live interface establishes applicability despite another interface's unknown state");
    Equal(VpnRoutingMode.Unverified, knownAndUnknown.Mode,
        "Incomplete competing route metadata still prevents confident route verification");
}

static void VpnVersionThreeUpgrade()
{
    using var workspace = new TestWorkspace();
    CreateVersionThreeFixture(workspace.CurrentPath);
    var tables = new[] { "daily_usage", "ConnectionProfiles", "NetworkMappings", "ConnectionSessions", "ConnectionEvents" };
    var before = tables.ToDictionary(table => table, table => (
        Schema: ReadText(workspace.CurrentPath, $"SELECT sql FROM sqlite_schema WHERE name='{table}';"),
        Root: ReadInteger(workspace.CurrentPath, $"SELECT rootpage FROM sqlite_schema WHERE name='{table}';"),
        Rows: SnapshotRows(workspace.CurrentPath, table)));

    var usage = new UsageStore(workspace.Root);

    Equal(5L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Version-three database reaches current version five");
    AssertFixtureTotals(usage);
    foreach (var table in tables)
    {
        Equal(before[table].Schema, ReadText(workspace.CurrentPath, $"SELECT sql FROM sqlite_schema WHERE name='{table}';"), $"{table} definition preserved");
        Equal(before[table].Root, ReadInteger(workspace.CurrentPath, $"SELECT rootpage FROM sqlite_schema WHERE name='{table}';"), $"{table} B-tree preserved");
        Equal(before[table].Rows, SnapshotRows(workspace.CurrentPath, table), $"All {table} fields preserved");
    }
    Equal(62L, new ConnectionHistoryStore(usage).GetActiveSession()!.Id, "Migration does not recover or replace the active physical interval");
    Equal(0L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM VpnSessions;"), "Migration invents no prior VPN intervals");
}

static string SnapshotRows(string path, string table, string? condition = null)
{
    using var connection = Open(path);
    using var command = connection.CreateCommand();
    command.CommandText = $"SELECT * FROM {table}" + (condition is null ? "" : $" WHERE {condition}") + " ORDER BY rowid;";
    using var reader = command.ExecuteReader();
    var rows = new List<string>();
    while (reader.Read())
        rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(index =>
            reader.IsDBNull(index) ? "<null>" : Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture))));
    return string.Join("\n", rows);
}

static void VpnFreshSchema()
{
    using var workspace = new TestWorkspace();
    var usage = new UsageStore(workspace.Root);
    Equal(5L, ReadInteger(usage.DatabasePath, "PRAGMA user_version;"), "Fresh install version");
    Equal(0L, ReadInteger(usage.DatabasePath, "SELECT count(*) FROM VpnSessions;"), "Fresh VPN history is empty");
    using var connection = Open(usage.DatabasePath);
    using var command = connection.CreateCommand();
    command.CommandText = "PRAGMA table_info(VpnSessions);";
    var columns = new List<string>();
    using (var reader = command.ExecuteReader())
        while (reader.Read()) columns.Add(reader.GetString(1));
    foreach (var column in new[] { "Id", "ConnectionSessionId", "InterfaceIndex", "InterfaceGuid", "InterfaceName", "RoutingMode", "StartedAtUtc", "LastConfirmedAtUtc", "EndedAtUtc", "EndReason" })
        Require(columns.Contains(column), $"VPN history includes {column}.");
    Require(!columns.Any(column => column.Contains("Bytes", StringComparison.OrdinalIgnoreCase)), "VPN history has no traffic accounting columns.");
    command.CommandText = "PRAGMA foreign_key_list(VpnSessions);";
    using (var reader = command.ExecuteReader())
    {
        Require(reader.Read(), "VPN interval has a physical parent relationship.");
        Equal("ConnectionSessions", reader.GetString(2), "VPN parent table");
        Equal("ConnectionSessionId", reader.GetString(3), "VPN parent key");
        Equal("RESTRICT", reader.GetString(6), "Historical parent deletion is restricted");
        Require(!reader.Read(), "VPN interval has one physical parent relationship.");
    }
    Require(new VpnSessionStore(usage).GetActiveSession() is null, "Fresh schema has no active VPN interval.");
}

static void VpnFailedMigration()
{
    using var workspace = new TestWorkspace();
    CreateVersionThreeFixture(workspace.CurrentPath);
    var sessions = SnapshotRows(workspace.CurrentPath, "ConnectionSessions");
    var events = SnapshotRows(workspace.CurrentPath, "ConnectionEvents");
    using (var connection = Open(workspace.CurrentPath))
        Execute(connection, "CREATE TABLE VpnSessions(sentinel TEXT); INSERT INTO VpnSessions VALUES('retain conflicting VPN data');");

    ExpectStartupFailure(workspace.Root);

    Equal(3L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Failed upgrade retains version three");
    Equal("retain conflicting VPN data", ReadText(workspace.CurrentPath, "SELECT sentinel FROM VpnSessions;"), "Conflicting data is preserved");
    Equal(sessions, SnapshotRows(workspace.CurrentPath, "ConnectionSessions"), "Failed migration preserves all physical sessions");
    Equal(events, SnapshotRows(workspace.CurrentPath, "ConnectionEvents"), "Failed migration preserves all existing events");
    Equal(126_456L, ReadInteger(workspace.CurrentPath, "SELECT sum(downloaded) FROM daily_usage;"), "Failed migration preserves usage");
    Equal(1L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM ConnectionProfiles;"), "Failed migration preserves profiles");
    Equal(2L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM NetworkMappings;"), "Failed migration preserves mappings");
    Equal(0L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM sqlite_schema WHERE name LIKE '%VpnSessions%' AND type IN ('index','trigger');"), "Partial VPN schema objects roll back");
}

static void VpnLegacyVersionThreeUpgrade()
{
    using var workspace = new TestWorkspace();
    CreateVersionThreeFixture(workspace.LegacyPath);
    var original = Hash(workspace.LegacyPath);
    var originalSessions = SnapshotRows(workspace.LegacyPath, "ConnectionSessions");
    var usage = new UsageStore(workspace.Root);
    AssertFixtureTotals(usage);
    Equal(5L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Copied legacy database reaches current version five");
    Equal(3L, ReadInteger(workspace.LegacyPath, "PRAGMA user_version;"), "Legacy rollback schema version is unchanged");
    Equal(original, Hash(workspace.LegacyPath), "Legacy rollback database bytes are untouched");
    Equal(originalSessions, SnapshotRows(workspace.CurrentPath, "ConnectionSessions"), "Copied physical history is preserved");
    Equal(0L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM VpnSessions;"), "Legacy migration fabricates no VPN history");
}

static VpnRoutingState VpnStoredObservation(VpnRoutingMode mode = VpnRoutingMode.Unverified,
    VpnInterfaceInfo? tunnel = null)
{
    tunnel ??= DetectedVpn().Interfaces.Single();
    var ipv4 = new VpnFamilyRoutingResult(AddressFamily.InterNetwork, mode,
        Array.Empty<VpnRouteEvidence>(), "Controlled routing observation", IpFamilyAvailability.Usable);
    var ipv6 = new VpnFamilyRoutingResult(AddressFamily.InterNetworkV6, mode,
        Array.Empty<VpnRouteEvidence>(), "Controlled routing observation", IpFamilyAvailability.Usable);
    return new(new(VpnStatus.TunnelDetected, new[] { tunnel }, null),
        new[] { new VpnTunnelRoutingResult(tunnel, mode, ipv4, ipv6) });
}

static VpnRoutingState VpnAbsentObservation(VpnStatus status = VpnStatus.NoVpn) =>
    new(new(status, Array.Empty<VpnInterfaceInfo>(), status == VpnStatus.Indeterminate ? "Native query failed" : null),
        Array.Empty<VpnTunnelRoutingResult>());

static long StartVpnPhysicalParent(SessionTestContext test)
{
    test.Tracker.ObserveConnection(Connected(Wifi()));
    test.Tracker.Flush();
    return test.Tracker.Current!.Id;
}

static DateTimeOffset ConfirmVpnPhysicalParent(SessionTestContext test, long id, int seconds = 1)
{
    test.Clock.Advance(TimeSpan.FromSeconds(seconds));
    var parent = test.History.GetActiveSession() ?? throw new InvalidOperationException("Expected an active physical parent.");
    test.History.Confirm(id, test.Clock.GetUtcNow(), parent.DownloadBytes, parent.UploadBytes);
    return test.Clock.GetUtcNow();
}

static void VpnSessionStarts()
{
    using var test = new SessionTestContext();
    var id = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    Require(store.Observe(id, VpnAbsentObservation(), test.Clock.GetUtcNow()) is null, "VPN off creates no child interval.");
    var startedAt = ConfirmVpnPhysicalParent(test, id);
    var row = store.Observe(id, VpnStoredObservation(VpnRoutingMode.FullTunnel), startedAt)!;
    Equal(id, row.ConnectionSessionId, "VPN interval belongs to the current physical session");
    Equal(startedAt, row.StartedAtUtc, "VPN start timestamp");
    Equal(startedAt, row.LastConfirmedAtUtc, "VPN initial confirmation");
    Equal(VpnRoutingMode.FullTunnel, row.RoutingMode, "Verified mode is stored");
    Equal(DetectedVpn().Interfaces[0].InterfaceGuid, row.InterfaceGuid, "Native interface GUID is preserved");
    Equal(DetectedVpn().Interfaces[0].InterfaceIndex, row.InterfaceIndex, "Native interface index is preserved");
    Equal(DetectedVpn().Interfaces[0].InterfaceName, row.InterfaceName, "Native display name is preserved");
    Require(row.EndedAtUtc is null && row.EndReason is null, "The new VPN interval remains open.");
    Equal(1, test.History.GetRecentSessions().Count, "VPN activation does not start another physical session");
    Equal(id, test.History.GetActiveSession()!.Id, "VPN activation retains the physical identity");
}

static void VpnSessionRepeated()
{
    using var test = new SessionTestContext();
    var id = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    var row = store.Observe(id, VpnStoredObservation(), test.Clock.GetUtcNow())!;
    for (var index = 0; index < 8; index++)
    {
        var observedAt = ConfirmVpnPhysicalParent(test, id);
        store = new VpnSessionStore(test.DatabasePath);
        Equal(row.Id, store.Observe(id, VpnStoredObservation(), observedAt)!.Id, "Repeated observations retain the same persisted interval");
    }
    Equal(1, store.GetRecentSessions().Count, "Reopening and repeated observations do not duplicate VPN history");
    Equal(test.Clock.GetUtcNow(), store.GetActiveSession()!.LastConfirmedAtUtc, "Repeated observations persist VPN liveness");
    Equal(row.StartedAtUtc, store.GetActiveSession()!.StartedAtUtc, "Repeated observations retain the original VPN start");
}

static void VpnSessionDisconnects()
{
    using var test = new SessionTestContext();
    var id = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    var vpn = store.Observe(id, VpnStoredObservation(), test.Clock.GetUtcNow())!;
    var endedAt = ConfirmVpnPhysicalParent(test, id, 5);
    Require(store.Observe(id, VpnAbsentObservation(), endedAt) is null, "VPN off has no active child interval.");
    Require(store.GetActiveSession() is null, "The VPN interval is closed.");
    var ended = store.GetRecentSessions().Single();
    Equal(vpn.Id, ended.Id, "VPN disconnect closes the original interval");
    Equal<DateTimeOffset?>(endedAt, ended.EndedAtUtc, "VPN disconnect boundary");
    Equal("VpnDisconnected", ended.EndReason, "VPN disconnect reason");
    Equal(id, test.History.GetActiveSession()!.Id, "VPN disconnect does not end the physical session");
    Equal(1, test.History.GetRecentEvents().Count(item => !IsVpnEvent(item)), "VPN disconnect creates no physical events");
    Equal(2, GetVpnEvents(test.History).Length, "VPN interval records its connected and disconnected events");
    store.Observe(id, VpnAbsentObservation(), endedAt);
    Equal(1, store.GetRecentSessions().Count, "Repeated VPN off does not create rows");
    var restartedAt = ConfirmVpnPhysicalParent(test, id);
    var restarted = store.Observe(id, VpnStoredObservation(), restartedAt)!;
    Require(restarted.Id != vpn.Id, "VPN reconnect creates a new VPN interval on the same physical session.");
    Equal(id, restarted.ConnectionSessionId, "VPN reconnect retains the physical parent");
}

static void VpnSessionParentCloses()
{
    foreach (var transition in new[] { "Disconnected", "NetworkChanged", "ApplicationExit" })
    {
        using var test = new SessionTestContext();
        var id = StartVpnPhysicalParent(test);
        var store = new VpnSessionStore(test.Usage);
        var vpn = store.Observe(id, VpnStoredObservation(), test.Clock.GetUtcNow())!;
        test.Clock.Advance(TimeSpan.FromSeconds(10));
        if (transition == "Disconnected") test.Tracker.ObserveConnection(Disconnected());
        else if (transition == "NetworkChanged") test.Tracker.ObserveConnection(Connected(Wifi("Other Wi-Fi")));
        else test.Tracker.Close();
        Require(store.GetActiveSession() is null, $"{transition} closes the active VPN interval atomically.");
        var parent = test.History.GetRecentSessions().Single(row => row.Id == id);
        var child = store.GetRecentSessions().Single();
        Equal(vpn.Id, child.Id, "Parent closure retains the child interval");
        Equal(parent.EndedAtUtc, child.EndedAtUtc, "Child ends at its physical parent's boundary");
        Require(!string.IsNullOrWhiteSpace(child.EndReason), "Physical boundary records a VPN end reason.");
        Equal(1L, ReadInteger(test.DatabasePath, "SELECT count(*) FROM VpnSessions;"), "A physical boundary does not create VPN history for the successor");
    }
}

static void VpnSessionRouteChanges()
{
    using var test = new SessionTestContext();
    var id = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    var first = store.Observe(id, VpnStoredObservation(), test.Clock.GetUtcNow())!;
    foreach (var mode in new[] { VpnRoutingMode.FullTunnel, VpnRoutingMode.SplitTunnel, VpnRoutingMode.Unverified, VpnRoutingMode.FullTunnel })
    {
        var observedAt = ConfirmVpnPhysicalParent(test, id);
        var current = store.Observe(id, VpnStoredObservation(mode), observedAt)!;
        Equal(first.Id, current.Id, "Route mode changes retain interval identity");
        Equal(mode, current.RoutingMode, "Latest routing mode is stored deterministically");
        Equal(mode, new VpnSessionStore(test.DatabasePath).GetActiveSession()!.RoutingMode, "Updated route mode survives repository reopening");
        Equal(first.StartedAtUtc, current.StartedAtUtc, "Routing mode change does not restart the VPN interval");
    }
    Equal(1, store.GetRecentSessions().Count, "Routing changes do not duplicate VPN intervals");
}

static void VpnSessionSingleActive()
{
    using var test = new SessionTestContext();
    var id = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    store.Observe(id, VpnStoredObservation(), test.Clock.GetUtcNow());
    using var connection = Open(test.DatabasePath);
    Execute(connection, "PRAGMA foreign_keys=ON;");
    using var command = connection.CreateCommand();
    command.CommandText = "PRAGMA table_info(VpnSessions);";
    var columns = new List<string>();
    using (var reader = command.ExecuteReader())
        while (reader.Read())
            if (reader.GetString(1) != "Id") columns.Add(reader.GetString(1));
    var copyColumns = string.Join(",", columns);
    var failure = ExpectFailure(() => Execute(connection, $"INSERT INTO VpnSessions({copyColumns}) SELECT {copyColumns} FROM VpnSessions;"), "Duplicate active VPN interval");
    Require(failure is SqliteException { SqliteErrorCode: 19 }, "SQLite enforces the single-active VPN constraint.");
    Require(failure.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase), "The duplicate is rejected by the active interval uniqueness rule.");
    Equal(1L, ReadInteger(test.DatabasePath, "SELECT count(*) FROM VpnSessions WHERE EndedAtUtc IS NULL;"), "Only one active interval remains");
}

static void VpnSessionRelationships()
{
    using var test = new SessionTestContext();
    var id = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    store.Observe(id, VpnStoredObservation(), test.Clock.GetUtcNow());
    test.Clock.Advance(TimeSpan.FromSeconds(5));
    test.Tracker.ObserveConnection(Disconnected());
    using var connection = Open(test.DatabasePath);
    Execute(connection, "PRAGMA foreign_keys=ON;");
    // Remove independent physical events so this failure proves the VPN child's relationship.
    Execute(connection, "DELETE FROM ConnectionEvents;");
    var deletion = ExpectFailure(() => Execute(connection, $"DELETE FROM ConnectionSessions WHERE Id={id};"), "Deleting a VPN interval's physical parent");
    Require(deletion is SqliteException { SqliteErrorCode: 19 }, "Historical VPN parent deletion is restricted.");
    var reassignment = ExpectFailure(() => Execute(connection, "UPDATE VpnSessions SET ConnectionSessionId=999999;"), "Assigning a nonexistent physical parent");
    Require(reassignment is SqliteException { SqliteErrorCode: 19 }, "VPN relationships reject nonexistent parent IDs.");
    Equal(id, store.GetRecentSessions().Single().ConnectionSessionId, "Relationship failures retain the original historical parent");
}

static void VpnSessionAtomicTransition()
{
    using var test = new SessionTestContext();
    var id = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    var first = store.Observe(id, VpnStoredObservation(), test.Clock.GetUtcNow())!;
    var observedAt = ConfirmVpnPhysicalParent(test, id);
    using (var connection = Open(test.DatabasePath))
        Execute(connection, "CREATE TRIGGER FailVpnInsert BEFORE INSERT ON VpnSessions BEGIN SELECT RAISE(ABORT,'simulate VPN storage failure'); END;");
    var other = DetectedVpn().Interfaces[0] with { InterfaceGuid = Guid.NewGuid(), InterfaceIndex = 43 };
    ExpectFailure(() => store.Observe(id, VpnStoredObservation(tunnel: other), observedAt), "Failed tunnel replacement");
    var retained = store.GetActiveSession()!;
    Equal(first.Id, retained.Id, "Insert failure rolls back closure of the prior VPN interval");
    Equal(first.LastConfirmedAtUtc, retained.LastConfirmedAtUtc, "Failed replacement does not manufacture liveness");
    Equal(1, store.GetRecentSessions().Count, "Failed replacement creates no partial VPN history");
    using (var connection = Open(test.DatabasePath)) Execute(connection, "DROP TRIGGER FailVpnInsert;");
    var replacement = store.Observe(id, VpnStoredObservation(tunnel: other), observedAt)!;
    Require(replacement.Id != first.Id, "The same transition can succeed after a storage failure is corrected.");
    Equal(2, store.GetRecentSessions().Count, "Successful retry publishes both complete intervals");
}

static void VpnSessionUncertainDetection()
{
    using var test = new SessionTestContext();
    var id = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    Require(store.Observe(id, VpnAbsentObservation(VpnStatus.Indeterminate), test.Clock.GetUtcNow()) is null, "Uncertain detection cannot start a VPN interval.");
    Equal(0, store.GetRecentSessions().Count, "Uncertainty fabricates no VPN history");
    var first = store.Observe(id, VpnStoredObservation(), test.Clock.GetUtcNow())!;
    var failedAt = ConfirmVpnPhysicalParent(test, id, 10);
    store.Observe(id, VpnAbsentObservation(VpnStatus.Indeterminate), failedAt);
    var ended = store.GetRecentSessions().Single();
    Equal<DateTimeOffset?>(first.LastConfirmedAtUtc, ended.EndedAtUtc, "Lost detection ends at the last confirmed VPN observation");
    Equal("DetectionUnavailable", ended.EndReason, "Uncertain detection is distinct from VPN off");
    var nextAt = ConfirmVpnPhysicalParent(test, id);
    store.Observe(id, VpnStoredObservation(), nextAt);
    var active = store.GetActiveSession()!;
    var firstTunnel = DetectedVpn().Interfaces.Single();
    var secondTunnel = firstTunnel with { InterfaceGuid = Guid.NewGuid(), InterfaceIndex = 43 };
    var ambiguous = new VpnRoutingState(new(VpnStatus.TunnelDetected, new[] { firstTunnel, secondTunnel }, null),
        new[] { VpnStoredObservation(tunnel: firstTunnel).Tunnels[0], VpnStoredObservation(tunnel: secondTunnel).Tunnels[0] });
    var ambiguousAt = ConfirmVpnPhysicalParent(test, id);
    Require(store.Observe(id, ambiguous, ambiguousAt) is null, "Multiple active candidates do not select an arbitrary VPN interval.");
    var last = store.GetRecentSessions().Single(row => row.Id == active.Id);
    Equal<DateTimeOffset?>(active.LastConfirmedAtUtc, last.EndedAtUtc, "Ambiguous detection closes at known VPN liveness");
    Equal("AmbiguousTunnel", last.EndReason, "Ambiguous identity has a distinct end reason");
    Equal(id, test.History.GetActiveSession()!.Id, "Detection uncertainty does not end physical connectivity");
}

static void VpnSessionTunnelChanges()
{
    using var test = new SessionTestContext();
    var id = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    var originalTunnel = DetectedVpn().Interfaces.Single();
    var first = store.Observe(id, VpnStoredObservation(tunnel: originalTunnel), test.Clock.GetUtcNow())!;
    var nameAt = ConfirmVpnPhysicalParent(test, id);
    var renamed = originalTunnel with { InterfaceName = "A friendly renamed VPN" };
    var same = store.Observe(id, VpnStoredObservation(tunnel: renamed), nameAt)!;
    Equal(first.Id, same.Id, "Native GUID identity does not split solely for a friendly-name change");
    var changedAt = ConfirmVpnPhysicalParent(test, id);
    var changed = renamed with { InterfaceGuid = Guid.NewGuid(), InterfaceIndex = 43 };
    var second = store.Observe(id, VpnStoredObservation(tunnel: changed), changedAt)!;
    Require(second.Id != first.Id, "A different native tunnel creates a new VPN interval.");
    var completed = store.GetRecentSessions().Single(row => row.Id == first.Id);
    Equal("TunnelChanged", completed.EndReason, "Tunnel identity transition reason");
    Equal<DateTimeOffset?>(changedAt, completed.EndedAtUtc, "Old VPN interval ends at the transition");
    Equal(changedAt, second.StartedAtUtc, "New VPN interval begins at the transition");
    Equal(id, second.ConnectionSessionId, "Tunnel changes preserve the physical parent");
    Equal(1, test.History.GetRecentSessions().Count, "Changing VPN identity creates no physical sessions");
}

static void VpnSessionRasIdentity()
{
    using var test = new SessionTestContext();
    var id = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    var ras = new VpnInterfaceInfo(null, null, "Work VPN", "WAN Miniport (IKEv2)", null, null, null,
        "Windows RAS confirms VPN connectivity without interface linkage");
    var first = store.Observe(id, VpnStoredObservation(tunnel: ras), test.Clock.GetUtcNow())!;
    Require(first.InterfaceGuid is null && first.InterfaceIndex is null, "RAS-only observations never invent native IDs.");
    var at = ConfirmVpnPhysicalParent(test, id);
    Equal(first.Id, new VpnSessionStore(test.DatabasePath).Observe(id, VpnStoredObservation(tunnel: ras), at)!.Id,
        "An identical single RAS identity retains its persisted interval");
    var changedAt = ConfirmVpnPhysicalParent(test, id);
    var changed = store.Observe(id, VpnStoredObservation(tunnel: ras with { InterfaceName = "Other RAS VPN" }), changedAt)!;
    Require(changed.Id != first.Id, "A different opaque RAS connection name starts another child interval.");
    Equal(VpnRoutingMode.Unverified, changed.RoutingMode, "Unlinked RAS routing remains unverified");
}

static void VpnSessionObservationSafety()
{
    using var test = new SessionTestContext();
    var id = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    var first = store.Observe(id, VpnStoredObservation(VpnRoutingMode.FullTunnel), test.Clock.GetUtcNow())!;
    var confirmedAt = ConfirmVpnPhysicalParent(test, id, 10);
    store.Observe(id, VpnStoredObservation(VpnRoutingMode.SplitTunnel), confirmedAt);
    store.Observe(id, VpnStoredObservation(VpnRoutingMode.Unverified), first.StartedAtUtc);
    store.Observe(id, VpnAbsentObservation(), first.StartedAtUtc);
    var current = store.GetActiveSession()!;
    Equal(first.Id, current.Id, "Older VPN observations cannot end or replace the interval");
    Equal(confirmedAt, current.LastConfirmedAtUtc, "VPN confirmation never moves backward");
    Equal(VpnRoutingMode.SplitTunnel, current.RoutingMode, "Old route results cannot overwrite the confirmed route mode");
    var offsetAt = ConfirmVpnPhysicalParent(test, id).ToOffset(TimeSpan.FromHours(8));
    var offsetRow = store.Observe(id, VpnStoredObservation(), offsetAt)!;
    Equal(TimeSpan.Zero, offsetRow.LastConfirmedAtUtc.Offset, "VPN timestamps are returned as UTC");
    Require(ReadText(test.DatabasePath, "SELECT LastConfirmedAtUtc FROM VpnSessions WHERE EndedAtUtc IS NULL;").EndsWith("+00:00", StringComparison.Ordinal), "VPN timestamps are persisted in UTC.");
    store.Observe(id, VpnAbsentObservation(), offsetAt);
    Require(store.Observe(id, VpnStoredObservation(), first.StartedAtUtc) is null, "A delayed on observation cannot reopen history before a newer confirmed VPN off boundary.");
    Equal(1, store.GetRecentSessions().Count, "Older on callbacks after closure create no duplicate interval");
    var freshAt = ConfirmVpnPhysicalParent(test, id);
    Require(store.Observe(id, VpnStoredObservation(), freshAt) is not null, "A genuinely newer on observation can create a successor interval.");
}

static void VpnSessionPreservesPhysicalAccounting()
{
    using var test = new SessionTestContext();
    var id = StartVpnPhysicalParent(test);
    test.Usage.AddUsage(new DateOnly(2026, 1, 15), 4096, 512);
    test.Clock.Advance(TimeSpan.FromSeconds(1));
    Require(test.Tracker.RecordSample(id, Wifi().AdapterId, 1, test.Clock.GetUtcNow(), 2048, 256), "Existing physical sampler accepts its own delta.");
    test.Tracker.Flush();
    var before = SnapshotRows(test.DatabasePath, "ConnectionSessions");
    var events = SnapshotPhysicalEvents(test.DatabasePath);
    var usage = SnapshotRows(test.DatabasePath, "daily_usage");
    var store = new VpnSessionStore(test.Usage);
    var timestamp = test.Clock.GetUtcNow();
    store.Observe(id, VpnStoredObservation(VpnRoutingMode.FullTunnel), timestamp);
    store.Observe(id, VpnStoredObservation(VpnRoutingMode.SplitTunnel), timestamp);
    store.Observe(id, VpnAbsentObservation(), timestamp);
    Equal(before, SnapshotRows(test.DatabasePath, "ConnectionSessions"), "VPN lifecycle does not alter physical rows or byte accounting");
    Equal(events, SnapshotPhysicalEvents(test.DatabasePath), "VPN lifecycle preserves every existing physical event field");
    Equal(3, GetVpnEvents(test.History).Length, "VPN lifecycle records only its connected, route changed and disconnected events");
    Equal(usage, SnapshotRows(test.DatabasePath, "daily_usage"), "VPN lifecycle does not alter daily usage");
}

static void VpnSessionParentValidation()
{
    using var test = new SessionTestContext();
    var id = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    var futureAt = test.Clock.GetUtcNow().AddMinutes(1);
    var unconfirmed = ExpectFailure(() => store.Observe(id, VpnStoredObservation(), futureAt), "VPN observation beyond physical confirmation");
    Require(unconfirmed is InvalidOperationException, "Parent confirmation failures produce a useful state error.");
    Equal(0, store.GetRecentSessions().Count, "An unconfirmed parent cannot publish a VPN interval");
    test.Clock.Advance(TimeSpan.FromSeconds(5));
    test.Tracker.ObserveConnection(Disconnected());
    var closed = ExpectFailure(() => store.Observe(id, VpnStoredObservation(), test.Clock.GetUtcNow()), "VPN observation for a closed physical interval");
    Require(closed is InvalidOperationException, "Closed physical parent cannot receive VPN observations.");
    var nonexistent = ExpectFailure(() => store.Observe(999999, VpnStoredObservation(), test.Clock.GetUtcNow()), "VPN observation for a nonexistent physical interval");
    Require(nonexistent is InvalidOperationException, "Nonexistent physical parent cannot receive VPN observations.");
    Equal(0, store.GetRecentSessions().Count, "Invalid parent observations leave VPN history untouched");
}

static void VpnSessionHistoryQueries()
{
    using var test = new SessionTestContext();
    var firstId = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    var first = store.Observe(firstId, VpnStoredObservation(), test.Clock.GetUtcNow())!;
    var offAt = ConfirmVpnPhysicalParent(test, firstId);
    store.Observe(firstId, VpnAbsentObservation(), offAt);
    var secondAt = ConfirmVpnPhysicalParent(test, firstId);
    var second = store.Observe(firstId, VpnStoredObservation(), secondAt)!;
    test.Clock.Advance(TimeSpan.FromSeconds(1));
    test.Tracker.ObserveConnection(Connected(Wifi("Another physical connection")));
    var nextId = test.Tracker.Current!.Id;
    test.Tracker.Flush();
    var next = store.Observe(nextId, VpnStoredObservation(), test.Clock.GetUtcNow())!;
    Require(store.GetSessionsByConnectionSession(firstId).Select(row => row.Id).SequenceEqual(new[] { second.Id, first.Id }), "Parent query returns its intervals in descending start order.");
    Equal(next.Id, store.GetSessionsByConnectionSession(nextId).Single().Id, "Parent query excludes other physical connections");
    Require(store.GetRecentSessions(2).Select(row => row.Id).SequenceEqual(new[] { next.Id, second.Id }), "Recent query applies deterministic newest-first limits.");
    Equal(next.Id, store.GetActiveSession()!.Id, "Current child query returns the only active VPN interval");
    ExpectFailure(() => store.GetRecentSessions(0), "Nonpositive recent VPN limit");
    ExpectFailure(() => store.GetSessionsByConnectionSession(firstId, 0), "Nonpositive parent VPN limit");
}

static void VpnSessionMonitorObservations()
{
    using var test = new SessionTestContext();
    var id = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    var source = new FakeVpnInterfaceSource { Inventory = VpnInventory(VpnInterface()) };
    var verifier = new VpnRouteVerifier(new VpnDetectionService(source), new FakeVpnRouteSource());
    using (var monitor = new VpnSessionMonitor(test.Tracker, store, verifier))
    {
        monitor.Start();
        WaitUntil(() => store.GetActiveSession() is not null, "VPN monitor persists the detected tunnel");
        var first = store.GetActiveSession()!;
        Equal(id, first.ConnectionSessionId, "Monitoring uses the existing physical parent");
        var priorReads = source.Reads;
        monitor.Refresh();
        WaitUntil(() => source.Reads > priorReads, "Explicit refresh obtains another native observation");
        Equal(first.Id, store.GetActiveSession()!.Id, "Repeated monitor observations do not duplicate the interval");
        source.Inventory = VpnInventory();
        monitor.Refresh();
        WaitUntil(() => store.GetActiveSession() is null, "VPN monitor persists the off transition");
        Equal(1, store.GetRecentSessions().Count, "On and off observations create one completed child interval");
        Equal(id, test.History.GetActiveSession()!.Id, "VPN monitoring leaves physical connectivity active");
        Equal(1, test.History.GetRecentEvents().Count(item => !IsVpnEvent(item)), "VPN monitoring creates no physical events");
        Equal(2, GetVpnEvents(test.History).Length, "VPN monitoring records only one connected and one disconnected event");
    }
    Equal(id, test.History.GetActiveSession()!.Id, "Disposing only the VPN monitor does not create or close physical intervals");
    Equal(1, test.History.GetRecentSessions().Count, "The physical interval was never duplicated");
    Equal(3, test.History.GetRecentEvents().Count, "VPN monitor disposal creates no additional events");

    var missingProfile = new ConnectionProfile(999999, "Missing ISP", "Missing device", test.Clock.GetUtcNow(), test.Clock.GetUtcNow());
    test.Clock.Advance(TimeSpan.FromSeconds(1));
    ExpectFailure(() => test.Tracker.ObserveConnection(Connected(Wifi("Failed successor"), missingProfile)), "A failed physical transition pauses confirmation");
    Require(test.Tracker.ConfirmedSession is null, "A failed physical transition does not expose its retained old interval as confirmed connectivity.");
    Equal(id, test.Tracker.Current!.Id, "Rollback retains the original physical row for retry");
    using var queried = new ManualResetEventSlim();
    source.Inventory = VpnInventory(VpnInterface());
    source.DetectionStarted = queried;
    using var paused = new VpnSessionMonitor(test.Tracker, store, verifier);
    paused.Start();
    Require(!queried.Wait(TimeSpan.FromMilliseconds(500)), "VPN monitoring does not query or publish while physical attribution is paused.");
    Require(store.GetActiveSession() is null, "Uncertain retained physical history cannot acquire a new VPN child.");
    test.Clock.Advance(TimeSpan.FromSeconds(1));
    test.Tracker.ObserveConnection(Connected(Wifi("Successful successor")));
    test.Tracker.Flush();
    paused.Refresh();
    WaitUntil(() => store.GetActiveSession() is not null, "A successful physical retry accepts fresh VPN evidence");
    Equal(test.Tracker.Current!.Id, store.GetActiveSession()!.ConnectionSessionId, "After retry the VPN child belongs only to the successfully persisted successor");
}

static void VpnSessionMonitorStaleParent()
{
    using var test = new SessionTestContext();
    var firstId = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    using var queryStarted = new ManualResetEventSlim();
    using var releaseQuery = new ManualResetEventSlim();
    var source = new FakeVpnInterfaceSource
    {
        Inventory = VpnInventory(VpnInterface()),
        DetectionStarted = queryStarted,
        ReleaseDetection = releaseQuery
    };
    var verifier = new VpnRouteVerifier(new VpnDetectionService(source), new FakeVpnRouteSource());
    var monitor = new VpnSessionMonitor(test.Tracker, store, verifier);
    try
    {
        monitor.Start();
        Require(queryStarted.Wait(TimeSpan.FromSeconds(5)), "The delayed native VPN query starts.");
        test.Clock.Advance(TimeSpan.FromSeconds(5));
        test.Tracker.ObserveConnection(Connected(Wifi("Successor Wi-Fi")));
        test.Tracker.Flush();
        Require(test.Tracker.Current!.Id != firstId, "The physical network changes while VPN detection is blocked.");
        releaseQuery.Set();
    }
    finally
    {
        releaseQuery.Set();
        monitor.Dispose();
    }
    Equal(0, store.GetRecentSessions().Count, "A completed stale query does not attach its tunnel to the successor parent");
    source.DetectionStarted = null;
    source.ReleaseDetection = null;
    using var fresh = new VpnSessionMonitor(test.Tracker, store, verifier);
    fresh.Start();
    WaitUntil(() => store.GetActiveSession() is not null, "The successor accepts a fresh VPN observation");
    Equal(test.Tracker.Current!.Id, store.GetActiveSession()!.ConnectionSessionId, "Fresh VPN evidence belongs to the successor physical interval");
    Equal(2, test.History.GetRecentSessions().Count, "Only the genuine physical transition creates physical history");
}

static bool IsVpnEvent(ConnectionEvent item) =>
    item.EventType is "VpnConnected" or "VpnDisconnected" or "VpnRouteChanged";

static ConnectionEvent[] GetVpnEvents(ConnectionHistoryStore history) =>
    history.GetRecentEvents().Where(IsVpnEvent).OrderBy(item => item.Id).ToArray();

static string SnapshotPhysicalEvents(string path) => SnapshotRows(path, "ConnectionEvents",
    "EventType NOT IN ('VpnConnected','VpnDisconnected','VpnRouteChanged')");

static void VpnEventsConnected()
{
    using var test = new SessionTestContext();
    var id = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    store.Observe(id, VpnAbsentObservation(), test.Clock.GetUtcNow());
    Equal(0, GetVpnEvents(test.History).Length, "VPN off without an interval invents no event");
    var at = ConfirmVpnPhysicalParent(test, id);
    var tunnel = DetectedVpn().Interfaces.Single() with { InterfaceName = "Work's tunnel; 日本" };
    store.Observe(id, VpnStoredObservation(VpnRoutingMode.FullTunnel, tunnel), at);

    var item = GetVpnEvents(test.History).Single();
    Equal("VpnConnected", item.EventType, "VPN start event type");
    Equal<long?>(id, item.SessionId, "VPN event references its physical session");
    Equal<long?>(null, item.ProfileId, "An unassigned physical network retains null profile context");
    Equal(Wifi().NetworkType, item.NetworkType, "VPN event retains physical network type");
    Equal(Wifi().NetworkIdentifier, item.NetworkIdentifier, "VPN event retains physical network identity");
    Equal(at, item.OccurredAtUtc, "Connected event occurs at the VPN start boundary");
    Require(item.Message.Contains(tunnel.InterfaceName, StringComparison.Ordinal), "Connected details preserve apostrophes, Unicode and the VPN name.");
    Require(item.Message.Contains("FullTunnel", StringComparison.Ordinal), "Connected details include routing mode.");
    Equal(1, test.History.GetRecentSessions().Count, "The VPN event creates no physical session");
}

static void VpnEventsRepeated()
{
    using var test = new SessionTestContext();
    var id = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    var first = store.Observe(id, VpnStoredObservation(VpnRoutingMode.FullTunnel), test.Clock.GetUtcNow())!;
    var events = SnapshotRows(test.DatabasePath, "ConnectionEvents");
    for (var index = 0; index < 5; index++)
    {
        var at = ConfirmVpnPhysicalParent(test, id);
        var usage = new UsageStore(test.Root);
        store = new VpnSessionStore(usage);
        Equal(first.Id, store.Observe(id, VpnStoredObservation(VpnRoutingMode.FullTunnel), at)!.Id,
            "Repeated observations retain the same VPN interval after reopening");
    }
    Equal(events, SnapshotRows(test.DatabasePath, "ConnectionEvents"), "Liveness updates and reopening do not duplicate any events");
    Equal(1, GetVpnEvents(test.History).Count(item => item.EventType == "VpnConnected"), "Exactly one connected event remains");
    Equal(id, test.History.GetActiveSession()!.Id, "Repeated VPN observations retain the physical parent");
}

static void VpnEventsRouteChanged()
{
    using var test = new SessionTestContext();
    var id = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    var tunnel = DetectedVpn().Interfaces.Single();
    var first = store.Observe(id, VpnStoredObservation(VpnRoutingMode.FullTunnel, tunnel), test.Clock.GetUtcNow())!;
    var changedAt = ConfirmVpnPhysicalParent(test, id);
    var changed = store.Observe(id, VpnStoredObservation(VpnRoutingMode.SplitTunnel, tunnel), changedAt)!;
    var item = GetVpnEvents(test.History).Single(item => item.EventType == "VpnRouteChanged");
    Equal(changedAt, item.OccurredAtUtc, "Route event occurs at the actual changed observation");
    Equal<long?>(id, item.SessionId, "Route event uses the same physical session");
    Require(item.Message.Contains(tunnel.InterfaceName, StringComparison.Ordinal), "Route details include the VPN name.");
    Require(item.Message.Contains("FullTunnel", StringComparison.Ordinal) && item.Message.Contains("SplitTunnel", StringComparison.Ordinal),
        "Route details explain both the previous and new mode.");
    for (var index = 0; index < 3; index++)
    {
        var at = ConfirmVpnPhysicalParent(test, id);
        store.Observe(id, VpnStoredObservation(VpnRoutingMode.SplitTunnel, tunnel with { InterfaceName = "Renamed display alias" }), at);
    }
    Equal(1, GetVpnEvents(test.History).Count(item => item.EventType == "VpnRouteChanged"), "Unchanged mode and friendly name updates create no route events");
    Equal(first.Id, changed.Id, "Route changes do not restart the VPN interval");
    Equal(1, store.GetRecentSessions().Count, "Route changes do not duplicate VPN intervals");
    Equal(id, test.History.GetActiveSession()!.Id, "Route changes do not restart the physical interval");
}

static void VpnEventsDisconnected()
{
    using var test = new SessionTestContext();
    var id = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    var tunnel = DetectedVpn().Interfaces.Single();
    store.Observe(id, VpnStoredObservation(VpnRoutingMode.FullTunnel, tunnel), test.Clock.GetUtcNow());
    var endedAt = ConfirmVpnPhysicalParent(test, id, 5);
    store.Observe(id, VpnAbsentObservation(), endedAt);
    for (var index = 0; index < 3; index++)
        new VpnSessionStore(test.DatabasePath).Observe(id, VpnAbsentObservation(), ConfirmVpnPhysicalParent(test, id));
    var item = GetVpnEvents(test.History).Single(item => item.EventType == "VpnDisconnected");
    Equal(endedAt, item.OccurredAtUtc, "Disconnect event uses the VPN end boundary");
    Equal<long?>(id, item.SessionId, "Disconnect event retains the physical parent");
    Require(item.Message.Contains(tunnel.InterfaceName, StringComparison.Ordinal) && item.Message.Contains("FullTunnel", StringComparison.Ordinal),
        "Disconnect details include VPN name and final routing mode.");
    Require(item.Message.Contains("VpnDisconnected", StringComparison.Ordinal), "Disconnect details explain the stored end reason.");
    Equal(2, GetVpnEvents(test.History).Length, "Repeated off observations create no duplicate disconnect events");
    Equal(id, test.History.GetActiveSession()!.Id, "VPN off leaves the physical connection active");
}

static void VpnEventsPhysicalContext()
{
    using var test = new SessionTestContext();
    var id = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    store.Observe(id, VpnStoredObservation(VpnRoutingMode.FullTunnel), test.Clock.GetUtcNow());
    var connected = GetVpnEvents(test.History).Single();
    var profile = test.Profiles.CreateProfile("DITO", "Vivo Phone");
    test.Clock.Advance(TimeSpan.FromSeconds(1));
    test.Tracker.ObserveConnection(Connected(Wifi(), profile));
    test.Tracker.Flush();
    var at = ConfirmVpnPhysicalParent(test, id);
    store.Observe(id, VpnStoredObservation(VpnRoutingMode.SplitTunnel), at);
    store.Observe(id, VpnAbsentObservation(), at);
    var events = GetVpnEvents(test.History);
    Equal<long?>(null, events.Single(item => item.EventType == "VpnConnected").ProfileId, "Assigning the physical profile does not rewrite the old VPN event");
    Equal(connected, events.Single(item => item.EventType == "VpnConnected"), "Existing connected event remains immutable historical context");
    foreach (var item in events.Where(item => item.EventType != "VpnConnected"))
    {
        Equal<long?>(profile.Id, item.ProfileId, "New VPN events use the current physical profile");
        Equal<long?>(id, item.SessionId, "Profile assignment retains the physical session ID");
        Equal(Wifi().NetworkIdentifier, item.NetworkIdentifier, "New events retain the real physical network identity");
    }
    Equal(1, test.History.GetRecentSessions().Count, "Neither profile assignment nor VPN changes duplicate the physical interval");
}

static void VpnEventsParentClosure()
{
    foreach (var transition in new[] { "Disconnected", "NetworkChanged", "ApplicationExit" })
    {
        using var test = new SessionTestContext();
        var id = StartVpnPhysicalParent(test);
        var store = new VpnSessionStore(test.Usage);
        store.Observe(id, VpnStoredObservation(VpnRoutingMode.SplitTunnel), test.Clock.GetUtcNow());
        test.Clock.Advance(TimeSpan.FromSeconds(5));
        if (transition == "Disconnected")
        {
            test.Tracker.ObserveConnection(Disconnected());
            test.Tracker.ObserveConnection(Disconnected());
        }
        else if (transition == "NetworkChanged")
        {
            test.Tracker.ObserveConnection(Connected(Wifi("Successor physical Wi-Fi")));
            test.Tracker.ObserveConnection(Connected(Wifi("Successor physical Wi-Fi")));
        }
        else
        {
            test.Tracker.Close();
            test.Tracker.Close();
        }
        var disconnected = GetVpnEvents(test.History).Single(item => item.EventType == "VpnDisconnected");
        var parent = test.History.GetRecentSessions().Single(item => item.Id == id);
        Equal<long?>(id, disconnected.SessionId, $"{transition} disconnect references the original physical parent");
        Equal<DateTimeOffset?>(parent.EndedAtUtc, disconnected.OccurredAtUtc, "VPN disconnect event uses the parent's closure boundary");
        var expectedReason = transition == "ApplicationExit" ? "ApplicationExit" : "PhysicalSessionEnded";
        Require(disconnected.Message.Contains(expectedReason, StringComparison.Ordinal), "Details distinguish the physical lifecycle cause from a confirmed VPN off observation.");
        Equal(2, GetVpnEvents(test.History).Length, "Repeated physical observations cannot duplicate VPN disconnect events");
        var physical = test.History.GetRecentEvents().Where(item => !IsVpnEvent(item)).OrderBy(item => item.Id).ToArray();
        Require(physical.Select(item => item.EventType).SequenceEqual(new[] { "Connected", transition }), "Existing physical transition event kinds remain unchanged.");
        Equal(transition == "NetworkChanged" ? 2 : 1, test.History.GetRecentSessions().Count, "Only a real physical transition can create a successor physical interval");
    }
}

static void VpnEventsAtomicFailure()
{
    foreach (var operation in new[] { "VpnConnected", "VpnRouteChanged", "VpnDisconnected", "PhysicalClosure" })
    {
        using var test = new SessionTestContext();
        var id = StartVpnPhysicalParent(test);
        var store = new VpnSessionStore(test.Usage);
        if (operation != "VpnConnected")
            store.Observe(id, VpnStoredObservation(VpnRoutingMode.FullTunnel), test.Clock.GetUtcNow());
        var at = ConfirmVpnPhysicalParent(test, id);
        var beforeVpn = SnapshotRows(test.DatabasePath, "VpnSessions");
        var beforePhysical = SnapshotRows(test.DatabasePath, "ConnectionSessions");
        var beforeEvents = SnapshotRows(test.DatabasePath, "ConnectionEvents");
        var eventType = operation == "PhysicalClosure" ? "VpnDisconnected" : operation;
        using (var connection = Open(test.DatabasePath))
            Execute(connection, $"CREATE TRIGGER FailVpnEvent BEFORE INSERT ON ConnectionEvents WHEN NEW.EventType='{eventType}' BEGIN SELECT RAISE(ABORT,'simulate VPN event storage failure'); END;");
        Action transition = () =>
        {
            if (operation == "PhysicalClosure")
                test.History.Transition(id, at, "ApplicationExit", 0, 0, null, null, at, "ApplicationExit", "Test physical closure");
            else if (operation == "VpnDisconnected") store.Observe(id, VpnAbsentObservation(), at);
            else store.Observe(id, VpnStoredObservation(operation == "VpnRouteChanged" ? VpnRoutingMode.SplitTunnel : VpnRoutingMode.FullTunnel), at);
        };
        var failure = ExpectFailure(transition, $"Failed {operation} event insertion");
        Require(failure is SqliteException { SqliteErrorCode: 19 }, "A failed event insert aborts the lifecycle transaction.");
        Equal(beforeVpn, SnapshotRows(test.DatabasePath, "VpnSessions"), "Event failure rolls back every VPN interval field");
        Equal(beforePhysical, SnapshotRows(test.DatabasePath, "ConnectionSessions"), "Event failure cannot partially close the physical parent");
        Equal(beforeEvents, SnapshotRows(test.DatabasePath, "ConnectionEvents"), "Event failure leaves no partial event history");
        using (var connection = Open(test.DatabasePath)) Execute(connection, "DROP TRIGGER FailVpnEvent;");
        transition();
        Equal(1, GetVpnEvents(test.History).Count(item => item.EventType == eventType), "Retry publishes exactly one corresponding VPN event");
    }
}

static void VpnEventsExistingVersionFour()
{
    using var test = new SessionTestContext();
    var id = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    store.Observe(id, VpnStoredObservation(VpnRoutingMode.FullTunnel), test.Clock.GetUtcNow());
    store.Observe(id, VpnAbsentObservation(), ConfirmVpnPhysicalParent(test, id));
    var activeAt = ConfirmVpnPhysicalParent(test, id);
    var active = store.Observe(id, VpnStoredObservation(VpnRoutingMode.FullTunnel), activeAt)!;
    var triggerNames = new[] { "TR_VpnSessions_ConnectedEvent", "TR_VpnSessions_DisconnectedEvent", "TR_VpnSessions_RouteChangedEvent" };
    using (var connection = Open(test.DatabasePath))
    {
        foreach (var name in triggerNames) Execute(connection, $"DROP TRIGGER {name};");
        Execute(connection, "DELETE FROM ConnectionEvents WHERE EventType IN ('VpnConnected','VpnDisconnected','VpnRouteChanged');");
    }
    var tables = new[] { "daily_usage", "ConnectionProfiles", "NetworkMappings", "ConnectionSessions", "ConnectionEvents", "VpnSessions" };
    var before = tables.ToDictionary(table => table, table => (
        Rows: SnapshotRows(test.DatabasePath, table),
        Schema: ReadText(test.DatabasePath, $"SELECT sql FROM sqlite_schema WHERE name='{table}';"),
        Root: ReadInteger(test.DatabasePath, $"SELECT rootpage FROM sqlite_schema WHERE name='{table}';")));
    var upgraded = new UsageStore(test.Root);
    Equal(5L, ReadInteger(test.DatabasePath, "PRAGMA user_version;"), "Installing VPN events does not change current schema version five");
    foreach (var table in tables)
    {
        Equal(before[table].Rows, SnapshotRows(test.DatabasePath, table), $"Existing {table} data is not rewritten or backfilled");
        Equal(before[table].Schema, ReadText(test.DatabasePath, $"SELECT sql FROM sqlite_schema WHERE name='{table}';"), $"Existing {table} schema is preserved");
        Equal(before[table].Root, ReadInteger(test.DatabasePath, $"SELECT rootpage FROM sqlite_schema WHERE name='{table}';"), $"Existing {table} B-tree is preserved");
    }
    foreach (var name in triggerNames)
        Equal(1L, ReadInteger(test.DatabasePath, $"SELECT count(*) FROM sqlite_schema WHERE type='trigger' AND name='{name}';"), "An existing version-four database gains each VPN event trigger");
    store = new VpnSessionStore(upgraded);
    Equal(active.Id, store.Observe(id, VpnStoredObservation(VpnRoutingMode.FullTunnel), activeAt)!.Id, "Reopening retains an already active interval");
    Equal(0, GetVpnEvents(test.History).Length, "Installation and identical observations invent no retroactive VPN connected event");
    store.Observe(id, VpnStoredObservation(VpnRoutingMode.SplitTunnel), ConfirmVpnPhysicalParent(test, id));
    Equal("VpnRouteChanged", GetVpnEvents(test.History).Single().EventType, "The next actual state change is recorded after trigger installation");

    using var broken = new SessionTestContext();
    var brokenId = StartVpnPhysicalParent(broken);
    new VpnSessionStore(broken.Usage).Observe(brokenId, VpnStoredObservation(), broken.Clock.GetUtcNow());
    using (var connection = Open(broken.DatabasePath))
    {
        foreach (var name in triggerNames) Execute(connection, $"DROP TRIGGER {name};");
        Execute(connection, "CREATE TRIGGER TR_VpnSessions_RouteChangedEvent AFTER UPDATE OF RoutingMode ON VpnSessions BEGIN SELECT 1; END;");
    }
    var beforeSchema = SnapshotRows(broken.DatabasePath, "sqlite_schema");
    var brokenVpn = SnapshotRows(broken.DatabasePath, "VpnSessions");
    var brokenEvents = SnapshotRows(broken.DatabasePath, "ConnectionEvents");
    ExpectStartupFailure(broken.Root);
    Equal(beforeSchema, SnapshotRows(broken.DatabasePath, "sqlite_schema"), "A malformed existing event trigger rolls back all attempted missing-trigger installations");
    Equal(brokenVpn, SnapshotRows(broken.DatabasePath, "VpnSessions"), "Failed event setup preserves existing VPN interval fields");
    Equal(brokenEvents, SnapshotRows(broken.DatabasePath, "ConnectionEvents"), "Failed event setup preserves existing timeline rows");
    Equal(5L, ReadInteger(broken.DatabasePath, "PRAGMA user_version;"), "Failed event setup preserves current version five");
}

static void VpnEventsTunnelReplacement()
{
    using var test = new SessionTestContext();
    var id = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    var original = DetectedVpn().Interfaces.Single();
    store.Observe(id, VpnStoredObservation(VpnRoutingMode.FullTunnel, original), test.Clock.GetUtcNow());
    var at = ConfirmVpnPhysicalParent(test, id);
    var next = original with { InterfaceIndex = 43, InterfaceGuid = Guid.NewGuid(), InterfaceName = "Second tunnel" };
    store.Observe(id, VpnStoredObservation(VpnRoutingMode.SplitTunnel, next), at);
    store.Observe(id, VpnStoredObservation(VpnRoutingMode.SplitTunnel, next), at);
    var events = GetVpnEvents(test.History);
    Require(events.Select(item => item.EventType).SequenceEqual(new[] { "VpnConnected", "VpnDisconnected", "VpnConnected" }),
        "Tunnel replacement records the prior disconnect before the successor connect, without a route-only event.");
    Require(events.All(item => item.SessionId == id), "Both VPN intervals and all events belong to the same physical parent.");
    Equal(at, events[1].OccurredAtUtc, "Replacement disconnect timestamp");
    Equal(at, events[2].OccurredAtUtc, "Replacement connect timestamp");
    Require(events[1].Message.Contains(original.InterfaceName, StringComparison.Ordinal) && events[1].Message.Contains("TunnelChanged", StringComparison.Ordinal),
        "Replacement disconnect details identify the previous tunnel and its reason.");
    Require(events[2].Message.Contains(next.InterfaceName, StringComparison.Ordinal), "Replacement connect identifies the new tunnel.");
    Equal(1, test.History.GetRecentSessions().Count, "Tunnel replacement creates no physical session");
}

static void AssertVpnParentSafety(SessionTestContext test)
{
    Equal(0L, ReadInteger(test.DatabasePath, """
        SELECT count(*) FROM VpnSessions v LEFT JOIN ConnectionSessions p ON p.Id=v.ConnectionSessionId
        WHERE v.EndedAtUtc IS NULL AND (p.Id IS NULL OR p.EndedAtUtc IS NOT NULL);
        """), "No active VPN interval has a closed or missing physical parent");
    Require(ReadInteger(test.DatabasePath, "SELECT count(*) FROM VpnSessions WHERE EndedAtUtc IS NULL;") <= 1,
        "There is at most one active VPN interval.");
}

static void VpnLifecycleSleep()
{
    using var test = new SessionTestContext();
    var id = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    var first = store.Observe(id, VpnStoredObservation(VpnRoutingMode.FullTunnel), test.Clock.GetUtcNow())!;
    test.Clock.Advance(TimeSpan.FromSeconds(15));
    var sleepAt = test.Clock.GetUtcNow();
    test.Tracker.Sleep();
    test.Tracker.Sleep();

    Require(store.GetActiveSession() is null && test.History.GetActiveSession() is null,
        "Sleep closes both intervals without leaving an active VPN child.");
    var ended = store.GetRecentSessions().Single();
    Equal(first.Id, ended.Id, "Sleep closes the existing VPN interval");
    Equal<DateTimeOffset?>(sleepAt, ended.EndedAtUtc, "VPN ends at the observed sleep boundary");
    Equal("Sleep", ended.EndReason, "VPN sleep end reason");
    var events = test.History.GetRecentEvents();
    var disconnected = events.Single(item => item.EventType == "VpnDisconnected");
    Equal(sleepAt, disconnected.OccurredAtUtc, "VPN disconnect event uses the sleep boundary");
    Require(disconnected.Message.Contains("Sleep", StringComparison.Ordinal), "VPN event explains suspension.");
    Require(disconnected.Id < events.Single(item => item.EventType == "Sleep").Id,
        "VPN closure precedes the existing physical sleep event in the same lifecycle transaction.");
    Equal(2, GetVpnEvents(test.History).Length, "Repeated sleep callbacks do not duplicate VPN events");
    AssertVpnParentSafety(test);
}

static void VpnLifecycleResumeActive()
{
    using var test = new SessionTestContext();
    var firstId = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    using var queried = new ManualResetEventSlim();
    var source = new FakeVpnInterfaceSource { Inventory = VpnInventory(VpnInterface()) };
    var verifier = new VpnRouteVerifier(new VpnDetectionService(source), new FakeVpnRouteSource());
    using var monitor = new VpnSessionMonitor(test.Tracker, store, verifier);
    monitor.Start();
    WaitUntil(() => store.GetActiveSession() is not null, "Initial active VPN is observed");
    var firstVpn = store.GetActiveSession()!;
    monitor.Suspend();
    monitor.Suspend();
    test.Tracker.Sleep();
    test.Clock.Advance(TimeSpan.FromHours(1));
    test.Tracker.Resume();
    test.Tracker.Resume();
    source.DetectionStarted = queried;
    monitor.Resume();
    monitor.Resume();
    Require(!queried.Wait(TimeSpan.FromMilliseconds(500)), "Resume does not reuse an old parent before fresh physical detection.");
    Require(store.GetActiveSession() is null, "Resume alone does not presume the old VPN survived.");

    test.Tracker.ObserveConnection(Connected(Wifi()));
    test.Tracker.Flush();
    monitor.Refresh();
    WaitUntil(() => store.GetActiveSession() is not null, "Resume freshly detects the currently active VPN");
    var next = store.GetActiveSession()!;
    Require(next.Id != firstVpn.Id, "Same tunnel after sleep starts a new VPN interval.");
    Require(next.ConnectionSessionId != firstId, "Resumed VPN belongs to the newly detected physical session.");
    Equal(test.Tracker.Current!.Id, next.ConnectionSessionId, "Resumed VPN uses the current physical parent");
    Equal(2, test.History.GetRecentSessions().Count, "Only the physical sleep/resume lifecycle creates a new parent");
    Equal(2, store.GetRecentSessions().Count, "There is one VPN interval on each side of sleep");
    Equal(2, GetVpnEvents(test.History).Count(item => item.EventType == "VpnConnected"), "Fresh resume records one new connected event");
    Equal(1, GetVpnEvents(test.History).Count(item => item.EventType == "VpnDisconnected"), "Sleep records only one disconnected event");
    AssertVpnParentSafety(test);
}

static void VpnLifecycleResumeOff()
{
    using var test = new SessionTestContext();
    StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    var source = new FakeVpnInterfaceSource { Inventory = VpnInventory(VpnInterface()) };
    var verifier = new VpnRouteVerifier(new VpnDetectionService(source), new FakeVpnRouteSource());
    using var monitor = new VpnSessionMonitor(test.Tracker, store, verifier);
    monitor.Start();
    WaitUntil(() => store.GetActiveSession() is not null, "Initial VPN interval is observed");
    monitor.Suspend();
    test.Tracker.Sleep();
    source.Inventory = VpnInventory();
    using var queried = new ManualResetEventSlim();
    source.DetectionStarted = queried;
    test.Clock.Advance(TimeSpan.FromMinutes(20));
    test.Tracker.Resume();
    test.Tracker.ObserveConnection(Connected(Wifi()));
    test.Tracker.Flush();
    monitor.Resume();
    Require(queried.Wait(TimeSpan.FromSeconds(5)), "Resume obtains a fresh native VPN-off observation.");
    monitor.Dispose();
    Require(store.GetActiveSession() is null, "VPN off after resume creates no active child.");
    Equal(1, store.GetRecentSessions().Count, "No successor VPN interval is invented when VPN is off");
    Equal(2, GetVpnEvents(test.History).Length, "Fresh VPN-off observation does not duplicate the sleep disconnect");
    Require(test.History.GetActiveSession() is not null, "Physical connectivity remains active when resumed VPN is off.");
    AssertVpnParentSafety(test);
}

static void VpnLifecycleCrashBoundary()
{
    using var test = new SessionTestContext();
    var id = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    store.Observe(id, VpnStoredObservation(VpnRoutingMode.FullTunnel), test.Clock.GetUtcNow());
    var vpnConfirmed = ConfirmVpnPhysicalParent(test, id, 10);
    var stale = store.Observe(id, VpnStoredObservation(VpnRoutingMode.FullTunnel), vpnConfirmed)!;
    var parentConfirmed = ConfirmVpnPhysicalParent(test, id, 20);
    var beforeRecovery = SnapshotRows(test.DatabasePath, "daily_usage");
    test.Clock.Advance(TimeSpan.FromHours(8));
    var recoveredAt = test.Clock.GetUtcNow();
    var restarted = new ConnectionSessionTracker(new ConnectionHistoryStore(new UsageStore(test.Root)), test.Clock);

    Require(restarted.Current is null && store.GetActiveSession() is null, "Startup recovery does not carry forward either old active interval.");
    var child = store.GetRecentSessions().Single();
    Equal(stale.Id, child.Id, "Recovery closes the old VPN row");
    Equal(vpnConfirmed, child.LastConfirmedAtUtc, "Recovery does not move persisted VPN confirmation");
    Equal<DateTimeOffset?>(vpnConfirmed, child.EndedAtUtc, "VPN crash history ends exactly at its own persisted confirmation");
    Equal("UnexpectedExit", child.EndReason, "VPN crash end reason is explicit");
    var parent = test.History.GetRecentSessions().Single();
    Equal<DateTimeOffset?>(parentConfirmed, parent.EndedAtUtc, "Existing physical recovery retains its independent later confirmation");
    Equal(parentConfirmed, parent.LastConfirmedAtUtc, "VPN recovery does not change physical recovery timestamps");
    var disconnected = GetVpnEvents(test.History).Single(item => item.EventType == "VpnDisconnected");
    Equal(vpnConfirmed, disconnected.OccurredAtUtc, "Recovered VPN disconnect is timestamped at its own evidence boundary");
    Require(disconnected.Message.Contains("UnexpectedExit", StringComparison.Ordinal), "Disconnect details explain uncertain termination.");
    Equal(recoveredAt, test.History.GetRecentEvents().Single(item => item.EventType == "SessionRecoveredAfterUnexpectedExit").OccurredAtUtc,
        "The physical recovery event retains its startup timestamp");
    Equal(beforeRecovery, SnapshotRows(test.DatabasePath, "daily_usage"), "VPN recovery leaves daily usage unchanged");
    AssertVpnParentSafety(test);
}

static void VpnLifecycleStartupActive()
{
    using var test = new SessionTestContext();
    var oldParent = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    var oldVpn = store.Observe(oldParent, VpnStoredObservation(VpnRoutingMode.SplitTunnel), test.Clock.GetUtcNow())!;
    test.Clock.Advance(TimeSpan.FromHours(2));
    var restarted = new ConnectionSessionTracker(new ConnectionHistoryStore(new UsageStore(test.Root)), test.Clock);
    var source = new FakeVpnInterfaceSource { Inventory = VpnInventory(VpnInterface()) };
    var verifier = new VpnRouteVerifier(new VpnDetectionService(source), new FakeVpnRouteSource());
    using var queried = new ManualResetEventSlim();
    source.DetectionStarted = queried;
    using var monitor = new VpnSessionMonitor(restarted, store, verifier);
    monitor.Start();
    Require(!queried.Wait(TimeSpan.FromMilliseconds(500)), "Startup VPN observations wait for fresh physical identity.");
    restarted.ObserveConnection(Connected(Wifi()));
    restarted.Flush();
    monitor.Refresh();
    WaitUntil(() => store.GetActiveSession() is not null, "Startup detects an active VPN after physical recovery");
    var current = store.GetActiveSession()!;
    Require(current.Id != oldVpn.Id && current.ConnectionSessionId != oldParent,
        "Fresh startup creates new physical and VPN intervals even on the same network/tunnel.");
    Equal(restarted.Current!.Id, current.ConnectionSessionId, "New VPN interval belongs to the recovered run's current parent");
    Equal(VpnRoutingMode.FullTunnel, current.RoutingMode, "New startup routes are verified rather than inherited from the old interval");
    Equal(2, store.GetRecentSessions().Count, "Restart produces one closed and one fresh VPN interval");
    Equal(1, test.History.GetRecentEvents().Count(item => item.EventType == "SessionRecoveredAfterUnexpectedExit"), "Recovery event is recorded once");
    AssertVpnParentSafety(test);
}

static void VpnLifecycleNormalExit()
{
    using var test = new SessionTestContext();
    var id = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    store.Observe(id, VpnStoredObservation(), test.Clock.GetUtcNow());
    test.Clock.Advance(TimeSpan.FromSeconds(7));
    Require(test.Tracker.RecordSample(id, Wifi().AdapterId, 1, test.Clock.GetUtcNow(), 8192, 512), "Unflushed physical sample is accepted before shutdown.");
    var exitAt = test.Clock.GetUtcNow();
    test.Tracker.Flush();
    test.Tracker.Close();
    test.Tracker.Close();
    var child = store.GetRecentSessions().Single();
    Equal("ApplicationExit", child.EndReason, "Graceful VPN exit has the application shutdown cause");
    Equal<DateTimeOffset?>(exitAt, child.EndedAtUtc, "Graceful VPN exit uses the physical shutdown boundary");
    var parent = test.History.GetRecentSessions().Single();
    Equal(8192L, parent.DownloadBytes, "Shutdown persists pending physical downloads without VPN accounting");
    Equal(512L, parent.UploadBytes, "Shutdown persists pending physical uploads");
    Require(store.GetActiveSession() is null && test.History.GetActiveSession() is null, "Normal exit leaves no active intervals.");
    var vpnEvents = GetVpnEvents(test.History);
    Equal(1, vpnEvents.Count(item => item.EventType == "VpnDisconnected"), "Repeated close does not duplicate VPN disconnection");
    Require(vpnEvents.Single(item => item.EventType == "VpnDisconnected").Message.Contains("ApplicationExit", StringComparison.Ordinal),
        "VPN event identifies a clean exit.");
    var history = SnapshotRows(test.DatabasePath, "ConnectionEvents");
    _ = new ConnectionSessionTracker(new ConnectionHistoryStore(new UsageStore(test.Root)), test.Clock);
    Equal(history, SnapshotRows(test.DatabasePath, "ConnectionEvents"), "Clean shutdown requires no startup recovery or duplicate disconnect");
    AssertVpnParentSafety(test);
}

static void VpnLifecycleParentSafety()
{
    using var test = new SessionTestContext();
    var id = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    store.Observe(id, VpnStoredObservation(), test.Clock.GetUtcNow());
    var endedAt = ConfirmVpnPhysicalParent(test, id, 10);
    using (var connection = Open(test.DatabasePath))
        Execute(connection, $"UPDATE ConnectionSessions SET EndedAtUtc='{endedAt:O}',EndReason='Disconnected' WHERE Id={id};");
    Require(store.GetActiveSession() is null, "The database closure trigger remains a safety net for physical writes outside the lifecycle service.");
    Equal("PhysicalSessionEnded", store.GetRecentSessions().Single().EndReason, "Generic closure safety retains its existing end reason");
    using (var connection = Open(test.DatabasePath))
    {
        var failure = ExpectFailure(() => Execute(connection, "UPDATE VpnSessions SET EndedAtUtc=NULL,EndReason=NULL;"), "Reopening a child with a closed parent");
        Require(failure is SqliteException { SqliteErrorCode: 19 }, "Database constraints reject orphan child reactivation.");
    }
    AssertVpnParentSafety(test);
    Equal(1, GetVpnEvents(test.History).Count(item => item.EventType == "VpnDisconnected"), "Rejected child reactivation adds no events");
}

static void VpnLifecycleRecoveryDedup()
{
    using var test = new SessionTestContext();
    var id = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    store.Observe(id, VpnStoredObservation(), test.Clock.GetUtcNow());
    var vpnConfirmed = ConfirmVpnPhysicalParent(test, id, 5);
    store.Observe(id, VpnStoredObservation(), vpnConfirmed);
    ConfirmVpnPhysicalParent(test, id, 5);
    test.Clock.Advance(TimeSpan.FromHours(1));
    Equal(1, test.History.RecoverUnfinishedSessions(test.Clock.GetUtcNow()).Count, "First recovery handles exactly one stale physical interval");
    var intervals = SnapshotRows(test.DatabasePath, "VpnSessions");
    var events = SnapshotRows(test.DatabasePath, "ConnectionEvents");
    for (var index = 0; index < 3; index++)
    {
        test.Clock.Advance(TimeSpan.FromMinutes(1));
        Equal(0, test.History.RecoverUnfinishedSessions(test.Clock.GetUtcNow()).Count, "Repeated recovery has no stale interval left");
        _ = new ConnectionSessionTracker(new ConnectionHistoryStore(new UsageStore(test.Root)), test.Clock);
    }
    Equal(intervals, SnapshotRows(test.DatabasePath, "VpnSessions"), "Repeated recovery leaves completed VPN history immutable");
    Equal(events, SnapshotRows(test.DatabasePath, "ConnectionEvents"), "Repeated startup/recovery does not duplicate any timeline events");
    Equal(1, GetVpnEvents(test.History).Count(item => item.EventType == "VpnDisconnected"), "One recovered VPN disconnect exists");
    Equal(1, test.History.GetRecentEvents().Count(item => item.EventType == "SessionRecoveredAfterUnexpectedExit"), "One physical recovery event exists");
    AssertVpnParentSafety(test);
}

static void VpnLifecycleAtomicFailure()
{
    foreach (var lifecycle in new[] { "Sleep", "ApplicationExit", "UnexpectedExit" })
    {
        using var test = new SessionTestContext();
        var id = StartVpnPhysicalParent(test);
        var store = new VpnSessionStore(test.Usage);
        store.Observe(id, VpnStoredObservation(VpnRoutingMode.FullTunnel), test.Clock.GetUtcNow());
        var vpnLast = ConfirmVpnPhysicalParent(test, id, 5);
        store.Observe(id, VpnStoredObservation(VpnRoutingMode.FullTunnel), vpnLast);
        var parentLast = ConfirmVpnPhysicalParent(test, id, 5);
        test.Clock.Advance(TimeSpan.FromSeconds(5));
        var boundary = test.Clock.GetUtcNow();
        var before = new[] { "ConnectionSessions", "VpnSessions", "ConnectionEvents" }
            .ToDictionary(table => table, table => SnapshotRows(test.DatabasePath, table));
        using (var connection = Open(test.DatabasePath))
            Execute(connection, "CREATE TRIGGER FailLifecycleEvent BEFORE INSERT ON ConnectionEvents WHEN NEW.EventType='VpnDisconnected' BEGIN SELECT RAISE(ABORT,'simulated lifecycle event failure'); END;");
        Action transition = lifecycle == "Sleep" ? test.Tracker.Sleep
            : lifecycle == "ApplicationExit" ? test.Tracker.Close
            : () => test.History.RecoverUnfinishedSessions(test.Clock.GetUtcNow());
        var failure = ExpectFailure(transition, $"Failed {lifecycle} VPN lifecycle event");
        Require(failure is SqliteException { SqliteErrorCode: 19 }, "Child event failure aborts the complete lifecycle transaction.");
        foreach (var table in before.Keys)
            Equal(before[table], SnapshotRows(test.DatabasePath, table), $"Failed {lifecycle} rolls back all {table} fields");
        AssertVpnParentSafety(test);
        using (var connection = Open(test.DatabasePath)) Execute(connection, "DROP TRIGGER FailLifecycleEvent;");
        if (lifecycle == "Sleep")
        {
            Require(test.Tracker.IsSleeping && test.Tracker.ConfirmedSession is null, "Failed sleep still suspends physical and VPN admission.");
            test.Clock.Advance(TimeSpan.FromHours(1));
            test.Tracker.Resume();
        }
        else transition();
        var ended = store.GetRecentSessions().Single();
        var expectedEnd = lifecycle == "UnexpectedExit" ? vpnLast : boundary;
        Equal<DateTimeOffset?>(expectedEnd, ended.EndedAtUtc, "Retry retains the correct lifecycle evidence boundary");
        Equal(lifecycle, ended.EndReason, "Retry records the original lifecycle cause");
        var parent = test.History.GetRecentSessions().Single();
        Equal<DateTimeOffset?>(lifecycle == "UnexpectedExit" ? parentLast : boundary, parent.EndedAtUtc,
            "Retry retains the existing physical recovery/suspension/shutdown boundary");
        Equal(1, GetVpnEvents(test.History).Count(item => item.EventType == "VpnDisconnected"), "Successful retry records one child disconnect");
        AssertVpnParentSafety(test);
    }
}

static void VpnLifecycleStaleSleepQuery()
{
    using var test = new SessionTestContext();
    var oldParent = StartVpnPhysicalParent(test);
    var store = new VpnSessionStore(test.Usage);
    store.Observe(oldParent, VpnStoredObservation(VpnRoutingMode.SplitTunnel), test.Clock.GetUtcNow());
    using var queryStarted = new ManualResetEventSlim();
    using var releaseQuery = new ManualResetEventSlim();
    var source = new FakeVpnInterfaceSource
    {
        Inventory = VpnInventory(VpnInterface()),
        DetectionStarted = queryStarted,
        ReleaseDetection = releaseQuery
    };
    var verifier = new VpnRouteVerifier(new VpnDetectionService(source), new FakeVpnRouteSource());
    using var monitor = new VpnSessionMonitor(test.Tracker, store, verifier);
    try
    {
        monitor.Start();
        Require(queryStarted.Wait(TimeSpan.FromSeconds(5)), "Pre-sleep native VPN query is deliberately blocked.");
        var suspension = Task.Run(monitor.Suspend);
        Require(suspension.Wait(TimeSpan.FromSeconds(2)), "Suspend invalidates an in-flight query without waiting for native completion.");
        test.Clock.Advance(TimeSpan.FromSeconds(5));
        test.Tracker.Sleep();
        test.Clock.Advance(TimeSpan.FromHours(1));
        test.Tracker.Resume();
        test.Tracker.ObserveConnection(Connected(Wifi()));
        test.Tracker.Flush();
        var resumedParent = test.Tracker.Current!.Id;
        Require(resumedParent != oldParent, "Sleep/resume creates a new physical parent even on the same SSID.");
        source.Inventory = VpnInventory(VpnInterface() with { InterfaceName = "Fresh resumed tunnel", InterfaceIndex = 43 });
        monitor.Resume();
        monitor.Refresh();
        // Allow the resumed callback to collide with the old query. It must retry promptly,
        // rather than lose the fresh generation until the ordinary 30-second persistence tick.
        Require(!releaseQuery.Wait(TimeSpan.FromMilliseconds(500)), "Old native query remains blocked across resume.");
        releaseQuery.Set();
        WaitUntil(() => store.GetActiveSession() is not null, "Fresh resumed VPN query retries promptly after the obsolete worker completes");
        var current = store.GetActiveSession()!;
        Equal(resumedParent, current.ConnectionSessionId, "Only fresh evidence attaches to the resumed physical parent");
        Equal<uint?>(43, current.InterfaceIndex, "A captured pre-sleep tunnel result is discarded");
        Equal("Fresh resumed tunnel", current.InterfaceName, "The new interval uses post-resume native identity");
        Equal(2, store.GetRecentSessions().Count, "Only the original and genuinely resumed VPN intervals exist");
        Equal(2, GetVpnEvents(test.History).Count(item => item.EventType == "VpnConnected"), "Stale query cannot manufacture a third VPN connection event");
        AssertVpnParentSafety(test);
    }
    finally { releaseQuery.Set(); }
}

static void VpnLifecycleKilledOwner()
{
    using var workspace = new TestWorkspace();
    using var owner = new LeaseChildProcess(workspace.Root, "hold-vpn");
    Equal("READY", owner.ReadLine(), "Owned child persists independent physical and VPN confirmation times");
    var history = new ConnectionHistoryStore(workspace.CurrentPath);
    var store = new VpnSessionStore(workspace.CurrentPath);
    var parent = history.GetActiveSession()!;
    var child = store.GetActiveSession()!;
    Require(parent.LastConfirmedAtUtc > child.LastConfirmedAtUtc, "Killed fixture has a later physical confirmation than VPN confirmation.");
    owner.KillOwnedProcess();
    Equal(child, store.GetActiveSession(), "Hard kill bypasses normal VPN shutdown and leaves persisted proof intact");
    using (var next = new LeaseChildProcess(workspace.Root, "start"))
    {
        Equal("ACQUIRED", next.ReadLine(), "Next process acquires ownership and recovers stale histories");
        next.ExpectExit(0);
    }
    var recovered = store.GetRecentSessions().Single();
    Equal<DateTimeOffset?>(child.LastConfirmedAtUtc, recovered.EndedAtUtc, "Real process kill recovery ends VPN exactly at persisted VPN confirmation");
    Equal("UnexpectedExit", recovered.EndReason, "Hard-killed VPN has the crash end reason");
    var recoveredParent = history.GetRecentSessions().Single(item => item.Id == parent.Id);
    Equal<DateTimeOffset?>(parent.LastConfirmedAtUtc, recoveredParent.EndedAtUtc, "Physical hard-kill recovery retains its original separate confirmation");
    Equal(1, GetVpnEvents(history).Count(item => item.EventType == "VpnDisconnected"), "Hard-kill recovery records one VPN disconnect");
    Equal(1, history.GetRecentEvents().Count(item => item.EventType == "SessionRecoveredAfterUnexpectedExit"), "Hard-kill recovery records one physical recovery event");
    Require(store.GetActiveSession() is null && history.GetActiveSession() is null, "The subsequent graceful run leaves no active history.");
}

static void BackgroundAutomaticMonitoring()
{
    using var test = new HeadlessMonitoringContext();
    var initial = test.Service.GetSnapshot();
    var parentId = initial.Session!.Id;
    Require(initial.IsRunning && initial.SelectedAdapter?.Id == test.Adapter.Id,
        "The application core starts without constructing any WPF window or dispatcher.");
    test.AdvanceCounters(2048, 128, sampleNow: false);
    WaitUntil(() => test.Service.GetSnapshot().SessionDownloaded == 2048,
        "The independent one-second sampler runs with no dashboard");
    Equal(128L, test.Service.GetSnapshot().SessionUploaded, "Headless upload sampling");
    Equal(parentId, test.Service.GetSnapshot().Session!.Id, "Sampling retains the existing physical interval");
    test.Service.Flush();
    Equal((2048L, 128L), test.Usage.GetTotals(test.Day, test.Day), "Headless usage persistence");
    Equal(2048L, test.Service.History.GetActiveSession()!.DownloadBytes, "Shared delta reaches physical session history");
    Equal(1, test.Service.History.GetRecentSessions().Count, "Headless sampling does not create extra physical sessions");
}

static void BackgroundBatchedPersistence()
{
    using var test = new HeadlessMonitoringContext();
    test.AdvanceCounters(1000, 100);
    Equal((0L, 0L), test.Usage.GetTotals(test.Day, test.Day), "An ordinary sample stays batched");
    Equal(1000L, test.Service.GetSnapshot().PendingDownloaded, "Unpersisted download batch");
    test.Clock.Advance(TimeSpan.FromSeconds(31));
    test.Service.SampleNow();
    Equal((1000L, 100L), test.Usage.GetTotals(test.Day, test.Day), "The existing batch interval persists without UI refresh");
    Equal(0L, test.Service.GetSnapshot().PendingDownloaded, "Committed pending bytes clear");
    test.Service.Flush();
    test.Service.Flush();
    Equal((1000L, 100L), test.Usage.GetTotals(test.Day, test.Day), "Repeated flush cannot double-count the batch");
}

static void BackgroundStartStop()
{
    using var test = new HeadlessMonitoringContext();
    var id = test.Service.GetSnapshot().Session!.Id;
    test.Service.Start();
    test.Service.Start();
    test.Identity.RaiseNetworkChanged();
    WaitUntil(() => test.Identity.Detections >= 2, "Repeated start continues normal identity observation");
    Equal(id, test.Service.GetSnapshot().Session!.Id, "Repeated start does not reopen physical history");
    Equal(1, test.Service.History.GetRecentSessions().Count, "Repeated start has one physical interval");
    test.Service.Stop();
    test.Service.Stop();
    test.Service.Dispose();
    Require(!test.Service.GetSnapshot().IsRunning && test.Identity.Disposed,
        "Stopping the application core stops recognition and releases its identity source.");
    var captured = test.Network.Captures;
    var events = SnapshotRows(test.DatabasePath, "ConnectionEvents");
    test.AdvanceCounters(100_000, 20_000, sampleNow: false);
    Thread.Sleep(1200);
    Equal(captured, test.Network.Captures, "The sampler cannot continue after core shutdown");
    Equal(events, SnapshotRows(test.DatabasePath, "ConnectionEvents"), "Stopped monitoring cannot append history");
    Require(test.Service.History.GetActiveSession() is null, "Core stop closes the physical interval.");
}

static void BackgroundSameAdapterBaseline()
{
    using var test = new HeadlessMonitoringContext();
    test.AdvanceCounters(100, 10);
    test.Service.SelectAdapter(test.Adapter.Id);
    test.Service.SelectAdapter(test.Adapter.Id);
    test.AdvanceCounters(300, 30);
    Equal(400L, test.Service.GetSnapshot().SessionDownloaded, "Selecting the unchanged adapter preserves counter continuity");
    Equal(40L, test.Service.GetSnapshot().SessionUploaded, "Unchanged adapter upload continuity");
    Equal(400L, test.Service.GetSnapshot().Session!.DownloadBytes, "The shared session receives the complete unchanged-adapter delta");
}

static void BackgroundPhysicalTransitionBaseline()
{
    using var test = new HeadlessMonitoringContext();
    test.AdvanceCounters(100, 10);
    var original = test.Service.GetSnapshot().Session!.Id;
    test.Identity.Network = new NetworkIdentity("WiFi", "Renamed physical hotspot", "Renamed physical hotspot", test.Adapter.Id);
    test.Identity.RaiseNetworkChanged();
    WaitUntil(() => test.Service.GetSnapshot().Session is { } current && current.Id != original,
        "A changed physical identity opens a successor interval without a dashboard");
    var nextId = test.Service.GetSnapshot().Session!.Id;
    test.AdvanceCounters(50_000, 1000);
    Equal(100L, test.Service.GetSnapshot().SessionDownloaded, "First sample on the new identity establishes a baseline");
    Equal(0L, test.Service.GetSnapshot().Session!.DownloadBytes, "Old connection traffic cannot bleed into the new interval");
    test.AdvanceCounters(200, 20);
    Equal(300L, test.Service.GetSnapshot().SessionDownloaded, "App totals accumulate only confirmed deltas");
    Equal(200L, test.Service.GetSnapshot().Session!.DownloadBytes, "The new interval receives its own valid delta");
    Equal(nextId, test.Service.GetSnapshot().Session!.Id, "Baseline sampling leaves the physical interval stable");
    var previous = test.Service.History.GetRecentSessions().Single(row => row.Id == original);
    Equal(100L, previous.DownloadBytes, "The original physical session retains its own traffic");
    Equal("NetworkChanged", previous.EndReason, "The original physical interval has a meaningful boundary");
}

static void BackgroundCounterReset()
{
    using var test = new HeadlessMonitoringContext();
    test.AdvanceCounters(100, 10);
    test.Clock.Advance(TimeSpan.FromSeconds(1));
    test.Network.SetSnapshot(test.Adapter.Id, new NetworkSnapshot(5, 2, test.Clock.GetUtcNow()));
    test.Service.SampleNow();
    Equal(100L, test.Service.GetSnapshot().SessionDownloaded, "Counter rollback cannot manufacture received bytes");
    Equal(10L, test.Service.GetSnapshot().SessionUploaded, "Counter rollback cannot manufacture sent bytes");
    test.Clock.Advance(TimeSpan.FromSeconds(1));
    test.Network.SetSnapshot(test.Adapter.Id, new NetworkSnapshot(25, 6, test.Clock.GetUtcNow()));
    test.Service.SampleNow();
    Equal(120L, test.Service.GetSnapshot().SessionDownloaded, "Post-reset received bytes use the reset baseline");
    Equal(14L, test.Service.GetSnapshot().SessionUploaded, "Post-reset sent bytes use the reset baseline");
    Equal(120L, test.Service.GetSnapshot().Session!.DownloadBytes, "The physical session reuses the safe dashboard delta");
}

static void BackgroundProfileRecognition()
{
    using var test = new HeadlessMonitoringContext();
    var id = test.Service.GetSnapshot().Session!.Id;
    Require(test.Service.GetSnapshot().Connection.CanAssign, "A real unknown network remains assignable in the headless core.");
    var profile = test.Service.Profiles.CreateProfile("DITO", "D2 Router");
    var network = test.Identity.Network!;
    test.Service.Profiles.AssignMapping(profile.Id, network.NetworkType, network.NetworkIdentifier, network.FriendlyName);
    test.Service.RefreshRecognition();
    WaitUntil(() => test.Service.GetSnapshot().Connection.Profile?.Id == profile.Id, "Headless recognition picks up an explicit assignment");
    Equal(id, test.Service.GetSnapshot().Session!.Id, "Profile assignment does not restart the physical interval");
    Equal<long?>(profile.Id, test.Service.GetSnapshot().Session!.ProfileId, "The active physical session gets the selected profile");
    test.Service.Profiles.UpdateProfile(profile.Id, "DITO", "Home Router");
    test.Service.RefreshRecognition();
    WaitUntil(() => test.Service.GetSnapshot().Connection.Profile?.DeviceName == "Home Router", "Headless recognition picks up a profile edit");
    Equal(id, test.Service.GetSnapshot().Session!.Id, "A profile rename leaves physical identity stable");
    Equal(1, test.Service.Profiles.GetProfiles().Count, "No automatic profiles are created");
}

static void BackgroundSleepResume()
{
    using var test = new HeadlessMonitoringContext(vpnActive: true);
    WaitUntil(() => test.Vpn.GetActiveSession() is not null, "An active VPN is observed without a dashboard");
    var firstParent = test.Service.GetSnapshot().Session!.Id;
    var firstVpn = test.Vpn.GetActiveSession()!;
    test.Clock.Advance(TimeSpan.FromSeconds(10));
    test.Service.Suspend();
    test.Service.Suspend();
    Require(test.Service.GetSnapshot().IsSuspended && test.Service.History.GetActiveSession() is null && test.Vpn.GetActiveSession() is null,
        "The existing power lifecycle closes VPN and physical intervals in background mode.");
    Equal("Sleep", test.Vpn.GetRecentSessions().Single().EndReason, "Background suspension retains the VPN sleep cause");
    var captures = test.Network.Captures;
    Thread.Sleep(1200);
    Equal(captures, test.Network.Captures, "Suspension stops the core sampler rather than only hiding UI");
    test.Clock.Advance(TimeSpan.FromMinutes(20));
    test.Service.Resume();
    test.Service.Resume();
    WaitUntil(() => test.Vpn.GetActiveSession() is not null, "Resume obtains fresh physical and VPN identities without a dashboard");
    var resumed = test.Vpn.GetActiveSession()!;
    Require(resumed.Id != firstVpn.Id && resumed.ConnectionSessionId != firstParent,
        "Resumed history uses new physical and VPN intervals.");
    Equal(test.Service.GetSnapshot().Session!.Id, resumed.ConnectionSessionId, "The resumed VPN belongs to its new physical parent");
    Equal(2, test.Service.History.GetRecentSessions().Count, "Repeated resume does not duplicate physical intervals");
    Equal(2, test.Vpn.GetRecentSessions().Count, "Repeated resume does not duplicate VPN intervals");
    Equal(1, GetVpnEvents(test.Service.History).Count(item => item.EventType == "VpnDisconnected"), "Sleep disconnect is recorded exactly once");
}

static void BackgroundCleanExit()
{
    using var test = new HeadlessMonitoringContext(vpnActive: true);
    WaitUntil(() => test.Vpn.GetActiveSession() is not null, "VPN activation is persisted before background exit");
    var parentId = test.Service.GetSnapshot().Session!.Id;
    test.AdvanceCounters(8192, 512);
    Equal((0L, 0L), test.Usage.GetTotals(test.Day, test.Day), "Exit fixture has a pending daily batch");
    test.Service.Stop();
    test.Service.Stop();
    Equal((8192L, 512L), test.Usage.GetTotals(test.Day, test.Day), "Core exit flushes daily usage without dashboard refresh");
    var parent = test.Service.History.GetRecentSessions().Single();
    Equal(parentId, parent.Id, "Exit does not create a successor physical interval");
    Equal("ApplicationExit", parent.EndReason, "Core exit preserves normal physical shutdown behavior");
    Equal(8192L, parent.DownloadBytes, "Core exit flushes physical session traffic");
    Equal(512L, parent.UploadBytes, "Core exit flushes physical session upload traffic");
    var child = test.Vpn.GetRecentSessions().Single();
    Equal("ApplicationExit", child.EndReason, "Core exit closes VPN cleanly with the existing lifecycle cause");
    Equal(parent.EndedAtUtc, child.EndedAtUtc, "VPN and physical clean exit use the same boundary");
    Require(test.Service.History.GetActiveSession() is null && test.Vpn.GetActiveSession() is null,
        "Clean core shutdown leaves no active physical or VPN history.");
    Equal(1, GetVpnEvents(test.Service.History).Count(item => item.EventType == "VpnDisconnected"), "Repeated shutdown creates one VPN disconnect");
    using var nextLease = new DatabaseRunLease(test.DatabasePath);
    nextLease.EnsureOwns(test.DatabasePath);
}

static void BackgroundShutdownJoin()
{
    using var test = new HeadlessMonitoringContext();
    using var queryStarted = new ManualResetEventSlim();
    using var releaseQuery = new ManualResetEventSlim();
    test.VpnSource.DetectionStarted = queryStarted;
    test.VpnSource.ReleaseDetection = releaseQuery;
    test.Service.RefreshRecognition();
    Require(queryStarted.Wait(TimeSpan.FromSeconds(5)), "Native VPN query is deliberately blocked during background shutdown.");
    var shutdown = Task.Run(test.Service.Stop);
    try
    {
        Require(!shutdown.Wait(TimeSpan.FromMilliseconds(300)), "Shutdown waits for admitted native workers before releasing ownership.");
        var rejected = ExpectFailure(() => { using var unexpectedLease = new DatabaseRunLease(test.DatabasePath); },
            "Lease remains held during worker join");
        Require(rejected.Message.Contains("already monitoring", StringComparison.OrdinalIgnoreCase), "Concurrent recovery remains blocked during shutdown.");
    }
    finally { releaseQuery.Set(); }
    Require(shutdown.Wait(TimeSpan.FromSeconds(5)), "Background shutdown completes after its owned native query returns.");
    using var nextLease = new DatabaseRunLease(test.DatabasePath);
    Require(test.Service.History.GetActiveSession() is null && test.Vpn.GetActiveSession() is null,
        "Obsolete native work cannot attach a VPN after physical shutdown.");
}

static void LiveTrafficMegabits()
{
    foreach (var (bytes, mbps) in new[] { (0d, 0d), (100_000d, 0.8d), (425_000d, 3.4d), (3_100_000d, 24.8d) })
        Equal($"{mbps:F2} Mbps", ByteFormatter.FormatRate(bytes), "Decimal megabit conversion");
}

static void LiveTrafficPrecision()
{
    foreach (var (bytes, mbps) in new[] { (36_700d, 0.29d), (36.7 * 1024, 0.30d), (612.5d, 0d), (750d, 0.01d) })
        Equal($"{mbps:F2} Mbps", ByteFormatter.FormatRate(bytes), "Rate rounding uses raw sampled bytes");
}

static void LiveTrafficInvalidRates()
{
    foreach (var bytes in new[] { -1d, double.MinValue, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        Equal($"{0d:F2} Mbps", ByteFormatter.FormatRate(bytes), "Invalid rate is safely displayed as zero");
    var large = ByteFormatter.FormatRate(double.MaxValue);
    Require(!large.Contains("NaN") && !large.Contains("Infinity"), "A finite input cannot overflow into a nonfinite display.");
}

static void DataQuantityFormatting()
{
    Equal("0 B", ByteFormatter.Format(-1), "Negative quantity remains zero");
    Equal("0 B", ByteFormatter.Format(0), "Empty quantity remains bytes");
    Equal("1023 B", ByteFormatter.Format(1023), "Byte quantity boundary");
    Equal($"{1d:F1} KB", ByteFormatter.Format(1024), "Kilobyte formatting is unchanged");
    Equal($"{1d:F2} MB", ByteFormatter.Format(1_048_576), "Megabyte formatting is unchanged");
    Equal($"{1d:F2} GB", ByteFormatter.Format(1_073_741_824), "Gigabyte formatting is unchanged");
}

static void TrafficPeaksFreshSchema()
{
    using var workspace = new TestWorkspace();
    var usage = new UsageStore(workspace.Root);
    Equal(5L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Fresh peak schema version");
    Equal((0d, 0d), usage.GetTrafficPeaks(new DateOnly(2026, 1, 15)), "No observation means zero peak");
    Equal(0L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM daily_traffic_peaks;"), "Startup invents no throughput measurements");
    using var connection = Open(workspace.CurrentPath);
    using var command = connection.CreateCommand();
    command.CommandText = "EXPLAIN QUERY PLAN SELECT * FROM daily_traffic_peaks WHERE day='2026-01-15';";
    using var reader = command.ExecuteReader();
    Require(reader.Read() && reader.GetString(3).Contains("INDEX", StringComparison.OrdinalIgnoreCase), "Daily peak lookups use the date primary-key index.");
    Equal((0L, 0L), usage.GetTotals(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)), "Peak table does not change usage quantities");
}

// Build the historical v4 schema from the independent v3 fixture and only the
// established v3->v4 builders. Never downgrade a current database to make a fixture.
static void CreateVersionFourPeakFixture(string path)
{
    CreateVersionThreeFixture(path);
    using var connection = Open(path);
    using var transaction = connection.BeginTransaction();
    using var command = connection.CreateCommand();
    command.Transaction = transaction;
    var migrator = typeof(UsageStore).Assembly.GetType("NetTrace.Services.DatabaseMigrator")!;
    foreach (var method in new[] { "CreateVpnSessionSchema", "EnsureVpnEventTriggers" })
        migrator.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { command });
    command.CommandText = """
        INSERT INTO VpnSessions (ConnectionSessionId, InterfaceIndex, InterfaceGuid, InterfaceName, Description,
            RoutingMode, StartedAtUtc, LastConfirmedAtUtc)
        VALUES (62, 42, '825018c6-06a8-4721-9b04-9aedfbf70d23', 'Historical VPN', 'Native historical tunnel',
            'FullTunnel', '2026-01-15T03:05:00.0000000+00:00', '2026-01-15T03:10:00.0000000+00:00');
        PRAGMA user_version=4;
        """;
    command.ExecuteNonQuery();
    transaction.Commit();
}

static void TrafficPeaksVersionFourUpgrade()
{
    using var workspace = new TestWorkspace();
    CreateVersionFourPeakFixture(workspace.CurrentPath);
    var tables = new[] { "daily_usage", "ConnectionProfiles", "NetworkMappings", "ConnectionSessions", "ConnectionEvents", "VpnSessions" };
    var before = tables.ToDictionary(table => table, table => (
        Sql: ReadText(workspace.CurrentPath, $"SELECT sql FROM sqlite_schema WHERE name='{table}';"),
        Root: ReadInteger(workspace.CurrentPath, $"SELECT rootpage FROM sqlite_schema WHERE name='{table}';"),
        Rows: SnapshotRows(workspace.CurrentPath, table)));
    var usage = new UsageStore(workspace.Root);
    Equal(5L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Version four upgrades to five");
    AssertFixtureTotals(usage);
    foreach (var table in tables)
    {
        Equal(before[table].Sql, ReadText(workspace.CurrentPath, $"SELECT sql FROM sqlite_schema WHERE name='{table}';"), $"{table} SQL is preserved");
        Equal(before[table].Root, ReadInteger(workspace.CurrentPath, $"SELECT rootpage FROM sqlite_schema WHERE name='{table}';"), $"{table} B-tree is preserved");
        Equal(before[table].Rows, SnapshotRows(workspace.CurrentPath, table), $"{table} rows are preserved");
    }
    Equal(62L, new ConnectionHistoryStore(usage).GetActiveSession()!.Id, "Additive migration does not perform recovery");
    Equal(62L, new VpnSessionStore(usage).GetActiveSession()!.ConnectionSessionId, "Active VPN retains its physical parent");
    Equal((0d, 0d), usage.GetTrafficPeaks(new DateOnly(2026, 1, 15)), "Old byte totals are never reinterpreted as throughput");
}

static void TrafficPeaksFailedMigration()
{
    using var workspace = new TestWorkspace();
    CreateVersionFourPeakFixture(workspace.CurrentPath);
    using (var connection = Open(workspace.CurrentPath))
        Execute(connection, "CREATE TABLE daily_traffic_peaks(sentinel TEXT); INSERT INTO daily_traffic_peaks VALUES('preserve conflicting data');");
    var before = new[] { "daily_usage", "ConnectionProfiles", "NetworkMappings", "ConnectionSessions", "ConnectionEvents", "VpnSessions" }
        .ToDictionary(table => table, table => SnapshotRows(workspace.CurrentPath, table));
    ExpectStartupFailure(workspace.Root);
    Equal(4L, ReadInteger(workspace.CurrentPath, "PRAGMA user_version;"), "Failed v5 migration retains historical version four");
    Equal("preserve conflicting data", ReadText(workspace.CurrentPath, "SELECT sentinel FROM daily_traffic_peaks;"), "Conflicting table is not overwritten");
    foreach (var (table, rows) in before)
        Equal(rows, SnapshotRows(workspace.CurrentPath, table), $"Failed migration preserves {table}");
}

static void TrafficPeaksPersistence()
{
    using var workspace = new TestWorkspace();
    var usage = new UsageStore(workspace.Root);
    var day = new DateOnly(2026, 1, 15);
    usage.SaveTrafficPeaks(day, 1234.5, 67.25);
    usage.SaveTrafficPeaks(day, 1000, 90.5);
    usage.SaveTrafficPeaks(day, 1234.5, 90.5);
    usage.SaveTrafficPeaks(day.AddDays(1), 4.5, 0.25);
    usage.AddUsage(day, 8192, 512);
    var reopened = new UsageStore(workspace.Root);
    Equal((1234.5, 90.5), reopened.GetTrafficPeaks(day), "Fractional maxima survive restart and independent upserts");
    Equal((4.5, 0.25), reopened.GetTrafficPeaks(day.AddDays(1)), "Days have separate peak measurements");
    Equal(2L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM daily_traffic_peaks;"), "Repeat upserts do not duplicate dates");
    Equal((8192L, 512L), reopened.GetTotals(day, day), "Peak rates cannot alter byte totals");
}

static void TrafficPeaksValidation()
{
    using var workspace = new TestWorkspace();
    var usage = new UsageStore(workspace.Root);
    var day = new DateOnly(2026, 1, 15);
    foreach (var value in new[] { -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
    {
        Require(ExpectFailure(() => usage.SaveTrafficPeaks(day, value, 1), "Invalid download peak") is ArgumentOutOfRangeException, "Invalid download values are rejected before persistence.");
        Require(ExpectFailure(() => usage.SaveTrafficPeaks(day, 1, value), "Invalid upload peak") is ArgumentOutOfRangeException, "Invalid upload values are rejected before persistence.");
    }
    using var connection = Open(workspace.CurrentPath);
    foreach (var sql in new[] {
        "INSERT INTO daily_traffic_peaks VALUES('2026-01-15', -1, 1);",
        "INSERT INTO daily_traffic_peaks VALUES('2026-01-15', 1, -1);",
        "INSERT INTO daily_traffic_peaks VALUES('2026-01-15', 1e999, 1);",
        "INSERT INTO daily_traffic_peaks VALUES('2026-01-15', 1, 1e999);",
        "INSERT INTO daily_traffic_peaks VALUES('2026-01-15', 'invalid', 1);",
        "INSERT INTO daily_traffic_peaks VALUES('2026-02-30', 1, 1);",
        "INSERT INTO daily_traffic_peaks VALUES('2026-1-1', 1, 1);",
        "INSERT INTO daily_traffic_peaks VALUES(NULL, 1, 1);"
    })
        Require(ExpectFailure(() => Execute(connection, sql), $"Invalid raw peak row: {sql}") is SqliteException, "SQLite independently enforces peak constraints.");
    Equal(0L, ReadInteger(workspace.CurrentPath, "SELECT count(*) FROM daily_traffic_peaks;"), "Rejected values cannot create a row");
    usage.SaveTrafficPeaks(DateOnly.MinValue, double.MaxValue, 0);
    usage.SaveTrafficPeaks(DateOnly.MaxValue, 0, 0.25);
    Equal((double.MaxValue, 0d), usage.GetTrafficPeaks(DateOnly.MinValue), "Finite maxima and minimum DateOnly are supported");
    Equal((0d, 0.25), usage.GetTrafficPeaks(DateOnly.MaxValue), "Maximum DateOnly and fractional uploads are supported");
}

static void TrafficPeaksInvalidCurrentSchema()
{
    using var schema = new TestWorkspace();
    _ = new UsageStore(schema.Root);
    using (var connection = Open(schema.CurrentPath))
        Execute(connection, "DROP TABLE daily_traffic_peaks; CREATE TABLE daily_traffic_peaks(day TEXT NOT NULL PRIMARY KEY, downloaded_peak_bytes_per_second REAL NOT NULL, uploaded_peak_bytes_per_second REAL NOT NULL);");
    var definition = ReadText(schema.CurrentPath, "SELECT sql FROM sqlite_schema WHERE name='daily_traffic_peaks';");
    ExpectStartupFailure(schema.Root);
    Equal(definition, ReadText(schema.CurrentPath, "SELECT sql FROM sqlite_schema WHERE name='daily_traffic_peaks';"), "Damaged constraints are not silently rebuilt");
    Equal(5L, ReadInteger(schema.CurrentPath, "PRAGMA user_version;"), "Failed current-schema validation preserves version");
    using var rows = new TestWorkspace();
    _ = new UsageStore(rows.Root);
    using (var connection = Open(rows.CurrentPath))
        Execute(connection, "PRAGMA ignore_check_constraints=ON; INSERT INTO daily_traffic_peaks VALUES('2026-02-30', -1.0, 2.0);");
    var invalid = SnapshotRows(rows.CurrentPath, "daily_traffic_peaks");
    ExpectStartupFailure(rows.Root);
    Equal(invalid, SnapshotRows(rows.CurrentPath, "daily_traffic_peaks"), "Externally invalid observations remain preserved for recovery");
}

static void PausePeakTestSampler(MonitoringService service)
{
    var timer = (Timer)typeof(MonitoringService).GetField("_timer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
    timer.Change(Timeout.Infinite, Timeout.Infinite);
}

static void AdvancePeakTestDay(HeadlessMonitoringContext test)
{
    var local = test.Clock.GetLocalNow();
    test.Clock.Advance(local.DateTime.Date.AddDays(1).AddSeconds(1) - local.DateTime);
}

static void TrafficPeaksCoreAccumulation()
{
    using var test = new HeadlessMonitoringContext();
    PausePeakTestSampler(test.Service);
    test.AdvanceCounters(425_000, 6250);
    var sampled = test.Service.GetSnapshot();
    Equal(sampled.DownloadBytesPerSecond, sampled.PeakDownloadBytesPerSecond, "Download peak uses the exact live sample");
    Equal(sampled.UploadBytesPerSecond, sampled.PeakUploadBytesPerSecond, "Upload peak uses the exact live sample");
    Equal(test.Day, sampled.PeakDay, "Peak date follows the core's local day");
    test.Service.SampleNow();
    test.Service.SampleNow();
    Equal(425_000L, test.Service.GetSnapshot().SessionDownloaded, "Repeated timestamp cannot count bytes twice");
    Equal(425_000d, test.Service.GetSnapshot().PeakDownloadBytesPerSecond, "Repeated sample cannot replace the observed peak");
    test.AdvanceCounters(100, 10);
    Equal(425_000d, test.Service.GetSnapshot().PeakDownloadBytesPerSecond, "Lower live traffic cannot lower today's peak");
    Equal((0d, 0d), test.Usage.GetTrafficPeaks(test.Day), "Ordinary sampling does not write SQLite every second");
}

static void TrafficPeaksCoreBatchAndRestart()
{
    using var test = new HeadlessMonitoringContext();
    PausePeakTestSampler(test.Service);
    test.AdvanceCounters(1234, 56);
    Equal((0d, 0d), test.Usage.GetTrafficPeaks(test.Day), "Peak stays pending before thirty seconds");
    test.Clock.Advance(TimeSpan.FromSeconds(31));
    test.AdvanceCounters(0, 0);
    Equal((1234d, 56d), test.Usage.GetTrafficPeaks(test.Day), "Thirty-second batch persists both observed peaks");
    test.AdvanceCounters(2345, 67);
    test.Service.Stop();
    var identity = new FakeNetworkIdentityService { Network = test.Identity.Network };
    var verifier = new VpnRouteVerifier(new VpnDetectionService(new FakeVpnInterfaceSource()), new FakeVpnRouteSource());
    using var restarted = MonitoringService.CreateForDataRoot(test.Root, identity, verifier, test.Network, test.Clock,
        new IcmpMonitor(new NoInternetPingSource(), timeProvider: test.Clock));
    Equal((2345d, 67d), restarted.Usage.GetTrafficPeaks(test.Day), "Normal exit persists peaks newer than the last periodic flush");
    Equal(2345d, restarted.GetSnapshot().PeakDownloadBytesPerSecond, "Restart loads today's persisted download peak");
    Equal(67d, restarted.GetSnapshot().PeakUploadBytesPerSecond, "Restart loads today's persisted upload peak");
    Equal(0L, restarted.GetSnapshot().SessionDownloaded, "Today's peak is separate from the new app session's totals");
}

static void TrafficPeaksCoreRollover()
{
    using var test = new HeadlessMonitoringContext();
    PausePeakTestSampler(test.Service);
    test.AdvanceCounters(5000, 500);
    var yesterday = test.Day;
    test.Service.Suspend();
    AdvancePeakTestDay(test);
    var sleeping = test.Service.GetSnapshot();
    Equal(test.Day, sleeping.PeakDay, "Sleeping dashboard reads the new local date");
    Equal(0d, sleeping.PeakDownloadBytesPerSecond, "Sleep across midnight cannot expose yesterday's download peak");
    Equal(0d, sleeping.PeakUploadBytesPerSecond, "Sleep across midnight cannot expose yesterday's upload peak");
    Equal((5000d, 500d), test.Usage.GetTrafficPeaks(yesterday), "Sleep flush preserves yesterday's measurement");
    test.Identity.Network = null;
    test.Network.Adapters = Array.Empty<AdapterInfo>();
    test.Service.Resume();
    PausePeakTestSampler(test.Service);
    WaitUntil(() => test.Service.GetSnapshot().Connection.Status == ConnectionStatus.Disconnected, "Resume observes a genuinely disconnected physical connection");
    test.Service.SampleNow();
    Equal(0d, test.Service.GetSnapshot().PeakDownloadBytesPerSecond, "Offline sampling cannot carry an old peak into today");
    Equal((0d, 0d), test.Usage.GetTrafficPeaks(test.Day), "No traffic means no measured peak for the new date");
}

static void TrafficPeaksCoreMultiDayRetry()
{
    using var test = new HeadlessMonitoringContext();
    PausePeakTestSampler(test.Service);
    using (var connection = Open(test.DatabasePath))
        Execute(connection, "CREATE TRIGGER FailPeakInsert BEFORE INSERT ON daily_traffic_peaks BEGIN SELECT RAISE(ABORT, 'peak failure'); END;");
    var firstDay = test.Day;
    test.AdvanceCounters(1000, 100);
    AdvancePeakTestDay(test);
    test.AdvanceCounters(0, 0);
    var secondDay = test.Day;
    test.AdvanceCounters(2000, 200);
    Equal(2000d, test.Service.GetSnapshot().PeakDownloadBytesPerSecond, "A second day's peak does not reuse the first day");
    test.Service.Flush();
    Equal((0d, 0d), test.Usage.GetTrafficPeaks(firstDay), "Failed first-day peak remains uncommitted");
    Equal((0d, 0d), test.Usage.GetTrafficPeaks(secondDay), "Failed second-day peak remains uncommitted");
    using (var connection = Open(test.DatabasePath)) Execute(connection, "DROP TRIGGER FailPeakInsert;");
    test.Service.Flush();
    Equal((1000d, 100d), test.Usage.GetTrafficPeaks(firstDay), "Retry retains the first dirty day");
    Equal((2000d, 200d), test.Usage.GetTrafficPeaks(secondDay), "Retry retains the second dirty day");
    Equal((1000L, 100L), test.Usage.GetTotals(firstDay, firstDay), "First day's byte totals remain quantities");
    Equal((2000L, 200L), test.Usage.GetTotals(secondDay, secondDay), "Second day's byte totals remain quantities");
}

static void TrafficPeaksCoreWriteRetry()
{
    using var test = new HeadlessMonitoringContext();
    PausePeakTestSampler(test.Service);
    test.AdvanceCounters(1000, 100);
    using (var connection = Open(test.DatabasePath))
        Execute(connection, "CREATE TRIGGER FailPeakInsert BEFORE INSERT ON daily_traffic_peaks BEGIN SELECT RAISE(ABORT, 'peak failure'); END;");
    test.Service.Flush();
    Require(test.Service.GetSnapshot().PersistenceMessage?.Contains("peak", StringComparison.OrdinalIgnoreCase) == true, "Peak write failure is reported without pretending success.");
    Equal((1000L, 100L), test.Usage.GetTotals(test.Day, test.Day), "Peak failure does not roll back independently saved daily usage");
    Equal(0L, test.Service.GetSnapshot().PendingDownloaded, "A committed usage batch clears even when peak saving fails");
    test.AdvanceCounters(2000, 200);
    using (var connection = Open(test.DatabasePath)) Execute(connection, "DROP TRIGGER FailPeakInsert;");
    test.Service.Flush();
    test.Service.Flush();
    Equal((2000d, 200d), test.Usage.GetTrafficPeaks(test.Day), "Retry commits the latest retained maxima");
    Equal((3000L, 300L), test.Usage.GetTotals(test.Day, test.Day), "Retry never adds the previous committed usage twice");
    Equal(3000L, test.Service.History.GetActiveSession()!.DownloadBytes, "Physical history retains the same exact deltas");
    Require(test.Service.GetSnapshot().PersistenceMessage is null, "A successful retry clears the failure message.");
}

static void TrafficPeaksCoreUsageFailure()
{
    using var test = new HeadlessMonitoringContext(vpnActive: true);
    PausePeakTestSampler(test.Service);
    WaitUntil(() => test.Vpn.GetActiveSession() is not null, "VPN exists before the injected usage failure");
    test.AdvanceCounters(8192, 512);
    using (var connection = Open(test.DatabasePath))
        Execute(connection, "CREATE TRIGGER FailUsageInsert BEFORE INSERT ON daily_usage BEGIN SELECT RAISE(ABORT, 'usage failure'); END;");
    ExpectFailure(test.Service.Flush, "Injected daily usage failure");
    Equal((8192d, 512d), test.Usage.GetTrafficPeaks(test.Day), "Usage write failure cannot prevent independent peak saving");
    Equal(8192L, test.Service.GetSnapshot().PendingDownloaded, "Failed usage remains available for a later retry");
    Equal(8192L, test.Service.History.GetActiveSession()!.DownloadBytes, "Physical progress flush continues after failed usage persistence");
    test.Service.Stop();
    Require(test.Service.History.GetActiveSession() is null && test.Vpn.GetActiveSession() is null, "Daily usage failure cannot orphan physical or VPN intervals during exit.");
    Equal("ApplicationExit", test.Service.History.GetRecentSessions().Single().EndReason, "Physical exit remains clean");
    Equal("ApplicationExit", test.Vpn.GetRecentSessions().Single().EndReason, "VPN exit remains clean");
    using var nextLease = new DatabaseRunLease(test.DatabasePath);
}

static void TrafficPeaksCoreBaselineSafety()
{
    using var test = new HeadlessMonitoringContext();
    PausePeakTestSampler(test.Service);
    test.AdvanceCounters(100, 10);
    var initial = test.Service.GetSnapshot().Session!.Id;
    test.Identity.Network = new NetworkIdentity("WiFi", "A new peak-test hotspot", "A new peak-test hotspot", test.Adapter.Id);
    test.Identity.RaiseNetworkChanged();
    WaitUntil(() => test.Service.GetSnapshot().Session?.Id != initial, "Physical transition establishes a fresh session");
    test.AdvanceCounters(50_000_000, 1_000_000);
    Equal(100d, test.Service.GetSnapshot().PeakDownloadBytesPerSecond, "The first new-identity counters establish a baseline instead of a giant peak");
    test.AdvanceCounters(200, 20);
    Equal(200d, test.Service.GetSnapshot().PeakDownloadBytesPerSecond, "A valid post-baseline delta updates the peak");
    test.Clock.Advance(TimeSpan.FromSeconds(1));
    test.Network.SetSnapshot(test.Adapter.Id, new NetworkSnapshot(1, 1, test.Clock.GetUtcNow()));
    test.Service.SampleNow();
    Equal(200d, test.Service.GetSnapshot().PeakDownloadBytesPerSecond, "Counter reset cannot manufacture a huge passive peak");
    Equal(20d, test.Service.GetSnapshot().PeakUploadBytesPerSecond, "Upload reset cannot manufacture a huge passive peak");
}

static void StartupRegistrationAbsent()
{
    using var test = new StartupRegistrationContext();
    Require(!test.Registration.IsEnabled(), "A missing NetTrace value is disabled");
    test.Registration.Disable();
    using var key = Registry.CurrentUser.OpenSubKey(test.RegistrySubKey);
    Require(key is null, "Reading or disabling an absent registration must not create a key");
}

static void StartupRegistrationCommand()
{
    using var test = new StartupRegistrationContext();
    Require(test.ExecutablePath.Contains(' '), "The executable fixture exercises spaces in the path");
    test.Registration.Enable();
    using var key = test.OpenKey();
    Equal($"\"{test.ExecutablePath}\" --background", key.GetValue(WindowsStartupRegistration.ValueName), "Run command quotes only the executable and supplies the background argument");
    Equal(RegistryValueKind.String, key.GetValueKind(WindowsStartupRegistration.ValueName), "The registration is an ordinary literal REG_SZ");
    Require(test.Registration.IsEnabled(), "The actual newly written registration is enabled");
    Equal(1, key.ValueCount, "Enable creates exactly one named startup entry");
}

static void StartupRegistrationIdempotent()
{
    using var test = new StartupRegistrationContext();
    using (var key = test.CreateKey())
        key.SetValue("AnotherApplication", "Other.exe --keep", RegistryValueKind.String);
    test.Registration.Enable();
    var command = test.ReadCommand();
    test.Registration.Enable();
    using var reopened = test.OpenKey();
    Equal(2, reopened.ValueCount, "Enabling twice creates no duplicate value and preserves the other application");
    Equal(command, reopened.GetValue(WindowsStartupRegistration.ValueName), "Repeated enabling retains the same command");
    Equal("Other.exe --keep", reopened.GetValue("AnotherApplication"), "Repeated enabling leaves other startup data unchanged");
}

static void StartupRegistrationDisableScope()
{
    using var test = new StartupRegistrationContext();
    using (var key = test.CreateKey())
    {
        key.SetValue("AnotherApplication", "Other.exe --keep", RegistryValueKind.String);
        key.SetValue("NetTraceHelper", "Helper.exe --keep", RegistryValueKind.String);
        key.SetValue(string.Empty, "Unrelated default value", RegistryValueKind.String);
    }
    test.Registration.Enable();
    test.Registration.Disable();
    test.Registration.Disable();
    Require(!test.Registration.IsEnabled(), "Repeated disable is safe and leaves NetTrace disabled");
    using var keyAfterDisable = test.OpenKey();
    Require(keyAfterDisable.GetValue(WindowsStartupRegistration.ValueName) is null, "Only the NetTrace registration is removed");
    Equal(3, keyAfterDisable.ValueCount, "The key and all three unrelated values remain");
    Equal("Other.exe --keep", keyAfterDisable.GetValue("AnotherApplication"), "Another application's command remains unchanged");
    Equal("Helper.exe --keep", keyAfterDisable.GetValue("NetTraceHelper"), "A similarly named application remains unchanged");
    Equal("Unrelated default value", keyAfterDisable.GetValue(string.Empty), "The unnamed value remains unchanged");
}

static void StartupRegistrationToggle()
{
    using var test = new StartupRegistrationContext();
    test.Registration.Enable();
    var recreated = new WindowsStartupRegistration(test.ExecutablePath, test.RegistrySubKey);
    Require(recreated.IsEnabled(), "A recreated service reads persisted enabled state");
    recreated.Disable();
    Require(!test.Registration.IsEnabled(), "The original service observes another instance disabling registration immediately");
    recreated.Enable();
    Require(test.Registration.IsEnabled(), "Reenabling is visible immediately without restarting the application");
    Equal(test.Registration.Command, test.ReadCommand(), "Reenabling restores exactly one correct command");
}

static void StartupRegistrationExternalChanges()
{
    using var test = new StartupRegistrationContext();
    var wrongCommands = new[]
    {
        $"\"{test.ExecutablePath}\"",
        $"{test.ExecutablePath} --background",
        $"\"{test.ExecutablePath}\" --BACKGROUND",
        $"\"{test.ExecutablePath}\" --background --extra",
        $"\"{test.ExecutablePath}\" --background ",
        $"\"{test.ExecutablePath}\"  --background",
        $"\"{Path.Combine(test.Root, "Another NetTrace.exe")}\" --background"
    };
    foreach (var command in wrongCommands)
    {
        test.SetValue(command);
        Require(!test.Registration.IsEnabled(), "A changed executable, quoting or arguments is not this background registration");
        Equal(command, test.ReadCommand(), "Reading a changed registration does not rewrite it");
    }
    var sameWindowsPath = $"\"{test.ExecutablePath.ToUpperInvariant()}\" --background";
    test.SetValue(sameWindowsPath);
    Require(test.Registration.IsEnabled(), "Windows executable path comparison is case insensitive");
    Equal(sameWindowsPath, test.ReadCommand(), "Reading equivalent path casing does not rewrite the value");
    using (var key = test.OpenKey())
        key.DeleteValue(WindowsStartupRegistration.ValueName);
    Require(!test.Registration.IsEnabled(), "An externally removed value is reflected immediately");
}

static void StartupRegistrationValueKinds()
{
    using var test = new StartupRegistrationContext();
    test.SetValue(1, RegistryValueKind.DWord);
    Require(!test.Registration.IsEnabled(), "A numeric value cannot be a startup command");
    test.SetValue(new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
    Require(!test.Registration.IsEnabled(), "A binary value cannot be a startup command");
    test.SetValue(test.Registration.Command, RegistryValueKind.ExpandString);
    Require(!test.Registration.IsEnabled(), "Expandable values are not silently accepted as literal executable paths");
    using (var key = test.OpenKey())
        Equal(RegistryValueKind.ExpandString, key.GetValueKind(WindowsStartupRegistration.ValueName), "An inspection cannot normalize or change a foreign value kind");
    test.SetValue(test.Registration.Command, RegistryValueKind.String);
    Require(test.Registration.IsEnabled(), "The proper literal command is accepted after external correction");
}

static void StartupRegistrationInvalidPaths()
{
    using var test = new StartupRegistrationContext();
    var library = Path.Combine(test.Root, "NetTrace.dll");
    File.WriteAllBytes(library, Array.Empty<byte>());
    var directory = Path.Combine(test.Root, "Directory.exe");
    Directory.CreateDirectory(directory);
    var invalidPaths = new[]
    {
        string.Empty, " ", "NetTrace.exe", "App Install Folder\\NetTrace.exe", library,
        directory, Path.Combine(test.Root, "Missing.exe"), $"\"{test.ExecutablePath}\"",
        test.ExecutablePath + "\r", test.ExecutablePath + "\n", test.ExecutablePath + "\0"
    };
    foreach (var path in invalidPaths)
        ExpectFailure(() => new WindowsStartupRegistration(path, test.RegistrySubKey), "Invalid startup executable path");
    File.Delete(test.ExecutablePath);
    ExpectFailure(test.Registration.Enable, "An executable removed after reading the setting");
    using var key = Registry.CurrentUser.OpenSubKey(test.RegistrySubKey);
    Require(key is null, "Rejected paths cannot publish a startup entry or create the registry key");
}

static void StartupRegistrationUnicodePath()
{
    using var test = new StartupRegistrationContext();
    var install = Path.Combine(test.Root, "NetTrace install — café's folder");
    Directory.CreateDirectory(install);
    var executable = Path.Combine(install, "NetTrace.exe");
    File.WriteAllBytes(executable, Array.Empty<byte>());
    var registration = new WindowsStartupRegistration(executable, test.RegistrySubKey);
    registration.Enable();
    Equal($"\"{executable}\" --background", test.ReadCommand(), "Unicode and apostrophes are preserved inside a single double-quoted executable argument");
    Require(registration.IsEnabled(), "The Unicode installation path reads back as enabled");
}

static void StartupRegistrationCanonicalPath()
{
    using var test = new StartupRegistrationContext();
    var equivalent = Path.Combine(test.Root, "App Install Folder", "..", "App Install Folder", "NetTrace.exe");
    var registration = new WindowsStartupRegistration(equivalent, test.RegistrySubKey);
    Equal(test.ExecutablePath, registration.ExecutablePath, "Equivalent absolute paths normalize to the real executable");
    registration.Enable();
    Require(test.Registration.IsEnabled(), "The canonical startup command is recognized by a separately constructed service");
    Equal(test.Registration.Command, test.ReadCommand(), "Canonicalization cannot introduce multiple commands or arguments");
}

static int RunLeaseChild(string[] arguments)
{
    if (arguments.Length != 3) throw new ArgumentException("Lease child requires a test root and mode.");
    var root = Path.GetFullPath(arguments[1]);
    var testParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NetTrace.RegressionTests"));
    if (!string.Equals(Directory.GetParent(root)?.FullName, testParent, StringComparison.OrdinalIgnoreCase) ||
        !Guid.TryParseExact(Path.GetFileName(root), "N", out _) || !Directory.Exists(root))
        throw new InvalidOperationException("Lease subprocess refuses any root outside an existing owned temporary test directory.");
    if (arguments[2] is not ("hold" or "hold-vpn" or "start")) throw new ArgumentException("Unknown lease child mode.");
    DatabaseRunLease lease;
    try { lease = new DatabaseRunLease(Path.Combine(root, "NetTrace", "usage.db")); }
    catch (InvalidOperationException exception) when (exception.Message.Contains("already monitoring", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("BLOCKED");
        return 0;
    }
    using (lease)
    {
        var usage = new UsageStore(root, lease);
        var clock = new ManualTimeProvider();
        var tracker = new ConnectionSessionTracker(new ConnectionHistoryStore(usage), clock);
        var network = Wifi("LeaseTestSSID");
        tracker.ObserveConnection(Connected(network));
        clock.Advance(TimeSpan.FromSeconds(30));
        Require(tracker.RecordSample(tracker.Current!.Id, network.AdapterId, 1, clock.GetUtcNow(), 1_024, 128), "Child sample is accepted");
        tracker.Flush();
        if (arguments[2] == "hold-vpn")
        {
            new VpnSessionStore(usage).Observe(tracker.Current!.Id, VpnStoredObservation(VpnRoutingMode.FullTunnel), clock.GetUtcNow());
            clock.Advance(TimeSpan.FromSeconds(20));
            tracker.ObserveConnection(Connected(network));
            tracker.Flush();
        }
        if (arguments[2] is "hold" or "hold-vpn")
        {
            Console.WriteLine("READY");
            if (Console.ReadLine() != "release") throw new InvalidOperationException("Lease owner received no release command.");
        }
        tracker.Close();
    }
    Console.WriteLine(arguments[2] is "hold" or "hold-vpn" ? "RELEASED" : "ACQUIRED");
    return 0;
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

sealed class FakeVpnInterfaceSource : IVpnInterfaceSource
{
    public VpnInterfaceInventory Inventory { get; set; } = new(Array.Empty<VpnInterfaceEvidence>(), Array.Empty<VpnRasConnectionEvidence>());
    public Exception? Failure { get; set; }
    public int Reads { get; private set; }
    public ManualResetEventSlim? DetectionStarted { get; set; }
    public ManualResetEventSlim? ReleaseDetection { get; set; }

    public VpnInterfaceInventory Read()
    {
        Reads++;
        var captured = Inventory;
        DetectionStarted?.Set();
        ReleaseDetection?.Wait();
        if (Failure is not null) throw Failure;
        return captured;
    }
}

sealed class FakeVpnRouteSource : IVpnRouteSource
{
    private static readonly Guid TunnelGuid = new("825018c6-06a8-4721-9b04-9aedfbf70d23");
    public VpnRouteInventory IPv4 { get; set; } = new(AddressFamily.InterNetwork,
        new[] { new VpnRouteEvidence(42, TunnelGuid, IPAddress.Any, 0, IPAddress.Any, 1, 0, true) });
    public VpnRouteInventory IPv6 { get; set; } = new(AddressFamily.InterNetworkV6,
        new[] { new VpnRouteEvidence(42, TunnelGuid, IPAddress.IPv6Any, 0, IPAddress.IPv6Any, 1, 0, true) });
    public AddressFamily? FailureFamily { get; set; }
    public Exception? Failure { get; set; }
    public List<AddressFamily> Reads { get; } = new();

    public VpnRouteInventory Read(AddressFamily addressFamily)
    {
        Reads.Add(addressFamily);
        if (Failure is not null && FailureFamily == addressFamily) throw Failure;
        return addressFamily == AddressFamily.InterNetwork ? IPv4 : IPv6;
    }
}

// Every child is started by this test process against its own temporary workspace.
// Timeouts are bounded; cleanup can kill only the Process instance this helper created.
sealed class LeaseChildProcess : IDisposable
{
    private readonly Process _process;

    public LeaseChildProcess(string root, string mode)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the regression process host.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--lease-test-child");
        start.ArgumentList.Add(root);
        start.ArgumentList.Add(mode);
        _process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start the owned lease test process.");
    }

    public string ReadLine()
    {
        var line = _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        if (line is null)
            throw new InvalidOperationException($"Lease child exited without its expected response: {_process.StandardError.ReadToEnd()}");
        return line;
    }

    public void Release()
    {
        _process.StandardInput.WriteLine("release");
        _process.StandardInput.Flush();
    }

    public void ExpectExit(int expectedExitCode)
    {
        if (!_process.WaitForExit(15_000)) throw new TimeoutException("Owned lease test process did not exit in time.");
        if (_process.ExitCode != expectedExitCode)
            throw new InvalidOperationException($"Lease child exited with {_process.ExitCode}, expected {expectedExitCode}: {_process.StandardError.ReadToEnd()}");
    }

    public void KillOwnedProcess()
    {
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        if (!_process.WaitForExit(10_000)) throw new TimeoutException("Owned lease test process did not terminate in time.");
    }

    public void Dispose()
    {
        try { KillOwnedProcess(); }
        finally { _process.Dispose(); }
    }
}

// Startup tests create only a unique HKCU regression key, never the real Windows Run key.
// Empty executable fixtures validate command construction and are never launched.
sealed class StartupRegistrationContext : IDisposable
{
    private const string RegistryParent = @"Software\NetTrace.RegressionTests\Startup";
    private readonly TestWorkspace _workspace = new();
    private readonly string _registryId = Guid.NewGuid().ToString("N");
    public string Root => _workspace.Root;
    public string RegistrySubKey { get; }
    public string ExecutablePath { get; }
    public WindowsStartupRegistration Registration { get; }

    public StartupRegistrationContext()
    {
        RegistrySubKey = RegistryParent + "\\" + _registryId;
        var install = Path.Combine(Root, "App Install Folder");
        Directory.CreateDirectory(install);
        ExecutablePath = Path.Combine(install, "NetTrace.exe");
        File.WriteAllBytes(ExecutablePath, Array.Empty<byte>());
        Registration = new WindowsStartupRegistration(ExecutablePath, RegistrySubKey);
    }

    public RegistryKey OpenKey() => Registry.CurrentUser.OpenSubKey(RegistrySubKey, writable: true)
        ?? throw new InvalidOperationException("The owned startup regression key does not exist.");

    public RegistryKey CreateKey() => Registry.CurrentUser.CreateSubKey(RegistrySubKey, writable: true)
        ?? throw new InvalidOperationException("The owned startup regression key could not be created.");

    public object? ReadCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistrySubKey);
        return key?.GetValue(WindowsStartupRegistration.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    public void SetValue(object value, RegistryValueKind kind = RegistryValueKind.String)
    {
        using var key = CreateKey();
        key.SetValue(WindowsStartupRegistration.ValueName, value, kind);
    }

    public void Dispose()
    {
        try
        {
            if (!Guid.TryParseExact(_registryId, "N", out _)
                || !string.Equals(RegistrySubKey, RegistryParent + "\\" + _registryId, StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove a registry key outside this test's owned GUID path.");
            Registry.CurrentUser.DeleteSubKeyTree(RegistrySubKey, throwOnMissingSubKey: false);
        }
        finally { _workspace.Dispose(); }
    }
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

sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = new(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _utcNow;
    public void Advance(TimeSpan elapsed) => _utcNow = _utcNow.Add(elapsed);
}

sealed class SessionTestContext : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    public string Root => _workspace.Root;
    public string DatabasePath => _workspace.CurrentPath;
    public UsageStore Usage { get; }
    public ProfileStore Profiles { get; }
    public ConnectionHistoryStore History { get; }
    public ManualTimeProvider Clock { get; } = new();
    public ConnectionSessionTracker Tracker { get; }

    public SessionTestContext()
    {
        Usage = new UsageStore(Root);
        Profiles = new ProfileStore(Usage);
        History = new ConnectionHistoryStore(Usage);
        Tracker = new ConnectionSessionTracker(History, Clock);
    }

    public void Dispose() => _workspace.Dispose();
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
    public bool CaptureBeforeBlocking { get; set; }
    public event EventHandler? NetworkChanged;

    public NetworkIdentityResult GetCurrentNetwork()
    {
        Interlocked.Increment(ref _detections);
        var capturedNetwork = Network;
        DetectionStarted?.Set();
        ReleaseDetection?.Wait();
        if (Failure is { } failure)
            throw failure;
        var network = CaptureBeforeBlocking ? capturedNetwork : Network;
        return new(Availability ?? (network is null ? NetworkIdentityAvailability.Disconnected : NetworkIdentityAvailability.Available),
            network, DetectionMessage);
    }

    public void RaiseNetworkChanged() => NetworkChanged?.Invoke(this, EventArgs.Empty);
    public void Dispose() => Disposed = true;
}

// This fixture starts the production application core with no Application, Window,
// dispatcher or tray. Only native input inventories and the clock are controlled.
sealed class NoInternetPingSource : IPingSource
{
    public Task<IcmpPingReply> SendAsync(IPAddress target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new IcmpPingReply(IPStatus.TimedOut));
    }
}

sealed class HeadlessMonitoringContext : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private long _received = 10_000;
    private long _sent = 1000;
    public string DatabasePath => _workspace.CurrentPath;
    public string Root => _workspace.Root;
    public UsageStore Usage => Service.Usage;
    public DateOnly Day => DateOnly.FromDateTime(Clock.GetLocalNow().DateTime);
    public HeadlessTimeProvider Clock { get; } = new();
    public FakeNetworkIdentityService Identity { get; } = new();
    public FakeBandwidthMonitor Network { get; } = new();
    public FakeVpnInterfaceSource VpnSource { get; } = new();
    public AdapterInfo Adapter { get; } = new("headless-physical", "Physical Wi-Fi", "Owned fixture adapter", "Wireless80211", 100_000_000, "192.0.2.2", true);
    public VpnSessionStore Vpn { get; }
    public MonitoringService Service { get; }

    public HeadlessMonitoringContext(bool vpnActive = false)
    {
        Identity.Network = new NetworkIdentity("WiFi", "Headless hotspot", "Headless hotspot", Adapter.Id);
        Network.Adapters = new[] { Adapter };
        Network.SetSnapshot(Adapter.Id, new NetworkSnapshot(_received, _sent, Clock.GetUtcNow()));
        // Match the independent native VPN/routing fixture identity used throughout the harness.
        var interfaceInfo = new VpnInterfaceEvidence
        {
            InterfaceIndex = 42,
            InterfaceGuid = new Guid("825018c6-06a8-4721-9b04-9aedfbf70d23"),
            InterfaceName = "Headless VPN",
            Description = "Native tunnel device",
            InterfaceType = 131,
            TunnelType = 1,
            OperationalStatus = OperationalStatus.Up,
            MediaConnectState = 1
        };
        VpnSource.Inventory = vpnActive
            ? new VpnInterfaceInventory(new[] { interfaceInfo }, Array.Empty<VpnRasConnectionEvidence>())
            : new VpnInterfaceInventory(Array.Empty<VpnInterfaceEvidence>(), Array.Empty<VpnRasConnectionEvidence>());
        var verifier = new VpnRouteVerifier(new VpnDetectionService(VpnSource), new FakeVpnRouteSource());
        try
        {
            Service = MonitoringService.CreateForDataRoot(_workspace.Root, Identity, verifier, Network, Clock,
                new IcmpMonitor(new NoInternetPingSource(), timeProvider: Clock));
            Vpn = new VpnSessionStore(Service.Usage);
            Service.Start();
            var deadline = Stopwatch.StartNew();
            while (Service.GetSnapshot().Session is null)
            {
                if (deadline.Elapsed >= TimeSpan.FromSeconds(5)) throw new TimeoutException("Headless core did not establish its physical connection.");
                Thread.Sleep(20);
            }
            Service.SampleNow();
        }
        catch
        {
            Service?.Dispose();
            _workspace.Dispose();
            throw;
        }
    }

    public void AdvanceCounters(long received, long sent, bool sampleNow = true)
    {
        Clock.Advance(TimeSpan.FromSeconds(1));
        _received += received;
        _sent += sent;
        Network.SetSnapshot(Adapter.Id, new NetworkSnapshot(_received, _sent, Clock.GetUtcNow()));
        if (sampleNow) Service.SampleNow();
    }

    public void Dispose()
    {
        try { Service.Dispose(); }
        finally { _workspace.Dispose(); }
    }
}

sealed class HeadlessTimeProvider : TimeProvider
{
    private long _utcTicks = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero).Ticks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);
    public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _utcTicks, elapsed.Ticks);
}

sealed class FakeBandwidthMonitor : INetworkMonitor
{
    private readonly object _gate = new();
    private readonly Dictionary<string, NetworkSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private int _captures;
    public IReadOnlyList<AdapterInfo> Adapters { get; set; } = Array.Empty<AdapterInfo>();
    public int Captures => Volatile.Read(ref _captures);
    public IReadOnlyList<AdapterInfo> GetActiveAdapters() => Adapters;
    public NetworkSnapshot? Capture(string adapterId)
    {
        Interlocked.Increment(ref _captures);
        lock (_gate) return _snapshots.GetValueOrDefault(adapterId);
    }
    public void SetSnapshot(string adapterId, NetworkSnapshot snapshot)
    {
        lock (_gate) _snapshots[adapterId] = snapshot;
    }
}

delegate NetworkIdentity WifiIdentityFactory(ReadOnlySpan<byte> ssidBytes, string adapterId);
