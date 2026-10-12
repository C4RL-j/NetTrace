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

Dashboard 001 adds 32 checks in `DashboardSpeedChecks.cs` and runs with the full suite:

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
