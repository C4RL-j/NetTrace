# App Usage 003 validation

The explicit development workflow in [README.md](README.md) passed on
2026-10-13 (local time). Its Release service and diagnostic-client builds had
zero warnings and errors. The service was installed only as the demand-started
`NetTraceAppUsageCollectorDev` test registration; the normal-token diagnostic
clients used authenticated local named pipes and protocol version 2. The
existing App Usage 002 protocol version 1 remains available.

LocalService started Kernel-Network ETW and passed controlled byte transfers,
but could not resolve the live normal client's executable path and process
start time. The workflow therefore selected LocalSystem for this prototype.
The selected service resolved those fields and passed exact controlled IPv4
and IPv6 RX/TX, PID, family, and loopback checks. Passive snapshots contained
nonzero RX and TX for both Brave and the user's active NetMod transport; these
are diagnostic examples, not totals that reconcile with physical adapter usage.

Three consecutive SCM stop/start cycles retained one owned ETW session. The
normal client observed disconnections and subsequent connections. Forced
termination left the exact NetTrace ETW session stale on this Windows machine.
The finite SCM recovery policy started a new service process after its delay;
startup verified and reclaimed that exact session. The client then observed a
new collector instance, with its first new snapshot at zero events and later
snapshots accumulating events. The replacement had exactly one owned session.
No counters persisted across service lifetimes.

| Scenario | Service CPU, one core | Working set | ETW events/s | Snapshots/s | Lost events |
| --- | ---: | ---: | ---: | ---: | ---: |
| Idle, no client or intentional traffic | 1.48% | 39.79 MiB | 61.3 | 0 | 0 |
| Passive normal activity | 3.58% | 44.88 MiB | 45.5 | 0.42 | 0 |
| Bounded local high-event workload | 6.18% | 47.70 MiB | 3,638.4 | 0.42 | 0 |

The normal unelevated client used 4.30% of one core and 39.68 MiB at about
0.48 snapshots/s; its recurring polling CPU was 2.94% of one core. During the
local high-event workload the diagnostic client used 18.99% and 41.60 MiB,
including its traffic generator. These are short observations on this machine,
not steady-state or production guarantees. The idle service generated no
snapshots without a client request.

The final wrapper and independent checks confirmed `sc.exe query` returned
1060, with no service process, named-pipe server, owned ETW session or protected
run directory. The temporary SCM recovery policy was removed with the service
registration. Full NetTrace verification passed: App Usage 003 **62/62**, App
Usage 002 **53/53**, App Usage 000 **61/61**, Regression **651/651**, WPF
**674/674**; production NetTrace, Regression and WPF builds had zero warnings
and errors. `git diff --check` passed. The production NetTrace project and its
adapter accounting, startup, UI, and database were not changed.

For eventual always-on collection, **Automatic (Delayed Start)** is the
provisional startup recommendation: it avoids adding this collector to the
earliest boot work, at the cost of missing application attribution until the
service starts. The prototype does not install an automatic service. Real
machine sleep/resume was not performed; deterministic suspend/resume lifecycle
tests passed, and an actual sleep/resume run remains a production validation
step.
