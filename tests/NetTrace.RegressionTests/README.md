Run the isolated data migration and persistence regression checks from the repository root:

```powershell
dotnet run --project tests/NetTrace.RegressionTests/NetTrace.RegressionTests.csproj
```

The console harness uses the application's existing SQLite dependency and temporary data roots. It never opens the user's application data. Test directories are removed after each case. Startup-registration tests use only owned GUID subkeys under `HKEY_CURRENT_USER\Software\NetTrace.RegressionTests\Startup`; they never modify the actual Windows `Run` key, and remove their owned subkeys afterward.

The original Task 001 checks remain: clean initialization, legacy and current unversioned databases, repeat-launch precedence, persisted daily/date-range totals, uncheckpointed WAL migration, and safe rejection of invalid schemas, future versions, corrupt files, locked legacy sources, legacy database paths that are directories, and obstructed destination directories. Legacy file hashes and schema versions are checked for preservation. Successful startup now expects schema version 5; unsupported future-version fixtures use version 6. Historical v0, v1, v2, v3 and v4 fixtures retain their original schema definitions and versions.

Task 002 checks cover transactional v1 → v2 upgrades without replacing the usage table, rollback after a conflicting schema, profile/mapping persistence, multiple SSIDs per profile, unknown networks staying unassigned, duplicate and conflicting assignments, edits preserving recognition, required names, exact SSID case/whitespace, parameterized user text, atomic create-and-assign rollback, and restricted profile deletion. The native SSID decoder is exercised with UTF-8, invalid UTF-8 and control-byte fixtures to verify identities cannot collapse. A fake network identity source also checks Wi-Fi/unknown/disconnected/Ethernet transitions, suppressed repeat notifications, immediate assignment/edit refresh, detection failure diagnostics, disposal, and current-state reads during a deliberately blocked native query. These checks do not require changing the PC's actual connection.

Task 002.1 checks distinguish recognized, valid unassigned, connected identity unavailable, and disconnected states. Simulated Windows permission/API failures cannot expose assignment or consult a mapping, even when the source retains a stale SSID. Empty or malformed identities are rejected before any database lookup; nonempty whitespace SSIDs retain their exact identity. Repeated permission failures do not repeat UI state notifications, usage totals persist during unavailability, and a recovered real SSID can be assigned to an existing profile.

Task 003 checks migrate hand-built v2 fixtures while preserving usage/profile/mapping fields, table definitions and B-tree root pages; migrate legacy v2 copies without changing the source; and roll back a failed v3 migration. A deterministic clock drives connected/repeated/change/disconnect/reconnect transitions, same-SSID sleep/resume, retry of a failed sleep closure at its original boundary, graceful exit and crash recovery at the persisted last observation. Valid unassigned sessions, active-only profile assignment and profile renames retain historical identity and stable foreign keys. Shared sampler deltas are counted once, batched flushes are idempotent, zero-byte idle samples persist liveness, old adapters/session IDs and duplicate/out-of-order timestamps are rejected, and counter resets and clock rollback remain safe. Repository checks cover one-active-session constraints, restricted profile deletion, UTC timestamps, database run ownership, rejection of a damaged v3 active-session constraint, recent/profile/overlap queries, extensible event names, and atomic failed transitions that pause attribution. Captured observations preserve queued A → B → A transitions, while a freshness refresh drops a blocked pre-resume native query.

Task 003.1 checks use independent child processes against owned temporary roots. The first process persists an active session; another startup must reject ownership before SQLite initialization or recovery, with unchanged database bytes and session fields. Graceful release allows a new process to acquire. A forcibly killed child leaves its sidecar file but releases the Windows sharing lock; the next startup recovers exactly at the persisted confirmation and records one recovery event. Additional checks cover acquiring before the database exists, harmless stale sidecar contents, same-process duplicates, release from a different thread, repeated disposal, wrong-path/disposed ownership rejection, and release after failed initialization. Children have bounded startup/exit waits; cleanup terminates only processes created by this harness. No real NetTrace process is terminated. SQLite query-plan checks cover active/recent/profile/time-range sessions, recent events, and both event foreign-key lookups. Existing v3 databases gain missing event relationship indices without changing rows or version; malformed index definitions roll back all attempted repairs. These tasks comprise 77 regression checks.

