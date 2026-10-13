# NetTrace WPF integration tests

The Windows-only STA integration checks use isolated temporary databases and injected
physical/VPN identities. They exercise the existing dashboard, persistence,
profile, power, recovery and ownership behavior together with Task 005A tray and
background lifecycle behavior. They retain the previous 77 WPF lifecycle checks,
adapt the dashboard read-failure case to report an error while monitoring continues,
and add background/tray checks for window recreation, timer/subscription cleanup,
sampling, VPN persistence, power handling and clean shutdown. An owned child WPF
process verifies that dashboard closure keeps its process alive and actual Tray
Exit terminates its application loop and releases database ownership.

Task 005B adds checks for normal/background launch arguments, no dashboard construction
in background mode, live physical/VPN sampling before opening a view, tray restoration,
idempotent initialization and clean exit. Startup checkbox checks inject an in-memory
registration service: they verify immediate updates, read-back after failed or partial
writes, external changes, transient read failures and dashboard recreation. Separate
owned WPF child processes exercise normal and `--background` startup through the real
application loop and Tray Exit. These checks never modify the Windows Run key.

Network Health 001 adds eight background integration checks using an injected ping
source. They verify repeated completed cycles before any dashboard is constructed,
continued probing after dashboard closure, raw measurement access, all-failure loss
without changing connection status, the existing suspend/resume lifecycle and clean
Tray Exit. All existing monitoring fixtures use fake ICMP sources, including child
processes; the harness sends no ICMP traffic to the public internet.

UI Foundation 001 adds 37 checks for all five navigation pages, the active underline,
header hit targets, stable sampler/session state, dashboard summaries, startup
registration and the existing Profiles, Assign Profile and History dialogs. They
also exercise dashboard closure, tray restoration and clean exit through the new
shell. The harness renders each page at the normal and minimum window sizes into
`navigation-previews` beneath its isolated data directory for layout review.

Compact scrolling adds 16 checks in `ScrollingChecks.cs` for the 760-DIP startup
width, all five page layouts, dark native scrollbar mechanics, eased wheel frames,
rapid and reversed input, bounds, immediate keyboard/thumb/programmatic scrolling,
nested controls, disabling, and hide/unload cleanup. The owned synthetic scrollbar
preview is saved under `scrolling-previews`; no live network data is captured.
Run this group independently with:

```powershell
dotnet run --project tests/NetTrace.WpfTests/NetTrace.WpfTests.csproj -- --scrolling-checks
```

UI Foundation 001.1 adds 20 checks for the latest completed ICMP result on Health, the initial
waiting message, all-failed latency, local sample time and updates through the
existing window refresh timer. Controlled ping replies also verify immediate
refresh on returning to Health, unchanged probe ownership across navigation,
continued tray-only monitoring and the latest result after dashboard recreation.

Network Health 002 adds six checks and extends existing assertions for the latest
jitter value. Controlled bursts verify the formula, failed-reply exclusion, real
zero jitter, and unavailable jitter with fewer than two successes. The same worker
updates Health across navigation and dashboard recreation, accumulates ordered
completed samples in tray-only mode, and preserves previously obtained snapshots.
Unfinished bursts never enter rolling history.

Network Health 003 adds 21 checks in `IcmpStabilityChartChecks.cs` for the native
Connection Stability chart. They verify immediate existing-history display,
missing-value gaps and singleton points, unchanged-data reuse, new-cycle updates,
page/hide/minimize guards, immediate restore and tray recreation, and stable
bandwidth/session/worker state. A 60-cycle native pixel render checks both series
and saves `stability-chart-previews/60-cycles-with-gaps.png` beneath the isolated
test root. No chart timer or extra ping worker is introduced.

Network Health 004 adds 26 checks in `IcmpPacketLossChartChecks.cs` for the shared
packet-loss strip. They cover 100%-loss bars beside unavailable latency/jitter,
immediate synchronized snapshots, navigation/hide/minimize/restore/tray lifecycle,
61-cycle rollover, and unchanged timer/worker/bandwidth/session state. Six native
pixel fixtures verify zero, partial, single full loss, all-failed, empty and mixed
60-cycle renders; a minimum-window preview verifies compact placement and no
horizontal scrolling. PNGs are saved under `packet-loss-chart-previews` in the
owned test directory.

