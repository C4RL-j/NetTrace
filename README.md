# NetTrace

Lightweight Windows network usage and diagnostics monitor.

NetTrace V1.1 establishes the NetTrace identity and safely migrates local history from the previous Data Usage Monitor build (Task 001). Task 002 adds connection profiles and network mappings; Task 003 adds physical connection sessions and event history. Tasks 004C–004E persist VPN intervals, events and lifecycle boundaries. Tasks 005A–005B add the system tray, background monitoring and optional startup with Windows. Built with C#/.NET 8, WPF, and SQLite.

## Current features

- Active network adapter selector
- Live download/upload traffic in Mbps
- Persistent daily peaks from passive traffic observations
- Download/upload totals for the current app session
- Today total
- Last 7 days total
- Last 30 days total
- Download vs upload breakdown
- Adapter type, IPv4 address, and local Wi-Fi/Ethernet link rate
- Local SQLite history
- Connection profiles with Telco / ISP and Device names
- Multiple network identities mapped to one profile
- Physical connection sessions and recent connection events
- System tray with monitoring while the dashboard is closed
- Optional current-user Windows startup in tray mode
- Background ICMP latency and packet-loss measurements
- No telemetry

## Measurement meanings

**Live Traffic** is the current transfer rate on the monitored adapter, displayed in decimal Mbps (one megabit is 1,000,000 bits). It watches existing traffic and generates no speed-test traffic. **Data Usage** stays in byte quantities such as MB and GB. The app-session totals cover this NetTrace process, including time with the dashboard closed; a connection session covers one continuous observed physical connection.

**Peak today** is the highest sampled download/upload traffic NetTrace observed across monitored adapters during the local calendar day. Peaks are saved about every 30 seconds and on normal exit, survive restarts, and begin at zero on a new day. They are observations while NetTrace runs, not guaranteed maximum internet throughput. Earlier usage totals cannot reconstruct old traffic peaks, so peak recording begins with this build. A sudden termination can lose the most recent unsaved peak.

**Wi-Fi link rate** describes the local PC-to-router/hotspot link; Ethernet and other adapters are labeled accordingly. It is not measured internet speed. The status indicator describes monitoring activity. Raw ICMP measurements run in the background and the Health page shows the latest completed cycle. Active internet speed tests remain future work.

## Important limitation

Usage is recorded while the app runs. It does not import Windows Settings usage history.

## Requirements

- Windows 10 or Windows 11
- .NET 8 SDK for development

## Run from source

Open PowerShell inside this folder:

```powershell
dotnet restore
dotnet run
```

## Build a normal release

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

Output will be under:

```text
bin\Release\net8.0-windows\win-x64\publish\
```

## Database location

```text
%LOCALAPPDATA%\NetTrace\usage.db
```

Usage is written approximately every 30 seconds and once more when NetTrace exits normally. Closing the dashboard leaves monitoring running; use **Exit** in the tray menu to stop it.

On first launch, if the NetTrace database is absent and `%LOCALAPPDATA%\DataUsageMonitor\usage.db` exists, NetTrace copies a consistent SQLite snapshot, including committed WAL data, to the new location. The legacy database is retained for rollback. An existing NetTrace database always takes precedence; later legacy writes are not synchronized.

The copy is validated and atomically placed at the new path without overwriting a database. Copy or validation failures stop startup with an error instead of starting with empty history. SQLite's read-only source connection does not change legacy history; its WAL coordination files may be accessed while the old app is running.

The current schema uses `PRAGMA user_version = 5`. Compatible unversioned databases progress through version 1; version 1 databases gain `ConnectionProfiles` and `NetworkMappings`, version 2 databases gain `ConnectionSessions` and `ConnectionEvents`, version 3 databases gain `VpnSessions`, and version 4 databases gain `daily_traffic_peaks`. All upgrades run transactionally without rebuilding or changing existing usage history, profiles, mappings, sessions, or events. Version changes commit only after successful migration and validation. `Services\DatabaseMigrator.cs` is the single place for schema upgrades. Incompatible or newer schemas stop startup rather than being altered.

## Connection profiles

A profile represents Telco / ISP + Device, such as `DITO — D2 Router`. Network mappings are separate: several SSIDs can belong to the same profile. No telco is guessed and no profile is created automatically.

