# NetTrace WPF integration tests

The 370 Windows-only STA integration checks use isolated temporary databases and injected
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

Legacy sampler/lifecycle fixtures now use owned adapter inventory and counters,
including child processes. The complete suite requires no real adapter or network
connection. It never modifies Windows VPN settings, machine power
state, or the real NetTrace database. Test power notifications are sent only
to its own hidden lifecycle HWND. Test data is created beneath the test output
directory.