Network Health 005 adds 25 checks in `IcmpQualityChecks.cs`. Completed bursts from
the same injected worker display all six quality states and verify the minimum-data
gate, newest-12 evaluation against preserved 60-cycle history, shared chart snapshots,
unchanged-data cache reuse and inactive/hidden/minimized presentation guards.
Navigation, restore and tray recreation immediately show current quality without
another timer, probe worker or physical session. Six state previews and the minimum
760×600 layout are saved beneath `icmp-quality-previews` in the owned test directory;
the existing vertical scroll still exposes the full packet-loss strip.

Network Health 006 adds 34 checks in `InternetReachabilityChecks.cs`. Controlled
DNS/HTTPS probes exercise all four states, independent ICMP quality (including
Available reachability with Unavailable ICMP), the existing presentation timer,
immediate page entry, navigation/hide/minimize/restore/tray recreation, unfinished
sleep-cycle discard, fresh resume checks and cancellation on Tray Exit. Every
monitoring fixture, including child processes, injects `TestReachability` or another
controlled source so the full suite performs no public DNS/HTTPS requests. Seven
PNGs under `internet-reachability-previews` cover the states, ICMP combinations and 760×600
layout; the existing two cards and vertical chart scrolling remain intact.

```powershell
dotnet run --project tests/NetTrace.WpfTests/NetTrace.WpfTests.csproj
```

Connections 001 adds 29 checks in `ConnectionDetailsChecks.cs` for the ten live
adapter fields, missing IPv6/gateway/DNS, disconnected/unknown data, deterministic
address selection and compact DNS, mapped/unassigned profiles, complete adapter
switching, same-identity configuration updates and immediate page entry. Visibility,
minimize/restore, tray recreation and shutdown checks retain the existing workers
and timers. Thirteen PNG previews under `connection-details-previews` show the
requested states, each entire adapter card, and the 760×600 minimum layout with
long IPv6 and multiple DNS servers.

Connections 002 adds 35 checks in `ProfileManagementChecks.cs`. They exercise new
and edited profiles, required trimmed fields, preserved duplicate-name rules,
latest-identity mapping, duplicate no-ops, Move/Cancel conflicts, selected-mapping
removal, Delete/Cancel and the final-profile empty state. Failed writes retain UI
and persisted state; mapping changes during confirmation cannot move a replacement
network even if SQLite reuses its row ID. Active profile changes update Connections
without changing the physical session or adding monitors, timers or subscriptions.
Nine PNGs under `profile-management-previews` cover existing/new/empty profiles,
selected/unselected mappings, both confirmations, long names and the 620×500 minimum.

One explicit live validation can be run separately with:

```powershell
dotnet run --project tests/NetTrace.WpfTests/NetTrace.WpfTests.csproj -- --profiles-live-validation
```

This uses the real existing identity/adapter implementation with an owned temporary
database. It creates a temporary profile through Profiles, maps the current network,
verifies the main Connections presentation, removes the temporary mapping and deletes
the temporary profile while retaining its physical session. It never opens or edits
the user's real profile database, changes network settings or sends health probes.
It reports an unavailable identity as a skip and never runs in the automated suite.

Connections 003 adds 42 checks in `VpnRouteAwarenessChecks.cs`. Controlled local
interface/route inventories cover Direct, active VPN, installed inactive tunnels,
internal virtual devices, Unknown routing and multiple candidates. Separate IPv4/IPv6
rows independently cover VPN/Direct, VPN/VPN, Direct/Direct, VPN/Unavailable,
Direct/VPN, Direct/Unknown and VPN/Unknown; VPN uses the informational resource and
all other family values use the neutral resource. Current route and Health context
update without another observer/timer,
native UI query, adapter change, session reset or ICMP-history recalculation.
Cases A–D retain independent reachability/VPN/ICMP text; all status checks use shared
semantic resource identity rather than screenshot pixels. Visibility, restoration,
tray recreation, sleep/resume, unavailable physical identity and exit retain the
existing monitoring lifecycle. Eighteen PNGs under `vpn-route-awareness-previews`
cover the requested states and all three pages at 760×600.

