# NetTrace

Lightweight Windows network usage and diagnostics monitor.

NetTrace V1.1 establishes the NetTrace identity and safely migrates local history from the previous Data Usage Monitor build (Task 001). Task 002 adds connection profiles and network mappings; Task 003 adds physical connection sessions and event history. Tasks 004C–004E persist VPN intervals, events and lifecycle boundaries. Tasks 005A–005B add the system tray, background monitoring and optional startup with Windows. Built with C#/.NET 8, WPF, and SQLite.

## Current features

- Active network adapter selector
- Live download/upload traffic with adaptive byte-rate primary and Mbps secondary
- Persistent daily peaks from passive traffic observations
- Download/upload totals for the current app session
- Dashboard Data Usage summary with a compact cycling range selector and download/upload breakdown
- Adapter type, IPv4 address, and local Wi-Fi/Ethernet link rate
- Local SQLite history
- Connection profiles with Telco / ISP and Device names
- Multiple network identities mapped to one profile
- Physical connection sessions and recent connection events
- System tray with monitoring while the dashboard is closed
- Optional current-user Windows startup in tray mode
- Background ICMP latency and packet-loss measurements
- Background DNS/HTTPS internet reachability checks
- No telemetry

## Measurement meanings

**Live Traffic** is the current transfer rate on the monitored adapter. Download and Upload show a large adaptive byte-rate (B/s, KB/s, MB/s or GB/s), with the existing decimal Mbps directly below as a smaller secondary reading. Both formats use the same captured raw bytes-per-second measurement; 1 MB/s equals 8 Mbps. Byte rates use decimal units: 1 KB/s is 1,000 B/s, 1 MB/s is 1,000,000 B/s and 1 GB/s is 1,000,000,000 B/s. The unit is chosen before rounding, with compact magnitude-based precision and idle displayed as 0 B/s / 0.00 Mbps. A reading immediately below a unit boundary may round to 1000 in its current unit; the unit changes at the exact raw threshold.

Live Traffic watches existing traffic and generates no speed-test traffic. The stable help cue beside each Download/Upload label reuses the centralized wrapped help control, with the same hover, focus, click and Escape behavior as Connections. Peak today retains its Mbps format. **Data Usage** and App session remain cumulative byte quantities using the existing size formatter. The app-session totals cover this NetTrace process, including time with the dashboard closed; a connection session covers one continuous observed physical connection. This presentation change adds no timer, worker, sampler, network/database query or Settings preference.

**Peak today** is the highest sampled download/upload traffic NetTrace observed across monitored adapters during the local calendar day. Peaks are saved about every 30 seconds and on normal exit, survive restarts, and begin at zero on a new day. They are observations while NetTrace runs, not guaranteed maximum internet throughput. Earlier usage totals cannot reconstruct old traffic peaks, so peak recording begins with this build. A sudden termination can lose the most recent unsaved peak.

**Wi-Fi link rate** describes the local PC-to-router/hotspot link; Ethernet and other adapters are labeled accordingly. It is not measured internet speed. The status indicator describes monitoring activity. Raw ICMP measurements run in the background and the Health page shows the latest completed cycle. Active internet speed tests remain future work.

**Dashboard Data Usage** defaults to Today. Its compact attached cyan selector cycles Today → Last 7 Days → Last 30 Days → Today when clicked or activated with the keyboard. The same left summary card shows the existing total/download/upload values, and the selected range stays while navigating between pages in the same window. Range switching uses the existing usage totals query and quantity formatter; usage accounting is unchanged.

The right half shows a native WPF rolling **live traffic** graph for Download and Upload over the last 60 seconds, using the same raw bytes-per-second samples as the Live Traffic cards and their adaptive B/s, KB/s, MB/s and GB/s formatting. The existing monitoring sampler keeps at most 61 one-second observations in memory while monitoring is active, including when Dashboard is hidden, minimized or closed. Immutable raw snapshots are cached separately from chart projection. Only a visible Dashboard builds chart data and draws; returning immediately shows the already-collected recent minute. Hover shows the local sample time, each direction and combined speed. Idle samples remain zero; unavailable measurements and real sampling interruptions create gaps, while Dashboard visibility does not. Changing the monitored adapter starts a new trace. The compact selector continues to select accumulated summary totals while the graph stays live. It adds no sampler, timer, worker, database query or persisted speed history.

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

On **Connections**, use **Profiles** in the current-connection card to create or edit profiles and manage their mappings. **Map current connection** maps the latest usable identity to the selected saved profile. Repeating the same mapping is a no-op; moving another profile's mapping requires **Move** confirmation. **Remove mapping** removes only the selected network, and **Delete profile** requires explicit confirmation. For an **Unassigned network**, **Assign profile** retains the existing assignment workflow, including atomic creation and assignment together.

Wi-Fi recognition reads the actual associated SSID through Windows Native Wi-Fi APIs. Exact SSID case and whitespace are preserved; non-text SSIDs use lossless byte identities. Ethernet recognition uses an identified Windows network GUID, not an adapter GUID. Windows location permissions may be needed to read Wi-Fi SSIDs, and unidentified Ethernet networks cannot be assigned until Windows supplies a stable identity.

The Connection card distinguishes a recognized profile, a valid unassigned network, an unavailable network identity, and a disconnected connection. **Assign profile** is offered only for a valid unassigned identity. If Windows denies SSID access or an identity API fails, NetTrace displays the reason and keeps bandwidth monitoring independent; it does not create placeholder mappings or change Windows permissions.

Physical connection recognition is independent of the bandwidth adapter selector. Windows network-change events drive recognition without automatic dialogs. If several physical connections are active, gateway presence and the IPv4 default-route metric determine the preferred connection, with Ethernet as a tie-break. IPv6-only connections are supported without IPv6 route-metric ranking.

## Connection sessions and events

A session is one continuous observed physical network identity and adapter. Different SSIDs create different sessions even when they map to the same profile. Repeated callbacks and friendly-name changes do not split a session. Valid unassigned networks get sessions with no profile; assigning the current network associates only its active session. Completed sessions keep their profile association and captured network identity unless that profile is explicitly deleted. Profile names in history follow their stable profile ID; profile deletion clears the association while retaining the connection records.

