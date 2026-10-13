# App Usage 005.1 production installation validation

This explicit workflow exercises the production installer and the native App
Usage page. It does not use the temporary development service or an isolated
application-usage database. Only the physical monitoring fixture is isolated;
the page uses the production authenticated IPC client and the real persistent
`%ProgramData%\NetTrace\app-usage.db`.

Run from a normal PowerShell terminal after ordinary WPF suites have finished:

```powershell
powershell -NoProfile -ExecutionPolicy RemoteSigned -File tools/NetTrace.AppUsageService/Validate-ProductionInstall.ps1
```

The explicitly invoked workflow requests elevation once for its administrative
worker. NetTrace itself and the visible 760 x 600 WPF validation window use the
normal user token. NetTrace does not invoke the workflow or request elevation.
The normal and elevated tokens must belong to the same intended Windows user.

The worker refuses pre-existing production and development registrations. It
installs the production collector twice from the same kit and independently
checks SCM/registry configuration: `NetTraceAppUsageCollector`, LocalSystem,
automatic delayed start, restart delays of 5/15/60 seconds followed by no further
automatic restart, a one-day recovery reset, and recovery for reported non-crash
failures. The installer itself
verifies authenticated IPC availability after each installation.

The visible real MainWindow loads Today, 7D and 30D via production IPC and checks
the displayed totals and application rows against returned values. The worker
then stops the service until the existing page displays its unavailable state.
After starting the service, the same selected 30D page must recover without
navigation. All three ranges are checked again; persisted totals must not
decrease. No application traffic is intentionally generated.

Finally the worker cleanly stops and uninstalls the collector, runs uninstall
again, checks SCM 1060, and verifies the exact owned ETW session, collector
process and pipe are absent. The stopped database must retain its identical
SHA-256 and length across uninstall. Neither this workflow nor uninstall deletes
the database, WAL files, or existing history. Protected production binaries are
also retained for safe uninstall/reinstall.

Evidence is written below `bin/AppUsage0051Verification/run-<guid>`: installation
and uninstall logs, independent SCM configuration, normal-token page JSON,
screenshots, and final cleanup/database preservation checks. Build overrides
`-ApprovedBaselineIcon` and `-VerificationTargets` support verification when an
unrelated local icon edit cannot be used as a resource. `-SkipBuild` uses existing
separate Service/Installer/Wpf output directories under the verification root.

## Recorded result

Passed on 2026-10-13 after the full ordinary WPF suite completed. The final kits
were the separate `AllBuilds/NetTrace.AppUsageService` and
`AllBuilds/NetTrace.AppUsageInstaller` outputs; the normal WPF client used the
separate `Wpf` output. Evidence:
`bin/AppUsage0051Verification/run-b3a7193f4304419dbf5cb6e023c141f1`.

- Both install calls passed and reported authenticated IPC available; the second
  selected the update decision and reused the identical protected binary version.
- Independent SCM/registry checks confirmed start type 2, delayed-auto flag 1,
  LocalSystem and Running. SCM reported 5/15/60-second restart actions, a
  86400-second reset and enabled non-crash recovery.
- The real visible window used an unelevated token and loaded Today, 7D and 30D.
  Each range displayed five applications with 18.15 MB download, 83.2 KB upload
  and 18.23 MB logical total before restart. Returned rows and summary values
  matched the display.
- The stopped interval lasted 6.07 seconds. The same selected 30D page displayed
  unavailable and recovered after service start without navigation. All ranges
  then loaded five applications with 25.53 MB download, 111 KB upload and
  25.64 MB logical total. Only naturally occurring traffic was observed.
- The eight visible-page queries had maximum concurrency 1; the 760 x 600 view
  had no horizontal overflow. Seven screenshots record all ranges before/after
  restart and the stopped-service state.
- Both uninstall calls passed. SCM returned 1060, and the collector process,
  exact owned ETW session and pipe were absent.
- The existing production database remained 12288 bytes and had identical
  SHA-256 before/after uninstall:
  `AD75F39319BD5A5B367278D43AFC29E5A5EF92813398359B6386FAB35E8F3436`.
  Production history and protected binaries remain on disk.

An initial workflow attempt installed successfully but its extra textual check
expected a numeric `qfailureflag` value; this Windows build reports `TRUE`.
The workflow now verifies the registry DWORD and keeps the raw SCM output.
Its cleanup ordering also now stops and waits for the process before hashing
the database, so the final persistence checkpoint is included in the snapshot.
The initial service was uninstalled; its database was kept and used by the
successful rerun. These were validation-workflow corrections; no collector,
accounting, persistence or page behavior changes were needed.