Connections 004 adds 39 checks in `VpnTransportChecks.cs`. Owned connected RAS
endpoint inventories exercise all five transport states through the existing
observer. Mixed proves independent active IPv4 and IPv6 outer endpoints without
relaxing the existing conservative policy for ambiguous multiple PPP routes.
All five values use the shared informational or neutral brushes; unsupported enum
values fall back to neutral Unknown. Five independent
transport/route combinations and five connect/disconnect/endpoint transitions
retain the physical Wi-Fi session, totals, counters and sampler baseline. The route,
sampler, WPF, ICMP and reachability workers retain their identities. Tray closure
admits fresh cached transport without rendering and a recreated window shows that
cache. Health has no transport row and its status, tooltip and explanation remain
unchanged. Thirteen additional `transport-*.png` previews cover all values, the five
route combinations and Connections/Health/Dashboard at 760×600, while the previous
eighteen route-awareness previews remain intact.

An explicit passive validation can be run separately with:

```powershell
dotnet run --project tests/NetTrace.WpfTests/NetTrace.WpfTests.csproj -- --passive-vpn-validation --render-health
```

It captures real local Windows interface, RAS and route evidence, reports VPN
transport with a safe provenance reason alongside independent IPv4 and IPv6
VPN/Direct/Unknown/Unavailable values and the physical adapter type, and leaves the user's VPN/network
configuration unchanged. The optional render verifies both Connections family values
and the transport value with their semantic resources in `captured-vpn-family-routes.png`. Physical identity,
profile and bandwidth in this owned preview are validation placeholders.
The `captured-vpn-simulated-health.png` preview uses captured real routing with
explicitly labelled simulated Available reachability and Unavailable ICMP in an owned database. It sends
no health probes and never opens real NetTrace data. It is presentation validation,
not a live reachability or packet-loss measurement. Without a conclusively active
VPN, it reports live VPN validation unavailable and leaves the network unchanged.

Legacy sampler/lifecycle fixtures now use owned adapter inventory and counters,
including child processes. The complete suite requires no real adapter or network
connection. It never modifies Windows VPN settings, machine power
state, or the real NetTrace database. Test power notifications are sent only
to its own hidden lifecycle HWND. Test data is created beneath the test output
directory.

Connections 005 adds 87 checks in `WifiRadioChecks.cs`. Injected per-interface
radio snapshots cover complete connected Wi-Fi, missing RSSI and other metadata,
signal boundaries, 2.4/5/6 GHz, friendly PHY names, differing negotiated rates,
general-link-rate fallback, disconnect, Ethernet and two Wi-Fi adapters. Windows-reported
current-association ac remains visible alongside 2.4 GHz metadata, with the driver
reporting caveat in its wrapped help. The
existing Connections cards, VPN rows and adapter fields stay intact. Checks
exercise centralized explanations for all required labels plus general link rate,
finite wrapped tooltip widths, keyboard focus, click and Escape dismissal, and
the 760×600 layout without horizontal scrolling. Every information cue is measured
beside its associated label in the fixed left column, with no label/value overlap.
Raw radio and address/configuration values retain the established opaque normal
text color and readable contrast, independently from semantic status brushes.
Hidden-page, hidden-window and
minimized-window cases invalidate radio data and avoid WLAN polling while the
application-owned monitor continues. Suspend/resume clears old measurements and
obtains fresh association data. Native discovery is injected and runs off
the UI thread; these checks need no Wi-Fi hardware or WLAN service.
Suspension invalidates cached measurements before dispatcher work. Existing value
tooltip strings retain their content through a shared finite wrapped template.

Synthetic radio values only are saved under `wifi-radio-previews` in the owned
test directory. Page and radio views cover complete data, missing RSSI, long
IPv6 with existing VPN rows, disconnect, Ethernet and the minimum window. RSSI
and BSSID tooltip previews show the same wrapped hover content at both sizes.
Real Wi-Fi values are never saved as history, logs or screenshots.

An explicit passive one-shot live validation can be run separately:

```powershell
dotnet run --project tests/NetTrace.WpfTests/NetTrace.WpfTests.csproj -- --passive-wifi-validation
```

