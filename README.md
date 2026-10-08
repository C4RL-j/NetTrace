# NetTrace

Lightweight Windows network usage and diagnostics monitor.

NetTrace V1.1 Task 001 establishes the NetTrace identity and safely migrates local history from the previous Data Usage Monitor build. Built with C#/.NET 8, WPF, and SQLite; the existing monitoring features and interface are retained.

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

The current schema uses `PRAGMA user_version = 1`. Compatible unversioned databases are validated and marked version 1 in a transaction without rebuilding their tables. `Services\DatabaseMigrator.cs` is the single place for future schema upgrades. Incompatible or newer schemas stop startup rather than being altered.

## Migration verification

Run the isolated migration checks without touching your application data:

```powershell
dotnet run --project tests\NetTrace.RegressionTests\NetTrace.RegressionTests.csproj
```