Dashboard and Connections show the current session duration and traffic. **History** shows a compact Today summary (Sessions, Connected time, Download and Upload) and the newest four connection sessions, including the current active session. Choose **View full history** to open the unchanged detailed window with the latest 100 sessions and 200 events, with UTC timestamps displayed in local time. Events include connection/disconnection, network change, profile assignment/change, sleep, resume, unexpected-exit recovery, normal application exit, and network-identity unavailability.

Today means the current local calendar day. Each distinct session overlapping today counts once; connected time includes only the part inside today's boundaries, up to now for the confirmed active session. A session from 23:50 yesterday to 00:20 today contributes 20 minutes. Sessions record total bytes without a per-minute traffic timeline, so **download and upload belong to the local day on which the session started**, for both completed and active sessions. An active session started yesterday contributes today's connected time and session count, but its bytes remain attributed to yesterday. Traffic is never estimated from duration. These History totals can differ from Dashboard daily usage, which is sampled by local day.

The overview reuses the existing history store and live physical-session counters, replacing the persisted active record by ID without writing duplicate records. It reads only today's relevant records and the latest four sessions. Navigation, restore and existing monitoring changes refresh persisted data; the existing visible-window timer updates active time/bytes from the cached records. Hidden pages perform no overview queries or rendering and add no timers or workers. Deleted profiles retain their captured network identity with an Unassigned fallback. An empty history shows a plain message; an active-only first day shows the active session.

History's **DATA USAGE** section adds a native stacked daily bar chart: cyan Download and violet Upload. **7D** is the default and includes today plus the previous six local calendar days; **30D** includes today plus the previous 29. Dates run oldest to newest, with today at the right and zero-use dates retained. The selector uses the existing accent underline and remembers its choice for the current app session, including navigation and window recreation. The scale follows the highest visible daily total; 30D uses sparse date labels. Hover any day's column, including a zero day, for its local date and recorded usage in a compact wrapped tooltip. A period without usage displays a plain message inside the chart.

Daily chart bytes follow the same **local session start-day attribution** as History 001. A session from Dec 31 at 23:55 to Jan 1 at 00:15 contributes all of its recorded download/upload to Dec 31. Active counters replace the persisted representation by session ID exactly once, and an active session started yesterday contributes bytes to yesterday's chart bucket rather than today. Today and the chart receive one consistent live byte snapshot; the chart's latest bucket agrees with Today usage. Dashboard sampled-day totals can still differ from these session start-day totals.

The chart uses a bounded start-timestamp query against the existing history store, with no second database, charting dependency, animation, timer or worker. Persisted records refresh on entry, range selection and existing monitoring changes; cached live updates reuse the current window refresh cadence and retain the drawing snapshot when bytes have not changed. Hidden History pages do no chart data retrieval or updates. At the 760×600 minimum, vertical scrolling keeps both the chart and the unchanged recent-session rows/full-history action available without horizontal overflow. The separate detailed history window is unchanged.

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

The application explicitly shuts down only on tray Exit or Windows session ending. Exit stops and joins native observers, flushes pending usage, closes VPN/physical intervals with their existing shutdown behavior, disposes the tray icon/power listener and releases database ownership. Background mode performs no dashboard refresh or history-total rendering queries. The tray uses the NetTrace globe icon and the standard Windows menu. Tray startup does not install a Windows service; App Usage installation is a separate explicit administrative operation.

## Windows startup and background launch (Task 005B)

`NetTrace.exe` opens the dashboard and tray normally. `NetTrace.exe --background` starts the same monitoring services and tray without creating a dashboard or dashboard refresh timer. Use **Open NetTrace** in the tray to create the dashboard later; **Exit** still flushes and closes physical/VPN sessions cleanly. From source, use `dotnet run -- --background`.

Check **Start NetTrace with Windows** on **Settings** to enable startup for your current Windows user, or uncheck it to disable. Changes take effect immediately and require no administrator rights. The checkbox reads the actual registration when the window opens, regains focus or switches to Settings; a registry/path error appears inline and does not stop monitoring. Enabling is optional and never happens automatically on launch.

The `NetTrace` string value under `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run` contains the absolute apphost path, quoted for spaces, followed by `--background`: `"C:\Apps\NetTrace\NetTrace.exe" --background`. Repeated enabling updates that one value; disabling removes only that value and preserves other applications' startup entries. NetTrace never registers `dotnet.exe` or a test host.

Windows runs the entry when this user signs in. Keep the executable at a stable location; after moving it, enable startup again from the new location to update the path. Windows Task Manager or policy can separately block startup execution; NetTrace leaves those controls unchanged and displays its Run registration state.

## Top navigation (UI Foundation 001)

The existing dark window now has six pages, with a horizontal navigation bar and
an underline on the active page:

| Page | Contents |
| --- | --- |
| Dashboard | Live traffic, daily peaks, data usage and a compact current connection/session summary |
| Health | Internet reachability, latest ICMP measurements, recent ICMP quality, latency/jitter chart and packet-loss history |
| Connections | Current connection, monitored adapter/details, Profiles and Assign profile |
| History | Today's connection totals, 7D/30D daily data usage, newest four sessions and View full history |
| App Usage | Today/7D/30D tracked applications, cumulative download/upload and logical usage from the service |
| Settings | Start NetTrace with Windows and the local database location |

WPF's native tab control owns page selection and retains each page's controls.
Navigation does not create, restart or reset monitoring, counters or sessions.
The window still has one refresh timer; physical usage-total queries run only while
Dashboard is selected. App Usage reads its selected Today/7D/30D range through IPC
every four seconds while visible. Settings rereads the actual startup registration
when opened.
Closing the window and reopening it through the tray keeps the existing monitoring
service; a recreated window starts on Dashboard.

The dashboard opens at its minimum width of 760 DIP and remains resizable.
Page content uses compact dark vertical scrollbars and eased mouse-wheel scrolling.
Dragging the scrollbar and keyboard scrolling stay immediate; Windows animation
preferences and mouse-wheel scroll settings are respected.

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