It prints the current connected interface's native radio values to the console
only. It never starts a nearby-network scan, changes Wi-Fi configuration,
requests internet data, opens the real NetTrace database or writes radio data
to disk. Windows permission or unsupported driver metadata remains unavailable.

Scoped Wi-Fi WPF checks can be run separately while developing this section:

```powershell
dotnet run --project tests/NetTrace.WpfTests/NetTrace.WpfTests.csproj -- --wifi-radio-color-check
dotnet run --project tests/NetTrace.WpfTests/NetTrace.WpfTests.csproj -- --wifi-radio-checks
```

The first runs only the radio/configuration color assertion and prints synthetic
ARGB, inherited-value-source and brush-identity diagnostics. The second runs the
87 Wi-Fi checks, including label/cue layout and the owned previews, without the
other integration groups. Both reuse the same injected fixture, owned database
and shutdown behavior as the full suite; neither performs native WLAN discovery.

History 001 adds 44 deterministic overview checks in `HistoryOverviewChecks.cs`.
Owned databases and a fixed clock cover empty, one, four, active-only and
completed-plus-active sessions; summary byte/duration updates; long profile and
network names; GB totals; cached timer refresh; immediate navigation and profile,
sleep/resume refresh; hidden-page query and rendering suppression; hidden-window
rendering suppression; unchanged monitor/timer identity; and the minimum
760×600 layout with vertical scrolling and no horizontal overflow. The same
checks preserve the detailed Sessions/Events tables, 100/200 record limits,
newest-first ordering, local timestamps, Refresh and repeated modal open/close.
Synthetic screenshots are saved in `history-previews` under the isolated test
output directory.

```powershell
dotnet run --project tests/NetTrace.WpfTests/NetTrace.WpfTests.csproj -- --history-overview-checks
dotnet run --project tests/NetTrace.WpfTests/NetTrace.WpfTests.csproj -- --history-live-validation
```

The explicit live target opens the existing real database using SQLite
`ReadOnly` and `query_only`, then makes a consistent SQLite backup beneath the
temporary directory. The overview service and detailed window read that copy;
the main-window monitor uses a separate empty owned store and never starts.
Existing historical records and the running application are left untouched.
The command saves temporary screenshots and reports their directory. An unfinished
snapshot row shows `Last confirmed` and uses persisted confirmation time and byte
counters; it does not imply live connectivity or extend connected time to now.
The check cannot read unflushed counters from another running process.

History 002 adds 44 checks in `HistoryUsageChecks.cs` and runs with the full suite:

```powershell
dotnet run --project tests/NetTrace.WpfTests/NetTrace.WpfTests.csproj -- --history-usage-checks
```

The owned fixtures exercise empty/mixed 7D, varied/mostly-zero 30D, GB scaling,
native stacked geometry and measurement colors, sparse labels, wrapped exact-value
WPF tooltip popups (including zero days), and stationary tooltip freshness during
active usage. They verify active/persisted deduplication, start-day attribution after
midnight, Today consistency, cached view refresh, hidden query/render suppression,
minimized/restored behavior, app-session range retention across navigation/window
recreation, and unchanged workers/timers/history records. At 760×600, the chart,
long recent-profile identities and full-history action remain reachable by vertical
scrolling without horizontal overflow. Screenshots are saved beneath
`history-usage-previews` in the owned test directory.

`--history-live-validation` also renders 7D/30D charts and actual tooltip popups
from the same read-only real-history backup. A single frozen snapshot time keeps
Today/chart definitions consistent. An unfinished stored session's persisted bytes
are included once on its local start day; current unflushed process counters remain
unavailable. Session/event row hashes must remain unchanged after every view.

Dashboard 001 has 34 checks in `DashboardSpeedChecks.cs` and runs with the full suite:

```powershell
dotnet run --project tests/NetTrace.WpfTests/NetTrace.WpfTests.csproj -- --dashboard-speed-checks
dotnet run --project tests/NetTrace.WpfTests/NetTrace.WpfTests.csproj -- --dashboard-live-validation
```