On **Connections**, use **Profiles** in the current-connection card to create or edit profiles and see their mappings. For an **Unassigned network**, choose **Assign profile**, select an existing profile, or choose **New profile** and enter the Telco / ISP and Device names. Creating and assigning together is atomic. Repeating an assignment to the same profile keeps the existing mapping; a conflicting assignment is rejected. Profile deletion is deferred.

Wi-Fi recognition reads the actual associated SSID through Windows Native Wi-Fi APIs. Exact SSID case and whitespace are preserved; non-text SSIDs use lossless byte identities. Ethernet recognition uses an identified Windows network GUID, not an adapter GUID. Windows location permissions may be needed to read Wi-Fi SSIDs, and unidentified Ethernet networks cannot be assigned until Windows supplies a stable identity.

The Connection card distinguishes a recognized profile, a valid unassigned network, an unavailable network identity, and a disconnected connection. **Assign profile** is offered only for a valid unassigned identity. If Windows denies SSID access or an identity API fails, NetTrace displays the reason and keeps bandwidth monitoring independent; it does not create placeholder mappings or change Windows permissions.

Physical connection recognition is independent of the bandwidth adapter selector. Windows network-change events drive recognition without automatic dialogs. If several physical connections are active, gateway presence and the IPv4 default-route metric determine the preferred connection, with Ethernet as a tie-break. IPv6-only connections are supported without IPv6 route-metric ranking.

## Connection sessions and events

A session is one continuous observed physical network identity and adapter. Different SSIDs create different sessions even when they map to the same profile. Repeated callbacks and friendly-name changes do not split a session. Valid unassigned networks get sessions with no profile; assigning the current network associates only its active session. Completed sessions keep their profile association and captured network identity. Profile names in history follow their stable profile ID.

Dashboard and Connections show the current session duration and traffic. On **History**, choose **View history** to open the latest 100 sessions and 200 events, with UTC timestamps displayed in local time. Events include connection/disconnection, network change, profile assignment/change, sleep, resume, unexpected-exit recovery, normal application exit, and network-identity unavailability.

Session traffic uses the existing one-second byte delta, now owned by the application monitoring service, and only accepts samples from the current physical adapter. Recognition changes automatically select that adapter and establish a new baseline; profile edits and repeated observations retain the baseline. Manual adapter selection remains available. If another or virtual adapter is selected, monitoring follows it while physical-session traffic pauses; the Connection card explains which selection is needed. No virtual-interface traffic is attributed to physical sessions.

Progress and confirmed observations are saved about every 30 seconds and on transitions/normal exit. Identity is also refreshed at that batch interval to confirm idle connections. Uncertain transition intervals are discarded rather than counted on a new connection. A sudden termination can lose the current unflushed batch; restart closes the unfinished session exactly at its persisted `LastConfirmedAtUtc`, records recovery, and starts a fresh session after detection. Only one NetTrace run may own a history database at a time.

Startup acquires an exclusive Windows file handle on `usage.db.nettrace.lock` before opening, migrating, or recovering SQLite and keeps it until shutdown. Concurrent instances are rejected, including in another Windows login session. Tray Exit releases the handle; a hard kill or crash also releases operating-system ownership. The small sidecar remains on disk and is reused, so its mere existence never means the database is locked. No stale PID or manual lock-file cleanup is needed. Startup failures also release ownership.

SQLite indexes cover the single active session, recent sessions/events, profile sessions, and start-time range filtering. Event foreign keys have their own indexes. Earlier v3 databases receive the event relationship indexes transactionally without changing historical rows or the schema version.

NetTrace registers an application-owned hidden native window for Windows suspend/resume notifications, including connected standby. It stays alive without a dashboard. These messages end the active session on sleep and require a fresh identity observation before a new session starts after resume, including for the same SSID. Sleep time is excluded. When Windows cannot provide identity, NetTrace conservatively ends the old session at its last confirmation and keeps bandwidth sampling independent; a recovered identity begins a new session. Completed history is never broadly reassigned.

## VPN detection foundation (Task 004A)

`VpnDetectionService.GetCurrentState()` provides an independent, on-demand snapshot: `NoVpn`, `TunnelDetected`, or `Indeterminate`. Windows native interface status and GUID-matched driver component/service IDs identify operational local tunnel candidates; connected Windows RAS VPNs provide separate native evidence. Adapter aliases, descriptions and process names do not establish a VPN. Dormant/disconnected interfaces, physical hardware, loopback, standard virtualization adapters and IPv6 transition mechanisms are excluded. Ambiguous status, incomplete API results and TAP's Always Connected setting produce uncertainty.