Network Health 005 adds **ICMP quality** to the upper NETWORK HEALTH card. It evaluates
only the newest 12 completed cycles from the existing history, in completion order,
without another buffer or worker. Fewer than three completed cycles show **Measuring**.
Once three exist, no valid successful latency measurements means **Unavailable**,
not **Poor**: the target or network may block ICMP.

Recent loss is the mean of the selected cycles' loss percentages, including all-failed
cycles. Recent latency and jitter are unweighted means of valid per-cycle measurements;
missing values are excluded rather than replaced with zero. Quality is the worst
classification among the available metrics:

| Metric | Excellent | Good | Fair | Poor |
| --- | --- | --- | --- | --- |
| Latency | ≤ 50 ms | > 50 and ≤ 100 ms | > 100 and ≤ 200 ms | > 200 ms |
| Jitter | ≤ 10 ms | > 10 and ≤ 20 ms | > 20 and ≤ 40 ms | > 40 ms |
| Packet loss | 0% | > 0 and ≤ 1% | > 1 and ≤ 5% | > 5% |

The field updates with completed history and immediately on Health entry/restore.
Hidden, minimized and inactive views skip evaluation. The single-target ICMP result
does not determine internet availability or rate an ISP. With four probes per cycle,
the smallest nonzero 12-cycle mean loss is about 2.08%, so loss alone moves directly
from Excellent to Fair; Good can still reflect latency or jitter. No quality history
is persisted, and there are no notifications or additional ICMP probes.

Network Health 006 adds **Internet reachability** above ICMP quality, using a separate
application-owned worker. It starts asynchronously with monitoring, runs promptly at
startup and every 60 seconds thereafter, and continues in tray mode. Each cycle runs
two system DNS lookups and two HTTPS probes in parallel under one four-second deadline:

| Provider | DNS hostname | HTTPS endpoint |
| --- | --- | --- |
| Cloudflare | `cp.cloudflare.com` | `https://cp.cloudflare.com/generate_204` |
| Google | `www.gstatic.com` | `https://www.gstatic.com/generate_204` |

