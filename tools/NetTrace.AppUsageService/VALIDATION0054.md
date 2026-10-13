# App Usage 005.4 clear-history validation

Settings → App Tracking adds Clear App Usage History. Confirmation defaults to
No. Only the confirmed action launches the existing bundled privileged tool,
with the single `clear-history` argument and `runas`. NetTrace's identity and
token remain unchanged. Known missing storage disables the action. Protected
storage permits an explicit privileged presence check; no startup/status read
requests UAC or opens the database in the desktop process.

The new command's separate seam has no Stop, Delete, Configure, Start or recovery
mutators. A running collector uses a local SYSTEM/Administrator management pipe;
its elevated administrator token and the registered SYSTEM server are checked.
The ordinary read-only query protocol remains v2. Wrong frames and unauthorized
clients cannot clear. Missing management support returns exit 4 and the short
desktop message `Update App Tracking before clearing history.`; it never falls
back to direct writes against a running collector.

Clear validates the recognized App Usage schema and executes only DELETE FROM
daily_app_usage inside a transaction. No directory, database, schema or unrelated
file is deleted. The collector serializes clear with SQLite checkpoints, discards
old active and retained batches after commit, and filters buffered observations
at or before the clear boundary. Failed clear retains old history and pending
usage. New observations continue through the same collector and checkpoints.

Stopped/missing collector storage can be cleared under an exclusive protected
history file lease after service, process, pipe and ETW checks. The service holds
the same lease before SQLite initialization and through complete storage cleanup,
including a timed-out flush's actual transaction. This prevents offline clear
from overlapping a starting/stopping collector. The existing ETW ownership mutex
and service installation/configuration policy remain unchanged.

Success clears App Usage's cached rows and invalidates previous request
generations without changing the selected range. The visible page refreshes that
range immediately. Hidden App Usage sends zero IPC requests and refreshes on
return. Missing/stopped setup states remain appropriate when clearing offline.
Dashboard, connection History, desktop storage and physical sessions are outside
this operation.

## Verification commands

```powershell
dotnet run --project tests/NetTrace.WpfTests -- --app-usage-history-clear-checks
dotnet run --project tests/NetTrace.WpfTests -- --app-tracking-settings-checks
dotnet run --project tests/NetTrace.WpfTests -- --app-usage-setup-checks
dotnet run --project tests/NetTrace.WpfTests -- --app-usage-range-checks
dotnet run --project tests/NetTrace.WpfTests -- --app-usage-today-checks
dotnet run --project tests/NetTrace.AppUsageStorageTests -- --clear-history-checks
dotnet run --project tests/NetTrace.AppUsageCheckpointTests -- --clear-history-checks
dotnet run --project tests/NetTrace.WpfTests
dotnet run --project tests/NetTrace.RegressionTests
git diff --check
```

All project builds use the normal source ICO, Release configuration and separate
outputs under ignored `bin/AppUsage0054Verification`. UI outcomes use controlled
service boundaries and real owned WPF controls. Storage/checkpoint tests clear
real owned SQLite databases and prove all three ranges empty, schema/unrelated
data preserved, old pending observations rejected and new traffic written.
Installer/service tests cover privilege, fixed commands, service-state races,
management authentication, continued running configuration and lease ownership.

## Installed-service boundary

The production service was already running under LocalSystem with delayed
automatic start during this change. Its installed build predates 005.4's new
management endpoint and coordinated storage lifetime. It requires the existing
production installer update before running or stopped maintenance is available.
The newly built service's PE metadata contains the history-lease reference; the
helper checks verified installed metadata without loading or executing it.
Implementation verification uses
owned temporary data; this run does not upgrade, restart, stop or clear that
installed collector or its existing production history. No real UAC clear or
production-database deletion is claimed.

## Results

The focused WPF history-clear target passed 95/95 from the final Release build.
Existing focused Settings passed 73/73, Today 75/75, range switching 70/70 and
setup 58/58. The full WPF integration suite passed 1047/1047. Owned 760x600
Running and missing-storage Settings previews were inspected; the buttons,
status and vertical scrolling remained readable with no horizontal overflow.