Owned zero/KB/MB/GB and asymmetric rate fixtures verify large adaptive byte-rate
primaries with muted Mbps directly below, semantic shared styles, unchanged peak
Mbps and cumulative session/data usage labels, stable transfer-rate help cues,
wrapped native help popups with focus/click/Escape behavior, and two aligned cards
at 760×600. Minimum-size scrolling preserves access to the data usage/current
connection sections. Synthetic page and help screenshots are saved beneath
`dashboard-speed-previews` in the owned test directory.

A two-second physical-counter fixture independently supplies 10 MB download and
2 MB upload: both live pairs must show 5.00 MB/s / 40.00 Mbps and 1.00 MB/s /
8.00 Mbps from the same raw measurements. Repeated presentation refresh must not
read adapter counters, replace workers/timers, change rate/accounting state or
alter History 001/002 session bytes. A temporarily unavailable owned usage table
also verifies the speed presentation has no database dependency.

The explicit live target passively observes a confirmed physical adapter over
35 bounded one-second intervals without generating traffic or opening the real
NetTrace database. It renders exact observed raw pairs in a separate empty owned
UI store and reports which adaptive traffic conditions occurred. If ordinary
background traffic never reaches MB/s, that live condition is reported as
unavailable; deterministic MB/s coverage remains part of the owned WPF checks.

Dashboard Data Usage 001 and Dashboard Polish 003 use the summary checks in
`DashboardUsageChecks.cs`, included in the full suite:

```powershell
dotnet run --project tests/NetTrace.WpfTests/NetTrace.WpfTests.csproj -- --dashboard-usage-checks
```

Owned date buckets distinguish Today, seven-day and thirty-day totals, including
the existing inclusive boundaries and exclusion of future days. The compact attached
selector cycles Today → Last 7 Days → Last 30 Days → Today with the existing cyan
style. Checks refresh each range and retain 7D/30D selection across all five other
navigation pages. Keyboard focus and Space cycling, removal of the separate tabs
and old cards, the native graph as the right column's only control, readable zero/GB
values and compact flush selector/card geometry are verified at 760×600, with
resizing and vertical access to Current Connection preserved.
Previews are saved under `dashboard-usage-previews` in the owned test directory.

Dashboard Data Usage 002 and Dashboard Polish 003 use rolling live Download/Upload
graph checks in `DashboardTrafficChecks.cs`, included in the full suite:

```powershell
dotnet run --project tests/NetTrace.WpfTests/NetTrace.WpfTests.csproj -- --dashboard-traffic-checks
```

An injected clock and owned physical counters verify that the native speed curves
use the same raw snapshot as the existing live cards, preserve asymmetric KB/s and
MB/s values, advance the 60-second timeline and expire old samples and peaks. Idle
zero rates and unavailable intervals stay distinct and hoverable. Tooltips show
local sample time and directional rates; a stationary popup updates to the sample
beneath its pointer as the graph moves. Summary range selection preserves the live
graph and physical accounting, and repeated same-second updates do not duplicate
samples, samplers or timers. Navigation, hiding and minimizing each continue more
than a minute of source sampling while freezing chart data and native drawing and
clearing hover. The cached immutable source remains bounded to 61 chronological
one-second observations. Returning immediately renders that already-collected
continuous recent minute without a visibility gap; genuine unavailable measurements
still split both curves. Source collection uses one existing adapter capture per
monitoring sample and preserves the existing worker and timer identities.
Native drawing, legend, axis and tooltip bounds are checked at 760×600 and after
resizing. Synthetic previews are saved under `dashboard-traffic-previews`. These
checks do not open the user's database, require an App Usage service or generate
network traffic.

App Usage 004.4.1 adds focused Today presentation and WPF checks in
`AppUsageTodayChecks.cs`, included in the full suite:

```powershell
dotnet run --project tests/NetTrace.WpfTests/NetTrace.WpfTests.csproj -- --app-usage-today-checks
```

