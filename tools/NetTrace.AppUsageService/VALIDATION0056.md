# App Usage 005.6 lifecycle hardening

One production transport bug was reproduced and fixed. A client EOF changes
the managed pipe state to Broken, so IsConnected becomes false while the native
server instance still requires Disconnect. The conditional cleanup exhausted
all three listeners: the fourth fresh usage connection became unavailable even
though SCM and ETW remained healthy. IpcServer now disconnects both completed
and broken connections before reusing its existing guarded handles.

Real Windows pipe regressions failed 1/3 before the change and pass 3/3 after
it. They exercise 30 fresh service-protocol test connections with range
queries, the native EOF transition, and abandoned/malformed clients. Test-only
identity seams remain confined to the isolated test fixture. Production ACLs,
peer verification, protocol/framing, ETW, accounting and persistence are unchanged.

The UI lifecycle checks cover all three ranges, normal four-second retries,
SCM stopped states, long clock gaps, stale-response rejection, zero hidden-page
requests, retained selection and no automatic installation/elevation. Six
SQLite cases check committed history across clean restarts, crash windows,
repeated snapshots, local midnight, sleep gaps and PID reuse.

## Explicit production workflow

Build the final normal WPF output and invoke from an unelevated terminal:

```powershell
.\tools\NetTrace.AppUsageService\Validate-Lifecycle.ps1 `
  -WpfBuildDirectory C:\PROJECTs\DataUsageMonitorV1\bin\AppUsage0056Verification\Focused