`TunnelDetected` confirms local tunnel evidence, not routing, encryption or endpoint reachability. `NoVpn` means no supported active candidate was found; unrecognized or customized VPN drivers can be missed, and Wintun also supports non-VPN tunnels. RAS-only evidence retains connection/device names but leaves interface identifiers unavailable rather than inventing them. No network settings or permissions are changed.

## VPN routing verification (Task 004B)

`VpnRouteVerifier.GetCurrentState()` reuses Task 004A detection and reads IPv4/IPv6 routes separately through Windows `GetIpForwardTable2`, `GetIpInterfaceEntry` and `GetIfEntry2`. Each tunnel/family result retains prefixes, next hops, interface GUID/index, route/interface metrics, usability and an explanation. A fresh query reads new evidence; no routes are configured or packets sent.

`FullTunnel` requires the detected interface to win effective general routing through a default route or paired `/1` routes, with no effective public bypass. Selection uses longest matching prefix before combined route/interface metrics. All prefix boundaries are evaluated, rather than testing a few destinations. Private/LAN, loopback, link-local and multicast ranges do not defeat general-internet coverage. `SplitTunnel` requires effective VPN forwarding prefixes plus confirmed general routing through another interface. Installed, losing, expired, loopback or inactive routes alone cannot verify routing.

Missing metadata, equal-cost VPN/non-VPN alternatives, failed family queries, incomplete general coverage and public bypasses that could be VPN peer exceptions stay `Unverified`. RAS-only candidates without an interface identity also remain unverified. Source/interface-bound traffic and routing changes after the snapshot can differ from this global-table assessment.

Task 004B.1 separates each family's availability (`Usable`, `Unavailable`, `Unknown`) from its routing decision. A complete, valid snapshot with no usable non-loopback interfaces is unavailable for routing; this does not claim the Windows protocol stack is disabled. Local-only or physical-only family routes still establish usability, so missing VPN/default routes remain unverified. Failed or incomplete queries stay unknown. Combination ignores only confirmed unavailable families: all remaining families must verify Full for `FullTunnel`; a verified Split family yields `SplitTunnel`; any remaining unknown/unverified family, or both unavailable families, yields `Unverified`.

## VPN interval storage (Task 004C)

`VpnSessions` stores a physical `ConnectionSessionId`, available interface GUID/index, interface name/description, latest routing mode and UTC start/confirmation/end timestamps with an explicit end reason. There are no VPN byte counters. A restricted foreign key protects the parent relation; indexes cover physical-parent/recent queries, and a partial unique index enforces one unfinished VPN interval globally.

`VpnSessionMonitor` reuses native detection and routing verification off the UI thread, on Windows network-change callbacks and at a separate 30-second interval. Starting/stopping a VPN never creates or closes a physical session. Repeated identities keep the same VPN row; route-mode and display-name changes update it. A changed GUID/index starts a new interval. A sole RAS candidate without interface IDs uses its exact connection/device names as a limited identity; renamed RAS entries split intervals. Multiple candidates are not guessed into a primary VPN. Uncertain detection ends at the last VPN confirmation with an uncertainty reason; confirmed `NoVpn` ends with `VpnDisconnected`.

The observer captures the physical parent before querying, rejects stale work after a parent change and persists the existing physical tracker's confirmed progress before saving a VPN row. VPN timestamps are bounded by that parent confirmation, without changing byte totals or manufacturing physical liveness. Parent closure ends its active VPN interval in the same SQLite transaction. Shutdown stops and joins the observer before closing the parent and releasing database ownership. Completed intervals and captured identity are immutable.

VPN storage adds no VPN UI, health checks or traffic accounting. Intervals describe observed state: rapid changes between callbacks/polls may be missed, timestamps follow available physical confirmation, and only the latest routing mode is retained within an interval.

## VPN events (Task 004D)

The existing `ConnectionEvents` timeline records `VpnConnected` when an interval starts, `VpnDisconnected` when it ends, and `VpnRouteChanged` only when its active routing mode changes. Events retain the physical session/profile/network context and include the VPN name, routing mode and, for closure, the end reason. An uncertainty or physical-session closure ends an observed interval without claiming that Windows positively detected VPN OFF.

SQLite triggers write events in the same transaction as VPN transitions, including physical-parent closure. Repeated confirmations, alias-only edits and unchanged routing produce no events; a failed event write rolls back its interval transition. Existing schema-v4 databases gain these triggers transactionally on startup without rewriting history or backfilling events. Physical event behavior, dashboard layout and bandwidth accounting remain unchanged.