All 21 Release builds passed with zero warnings or errors using the unchanged
source ICO. Non-UI suites passed: regression 651/651; queries 71/71; checkpoints
73/73; storage 87/87; service lifecycle/management 81/81; IPC 53/53; probe 61/61;
normal-token probe 22/22; installer/history management 79/79. The eight App Usage
suites total 527/527. The new suites include 15 storage-clear cases, 17 checkpoint
clear/ownership cases, 14 management-pipe cases and 26 installer/lease cases.
Real owned SQLite results prove all three ranges become empty, schema and
unrelated files remain, and the same collector accepts and persists new traffic
after clear. Owned service tests confirm clear has no service control or
configuration changes; real production SCM still reports Running, LocalSystem,
Automatic (Delayed Start), with its existing image path.

The first regression run passed 650/651 and failed an existing queued-profile
deletion assertion. It ran alongside WPF checks. After WPF finished, the
unchanged regression retry passed 651/651; no profile/session/physical-accounting
code or related regression test was edited. The failure log is retained as
`bin/AppUsage0054Verification/regression-first-run-failure.log`.

Build logs, nine-suite JSON summaries and WPF logs are beneath ignored
`bin/AppUsage0054Verification/AllBuilds`, `full-wpf.log`, and `non-ui-retry.log`.
The compact clear previews are under
`AllBuilds/NetTrace.WpfTests/owned-app-usage-clear-fd16118ca53e47d98f676ef0c85ea7d3`.
Source hashes confirm that the ICO, physical monitoring, desktop SQLite store and
connection History sources are unchanged. XAML parsing and tracked/new-file
whitespace checks pass. HEAD remains
`e43b835e94647cd578b48e8e15ee030d1c61a416`; the staged diff remains empty.

## Changed files for 005.4

- `MainWindow.AppTracking.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`.
- `Presentation/AppTrackingSettingsController.cs`,
  `Presentation/AppTrackingSettingsPresentation.cs`,
  `Presentation/AppUsageTodayController.cs`, `Presentation/AppUsageTodayPresentation.cs`.
- `Services/AppTrackingSetupService.cs`, `Services/IAppTrackingSetupService.cs`.
- `tools/AppUsageStorage/DailyAppUsageStore.cs`, `tools/AppUsageStorage/README.md`.
- `tools/AppUsageCollectorCore/UsagePersistence.cs`,
  `tools/AppUsageCollectorCore/SqliteUsageCheckpointStore.cs`,
  `tools/AppUsageCollectorCore/AppUsageHistoryLease.cs` (new).
- `tools/AppUsageIpc/AppUsageHistoryManagement.cs` (new).
- `tools/NetTrace.AppUsageService/CollectorServiceHost.cs`, its `README.md`,
  and this validation document (new).
- `tools/NetTrace.AppUsageInstaller/InstallationModel.cs`, `Program.cs`,
  `WindowsInstallationSystem.cs`, `ScmNative.cs`, `README.md`,
  `HistoryClearEngine.cs` (new), `WindowsHistoryClearSystem.cs` (new).
- `tests/NetTrace.AppUsageStorageTests/Program.cs`, `ClearHistoryTests.cs` (new).
- `tests/NetTrace.AppUsageCheckpointTests/Program.cs`, `ClearHistoryTests.cs` (new).
- `tests/NetTrace.AppUsageServiceTests/Program.cs`, `HistoryManagementTests.cs` (new).
- `tests/NetTrace.AppUsageInstallerTests/Program.cs`, `HistoryClearTests.cs` (new).
- `tests/NetTrace.WpfTests/Program.cs`, `README.md`, `AppUsageHistoryClearChecks.cs` (new).
- Root `README.md`.

These are 37 files relative to the uncommitted 005.3 baseline. Earlier App Usage
changes and the unrelated ICO are retained. No staging or commit is performed.