Task 004A checks inject a native interface inventory independently of physical-network recognition. An empty inventory, dormant/down adapters, disconnected media, paused/filter/endpoint interfaces, Hyper-V/VMware/VirtualBox devices, loopback, physical hardware, and friendly names containing VPN product names cannot create active VPN candidates. Connected native encapsulation tunnels and exact WireGuard/Wintun/TAP/OpenVPN driver identities are recognized, including renamed aliases; similarly named driver services are rejected. IPv6 transition mechanisms (6to4, ISATAP, Teredo), PPP and connected modem/direct RAS entries are not assumed to be VPNs. Connected native RAS VPN entries retain their available metadata without inventing interface GUIDs or indices. Dormant, reconnecting and unknown RAS substates cannot prove a live VPN, while the no-substate and Reconnected states remain supported. Unknown operational/media states, authentication errors, opaque tunnel types, TAP Always Connected configuration, partial inventories and API failures return uncertainty safely. Candidate fields are checked individually, and repeated on-demand queries observe connection changes and recover after native failure without stale results. These fixtures do not enable a VPN, change network settings, probe routes, or open any application data. These prior tasks comprise 95 regression checks.

Task 004B checks inject IPv4 and IPv6 route snapshots without changing Windows routing. A detected adapter without routes remains unverified. Effective VPN defaults and paired half-defaults establish general routing only when ownership, usability, metadata and combined route/interface metrics support that conclusion. Corporate prefixes with a physical default establish split routing. IPv4/IPv6 results remain independent; one unknown or usable unverified family prevents an aggregate full-tunnel claim. Metric ties, overflow, unspecified metrics, incomplete/API-failed snapshots, expired/down/loopback/disabled routes, GUID mismatches and RAS-only candidates cannot produce false proof. Fixtures also cover public bypass uncertainty, allowed local LAN/multicast exceptions, missing half coverage, malformed/family-mismatched routes, deterministic reordered observations, duplicate rows and fresh reads after routes change. No route verification sends packets, changes VPN/network settings, or accesses application databases. These prior tasks comprise 114 regression checks.

Task 004B.1 checks preserve separate IP-family applicability and routing proof. IPv4-only full and split routing, and the symmetric IPv6-only full case, ignore only a proven unavailable family. Completed empty, loopback-only and confirmed-inactive snapshots can establish absence; a usable physical family without VPN routes still prevents a full-tunnel claim. Native failures, incomplete or wrong-family snapshots, missing interface state and contradictory availability declarations remain unknown. Expired or disabled routes cannot erase a confirmed live family, and explicit usable-family evidence remains applicable even when route proof is absent. If neither family is applicable, routing remains unverified. These prior tasks comprise 122 regression checks.

Task 004C adds 21 focused VPN storage checks. Independent v3 fixtures include closed and active physical intervals and connection events; migration must preserve every existing field, schema definition and B-tree root page. Legacy v3 migration checks the unchanged rollback file hash, fresh installs create v4, and a conflicting VPN table forces the entire migration to roll back. Native detection/routing fixtures drive off/on/repeated/off/reconnected intervals, in-place routing changes, tunnel replacement, honest RAS identities without interface identifiers, UTC confirmations, stale observations and unavailable/ambiguous detection. Delayed on observations cannot reopen an interval before a newer persisted off boundary. Direct SQL exercises the single-active constraint and protected parent relationships. A deliberately failed tunnel insertion must roll back the previous interval closure. Physical disconnect, network change and graceful exit close children at their parent boundary. Queries retain deterministic recent and parent-scoped ordering. VPN storage must leave physical byte totals, daily usage and existing physical events unchanged; closed, missing or insufficiently confirmed parents reject observations without writing. Injected monitor tests verify on/off persistence, reject a blocked pre-transition VPN query rather than attaching it to a successor physical interval, and pause observations during failed physical transitions until a successful retry establishes the current parent. No VPN, route, network setting or real application database is changed. These prior tasks comprise 143 regression checks.