Typed fake IPC responses cover the Today summaries and service ordering, normal
System/Unknown/NetMod rows, optional executable names, existing cumulative byte
formatting, loading, empty and safe unavailable states. Cancellation-resistant
controlled requests and an owned clock verify four-second scheduling, dispatcher
responsiveness, no overlapping requests, generation discard, rapid navigation,
failure recovery and disposal. Actual six-page navigation, hide/minimize/restore
and close behavior retain the existing single UI timer. Long application metadata
and GB totals render at 760×600 with ellipsis and vertical access to the final row;
synthetic screenshots are saved under `app-usage-today-previews`. The six-page
navigation and scrolling groups also inject unavailable range clients. Ordinary
tests never require or install the App Usage Windows Service.

App Usage 004.4.2 adds focused range-switching checks in `AppUsageRangeChecks.cs`,
included in the full suite:

```powershell
dotnet run --project tests/NetTrace.WpfTests/NetTrace.WpfTests.csproj -- --app-usage-range-checks
```

Typed responses cover Today, 7D and 30D summaries, exact empty messages,
unavailable/degraded states and response-range mismatch rejection. An owned clock
and cancellation-resistant requests verify immediate selection, the four-second
refresh of the selected range, retained selection across navigation, zero hidden
queries and generation discard during rapid range changes, including Today to 7D
to Today. Real controls verify exclusive selection, application-list updates,
loading/empty/unavailable states, hidden-window behavior and shared app-session
selection after view recreation. A synthetic `760x600-30d.png` preview beneath
`app-usage-range-previews` verifies the controls fit the minimum width. These checks
use injected clients and owned physical stores, require no service installation,
and never open the service App Usage database.

The explicitly requested live target is separate from the ordinary suite:

```powershell
dotnet run --project tests/NetTrace.WpfTests/NetTrace.WpfTests.csproj -- --app-usage-today-live-validation --output <json-path> --screenshots <directory>
```

It uses the normal authenticated IPC client against an already-running temporary
development service. Service creation and cleanup belong to the separate temporary
validation script; the WPF target never installs or starts a service and never
opens the App Usage database directly. `--absent` performs a short unavailable
page check after that service has been removed.

App Usage 005.1 adds `--app-usage-production-live-validation --output <absolute-json-path>`
for the visible, unelevated real page against the installed production collector.
Use it through `tools/NetTrace.AppUsageService/Validate-ProductionInstall.ps1`,
which coordinates the separate elevated install/restart/uninstall worker. The
page loads all three ranges, observes unavailable during a service stop and
recovers the selected range after restart. Physical monitoring uses an owned
fixture; app data comes from production IPC and the real retained ProgramData
database. This explicit workflow is separate from the ordinary WPF suite.

App Usage 005.2 adds `AppUsageSetupChecks.cs` to the full suite and a focused target:

```powershell
dotnet run --project tests/NetTrace.WpfTests/NetTrace.WpfTests.csproj -- --app-usage-setup-checks
```

Injected service states cover missing, running, stopped and unreadable collectors.
Controlled installer results verify explicit setup, cancellation and failure
feedback, a deliberate retry, automatic selected-range reconnection, no duplicate
launch, zero hidden-page work, and close during installation. The platform tests
inspect the fixed installer-only `runas` invocation and preserve the desktop
identity and token. They never launch UAC, install a service or open its database.
The actual 760x600 window saves setup and cancelled previews beneath
`app-usage-setup-previews`. Existing App Usage window, navigation and scrolling
checks inject a running setup service so the ordinary suite stays independent of
the machine's service installation. Normal startup, status polling, navigation
and window restoration are all checked to launch no installer automatically.

App Usage 005.3 adds 73 deterministic checks in `AppTrackingSettingsChecks.cs` to
the full suite and a focused Settings target:

```powershell
dotnet run --project tests/NetTrace.WpfTests -- --app-tracking-settings-checks
```

Injected management states cover Running, Stopped, Not installed and unavailable
status, including uninstall button availability. Controlled confirmation and
uninstall outcomes cover cancelled confirmation, explicit success, UAC
cancellation, operational failure, retry and duplicate-launch suppression.
The fixed elevated launch contains only the existing installer and its literal
`uninstall` argument; it has no database deletion option and never relaunches
NetTrace. Normal startup, navigation and refresh launch no installer.

