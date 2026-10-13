# App Usage production service installer

This tool installs the existing collector, SQLite persistence and IPC v2 service as `NetTraceAppUsageCollector`. It runs as **LocalSystem** with **Automatic (Delayed Start)** startup. NetTrace remains unelevated; neither NetTrace nor this tool launches an elevation prompt automatically.

Build a complete service kit and the installer into separate directories:

```powershell
dotnet build tools/NetTrace.AppUsageService/NetTrace.AppUsageService.csproj -c Release -o bin/AppUsageServiceProduction
dotnet build tools/NetTrace.AppUsageInstaller/NetTrace.AppUsageInstaller.csproj -c Release -o bin/AppUsageInstallerProduction
```

Open an Administrator terminal under the **same Windows account that runs NetTrace**, then install:

```powershell
& .\bin\AppUsageInstallerProduction\NetTrace.AppUsageInstaller.exe install --source (Resolve-Path .\bin\AppUsageServiceProduction).Path
```

The intended IPC client SID defaults to that account. An optional `--client-sid SID` must identify the same account; elevating with another account's credentials is rejected. An unelevated install/uninstall prints a clear error and exits with code 2. Invalid arguments also exit 2; an operational failure exits 1.

Installation checks the complete managed/native dependency kit and stages a SHA-256 version under `%ProgramFiles%\NetTrace\AppUsageCollector\Versions`. Protected ownership manifests bind the exact quoted executable, intended client SID, fixed service name and log path to its verified binary inventory. Existing directories must have trusted ownership, no non-administrator writes and no reparse points. Newly created directories and staged files are protected for SYSTEM and Administrators. The collector uses its existing `%ProgramData%\NetTrace\app-usage.db` default and writes a bounded service log under `%ProgramData%\NetTrace\AppUsageService`.

The installer registers or reconfigures one owned service, sets the service SID, delayed automatic startup and recovery, starts it, then verifies SCM configuration and an authenticated IPC v2 Today query. Repeating installation updates the same owned service. It rejects a foreign registration or any existing temporary development collector service/process because the collectors share the existing ETW session and pipe. A failed update restores the protected previous version and verifies its IPC; a failed first install removes its owned registration. If rollback also fails, the installer reports both failures and attempts to leave the owned service disabled and stopped. Database files are retained throughout.

Recovery restarts the collector after 5, 15 and 60 seconds; subsequent failures take no action. The failure count resets after 24 hours. SCM also applies this policy to reported non-crash failures. Update/uninstall disable startup before clearing recovery so an already queued SCM restart cannot relaunch the collector during maintenance.

Read actual SCM configuration without elevation:

```powershell
& .\bin\AppUsageInstallerProduction\NetTrace.AppUsageInstaller.exe status
```

Uninstall from the same Administrator terminal:

```powershell
& .\bin\AppUsageInstallerProduction\NetTrace.AppUsageInstaller.exe uninstall
```

Uninstall validates ownership, disables recovery/startup, stops the service and waits for its process to exit, removes the registration and verifies SCM error 1060. It obtains the existing protected ETW ownership mutex and cleans only a verified exact owned stale session, then verifies no collector process or pipe remains. It never deletes `app-usage.db`, its WAL/SHM files or the protected binary versions. Reinstall uses the existing database, preserving history. An already absent service is an idempotent uninstall, subject to the same competing-collector and exact-session checks.

Clear only persisted App Usage records from an Administrator terminal:

```powershell
& .\bin\AppUsageInstallerProduction\NetTrace.AppUsageInstaller.exe clear-history
```

This fixed command accepts no database paths, SQL, service-control options or other arguments. It validates elevation and the existing protected production layout, preserves the database/schema and all unrelated files, and never stops, uninstalls or reconfigures tracking. Settings invokes it through UAC only after the user confirms **Clear App Usage History**; NetTrace retains its normal token.

For a running production collector, the helper uses a separate local management pipe restricted to SYSTEM and elevated Administrators at high integrity. This pipe does not modify the read-only IPC v2 query protocol. SCM/process ownership authenticates the server; the server checks the caller's elevated administrator token after reading a bounded fixed request and reverts before clearing. The clear serializes with existing checkpoints, commits the row deletion, discards old pending deltas and rejects buffered pre-clear observations. New observations continue accumulating with capture running.

An older installed collector must first be updated using the existing `install --source ...` command, whether running or stopped. A missing management endpoint or stopped collector without the new storage lease returns exit code **4** with an update-required error; clearing does not automatically update or stop the service and never falls back to a direct write while it is running. Offline compatibility inspects the manifest-verified service DLL's type metadata without loading or executing it. The connection is bounded to three seconds. Once admitted, the service finishes the transaction even if the client disconnects; the helper waits up to 30 seconds for acknowledgement and reports an unconfirmed result as failure.

When the production service is stopped or absent, the helper acquires the protected `app-usage.history.lock` file, rechecks SCM and requires no collector process, owned ETW session or IPC pipe. Every collector holds the same exclusive file lease before SQLite initialization until all storage operations finish. Thus a concurrent start waits for the offline transaction; no ETW session is stopped to perform a clear. Missing storage returns exit code **3** and is never recreated by this command. Elevation/argument errors remain **2**, other failures **1**, and confirmed success **0**.

The installer requires the .NET 8 Windows runtime used by the existing service. Normal collection, retention, ranges, service configuration and read-only IPC remain unchanged. The deterministic installer checks use fake platform operations and isolated temporary files; they never touch production SCM, ETW or data. The separately invoked production validation workflow verifies real installation, an unelevated Today/7D/30D page, service restart recovery, uninstall and database preservation.