Task 004D adds 9 VPN event checks. Connected, disconnected and actual route-mode changes are recorded against the existing physical session, with its current profile and immutable network context. Tests verify human-readable VPN names, Unicode/apostrophes, current and previous routing modes, UTC boundaries and end reasons. Repeated observations, repository/application reopening, unchanged routing, alias edits and repeated physical callbacks cannot duplicate events or create physical intervals. Profile assignment supplies current context to subsequent events without rewriting older rows. Native tunnel replacement records an ordered disconnect/connect pair on the same physical parent. Event-insert failures roll back VPN connection, route and disconnection changes; a failing child event also rolls back physical parent closure and its event. Existing version-four databases gain the three event triggers transactionally without changing table definitions, B-tree roots, versions or historical data, and without backfilling past VPN events. A malformed existing trigger rolls back attempted installation of other missing triggers. These prior tasks comprise 152 regression checks.

Task 004E adds 11 lifecycle checks using the existing physical tracker, deterministic clocks and injected native VPN observations. Sleep closes the active child before its physical parent with a `Sleep` reason; repeated sleep notifications create one disconnect. Resume first waits for fresh physical identity, then observes VPN again: an active tunnel gets a new interval, while VPN off gets none. Crash fixtures deliberately persist different physical and VPN confirmation times, verifying the child ends exactly at its own last observation while physical recovery retains its existing later boundary. Startup obtains new routing evidence rather than inheriting stale VPN state. Normal exit flushes pending physical bytes and closes the child with `ApplicationExit`; repeated closure and startup add no duplicate history. Direct database writes cannot leave or reactivate orphan active children. Repeated recovery/startup creates one VPN disconnect and one existing physical recovery event. Failed VPN disconnect inserts roll back every lifecycle/session/event field, with safe retries at the original sleep or crash boundary. A deliberately blocked pre-sleep native query is discarded after resume and a fresh query retries promptly instead of waiting the normal 30-second interval. An independently spawned, owned child is hard-killed after persisting unequal parent/child confirmations; the next process must acquire ownership and recover each at its correct boundary. No real NetTrace process is terminated. The complete harness contains 163 regression checks.

Task 005A adds 10 headless core checks. The production monitoring owner runs with injected native inputs and a controlled clock, without constructing a WPF application, window, dispatcher or tray. Its independent one-second timer must sample traffic, while SQLite writes retain the existing thirty-second batching interval. Repeated starts and stops remain idempotent; stopping joins owned workers, stops counter reads and releases database ownership. Selecting an unchanged adapter preserves its baseline, physical SSID changes reset that baseline before attributing traffic, and counter resets cannot manufacture bytes. Profile assignments and edits resolve without a dashboard. Existing sleep/resume and normal-exit paths close VPN and physical intervals correctly, persist pending daily/session usage and retain event deduplication. A deliberately blocked native VPN query verifies ownership remains held throughout shutdown until obsolete work finishes. The complete console harness contains 173 regression checks.

The UI/data terminology work adds 17 checks. Four formatting checks verify decimal Mbps conversion directly from sampled bytes, rounding, safe nonfinite/negative inputs, and unchanged byte-quantity units. Thirteen peak checks cover the additive transactional v4 → v5 upgrade, preserved table SQL/B-tree roots and every usage/profile/mapping/physical/VPN/event row, conflicting-table rollback, fresh schema, fractional maxima surviving reopen, independent dates, idempotent maxima upserts, repository/raw SQL constraints and safe rejection of damaged schemas or externally invalid rows. The v4 fixture uses the independent hand-written v3 fixture and only the established v3 → v4 schema builders; it never downgrades a current database. Headless tests verify reuse of the exact live sample, suppressed duplicate timestamps, thirty-second batching, normal-exit persistence, startup loading, local-date rollover while asleep/offline, retained dirty days after failed writes, retry without double-counting usage, clean physical/VPN lifecycle despite daily-usage failure, and baseline/reset protection against false peaks. Only the sampler timer is paused for controlled peak observations; production persistence/lifecycle code remains in use. The complete console harness contains 190 regression checks.