The actual MainWindow uses owned physical-monitoring fixtures and injected
management services. Successful uninstall immediately updates Settings and
invalidates App Usage's old data and late responses. Its missing-service setup
state appears without restart and retains the selected range. Hidden App Usage
does not issue IPC requests. Compact previews and layout checks use 760x600.
Ordinary checks require no collector service, request no UAC and never open the
protected production App Usage database. The unchanged 005.1 installer tests
separately verify database/history preservation across uninstall outcomes.

App Usage 005.4 adds `AppUsageHistoryClearChecks.cs` to the full suite:

```powershell
dotnet run --project tests/NetTrace.WpfTests -- --app-usage-history-clear-checks
```

The focused checks cover known/protected/missing storage, explicit confirmation,
fixed installer-only elevation, cancelled UAC, failure and retry, collector-update
feedback, shared-operation exclusion and no automatic launch. Real Settings
controls exercise successful clear, retained 30D, immediate empty cached usage,
zero hidden IPC, three empty ranges and subsequent new usage. Owned Dashboard,
History and physical session values remain unchanged. Delayed responses and
closed-window completions cannot restore stale rows or mutate disposed controls.

These UI checks use injected management outcomes and owned data. Actual SQLite
clear, buffered-event filtering, schema preservation and new usage are verified
by storage/checkpoint suites; management authentication and unchanged service
actions are verified by service/installer suites. Ordinary checks never clear
the protected production database or launch UAC.

App Usage 005.5 adds `AppTrackingUpdateChecks.cs` to the full suite:

```powershell
dotnet run --project tests/NetTrace.WpfTests -- --app-tracking-update-checks
```

The focused checks cover the shared SHA-256 inventory algorithm, exact owned
SCM version-path parsing, matching packages with no update, update availability,
the explicit fixed installer-only `runas` invocation, cancelled UAC, failure and
retry. Controlled launchers also exercise shared operation exclusion, cancelled
waiters and a second window observing the same update without duplicate elevation.
Startup, polling and navigation never launch the installer automatically.

Settings controls use injected update services to verify Update available and
button availability, successful update, retained Today/7D/30D selection, automatic
App Usage reconnection, zero hidden-page IPC and rejection of stale responses.
Owned data checks retain recorded usage and Dashboard/History values. The desktop
identity and elevation token remain unchanged. The ordinary suite neither updates
the installed production service nor opens or clears its protected database.
The existing installer suites separately verify update database/configuration
preservation and rollback.

Live validation is an explicit workflow documented in
[005.5 validation](../../tools/NetTrace.AppUsageService/VALIDATION0055.md).

App Usage 005.6 adds 62 focused lifecycle checks to the complete suite:

```powershell
dotnet run --project tests/NetTrace.WpfTests -- --app-usage-lifecycle-checks
```

The explicit `--app-usage-lifecycle-live-validation --directory ABSOLUTE_PATH
--events Update,Restart,ForcedRecovery,Sleep --range 30D` mode observes actual
external lifecycle actions using only the existing four-second window timer.
It retains raw request and visibility diagnostics, validates fresh requests
after completion markers, preserves the range and checks zero hidden requests.
It never mutates the service or injects a synthetic reconnect notification.
See [005.6 validation](../../tools/NetTrace.AppUsageService/VALIDATION0056.md).

App Usage 006.1 adds 78 checks in `AppUsageRowIconChecks.cs` to the full suite:

```powershell
dotnet run --project tests/NetTrace.WpfTests -- --app-usage-row-icon-checks
```

The checks exercise friendly and executable names, case-insensitive duplicate
subtitles, distinct application/System/Unknown fallbacks, unavailable paths,
cached successes and failures, concurrent requests and the three-extractor
limit. A blocked injected extractor verifies dispatcher responsiveness; native
extraction also reads an existing local Windows executable without launching it.
Extracted WPF images are frozen and row notifications return to the dispatcher.

The actual 760x600 MainWindow checks icons, collapsed duplicate subtitles,
long-name ellipsis and horizontal bounds across Today, 7D and 30D. Late icon
completion during range switching cannot replace current rows or totals. Existing
range checks retain periodic selected-range refresh and zero hidden-page requests.
Owned fixtures never change the production collector, database or app permissions.
See [006.1 validation](VALIDATION0061.md) for commands, totals and preview evidence.
