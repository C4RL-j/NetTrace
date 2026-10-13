# App Usage 005.5 update validation

Settings compares the owned installed collector's protected content version
with the complete bundled collector kit. Running and Stopped retain their
existing status when the versions match. A different valid installed package
shows Update available and Update App Tracking. Missing service remains Not
installed. Read-only status never launches the installer or requests UAC.

Only the explicit update button launches the existing production install/update
invocation through UAC. The desktop identity and token remain unchanged. The
existing installer owns service stop, protected versioned binary installation,
configuration verification, restart and IPC verification. Update does not clear
or replace the database. Schema, ETW/accounting and persistence behavior remain
unchanged. Cancellation/failure returns a concise error and permits retry.

App Usage invalidates older requests and reconnects to the selected range when
the update completes. A visible page refreshes automatically; a hidden page
continues sending zero requests and loads its selected range on return.

## Explicit live workflow

Run from a normal terminal after building the final WPF output:

```powershell
.\tools\NetTrace.AppUsageService\Validate-CollectorUpdate.ps1 `
  -WpfBuildDirectory C:\PROJECTs\DataUsageMonitorV1\bin\AppUsage0055Verification\AllBuilds\NetTrace.WpfTests
```

The workflow expects the already installed older production package
`af379cbad2ab8ec23ac647a01a3e28731dee8f25adfa0dfa826d243f3543a7f2`.
It refuses a different starting package rather than adopting it implicitly.
The requested production service is left installed and Running afterward.
Existing manually opened NetTrace processes are outside this workflow.

The first explicit UAC prompt reads protected configuration and the entire
existing production SQLite history. It verifies exact service ownership,
manifest hashes, image path and normal-client SID. SQLite opens with
SQLITE_OPEN_READONLY and uses a consistent deferred read transaction; no
initialization, migration, writes, backup, maintenance or service control runs.
The report includes all date/application rows, their byte counters and metadata,
schema hash and database creation identity.

The normal-token real WPF window selects 30D, opens Settings, observes Update
available and clicks the actual Update App Tracking button. An older collector
with unavailable or incompatible IPC can still be repaired: its recorded
baseline transport result is retained, and range preservation uses the protected
read-only SQLite snapshot rather than requiring the old pipe to connect.
AccessDenied and PeerRejected are rejected rather than bypassing authentication.
This button requests
the second UAC prompt for the existing bundled installer. App Usage is made
visible while the update runs and recovers automatically without further
navigation. The harness verifies one update invocation, no install/uninstall or
clear-history invocation, unchanged normal desktop identity, selected 30D,
nondecreasing persisted range totals and a new authenticated collector instance.
If the old pipe provided no snapshot, no old GUID is invented; the independently
read protected before/after SCM PID verifies that the collector process changed.
A connected range completion must occur after the successful installer outcome,
so a query completed against the older collector cannot satisfy recovery.

The third explicit UAC prompt reads the protected post-update configuration and
SQLite rows. Every pre-update date/application key must remain, byte counters
and timestamps must stay equal or increase, and metadata may advance only with
a later observation. Schema and database creation identity must match. Raw
database hashes need not match because the running collector legitimately
continues writing. LocalSystem, Automatic (Delayed Start), description and exact
recovery actions are compared before/after. Changed service PID, current bundled
version and the new protected management endpoint confirm the new collector.
Management availability is observed with WaitNamedPipe; no connection, request,
clear action or history deletion is made. The clear-history Settings button is
observed enabled, and existing history is verified before any clear action.

Evidence is written beneath ignored `bin/AppUsage0055Verification/run-<GUID>`:
protected before/after JSON inventories, normal-token `Update-client.json`,
three 760x600 UI previews and `workflow-summary.json`. There is no cleanup
uninstall because this task explicitly updates the user's installed collector.

## Verification results

All 21 Release builds passed with zero warnings or errors using the normal
unchanged source ICO. The root desktop and WPF validation collector bundles
contain the same 68 files and content version
`c452b80663ae3434f5b0fbaa24aa11185ef601c455632d7e038851591006d131`.
No production clear-history action is part of this task.

Focused update checks passed 96/96. Existing focused Settings checks passed
73/73; clear-history 95/95; Today 75/75; ranges 70/70; and setup 58/58. The full
WPF suite passed 1143/1143. The final live-harness-only rebuild also passed with
zero warnings or errors, and update checks passed 96/96 again. Root desktop and
WPF collector bundle content versions remained equal after that rebuild.

Regression passed 651/651 on an unchanged retry after the first run passed
650/651 and failed the existing queued-profile-deletion assertion. No profile,
connection-session, Dashboard, History or physical accounting source was changed
to address that assertion. The eight other App Usage suites passed 527/527:
queries 71/71, checkpoints 73/73, storage 87/87, service 81/81, IPC 53/53,
ETW probe 61/61, normal-token probe 22/22 and installer 79/79. All 21 authoritative
Release builds passed with zero warnings/errors; the unchanged source ICO was
used throughout.

## Production update and protected history verification

The actual normal-token Settings button updated the already installed package
`af379cbad2ab8ec23ac647a01a3e28731dee8f25adfa0dfa826d243f3543a7f2`
to `c452b80663ae3434f5b0fbaa24aa11185ef601c455632d7e038851591006d131`.
One explicit update invocation succeeded in 8.36 seconds. No install, uninstall
or clear-history invocation occurred in the normal desktop harness. Its token
remained unelevated and the selected 30D range remained unchanged.

The protected post-update read and supplemental comparison passed. All 22
existing persisted date/application rows survived with nondecreasing bytes and
timestamps. Eight rows accumulated new bytes while tracking continued. Total
persisted download advanced from 3,045,856,340 to 3,051,690,354 bytes, and upload
advanced from 661,590,358 to 668,462,599 bytes. The exact schema hash and database
file creation identity remained unchanged. No clear action was invoked.

SCM PID changed from 10992 to 5516 and the updated service remained Running.
LocalSystem, Automatic (Delayed Start), ownership description and the exact
restart recovery configuration remained equal: 5/15/60 seconds followed by
None, reset after 86,400 seconds, with recovery for non-crash failures enabled.
The previously absent protected history-management endpoint is now available;
availability was observed without connecting or sending a clear command.

Evidence is in `bin/AppUsage0055Verification/run-88ce53aa45cd454bb80e9926fe561f7e`:
`Update-client.json`, `before-protected.json`, `after-protected.json` and
`protected-comparison.json`. The initial page observation after the successful
update timed out waiting for the selected range to reconnect. Automatic page
recovery was not verified in that original run; the protected verification
independently
confirms that the update, database preservation, continued tracking and service
configuration preservation succeeded.

A separate authenticated production IPC connection and 30D query passed after
the update. This confirms that the current collector exposes working ordinary
read-only IPC, but does not replace observation of the real page reconnecting.
The failed live workflow report is retained as failed rather than overwritten
with a successful combined result. Its After inventory was skipped when the
page observation timed out; `protected-comparison.json` records the later
successful database/configuration verification separately.

## Current normal-token page observation

A separate read-only normal-token UI observation passed using the installed
current collector. It can be explicitly repeated with the final WPF executable:

```powershell
.\bin\AppUsage0055Verification\AllBuilds\NetTrace.WpfTests\NetTrace.WpfTests.exe `
  --app-tracking-update-observe-current --output C:\ABSOLUTE\current-ui-observation.json
```