Task 005B adds 10 focused startup-registration checks. Missing registrations remain disabled without creating a key. Real HKCU read/write tests verify a literal quoted executable path with spaces and `--background`, idempotent enabling, immediate state after service recreation or external changes, and disabling only the exact `NetTrace` value while preserving similarly named, unrelated and unnamed values. Wrong executable paths, quoting, arguments and nonstring/expandable value kinds cannot masquerade as an enabled registration; inspection never rewrites them. Empty, relative, quoted, control-character, missing, directory and non-executable paths are rejected without publishing a key, and a removed executable is revalidated before enable. Unicode/apostrophe paths and canonical equivalent absolute paths preserve a single executable argument. Empty `.exe` files are command-validation fixtures only and are never run. The complete console harness contains 200 regression checks.

Window recreation, tray commands, background launch, startup setting controls, hidden-window power messages and WPF shutdown are exercised separately by the owned Windows integration harness:

```powershell
dotnet run --project tests/NetTrace.WpfTests/NetTrace.WpfTests.csproj
```

Network Health 001 adds 16 focused ICMP checks in `IcmpHealthTests.cs`, bringing the
console suite to 216 groups. Injected sources cover successful-reply averages, partial
and total loss, zero-millisecond valid replies, API failures, deadlines, UTC/target
metadata, prompt cancellation, noncooperative sources, no overlapping cycles,
sleep/resume and incomplete-cycle discard. The native source is checked with an
already-canceled token so it sends no packets. Existing monitoring fixtures also
inject fake ICMP sources; no test pings the public internet.

UI Foundation 001.1 adds a cancellation-cleanup regression, bringing the console
suite to 217 groups. A controlled source holds its cancellation callback while
verifying that Stop returns promptly, then releases it and verifies exactly one
source cleanup with no partial measurement. This covers the shutdown race found
by the Health-page integration checks.

Network Health 002 adds 13 groups in `IcmpRollingTests.cs`, bringing the console
suite to 230 groups. Injected bursts cover stable and varying jitter, insufficient
successful replies, failure/invalid-RTT exclusion, and per-burst calculation reset.
Controlled clocks and replies verify newest-60 eviction, completion order,
read-only stable snapshots, completed all-failed samples, partial-cycle discard
on sleep and exit, fresh post-resume jitter, and a single probe worker.

Network Health 003 adds 16 chart-presentation groups in `IcmpStabilityChartTests.cs`,
bringing the console suite to 246 groups. They cover empty/single/partial/full
history, newest-60 rollover and observation order, independent missing-value gaps,
valid zero measurements, insufficient jitter data, shared positions and finite
scaling, and immutable presentation snapshots. Existing monitor/lifecycle checks
remain in the same suite.

Network Health 004 adds 13 groups in `IcmpPacketLossChartTests.cs`, bringing the
console suite to 259 groups. They cover standard and arbitrary percentages,
safe presentation bounds, unavailable malformed inputs, 60-cycle rollover and
observation order, immutable shared positions, and independent percent/ms scales.
A completed 100%-loss cycle retains its loss point while latency/jitter have gaps.

Network Health 005 adds 45 groups in `IcmpQualityTests.cs`, bringing the console
suite to 304 groups. They cover all six states, independent and worst-metric
severity, exact and immediately-above thresholds, three-cycle minimum, unavailable
and mixed ICMP, missing/invalid measurements, unweighted per-cycle averages,
newest-12 selection and rollover, observation order, unchanged source history,
deterministic immutable summaries and finite extreme averages.

Network Health 006 adds 45 groups in `InternetReachabilityTests.cs`, bringing the
console suite to 349 groups. Fake probe sources, DNS delegates and HTTP handlers
cover Checking/Available/Limited/Unavailable, independent-provider fallback,
HTTP 200/204/301/403/404/503 responses, headers-only reads, transport reuse and
privacy, a shared parallel timeout with retained successes, expected failures,
nonblocking shutdown, late faults, no overlapping cycles, suspend discard,
fresh resume observations and independence from ICMP history/quality. All existing
monitoring fixtures explicitly inject fake DNS/HTTPS probes.

One live check can be run separately with:

```powershell
dotnet run --project tests/NetTrace.RegressionTests/NetTrace.RegressionTests.csproj -- --reachability-live-validation
```

This explicit command performs one real reachability cycle and prints the configured
endpoints, UTC result and DNS/HTTPS flags. It opens no database and never runs as part
of the automated cases.