## VPN lifecycle (Task 004E)

The existing Windows power handler pauses VPN observation and invalidates in-flight results before processing sleep. The physical history transaction closes the VPN interval with `Sleep` before closing its parent at the existing sleep boundary. Resume refreshes physical recognition and VPN detection; a fresh active tunnel creates a new interval, while VPN OFF creates none. Pre-sleep results cannot be reused, including when a native query completes after resume. No separate power listener or lifecycle polling is introduced.

At startup, existing physical-session recovery closes any unfinished VPN child exactly at its own persisted `LastConfirmedAtUtc`, with `UnexpectedExit`, before recovering its parent. The child's last confirmation can be earlier than the parent's; neither is extended through unknown downtime. Recovery and its disconnect event commit together, so retries cannot duplicate events. Fresh startup observations can then create new physical/VPN intervals under the existing database run lease.

Normal shutdown stops and joins the VPN observer, flushes pending usage and closes its active interval with `ApplicationExit` in the same transaction as physical shutdown. Sleep/exit closure first persists the existing final physical confirmation within that transaction so the child's end stays inside its parent. Completed VPN history is untouched. Confirmation remains periodic at the existing 30-second VPN observation interval; a crash can lose progress since the last committed observation. Database-write failures roll back lifecycle changes for retry; persistent storage failures are reported through the existing error/log paths.

## System tray and background monitoring (Task 005A)

Closing the dashboard destroys that view and stops its refresh timer. `MonitoringService` continues sampling, batched persistence, physical sessions, profile recognition and VPN tracking without any dashboard or WPF dispatcher. The tray menu provides **Open NetTrace** and **Exit**; double-click also opens NetTrace. Open recreates a dashboard from the current service state, or restores the existing window. App totals, selected adapter, sampling baseline and current session IDs survive dashboard recreation.

The application explicitly shuts down only on tray Exit or Windows session ending. Exit stops and joins native observers, flushes pending usage, closes VPN/physical intervals with their existing shutdown behavior, disposes the tray icon/power listener and releases database ownership. Background mode performs no dashboard refresh or history-total rendering queries. The tray uses the NetTrace globe icon and the standard Windows menu; no Windows service is installed.

## Windows startup and background launch (Task 005B)

`NetTrace.exe` opens the dashboard and tray normally. `NetTrace.exe --background` starts the same monitoring services and tray without creating a dashboard or dashboard refresh timer. Use **Open NetTrace** in the tray to create the dashboard later; **Exit** still flushes and closes physical/VPN sessions cleanly. From source, use `dotnet run -- --background`.

Check **Start NetTrace with Windows** on **Settings** to enable startup for your current Windows user, or uncheck it to disable. Changes take effect immediately and require no administrator rights. The checkbox reads the actual registration when the window opens, regains focus or switches to Settings; a registry/path error appears inline and does not stop monitoring. Enabling is optional and never happens automatically on launch.

The `NetTrace` string value under `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run` contains the absolute apphost path, quoted for spaces, followed by `--background`: `"C:\Apps\NetTrace\NetTrace.exe" --background`. Repeated enabling updates that one value; disabling removes only that value and preserves other applications' startup entries. NetTrace never registers `dotnet.exe` or a test host.

Windows runs the entry when this user signs in. Keep the executable at a stable location; after moving it, enable startup again from the new location to update the path. Windows Task Manager or policy can separately block startup execution; NetTrace leaves those controls unchanged and displays its Run registration state.

## Top navigation (UI Foundation 001)

The existing dark window now has five pages, with a horizontal navigation bar and
an underline on the active page:

| Page | Contents |
| --- | --- |
| Dashboard | Live traffic, daily peaks, data usage and a compact current connection/session summary |
| Health | Latest raw ICMP measurements, latency/jitter chart and packet-loss history |
| Connections | Current connection, monitored adapter/details, Profiles and Assign profile |
| History | Opens the existing recent sessions/events history window |
| Settings | Start NetTrace with Windows and the local database location |

WPF's native tab control owns page selection and retains each page's controls.
Navigation does not create, restart or reset monitoring, counters or sessions.
The window still has one refresh timer; usage-total queries run only while Dashboard
is selected. Settings rereads the actual startup registration when opened.
Closing the window and reopening it through the tray keeps the existing monitoring
service; a recreated window starts on Dashboard.

## ICMP latency and packet loss (Network Health 001)