Settings showed Running, the current installed/bundled content version matched
`c452b80663ae3434f5b0fbaa24aa11185ef601c455632d7e038851591006d131`, the Update
button was hidden, and Clear App Usage History was enabled. The real App Usage
30D view loaded 23 applications with summary values matching authenticated IPC.
The desktop token remained unelevated and its normal user identity did not
change. There were zero update, install, uninstall or clear-history calls.

An isolated local observer StateChanged notification, with no installer or
service action, exercised the page's completion-notification path. Requests
advanced from two to three and the visible page loaded another authenticated
30D result in 0.289 seconds while retaining its selected range. This verifies
that the current
normal desktop's refresh handler works, and is reported separately from the
earlier actual-update reconnect observation that timed out. The original
combined live workflow remains failed; no successful original reconnect
measurement is claimed.

`current-ui-observation.json` and the three readable 760x600 previews are under
the same production-update run directory: `760x600-current-settings.png`,
`760x600-current-30d.png`, and `760x600-current-observer-refreshed-30d.png`.
The final observer-only WPF build passed with zero warnings/errors, and focused
update checks passed 96/96 again. Logs are
`wpf-current-observation-build.log` and `focused-update-final-current-build.log`
under `bin/AppUsage0055Verification`.

## Changed files for 005.5

- `MainWindow.AppTracking.cs`, `MainWindow.xaml`.
- `Presentation/AppTrackingSettingsController.cs`,
  `Presentation/AppTrackingSettingsPresentation.cs`,
  `Presentation/AppUsageTodayController.cs`.
- `Services/AppTrackingSetupService.cs`, `Services/IAppTrackingSetupService.cs`.
- `tools/AppUsageIpc/CollectorPackageVersion.cs` (new),
  `tools/NetTrace.AppUsageInstaller/WindowsInstallationSystem.cs`.
- `tests/NetTrace.WpfTests/AppTrackingUpdateChecks.cs` (new),
  `AppTrackingUpdateLiveValidation.cs` (new), `Program.cs`, `README.md`.
- `tools/NetTrace.AppUsageService/Validate-CollectorUpdate.ps1` (new),
  this validation document (new), and service `README.md`.
- Root `README.md`.

These 17 files are relative to the existing uncommitted App Usage 005.4 baseline.
The new installer helper delegates its existing package hashing algorithm to the
shared implementation without changing install/update/rollback semantics.
No ETW, accounting, persistence, schema, query protocol or ICO changes are made.
No files are staged or committed.
