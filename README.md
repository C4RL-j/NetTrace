# NetTrace

Lightweight Windows network usage and diagnostics monitor.

NetTrace V1.1 establishes the NetTrace identity and safely migrates local history from the previous Data Usage Monitor build (Task 001). Task 002 adds connection profiles and network mappings; Task 003 adds physical connection sessions and event history. Task 004C persists VPN intervals under physical sessions; Task 004D records their transitions in the event timeline. Built with C#/.NET 8, WPF, and SQLite.

## Current features

- Active network adapter selector
- Live download speed
- Live upload speed
- Download/upload totals for the current app session
- Today total
- Last 7 days total
- Last 30 days total
- Download vs upload breakdown
- Adapter type, IPv4 address, and link speed
- Local SQLite history
- Connection profiles with Telco / ISP and Device names
- Multiple network identities mapped to one profile
- Physical connection sessions and recent connection events
- No background service
- No telemetry

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

Usage is written approximately every 30 seconds and once more when the app closes normally.

On first launch, if the NetTrace database is absent and `%LOCALAPPDATA%\DataUsageMonitor\usage.db` exists, NetTrace copies a consistent SQLite snapshot, including committed WAL data, to the new location. The legacy database is retained for rollback. An existing NetTrace database always takes precedence; later legacy writes are not synchronized.

The copy is validated and atomically placed at the new path without overwriting a database. Copy or validation failures stop startup with an error instead of starting with empty history. SQLite's read-only source connection does not change legacy history; its WAL coordination files may be accessed while the old app is running.

The current schema uses `PRAGMA user_version = 4`. Compatible unversioned databases progress through version 1; version 1 databases gain `ConnectionProfiles` and `NetworkMappings`, version 2 databases gain `ConnectionSessions` and `ConnectionEvents`, and version 3 databases gain `VpnSessions`. All upgrades run transactionally without rebuilding or changing existing usage history, profiles, or mappings. Version changes commit only after successful migration and validation. `Services\DatabaseMigrator.cs` is the single place for schema upgrades. Incompatible or newer schemas stop startup rather than being altered.

## Connection profiles

A profile represents Telco / ISP + Device, such as `DITO — D2 Router`. Network mappings are separate: several SSIDs can belong to the same profile. No telco is guessed and no profile is created automatically.

Use **Profiles** in the Connection card to create or edit profiles and see their mappings. For an **Unassigned network**, choose **Assign profile**, select an existing profile, or choose **New profile** and enter the Telco / ISP and Device names. Creating and assigning together is atomic. Repeating an assignment to the same profile keeps the existing mapping; a conflicting assignment is rejected. Profile deletion is deferred.

Wi-Fi recognition reads the actual associated SSID through Windows Native Wi-Fi APIs. Exact SSID case and whitespace are preserved; non-text SSIDs use lossless byte identities. Ethernet recognition uses an identified Windows network GUID, not an adapter GUID. Windows location permissions may be needed to read Wi-Fi SSIDs, and unidentified Ethernet networks cannot be assigned until Windows supplies a stable identity.

The Connection card distinguishes a recognized profile, a valid unassigned network, an unavailable network identity, and a disconnected connection. **Assign profile** is offered only for a valid unassigned identity. If Windows denies SSID access or an identity API fails, NetTrace displays the reason and keeps bandwidth monitoring independent; it does not create placeholder mappings or change Windows permissions.

Physical connection recognition is independent of the bandwidth adapter selector. Windows network-change events drive recognition without automatic dialogs. If several physical connections are active, gateway presence and the IPv4 default-route metric determine the preferred connection, with Ethernet as a tie-break. IPv6-only connections are supported without IPv6 route-metric ranking.

## Connection sessions and events

A session is one continuous observed physical network identity and adapter. Different SSIDs create different sessions even when they map to the same profile. Repeated callbacks and friendly-name changes do not split a session. Valid unassigned networks get sessions with no profile; assigning the current network associates only its active session. Completed sessions keep their profile association and captured network identity. Profile names in history follow their stable profile ID.

The Connection card shows the current session duration and traffic. **History** opens the latest 100 sessions and 200 events, with UTC timestamps displayed in local time. Events include connection/disconnection, network change, profile assignment/change, sleep, resume, unexpected-exit recovery, normal application exit, and network-identity unavailability.

Session traffic uses the dashboard's existing one-second byte delta and only accepts samples from the current physical adapter. Recognition changes automatically select that adapter and establish a new baseline; profile edits and repeated observations retain the baseline. Manual adapter selection remains available. If another or virtual adapter is selected, the dashboard still monitors it while physical-session traffic pauses; the Connection card explains which selection is needed. No virtual-interface traffic is attributed to physical sessions.

Progress and confirmed observations are saved about every 30 seconds and on transitions/normal exit. Identity is also refreshed at that batch interval to confirm idle connections. Uncertain transition intervals are discarded rather than counted on a new connection. A sudden termination can lose the current unflushed batch; restart closes the unfinished session exactly at its persisted `LastConfirmedAtUtc`, records recovery, and starts a fresh session after detection. Only one NetTrace run may own a history database at a time.

Startup acquires an exclusive Windows file handle on `usage.db.nettrace.lock` before opening, migrating, or recovering SQLite and keeps it until shutdown. Concurrent instances are rejected, including in another Windows login session. Normal close releases the handle; a hard kill or crash also releases operating-system ownership. The small sidecar remains on disk and is reused, so its mere existence never means the database is locked. No stale PID or manual lock-file cleanup is needed. Startup failures also release ownership.

SQLite indexes cover the single active session, recent sessions/events, profile sessions, and start-time range filtering. Event foreign keys have their own indexes. Earlier v3 databases receive the event relationship indexes transactionally without changing historical rows or the schema version.

NetTrace registers its window for Windows suspend/resume notifications, including connected standby. These messages end the active session on sleep and require a fresh identity observation before a new session starts after resume, including for the same SSID. Sleep time is excluded. When Windows cannot provide identity, NetTrace conservatively ends the old session at its last confirmation and keeps dashboard sampling independent; a recovered identity begins a new session. Completed history is never broadly reassigned.

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

VPN storage adds no VPN UI, standalone sleep/crash recovery, health checks or traffic accounting. Existing physical lifecycle closure also enforces the child relationship; it does not establish continuous VPN connectivity across sleep or an application restart. Intervals describe observed state: rapid changes between callbacks/polls may be missed, timestamps follow available physical confirmation, and only the latest routing mode is retained within an interval.

## VPN events (Task 004D)

The existing `ConnectionEvents` timeline records `VpnConnected` when an interval starts, `VpnDisconnected` when it ends, and `VpnRouteChanged` only when its active routing mode changes. Events retain the physical session/profile/network context and include the VPN name, routing mode and, for closure, the end reason. An uncertainty or physical-session closure ends an observed interval without claiming that Windows positively detected VPN OFF.

SQLite triggers write events in the same transaction as VPN transitions, including physical-parent closure. Repeated confirmations, alias-only edits and unchanged routing produce no events; a failed event write rolls back its interval transition. Existing schema-v4 databases gain these triggers transactionally on startup without rewriting history or backfilling events. Physical event behavior, dashboard layout and bandwidth accounting remain unchanged.

## Migration verification

Run the isolated migration checks without touching your application data:

```powershell
dotnet run --project tests\NetTrace.RegressionTests\NetTrace.RegressionTests.csproj
```