```

The existing production service is verified by exact description, LocalSystem
identity, protected versioned image/manifest hashes, normal-client SID, database
path and recovery configuration. A separate explicit UAC worker executes the
existing installer update, a manual SCM restart, termination of only the
verified SCM collector PID, and real S3 sleep/resume. The default event list is
Update,Restart,ForcedRecovery,Sleep. No uninstall or clear action occurs.

The normal real WPF window remains on 30D. Only its existing four-second timer
performs reconnects; there are no synthetic management signals or manual Tick
calls. Each successful range request must start after the actual event's
completion marker. Diagnostics retain every request start/completion/state,
raw counters, page visibility, selected range, authenticated collector identity
and observable loss counters. The first update can repair an unavailable old
pipe; its unknown old snapshot/counters are reported as unknown rather than
invented. Protected SQLite rows provide the independent history baseline.

Production storage intentionally excludes loopback and local-to-local traffic.
The initial validation incorrectly expected persisted loopback bytes and stopped
before any service mutation. That failed evidence is retained. The corrected
workload reads two small public HTTPS responses from example.com, with cookies,
credentials, proxies and redirects disabled, a 15-second request deadline and
16 KiB content limit per response. Bounded retries allow the network to become
ready after resume; attempts are recorded. Finite byte bounds reject implausible jumps.
After IPC repair, persisted deltas must equal the exact accepted non-loopback
event deltas from authenticated live snapshots, including transport overhead.
No accounting change was made to accommodate validation.

Read-only native SQLite transactions verify quick_check, schema/application
identity, every pre-event date/application row, nondecreasing counters/timestamps
and unchanged database creation identity. No schema creation, migration,
checkpoint, backup, maintenance or file deletion runs in the observer.
Protected before/after observations compare the exact service configuration,
one collector process, one exact owned ETW session, and no orphan NetTrace
sessions. Graceful SCM stop must leave the owned ETW session absent. Forced
recovery relies on SCM rather than a manual restart; restoration after a failed
test remains separately reported as a failure.

S3 sleep is attempted only with existing enabled AC/DC wake-timer policies.
A temporary absolute UTC waitable timer schedules a wake in 30 seconds, and
SetSuspendState requests sleep with wake events enabled. Kernel-Power sleep and
resume event records prove that real sleep happened. The timer handle is then
cancelled/closed; no persistent power policy or scheduled task is changed.
The implementation follows Microsoft's [SetSuspendState documentation](https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-setsuspendstate)
and [SetWaitableTimer documentation](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-setwaitabletimer).

Windows reboot is deferred while the interactive IDE/validation session is
active. Automatic delayed startup is verified as configuration, which does not
substitute for a real reboot/startup observation. No reboot success is claimed.

Evidence remains under ignored bin/AppUsage0056Verification. Service/database
snapshots, traffic deltas, actual event markers, lifecycle-client.json, compact
UI previews and workflow-summary.json remain separate from earlier failed runs.
The production collector is left installed and Running.

## Results

The only production change is native query-pipe cleanup. The installed package
was actually updated from
`c452b80663ae3434f5b0fbaa24aa11185ef601c455632d7e038851591006d131` to
`2b7f6636928d897deab1c6f345c5fcf003c1ed0f29315a0c95f3cba2f4f86c1a`.

The real normal-token page retained 30D and automatically loaded a fresh request
started after each completed external event. No manual Tick, synthetic observer
signal, further navigation or NetTrace elevation was used.

| Actual event | Action result | UI reconnect after completion marker |
| --- | --- | --- |
| Production update | Passed; old PID 5516 replaced by 11152 | 1.570 seconds |
| Manual SCM restart | Passed; new PID 8404; owned ETW absent during completed stop | 4.586 seconds |
| Forced termination/SCM recovery | Passed; SCM started new PID 852 without a manual start | 3.418 seconds |
| Real S3 sleep/resume | Passed; PID 852 retained; fresh collector lifetime | 1.123 seconds |

The new Kernel-Power records 2897/2898 confirm sleep/resume on 2026-10-13 at
19:21:26/19:21:29 local time. The observed sleep interval was 2.916 seconds;
the machine woke before the scheduled 30-second timer. A longer real sleep
window was not tested. The previous/fresh authenticated collector instance IDs
were different after resume, and all observed counters stayed nonnegative.
The six-second hidden-page interval sent zero queries. The normal WPF process
kept its unelevated token and identity, with zero service mutation calls.

All 25 baseline date/application rows survive. The final database still has
25 rows, quick_check returns ok, schema and database creation identity match,
and every committed row's bytes/timestamp stayed equal or increased. Source
hashes confirm ETW, accounting, persistence and the unrelated ICO are unchanged.
LocalSystem, Automatic (Delayed Start), and exact restart recovery at 5/15/60
seconds, then None, with 86400-second reset and non-crash recovery enabled, match
the pre-event configuration. Final PID 852 is Running, with exactly one collector
process and the one exact owned ETW session, no other NetTrace ETW sessions, and
an available native loss counter of zero. Observable before/after counters were
also zero; no loss claim is made for the unobserved crash interval.

The first post-resume remote HTTPS request hit its 15-second deadline. The
original combined workflow remains recorded as failed; its already completed
actual events and UI observations remain successful. A separate post-resume
normal-token traffic measurement subsequently passed: 6294 download bytes and
852 upload bytes exactly matched the accepted non-loopback event deltas, with
finite bounds and no negative/reset/duplicate persisted deltas. Final protected
history/integrity checks were performed after that measurement and all suites.
No additional service restart or sleep was used to obtain the follow-up result.

Live evidence is in `bin/AppUsage0056Verification/run-7e30fef77c534cb697708e4a53763635`;
post-resume traffic is in `run-631e6bf39e90493c847dd62bf4a06e2d`; final protected
state is in `run-999ee66e475e492e92eeab93276926d6`. The separate consolidated
comparison is `consolidated-evidence.json`; the timestamp-filtered current sleep
pair is `sleep-event-evidence.json`. Failed earlier reports were not overwritten.

Verification passed on the final production fix:

- Focused UI lifecycle: 62/62; SQLite lifecycle: 6/6; native pipe lifecycle: 3/3.
- Full WPF: 1205/1205; full regression: 651/651, without a retry.
- Full App Usage suites: 536/536 across query 71, checkpoints 79, storage 87,
  service 81, IPC 56, probe 61, unelevated probe 22 and installer 79.
- All 21 normal Release builds passed with zero warnings/errors.
- Tracked/new-file whitespace checks, script syntax, protected source/ICO hashes,
  unchanged HEAD and empty index passed. No files were staged or committed.

Reboot remains untested because the interactive IDE/session is active.

Forced process death can lose the in-memory checkpoint window and cannot
provide observation during downtime. Committed history preservation and zero
observable ETW loss do not prove zero lost traffic during that interval. The
workload is committed before each destructive event; persistence semantics
are preserved.

## Changed files relative to 005.5

- tools/AppUsageIpc/IpcServer.cs (the only production change).
- tests/NetTrace.AppUsageIpcTests/PipeLifecycleTests.cs, Program.cs and README.md.
- tests/NetTrace.AppUsageCheckpointTests/LifecyclePersistenceTests.cs,
  Program.cs and README.md.
- tests/NetTrace.WpfTests/AppUsageLifecycleChecks.cs,
  AppUsageLifecycleLiveValidation.cs, Program.cs and README.md.
- tools/NetTrace.AppUsageService/Validate-Lifecycle.ps1, this document,
  service README.md and root README.md.

These 15 files are relative to the authorized uncommitted 005.5 baseline.
No ICO edits, staging or commits.
