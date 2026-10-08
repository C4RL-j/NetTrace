# NetTrace

Lightweight Windows network usage and diagnostics monitor.

NetTrace V1.1 establishes the NetTrace identity and safely migrates local history from the previous Data Usage Monitor build (Task 001). Task 002 adds connection profiles and network mappings. Built with C#/.NET 8, WPF, and SQLite.

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

The current schema uses `PRAGMA user_version = 2`. Compatible unversioned databases progress through version 1; version 1 databases gain `ConnectionProfiles` and `NetworkMappings` in a transaction without rebuilding or changing usage history. Version changes commit only after successful migration and validation. `Services\DatabaseMigrator.cs` is the single place for schema upgrades. Incompatible or newer schemas stop startup rather than being altered.

## Connection profiles

A profile represents Telco / ISP + Device, such as `DITO — D2 Router`. Network mappings are separate: several SSIDs can belong to the same profile. No telco is guessed and no profile is created automatically.

Use **Profiles** in the Connection card to create or edit profiles and see their mappings. For an **Unassigned network**, choose **Assign profile**, select an existing profile, or choose **New profile** and enter the Telco / ISP and Device names. Creating and assigning together is atomic. Repeating an assignment to the same profile keeps the existing mapping; a conflicting assignment is rejected. Profile deletion is deferred.

Wi-Fi recognition reads the actual associated SSID through Windows Native Wi-Fi APIs. Exact SSID case and whitespace are preserved; non-text SSIDs use lossless byte identities. Ethernet recognition uses an identified Windows network GUID, not an adapter GUID. Windows location permissions may be needed to read Wi-Fi SSIDs, and unidentified Ethernet networks cannot be assigned until Windows supplies a stable identity.

The Connection card distinguishes a recognized profile, a valid unassigned network, an unavailable network identity, and a disconnected connection. **Assign profile** is offered only for a valid unassigned identity. If Windows denies SSID access or an identity API fails, NetTrace displays the reason and keeps bandwidth monitoring independent; it does not create placeholder mappings or change Windows permissions.

Physical connection recognition is independent of the bandwidth adapter selector. Windows network-change events refresh recognition without polling or automatic dialogs. If several physical connections are active, gateway presence and the IPv4 default-route metric determine the preferred connection, with Ethernet as a tie-break. IPv6-only connections are supported without IPv6 route-metric ranking.

## Migration verification

Run the isolated migration checks without touching your application data:

```powershell
dotnet run --project tests\NetTrace.RegressionTests\NetTrace.RegressionTests.csproj
```