Network Health 001 adds raw ICMP measurements through application-owned `IcmpMonitor`.
The default target is `1.1.1.1`; the first burst starts with monitoring, followed by
one burst every 15 seconds measured between cycle starts. Each burst makes four
sequential requests with a one-second timeout per request. This generates a small
amount of ICMP traffic, including while the dashboard is closed. The target,
interval, timeout and ping source are constructor settings, ready for later configuration.

Each completed burst exposes its UTC completion timestamp, target, successful/failed
reply counts, average successful-reply round-trip time and failed-attempt percentage.
All failures produce 100% loss and a null latency. Filtering, timeouts and local ping
errors can all prevent replies; these measurements never declare the internet offline
or change physical/VPN sessions. The latest result is available in memory through
`MonitoringService.LatestIcmpMeasurement`; measurements are not stored in the database.

UI Foundation 001.1 displays that latest completed result on **Health**. It shows
**Waiting for first measurement...** until a cycle completes, then displays the target,
average successful-reply latency, packet loss, successful replies and local sample time.
All-failed cycles show **Unavailable** latency and **100%** loss. The existing window
refresh timer reads the result within one second of completion; returning to Health
reads it immediately. Page navigation never starts, stops or restarts probes, and the
application-owned worker continues in tray-only mode.

Network Health 002 adds **Jitter** to the same Health card. Within each completed
burst, jitter is the mean absolute difference between consecutive successful RTTs
in attempt order: `sum(abs(RTT[i] - RTT[i-1])) / (successful replies - 1)`.
For example, 10, 20, 5 and 15 ms yield 11.67 ms jitter. Failed replies are skipped,
never substituted with zero; fewer than two successes show **Unavailable**. Each
burst starts a fresh calculation, including after resume.

`MonitoringService.LatestIcmpMeasurement` exposes latency, loss and `JitterMs`.
`MonitoringService.RollingIcmpSamples` returns a read-only snapshot of the newest
60 completed bursts, oldest first (about 15 minutes at the default interval).
Oldest entries are evicted automatically. Completed samples remain across sleep,
but unfinished bursts are discarded; history is in memory and resets on app exit.
No extra worker, graph or database storage is added. This measures short-burst RTT
variation, rather than long-term jitter or one-way media timing.

Network Health 003 adds a **Connection Stability** chart below the Health statistics.
Native WPF drawing plots average latency in blue and jitter in amber, on a shared
millisecond scale. It projects the existing read-only rolling history, up to 60
completed cycles, oldest to newest with the newest at the right. Missing values
break the relevant line; a real zero remains a valid point. A single valid cycle
is shown as a dot. Sparse local-time labels identify the first and last samples;
horizontal spacing represents completed cycles, rather than elapsed time.

The existing window refresh updates chart data only when a new cycle completes;
entering Health, showing the window or restoring it displays the current history
immediately. Unchanged snapshots reuse the current presentation. Hidden, inactive
or minimized views do not update the chart, and minimizing pauses the existing
window refresh timer. The application-owned worker continues unchanged, including
in tray mode. No chart dependency, additional timer or health persistence is added.

Network Health 004 adds a compact **Packet Loss** strip inside Connection Stability,
below the latency/jitter chart. Both controls share the exact same immutable
presentation snapshot and completed-cycle positions. Purple bar height is the
cycle's packet-loss percentage on a fixed 0–100% scale: zero is baseline-only,
25% is quarter height, and 100% is full height, including when latency/jitter are
unavailable. Arbitrary finite percentages are supported and clamped for display;
nonfinite inputs leave no bar rather than inventing a measurement. Empty history
shows **Waiting for packet loss samples...**.

The strip uses native WPF drawing and the same visible-page update/restore lifecycle.
It creates no worker, timer or separate history source. These bars describe ICMP
loss only; blocked ICMP replies cannot establish internet availability. History
remains in memory and is cleared when NetTrace exits.

The existing application lifecycle starts, pauses for sleep, resumes and cancels the
single ICMP worker. Incomplete or canceled bursts are discarded, bursts cannot overlap,
and exit does not wait for long ping timeouts. Tests inject ping sources and never
depend on public internet access.

## Verification

Run the isolated migration checks without touching your application data:

```powershell
dotnet run --project tests\NetTrace.RegressionTests\NetTrace.RegressionTests.csproj
dotnet run --project tests\NetTrace.WpfTests\NetTrace.WpfTests.csproj
```