Connections 001 adds 34 groups in `ConnectionDetailsTests.cs`, bringing the console
suite to 383 groups. Deterministic snapshots cover connected/disconnected/unknown
status, missing data, usable IPv4/global/unique IPv6 selection, scope-free display,
gateway family preference, compact configured DNS (including local resolvers),
profile mapping isolation, adapter switching and local Mbps/Gbps formatting.
Owned monitoring fixtures verify cached reads without discovery, unchanged-identity
configuration refresh, retained sessions/counters and recovery after detail failures.

An explicit passive live check can be run separately with:

```powershell
dotnet run --project tests/NetTrace.RegressionTests/NetTrace.RegressionTests.csproj -- --connections-live-validation
```

This command reads one default monitored adapter and the existing network identity.
It performs no lookups/probes, configuration changes or database access. Profile
mapping is intentionally not loaded from real application data. It never runs as
part of the automated cases.

Connections 002 adds 23 groups in `ProfileManagementTests.cs`, bringing the console
suite to 406 groups. They cover saved-profile creation/editing and name rules,
exact mapping identity, duplicate no-ops, guarded ownership moves/removal,
persistence after reopening, atomic profile deletion and rollback. Deletion keeps
physical/VPN history, events, usage and peaks while clearing references to the
deleted profile. A deliberately delayed resolved observation verifies immediate
recognition refresh rejects stale profile state without replacing the current
physical session or counter baseline. Existing schema and matching rules remain
unchanged.

Connections 003 adds 44 groups in `RouteAwarenessTests.cs`, bringing the console
suite to 450 groups. Local interface/route fixtures cover physical defaults, active
VPN defaults and paired `/1` routes, metrics/ties, multiple candidates, inactive and
internal virtual devices, correlated RAS PPP, conservative fallback/unknown cases,
independent IPv4/IPv6 VPN, Direct, Unavailable and Unknown values, mixed IP families
and fully shadowed VPN routes. A known peer route remains intact when the other family
is unknown. Expired or disabled-default routes retain known family usability and
cannot silently become Unavailable. Public forwarding ranges are checked
across prefix boundaries so local/private prefix bases cannot hide public VPN routing
or identify an unused broad-route tunnel. The shared observation retains legacy VPN
history classification. Controlled monitoring covers background transitions, unchanged
adapter/session/counter baseline, identity-unavailable routing, cached reads without
discovery, stale sleep observations, fresh resume and late disposal rejection.

Connections 004 adds 43 groups in `TransportAwarenessTests.cs`, bringing the console
suite to 493 groups. Connected Windows RAS remote endpoints prove IPv4/IPv6 only
with a unique association to the effective PPP tunnel. Independently confirmed
native VPN sessions with actual peers in both families establish global Mixed;
single-family unmatched siblings, missing endpoint metadata, ambiguous identities,
errors and reconnecting/dormant sessions retain Unknown. Native layout and decoder
fixtures check the remote endpoint union, byte order and confirmed-session gate.
DNS answer-like metadata, IPv6 Direct routes, adapter names/drivers, ordinary or
internal virtual adapters, unrelated application endpoints and local proxies cannot
prove the underlay. Explicit route-independence fixtures include IPv6 transport
with IPv4 VPN/IPv6 Direct and an allowed direct route to the VPN peer. Controlled
headless monitoring covers endpoint/connect/disconnect transitions, unchanged
physical adapter/session/byte baseline, cached reads, one serialized existing
worker, stale suspend/disposal rejection, fresh resume and metadata-failure recovery.
No DNS, socket discovery, network probes or new monitor workers are used by these tests.