Google documents its HTTPS endpoint as a [connectivity probe](https://support.google.com/chrome/a/answer/6334001?hl=en).
The transport uses system DNS, routing and proxy configuration, normal TLS validation
and one reusable HTTP client. Requests stop at response headers, do not drain response
bodies, follow no redirects, and carry no cookies, credentials or NetTrace statistics.
No ICMP packets are sent by this check.

Before the first completed cycle the field shows **Checking**. Any legitimate HTTPS
response, including 301, 403 or 404, means **Available**; no success-class status is
required. If HTTPS fails but either DNS hostname resolves, the result is **Limited**.
If both DNS and HTTPS fail it is **Unavailable**. A completed success remains valid
even if another parallel probe reaches the deadline. ICMP quality and history remain
independent, so **Available** reachability with **Unavailable** ICMP is valid.

Only the latest result and UTC completion time are retained in memory. DNS caching,
filtered endpoints, proxies and captive portals can influence these observations;
they do not prove every website works. There is no reachability history, database
storage, notification or automatic network repair.

The existing application lifecycle starts, pauses for sleep, resumes and cancels both
workers. Incomplete sleep/shutdown cycles are discarded; the last fully completed
reachability result is retained until a fresh check completes promptly after resume.
Cycles cannot overlap, and exit requests cancellation without waiting for network
timeouts. Health reads current results through its existing visible-page refresh;
no UI networking or additional presentation timer is added. Tests inject ping,
DNS and HTTPS sources and never depend on public internet access. A separate explicit
`--reachability-live-validation` regression-harness command performs one real check
without opening a database; it is never part of the automated suite.

## Connections 001 — Live Connection Details

Connections keeps CURRENT CONNECTION, its session duration/usage, Profiles and
assignment actions, and the existing MONITORED ADAPTER selector. The adapter card
now shows these rows in order:

| Field | Source |
| --- | --- |
| Adapter | Selected `AdapterInfo.Name` |
| Status | Selected interface's `NetworkInterface.OperationalStatus` |
| Network | Existing physical `NetworkIdentity.FriendlyName`, only when its adapter matches the connected selected interface |
| Profile | Existing resolved `CurrentConnectionState.Profile.DisplayName`, with the same adapter check |
| Type | Selected `AdapterInfo.Type`, with Wireless80211 presented as Wi-Fi |
| IPv4 / IPv6 | Selected interface's `IPInterfaceProperties.UnicastAddresses` |
| Default gateway | Selected interface's `IPInterfaceProperties.GatewayAddresses` |
| DNS | Selected interface's configured `IPInterfaceProperties.DnsAddresses` |
| Wi-Fi / Ethernet / Adapter link rate | Existing `NetworkInterface.Speed` source, in Mbps or Gbps |

Status means adapter state, rather than internet reachability. Up is Connected;
known non-Up states are Disconnected; Unknown, unexpected states and unavailable
interface details are Unknown. Disconnected/unknown details clear address, gateway,
DNS, link-rate and mapping values. Missing details use **Unavailable** consistently;
an unmapped or unmatched profile uses **Unassigned**. A manually selected interface
never inherits another adapter's network/profile.

Address selection is deterministic: filter unspecified, loopback, multicast and
broadcast unicast values, deduplicate, and sort by address bytes. IPv4 uses the
first remaining IPv4 address; no source-address routing inference is added. IPv6
uses the first usable non-link-local address, including unique-local addresses.
Link-local-only IPv6 is **Unavailable**, consistent with showing a usable global
or unique-local address. Displayed IPv6 addresses omit scope IDs; the full displayed
value is available in a tooltip when the row is trimmed.

Gateway selection prefers IPv4 when a usable IPv4 address is displayed, otherwise
IPv6, with an available opposite-family fallback. One gateway is shown; link-local
IPv6 gateways remain valid. DNS preserves configured local resolvers, filters
unspecified/multicast/broadcast values, deduplicates, and orders IPv4 before IPv6
then by address bytes. It shows up to two addresses and `(+N more)` for the remainder.
Neither field performs lookups or probes.

`MonitoringService` caches a small `NetworkAdapterDetails` observation at adapter
enumeration/selection and every accepted existing recognition observation, including
unchanged identities. Existing network notifications and the existing 30-second
recognition refresh update IP/gateway/DNS configuration without another worker or
timer. Snapshot reads and UI ticks perform no additional network discovery.
Accepted identity/profile state and adapter details share the monitoring gate;
read failures and sleep/resume invalidate details instead of retaining stale values.
Adapter switching refreshes details immediately and preserves existing session and
selection rules. The active-only selector still chooses an available adapter on
network changes; when none is selected, Status is Unknown and details are Unavailable.

Connections renders details only while its page/window is visible and restored,
using the existing UI refresh lifecycle. Entering Connections immediately reads
the latest snapshot for both cards. Long values trim with tooltips, and the existing
vertical scroll supports the 760×600 minimum window without horizontal page scrolling.
The Profiles window and Health 001–006 remain unchanged.

The Connections checks use owned snapshots and network/probe fakes. A separate
explicit `--connections-live-validation` regression-harness command reads one
current default monitored adapter and existing identity without changing adapter
configuration, sending probes, or opening the real application database.

## Connections 002 — Profile Management UX

The Profiles window retains its rectangular utility style, left profile list and
right editor. Telco / ISP and Device remain required, surrounding whitespace is
trimmed, and the existing `Telco — Device` preview updates while typing. Duplicate
display names remain allowed by the existing model; saving a selected profile
updates its ID rather than creating another row.

**New profile** clears selection, editable fields and mappings and enters an
explicit unsaved state. It writes nothing until Save and makes deletion unavailable.
General profile management creates only the profile on Save. The existing explicit
assignment workflow retains atomic Create and assign behavior.

**Map current connection** reads the latest eligible connection identity from the
existing `MonitoringService` snapshot. Disconnected, detecting, sleeping and
identity-unavailable states cannot create a mapping. Save edits before mapping a
selected profile. Repeated identical mappings keep the same row. A mapping owned
by another profile opens a compact **Move / Cancel** confirmation naming its owner;
Move transfers the existing row transactionally and checks that its owner and the
current connection have not changed while the confirmation was open.

**Remove mapping** is enabled only for a selected mapping. It removes that row
immediately, leaves the profile and other networks intact, and refreshes resolution.
**Delete profile** is available only for saved profiles and requires **Delete / Cancel**
confirmation. One transaction clears the deleted profile's nullable associations
in sessions/events, removes its mappings and then removes the profile. History,
network identities, timestamps, byte totals and VPN intervals remain. The deleted
profile's name is no longer available through those detached history associations;
existing event messages remain. Another profile is selected afterward, or the
editor enters a clean empty/new state. The existing schema and foreign-key rules
are unchanged.

All actions persist through `ProfileStore` in the existing SQLite database. Failed
writes keep saved state intact and show an inline message. Successful mutations
raise the existing `ProfilesChanged` event, and the existing recognition refresh
updates both current-connection presentation and Connections 001 details without
restarting monitoring or splitting a session. Profile refreshes reject resolutions
captured before the write while retaining the visible physical connection. The
window consumes monitoring notifications and detaches on close; it starts no
worker, timer, watcher or polling loop and does not reset editor drafts on network
changes.

The editor uses vertical scrolling at the existing 620×500 minimum size. Long
profile/network names trim or wrap with tooltips within their columns. Move and
Delete confirmations use the same simple rectangular styling.

Deterministic regression/WPF checks use owned data and fake network/probe sources.
The WPF harness also offers an explicit `--profiles-live-validation` command. It
opens Profiles against an isolated database, maps a real current identity, checks
the main Connections presentation, removes the temporary mapping/profile and
retains the connection session. Health transports are fakes, and it never opens
the user's real profile database or changes network configuration. An unavailable
native identity skips that live-only check with its reason instead of guessing.

## Connections 003 — VPN / Tunnel & Route Awareness

Connections adds compact **VPN / tunnel**, **IPv4 route**, **IPv6 route** and
**Physical adapter** rows inside the existing current-connection card. Health adds
VPN context between internet reachability and ICMP quality. Physical identity, the
monitored traffic adapter, effective routing, DNS/HTTPS reachability and ICMP quality
remain separate.
VPN transitions do not switch the selected adapter, restart monitoring or split the
physical connection session.

Awareness uses the existing local Windows interface and IPv4/IPv6 route sources.
Installed, disconnected and non-routed tunnel devices do not establish an active
internet VPN. Loopback and ordinary internal virtualization interfaces are excluded.
Native encapsulation types and recognized tunnel-driver metadata provide candidates.
A PPP route participant also needs an unambiguous matching connected RAS VPN; modem
or direct dial-up evidence is insufficient. A virtual Ethernet name is a fallback
signal only when operational native state and effective route ownership agree.
TAP Always Connected and unsupported or ambiguous forwarding owners remain Unknown.
Effective general routing considers default routes and paired `/1` routes, interface
usability and combined route/interface metrics. Ambiguous ownership, incomplete
native evidence and unknown routing remain **Unknown**. No provider lookup, public-IP
comparison, interface binding, route change or VPN control is introduced.

A proven VPN general-route participant in either usable IP family makes VPN / tunnel
**Active**. Each route row independently displays **VPN**, **Direct**, **Unavailable**
(the family is not usable/configured), or **Unknown** (ambiguous or incomplete evidence).
For example, IPv4 through the tunnel and IPv6 over Wi-Fi display **IPv4 route: VPN**
and **IPv6 route: Direct**. Direct IPv6 is a routing observation without a leak or
security verdict. A known result stays visible even when the other family is Unknown.
These values do not claim that every destination uses a VPN or prove encryption. The
existing stricter FullTunnel/SplitTunnel verification and VPN history rules remain
independent of these compact awareness values.

The existing background VPN observer publishes a cached route snapshot on network
changes and its established 30-second cadence. UI reads do not query native routes.
Sleep invalidates pending observations; resume obtains fresh evidence. Tray monitoring
continues without rendering hidden views or creating another timer/worker.
Physical identity permission failures do not prevent independently proven route
awareness; existing VPN history writes still require a confirmed physical parent.

When reachability is Available, VPN is Active and ICMP quality is Unavailable, Health
explains that ICMP **may be blocked or filtered** by the active VPN while internet
reachability remains available. Other combinations retain the general distinction
between ICMP replies and independently checked DNS/HTTPS reachability. Probe targets,
packet-loss history, quality evaluation and charts are unchanged.

Shared semantic resources provide secondary color cues alongside readable status text:

| Resource | Uses |
| --- | --- |
| `StatusPositiveBrush` | Available reachability, Excellent/Good ICMP, Connected adapter, Monitoring |
| `StatusCautionBrush` | Limited reachability, Fair ICMP |
| `StatusNegativeBrush` | Unavailable reachability, Poor ICMP, Disconnected adapter |
| `StatusInfoBrush` | Active VPN, either family route marked VPN, Measuring ICMP |
| `StatusNeutralBrush` | Unknown/Inactive VPN, Direct/Unknown/Unavailable family routes, Unavailable ICMP, Checking reachability |

Address, DNS, gateway, adapter name and other informational rows retain normal text.
The existing cards, charts and minimum-window scrolling remain in place.

An explicit passive validation is available from the WPF harness:

```powershell
dotnet run --project tests/NetTrace.WpfTests/NetTrace.WpfTests.csproj -- --passive-vpn-validation --render-health
```

It reports IPv4 and IPv6 route values from local native tunnel/route evidence without
changing the user's VPN or network configuration. The optional render checks the
captured family values and semantic colors on Connections, then combines that routing with clearly
labelled simulated reachability/ICMP evidence in an owned database. It sends no probes
and never opens the real NetTrace database. This validates presentation; it does not
measure live DNS/HTTPS reachability or packet loss. Automated suites use injected
interfaces, routes, counters and health sources and require no real VPN.

## Connections 004 — VPN transport / underlay awareness

The existing CURRENT CONNECTION card adds **VPN transport** between VPN / tunnel
and the independent IPv4/IPv6 application route rows. It displays **IPv4**, **IPv6**,
**Mixed**, **Unknown**, or **Inactive**. Known transport families use the shared
informational cyan brush; Unknown and Inactive use the neutral brush. Transport
describes the VPN's outer server connection, without adding a health or security
verdict. Health and both application-route classifications are unchanged.

The existing Windows RAS status query already returns `RASCONNSTATUS.remoteEndPoint`:
the actual remote VPN server endpoint, not a projected payload address or a DNS
answer. NetTrace now decodes that native union for connected VPN sessions with no
error and a confirmed substate. Unknown endpoint types, unspecified, loopback,
multicast, broadcast, mapped-family ambiguity and unscoped link-local peers do not
establish a displayed family. Private unicast endpoints remain valid evidence.
The native status layout and byte ordering are checked against the Windows SDK.
See Microsoft's [RASCONNSTATUS documentation](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/legacy/aa376728(v=vs.85)).

A single-family result additionally requires an unambiguous association between
every proven effective PPP tunnel participant and its connected RAS VPN session,
using the existing PPP/RAS name and device association. Incomplete inventory,
ambiguous associations, unknown active peers and unrelated active RAS siblings
remain Unknown. A custom/opaque routed tunnel cannot borrow an unrelated RAS
session's single-family endpoint. **Mixed** requires actual usable remote endpoints
over both families from independently confirmed active RAS sessions; it describes
proven active VPN transports collectively, without assigning an opaque tunnel to a
particular peer. The existing conservative multi-RAS PPP route rules are preserved.

For example, IPv6 transport with IPv4 route VPN and IPv6 route Direct is valid.
The direct physical route to the VPN server is an ordinary underlay path and never
changes application-family route values. An active custom VPN without an exposed,
reliably associated RAS remote peer correctly displays **Unknown** transport, even
when manual IPv6 dependency tests or dual-stack DNS suggest IPv6.

No sockets or processes are enumerated. DNS records, adapter names/presence,
browser or SSH connections, proxies and virtual adapters cannot supply transport
proof. There are no new probes, public-IP requests, network changes, timers,
workers, persistence fields or administrator requirements. Access failures leave
transport Unknown without interrupting monitoring. Remote endpoint addresses and
session/process identifiers are not displayed or persisted.

Transport is part of the existing immutable route snapshot. The established
background VPN observer refreshes it on network changes, its 30-second cadence
and resume, including endpoint changes discovered by that observation. Tray mode
keeps the cache current without hidden WPF rendering. Existing generation checks
reject pre-sleep or pre-disposal results. Monitored adapter selection, sampler
baseline and physical session totals remain unchanged.

The explicit `--passive-vpn-validation` WPF command also reports the transport
value and a safe evidence explanation. It reads local Windows state only and
never connects, disconnects or reconfigures a VPN. Automated transport tests use
owned evidence and counters; their rendered examples are simulated presentation
checks, rather than claims about the user's VPN.

## Connections 005 — Wi-Fi radio details and help

The MONITORED ADAPTER card shows a compact **WI-FI RADIO** subsection for the
selected, connected Wi-Fi interface when Windows supplies useful measurements:
signal quality, actual RSSI, channel, band/frequency, connected access-point BSSID,
Windows-reported current-association PHY type, and separate receive/transmit link rates. Missing fields display
**Unavailable**; disconnected and non-Wi-Fi interfaces have no radio block. The
existing general link rate remains the fallback when either separate rate is
missing. Link rates describe the local wireless link, rather than internet speed.

`WindowsWifiRadioSource` queries the selected interface GUID with native
`WlanQueryInterface`. RSSI is the driver's dBm measurement, never a conversion from
signal quality. Frequency comes only from a fresh, exact current-association match
in the existing Windows WLAN BSS cache. An available native channel must agree;
when that query is unsupported, the tested helper derives the channel from the
measured frequency after the same freshness and association checks.
Unknown, old or inconsistent cache evidence leaves frequency unavailable. The
shared tested frequency helper maps supported 2.4, 5 and 6 GHz channel centers.
See Microsoft's [WLAN association attributes](https://learn.microsoft.com/en-us/windows/win32/api/wlanapi/ns-wlanapi-wlan_association_attributes)
and [WLAN BSS entry](https://learn.microsoft.com/en-us/windows/win32/api/wlanapi/ns-wlanapi-wlan_bss_entry)
documentation. No scan is initiated, nearby access points are not displayed, and
permission/driver failures do not interrupt monitoring.

PHY describes the current association reported by Windows, rather than the
adapter's supported capabilities. Some drivers may report a PHY that differs
from the reported band or frequency; NetTrace preserves those native values.

The view-owned `WifiRadioService` uses the existing visible Connections refresh
with a five-second throttle and a single asynchronous native read. It adds no
timer or background polling loop. Hiding/minimizing the view, leaving Connections,
changing adapters, disconnecting and suspension invalidate cached measurements;
late results cannot restore them. Resume and reopening obtain fresh measurements.
Radio values stay in memory and are never added to history or written to disk.
Adapter selection, traffic accounting, VPN routing and Health remain unchanged.

Small **ⓘ** cues directly beside their labels provide centralized plain-language
definitions for unfamiliar radio/address/DNS/gateway/link-rate/VPN-transport terms. Hover shows a wrapped,
width-limited tooltip; focus and click open the same explanation, with Escape or
focus loss dismissing it. Technical values retain the normal text color without
signal judgments or alerts. The existing cards and vertical scrolling support the
760×600 minimum window without horizontal expansion.

Deterministic regression and WPF checks use owned Wi-Fi evidence, including
multiple interfaces, unavailable fields, native decoding, stale-result rejection,
sleep/resume, hidden views and wrapped help. The explicit command below performs
a passive local observation and prints current values to the console only:

```powershell
dotnet run --project tests/NetTrace.WpfTests/NetTrace.WpfTests.csproj -- --passive-wifi-validation
```

It does not change Wi-Fi configuration, initiate scans, issue internet requests or
open the real NetTrace database.

## Verification

Run the isolated migration checks without touching your application data:

```powershell
dotnet run --project tests\NetTrace.RegressionTests\NetTrace.RegressionTests.csproj
dotnet run --project tests\NetTrace.WpfTests\NetTrace.WpfTests.csproj
```

Focused Dashboard checks cover the compact Today → 7D → 30D selector cycle,
existing date boundaries and summary values, retained selection through navigation,
continuous background traffic samples, genuine unavailable gaps, hidden rendering
suppression and the 760×600 minimum. The WPF checks save isolated layout previews.

```powershell
dotnet run --project tests\NetTrace.RegressionTests -- --dashboard-usage-checks
dotnet run --project tests\NetTrace.WpfTests -- --dashboard-usage-checks
dotnet run --project tests\NetTrace.RegressionTests -- --dashboard-speed-checks
dotnet run --project tests\NetTrace.WpfTests -- --dashboard-speed-checks
dotnet run --project tests\NetTrace.RegressionTests -- --dashboard-traffic-checks
dotnet run --project tests\NetTrace.WpfTests -- --dashboard-traffic-checks
```

## App Usage 004.1 — daily usage storage foundation

The standalone [App Usage storage library](tools/AppUsageStorage/README.md)
provides SQLite daily application aggregates at
`%ProgramData%\NetTrace\app-usage.db`. Schema version 1 aggregates by normalized
executable identity and observed local calendar date, preserves historical
display metadata and provides explicit 90-day retention cleanup. The App Usage
service uses this store through the 004.2 checkpoint integration below.

Run its isolated storage tests, existing App Usage 003 checks and library build:

```powershell
dotnet run --project tests/NetTrace.AppUsageStorageTests/NetTrace.AppUsageStorageTests.csproj
dotnet run --project tests/NetTrace.AppUsageServiceTests/NetTrace.AppUsageServiceTests.csproj
dotnet build tools/AppUsageStorage/AppUsageStorage.csproj
```

## App Usage 004.2 — service persistence checkpoints

The headless development service accumulates accepted non-loopback ETW byte
deltas by observed local calendar date and normalized application identity.
Five-second checkpoints write aggregate batches through the 004.1 store;
failed batches remain pending for later retries. A final bounded flush drains
accepted events after capture is quiesced, and retention runs once at service
startup. Internal persistence health and bounded logs support diagnostic
validation; the production UI keeps its existing behavior.

Forced termination can lose usage since the latest successful checkpoint,
normally roughly the latest five-second window while storage is healthy.
An outage can extend that window. This is not zero-loss crash accounting.

```powershell
dotnet run --project tests/NetTrace.AppUsageCheckpointTests/NetTrace.AppUsageCheckpointTests.csproj
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File tools/NetTrace.AppUsageService/Validate-Persistence.ps1
```

The explicit validation workflow temporarily runs the development service as
LocalSystem, collects small controlled IPv4/IPv6 traffic and passive application
evidence, verifies restart persistence, then removes its owned service, trace,
pipe and validation database. See the [service documentation](tools/NetTrace.AppUsageService/README.md).

## App Usage 004.3 — read-only usage queries

The authenticated local service pipe adds `GetAppUsageRange` to service protocol
2. Its typed request accepts only Today, 7D or 30D: today and the previous
0, 6 or 29 local calendar dates, inclusive. Existing App Usage 002/003 operations
retain their wire format and protocol versions.

The service reads persisted history through the storage library with independent
read-only SQLite connections. It aggregates by historical application identity,
omits zero-use applications and sorts by total bytes descending, then identity
ascending. Results retain executable metadata, System/Unknown rows and normal
NetMod logical usage, with download, upload and logical totals. A valid empty
history returns zero totals; failed reads return typed unavailable state, and
readable history with unhealthy persistence returns degraded state. Query reads,
waiting work and response frames are bounded; no query changes checkpoint data.

The existing diagnostic client exposes these queries without elevation:

```powershell
dotnet run --project tools/AppUsageDiagnosticClient -- --service --app-usage Today
dotnet run --project tools/AppUsageDiagnosticClient -- --service --app-usage-all
dotnet run --project tests/NetTrace.AppUsageQueryTests/NetTrace.AppUsageQueryTests.csproj
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File tools/NetTrace.AppUsageService/Validate-UsageQueries.ps1
```

The explicit validation uses protected isolated storage and passive traffic,
checks normal-client reads and service restart recovery, then removes its owned
service, ETW session, pipe and database. See [004.3 validation](tools/NetTrace.AppUsageService/VALIDATION0043.md).

## App Usage 004.4.1 — Today page

**App Usage** appears between History and Settings. The original Today page shows
Today's tracked app count, download, upload and logical total, followed by the
ordered application rows returned by service protocol 2. Existing cumulative
byte formatting is shared with the other pages. System, Unknown and NetMod rows
remain ordinary logical usage; these totals do not reconcile Dashboard usage.

The existing view timer refreshes Today approximately every four seconds while
the page is visible, with one request at a time. Opening or returning to the page
shows loading and starts a fresh asynchronous read. Navigation away, window hide,
minimize and close cancel presentation work and discard late results. Empty
history shows **No app network usage recorded today.**; an absent service,
incompatible protocol or unavailable storage shows **App tracking unavailable.**

The desktop client uses only authenticated IPC for this page. Normal launch
requires no App Usage service and never automatically installs or elevates one. Range
selectors are added by 004.4.2 below; there are no charts or application icons.

```powershell
dotnet run --project tests/NetTrace.WpfTests -- --app-usage-today-checks
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File tools/NetTrace.AppUsageService/Validate-TodayPage.ps1
```

See [Today page validation](tools/NetTrace.AppUsageService/VALIDATION00441.md) for
the explicit temporary-service workflow and its cleanup evidence.

## App Usage 004.4.2 — Today / 7D / 30D range switching

The App Usage page starts with **Today** selected. Selecting **7D** or **30D**
immediately reads that range through the existing IPC v2 `GetAppUsageRange`
operation. Navigation away and back, including reopening the window from the tray,
retains the selected range during the app session. A new app session starts on
Today. The existing four-second refresh reads the selected range only while
the page is visible, with one request at a time. Switching ranges shows loading;
cancelled or older responses cannot replace the newly selected range's data.

The summary, application rows and unavailable state remain the same. An empty
result uses the selected range's message:

| Range | Empty message |
| --- | --- |
| Today | No app network usage recorded today. |
| 7D | No app network usage recorded in the last 7 days. |
| 30D | No app network usage recorded in the last 30 days. |

Hidden pages send no refresh requests. The range controls use the existing dark
radio-button style and do not affect History's separate range selection.

```powershell
dotnet run --project tests/NetTrace.WpfTests -- --app-usage-range-checks
dotnet run --project tests/NetTrace.WpfTests -- --app-usage-today-checks
```

## App Usage 005.1 — Production service installation

The separate [App Usage installer](tools/NetTrace.AppUsageInstaller/README.md)
registers **NetTraceAppUsageCollector** as a LocalSystem Windows Service with
**Automatic (Delayed Start)**. Build its service package and installer from a
normal terminal:

```powershell
dotnet publish tools/NetTrace.AppUsageService -c Release -o bin/AppUsageService
dotnet publish tools/NetTrace.AppUsageInstaller -c Release -o bin/AppUsageInstaller
```

Run installation or uninstall from an elevated terminal under the intended
Windows account. The tool requires elevation and never requests it automatically:

```powershell
.\bin\AppUsageInstaller\NetTrace.AppUsageInstaller.exe install --source (Resolve-Path .\bin\AppUsageService).Path
.\bin\AppUsageInstaller\NetTrace.AppUsageInstaller.exe uninstall
```

Installation stages protected versioned binaries under Program Files, validates
ownership before updating an existing registration, configures SCM recovery and
verifies authenticated IPC readiness. Failure recovery restarts after 5, 15 and
60 seconds, then takes no further action until the failure count resets after
24 hours without a failure. Failed updates restore the previous protected
version when it can be verified safely.

Normal NetTrace stays unelevated and continues using the existing Today/7D/30D
page. Installation uses the separate administrative tool; 005.2 adds an explicit
desktop action to launch that tool. Uninstall stops and removes
the service, cleans only the exact owned ETW session, and preserves
`%ProgramData%\NetTrace\app-usage.db` and its history. Protected binary versions
are also retained. Persistence, five-second checkpoints, 90-day retention and
IPC v2 query behavior are unchanged.

See [005.1 validation](tools/NetTrace.AppUsageService/VALIDATION0051.md) for the
explicit install/update, unelevated page recovery and database preservation checks.

## App Usage 005.2 — App tracking setup

When the production service is missing, App Usage displays **App tracking is not
installed.**, **Track network usage by application in the background.**, and an
**Install App Tracking** button. An installed but stopped service shows a concise
stopped state without an install button. Running service queries keep their
existing Today/7D/30D summary, application list and four-second visible-page refresh.

Clicking **Install App Tracking** explicitly opens the existing production
installer through Windows UAC. Only that installer child runs elevated; NetTrace
keeps its normal permissions. The app never launches installation or requests
elevation automatically at startup or when opening App Usage. A cancelled UAC
prompt or failed install returns to the setup state with a short inline error.
There is no uninstall action on the page.

After successful installation and service start, the page reconnects automatically
and loads the range already selected. Today/7D/30D selection survives installation,
navigation and reconnect within the app session. Hidden pages keep sending no
refresh requests. The existing installer and service packages are included beneath
`AppTracking/Installer` and `AppTracking/Service` in build and publish output.
Service configuration, ETW capture, persistence, accounting and IPC v2 are unchanged.

```powershell
dotnet run --project tests/NetTrace.WpfTests -- --app-usage-setup-checks
dotnet run --project tests/NetTrace.WpfTests -- --app-usage-range-checks
dotnet run --project tests/NetTrace.WpfTests -- --app-usage-today-checks
```

## App Usage 005.3 — App tracking management in Settings

Settings includes **APP TRACKING**, with the current production service status:
**Running**, **Stopped** or **Not installed**. **Uninstall App Tracking** is
available only for an installed service. Status is checked when Settings opens
and approximately every four seconds while it is visible, using the existing
window timer. No installer launches automatically.

Uninstall first asks for confirmation, with **No** selected by default and a
reminder that recorded usage history is kept. Confirming explicitly launches the
existing installer with `uninstall` through UAC. Only that child runs elevated;
NetTrace retains its normal permissions. Cancellation or failure shows a short
inline error and allows retry once the current service status is known.

Successful uninstall immediately changes Settings to **Not installed** and
returns App Usage to its existing missing-service setup state without restarting
NetTrace. The selected Today/7D/30D range survives; old query responses cannot
restore removed service data, and hidden App Usage sends no refresh requests.
The existing tool preserves `%ProgramData%\NetTrace\app-usage.db` and recorded
history. This phase adds no Start/Stop controls; history clearing is described
below. The 005.3 change leaves service configuration, ETW, persistence,
accounting, IPC and installer semantics unchanged.

```powershell
dotnet run --project tests/NetTrace.WpfTests -- --app-tracking-settings-checks
```

See [005.3 validation](tools/NetTrace.AppUsageService/VALIDATION0053.md) for the
owned UI checks, build commands and live-validation boundaries.

## App Usage 005.4 — Clear App Usage History

Settings → App Tracking includes **Clear App Usage History**. It becomes enabled
when App Usage storage is present. If Windows protects the storage from a normal
presence check, the confirmed elevated tool checks it before clearing. Known
missing storage disables the action. Opening Settings never requests elevation.

Confirmation defaults to No. A confirmed click elevates only the bundled
installer's fixed `clear-history` command; NetTrace remains unelevated. The tool
deletes only the recognized App Usage rows in a SQLite transaction, preserving
the database, schema, unrelated files and service configuration. Dashboard,
connection History and physical accounting retain their existing data.

The running collector clears its old pending batches together with persisted
history through a separate administrator-only management endpoint. Buffered
pre-clear events cannot restore deleted usage. Tracking continues through the
same collector, and new events accumulate through normal checkpoints. The
read-only IPC v2 queries remain unchanged. Existing storage can also be cleared
when tracking is stopped or uninstalled, using an exclusive storage lease and
service-state checks without starting or stopping a collector.

Success immediately invalidates old App Usage responses and clears its cached
rows while preserving Today/7D/30D selection. A visible page refreshes its selected
range immediately; a hidden page sends no requests. Cancellation or failure
shows short retry feedback. A collector installed before 005.4 must first be
updated with **Update App Tracking** in Settings or through the existing
production installer; clear never upgrades or
restarts it automatically.

```powershell
dotnet run --project tests/NetTrace.WpfTests -- --app-usage-history-clear-checks
dotnet run --project tests/NetTrace.AppUsageStorageTests -- --clear-history-checks
dotnet run --project tests/NetTrace.AppUsageCheckpointTests -- --clear-history-checks
```

See [005.4 validation](tools/NetTrace.AppUsageService/VALIDATION0054.md).

## App Usage 005.5 — Installed collector update / repair

Settings → App Tracking checks the installed production collector against the
bundled service kit. Its status is **Running**, **Stopped**, **Update available**
or **Not installed**. An installed collector with a different package version
shows **Update App Tracking**. Matching versions keep the existing status and
do not request an update.

The production installer identifies a version by the SHA-256 of its complete,
sorted service-file inventory, including PDBs. The desktop uses the same algorithm
for the bundled kit and reads the installed identity from the owned SCM service
command's protected `Versions/<hash>` path. This distinguishes builds whose
assembly version remains `1.0.0.0`; it compares package equality rather than
ordering content hashes. Status checks never open the protected database or
request elevation.

Only an explicit **Update App Tracking** click launches the existing bundled
installer through UAC with its `install --source ... --client-sid ...` invocation.
NetTrace remains unelevated. The installer switches the stopped owned service
to its new staged protected binaries and restarts it, preserving `app-usage.db`, its
schema and existing App Usage history. The production configuration remains
Automatic (Delayed Start), LocalSystem, with the existing restart recovery
policy. Its existing rollback protects a previously valid installation when
an update fails. Cancelled UAC or failure shows a short inline error and allows
retry; the application never installs or updates automatically at startup.

After the updated service passes its running/IPC checks, App Usage reconnects
automatically using the already selected Today/7D/30D range. Hidden App Usage
continues sending zero refresh requests. Updating a collector installed before
005.4 makes its clear-history management capability available without clearing
history. Dashboard, connection History, physical accounting, ETW, storage and
read-only IPC semantics are unchanged.

```powershell
dotnet run --project tests/NetTrace.WpfTests -- --app-tracking-update-checks
dotnet run --project tests/NetTrace.WpfTests -- --app-tracking-settings-checks
dotnet run --project tests/NetTrace.WpfTests -- --app-usage-range-checks
```

See [005.5 validation](tools/NetTrace.AppUsageService/VALIDATION0055.md) for
verification results and the explicit update of the installed older collector.

## App Usage 005.6 — Lifecycle hardening

Closed or broken query connections now release their native pipe instances
before reuse. This fixes a Running collector becoming unreachable after its
three listeners were exhausted. Authentication, protocol, ETW, accounting and
storage behavior remain unchanged.

Real production update, SCM restart, forced SCM recovery and S3 sleep/resume
are observed through the existing App Usage refresh timer with the selected
range retained. History integrity, small non-loopback traffic, exact captured
versus persisted deltas and owned process/session cleanup are verified separately.
See [005.6 validation](tools/NetTrace.AppUsageService/VALIDATION0056.md) for the
explicit elevated worker, unelevated UI, evidence and reboot limitations.

## App Usage 006.1 — Application row icons and names

App Usage rows show a small application icon alongside their title. Local
executable icons load in the background and are cached by normalized,
case-insensitive path for the app session, including failed extractions. A
generic application icon appears immediately and remains when a path or icon is
unavailable. System and Unknown / Unattributed have distinct fallback icons.

The title uses the friendly display name when present, otherwise the executable
name. The executable subtitle appears only when it differs from the title,
ignoring case and surrounding spaces. Long names retain ellipsis and tooltips.
Today, 7D and 30D retain their existing totals, selection and refresh behavior.

```powershell
dotnet run --project tests/NetTrace.WpfTests -- --app-usage-row-icon-checks
```

See [006.1 validation](tests/NetTrace.WpfTests/VALIDATION0061.md) for cache,
native-icon and compact-window checks.