Connections 005 adds 60 groups in `WifiRadioTests.cs`, bringing the console suite
to 553 groups. Injected selected-interface observations cover complete radio data,
quality boundaries/clamping, actual versus missing RSSI, standard 2.4/5/6 GHz
frequency/channel conversion, invalid centers, BSSID normalization, negotiated PHY,
independent RX/TX units and missing-rate fallback. Disconnected, non-Wi-Fi, absent
and wrong-interface observations cannot retain radio values. Controlled source gates
verify immediate cached reads, a lightweight refresh interval, single-flight queries,
adapter switching, hide/sleep invalidation, stale-result rejection, safe disposal,
permission-failure recovery and the absence of timer or persistence dependencies.
Native fixtures check WLAN struct sizes/offsets, kbit/s and kHz units, actual signed
RSSI, connected-state gating, association rechecks and exact fresh SSID/BSSID cache
matches. Nearby, old, future, conflicting and malformed cached entries cannot supply
current frequency. Cached frequency must agree with an available native channel;
if that scalar is unsupported, a fresh exact match supplies the tested derived
channel and band without weakening the cache or association guards.
Current-association PHY fixtures distinguish the native type from its separate
index and adapter capability descriptions. Native n/ac/ax, missing/unknown types,
and Windows-reported ac with a 2.4 GHz frequency retain their defined presentation.
All required help definitions are centrally associated, concise and immutable; BSSID,
link-rate direction/internet-speed differences and independent VPN transport meanings
are checked explicitly. No Wi-Fi hardware, scans or internet access are required.

Run only these deterministic Connections 005 groups with:

```powershell
dotnet run --project tests/NetTrace.RegressionTests/NetTrace.RegressionTests.csproj -- --wifi-radio-checks
```

History 001 adds 25 deterministic groups in `HistoryOverviewTests.cs`, bringing the
console suite to 578 groups. They cover empty, completed, old, active-only and mixed
history, distinct-ID/live-progress replacement, half-open local-day bounds,
midnight-crossing intervals, compact durations, existing byte formatting, retained
network context after profile deletion, immediate rename/deletion presentation,
newest-four ordering and 23/25-hour daylight-saving days. Connected time is clipped
to local midnight and now; an unfinished persisted row without confirmed live state
stops at its last confirmation. Since session totals have no per-day breakdown,
both completed and active bytes belong to the local day the session started; recent
rows still show their full actual totals. Temporary-store fixtures verify unchanged
schema/data, bounded reads, cache-only live ticks, explicit refresh and invalidation
on active identity, local date or timezone changes.

Run only these deterministic History 001 groups with:

```powershell
dotnet run --project tests/NetTrace.RegressionTests/NetTrace.RegressionTests.csproj -- --history-overview-checks
```

History 002 adds 43 deterministic groups in `HistoryUsageTests.cs`, bringing the
console suite to 621 groups. Integer download/upload totals aggregate by local
session start date into exactly 7 or 30 chronological, zero-filled buckets. Completed
and active session bytes follow History 001's same start-day rule, including the
explicit December 31 to January 1 case; current live counters replace the persisted
representation of the same stable session ID. Tests cover local range endpoints,
future starts, missing dates, month/year transitions, DST, zero traffic, readable
byte axes and hover values, sparse 30D labels, overflow safety and immutable data.
Owned SQLite fixtures verify start-only bounded retrieval through the existing
timestamp index, unchanged schema/rows, range/date/timezone/active-ID cache
invalidation, query-free live updates and reuse of unchanged visual snapshots. A
shared explicit time/zone context keeps Today and chart consistent exactly at
midnight while preserving the existing provider-based service defaults.

Run only these deterministic History 002 groups with:

```powershell
dotnet run --project tests/NetTrace.RegressionTests/NetTrace.RegressionTests.csproj -- --history-usage-checks
```

Dashboard 001 adds 30 deterministic groups in `DashboardSpeedTests.cs`, bringing
the console suite to 651 groups. Paired live values derive from the same raw
bytes-per-second sample; Mbps continues using the existing formatter. Adaptive
byte rates use decimal B/s, KB/s, MB/s and GB/s with compact magnitude-based
precision. Tests cover the approved conversion/readability examples, exact and
adjacent raw thresholds, rounding without premature unit promotion, fractions,
small/invalid/negative-zero/extreme samples, English/French cultures and raw-sample
pairing that avoids conversion through rounded display text. Existing Mbps and
cumulative binary byte-quantity formatting remain covered unchanged.

Run only these deterministic Dashboard 001 groups with:

```powershell
dotnet run --project tests/NetTrace.RegressionTests/NetTrace.RegressionTests.csproj -- --dashboard-speed-checks
```

Both harnesses use owned temporary databases and injected network/VPN/ICMP evidence. They do not open real application data or change Windows network settings.
