# App Usage 003 service and crash-recovery prototype

This is a separate, headless Windows Service prototype. It owns metadata-only
Kernel-Network ETW capture, in-memory application counters and local named-pipe
snapshots. Normal NetTrace has no dependency on the service: its UI, startup,
monitoring, physical adapter accounting, database and permissions are unchanged.
No App Usage production page or persistent application history is added.

The shared collector core reuses the installed-provider discovery, parser and
consumer from App Usage 000 and the aggregation/IPC contract from App Usage 002.
Service hosting, ETW ownership, aggregation and the diagnostic client remain
separate components. TraceEvent stays outside the production WPF project.

Run the explicit development validation workflow from a **normal terminal**:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tools\NetTrace.AppUsageService\Validate-Service.ps1
```

The wrapper builds Release service/client artifacts in the ignored verification
directory and explicitly requests UAC for a separate administrative SCM worker.
`RemoteSigned` applies to these PowerShell processes only; the script changes no
machine execution policy.
The diagnostic clients are launched by the original normal-token parent. The
production NetTrace application never requests UAC, installs or starts a service.
There is no production installer or automatic startup registration.

The worker first tests LocalService using its existing privileges, including
real ETW capture and attribution of a live normal user's executable path and
process lifetime. It uses LocalSystem only if ETW access or that required
attribution is insufficient. It changes
no user group membership, machine ETW permissions or global recovery settings.
The temporary development service is `NetTraceAppUsageCollectorDev`, demand-started.
Its image is copied into an administrator-controlled temporary ProgramData
directory; the intended client has read/execute access, never image-write access.
Temporary lifecycle logs are bounded and contain no per-event data.

The workflow exercises controlled IPv4/IPv6 transfers, a no-client/no-intentional
traffic window, passive activity, a bounded local stress workload, repeated SCM
start/stop, forced process termination and delayed SCM restart. The administrative
worker announces diagnostic phases through the temporary service's SCM description;
service/client telemetry always uses authenticated pipes. Diagnostic reports are
test artifacts, not IPC or historical accounting.

A `finally` block disables queued recovery, stops and deletes only the registration
created by this run, safely removes only the exact eligible NetTrace trace session,
and verifies SCM absence, no service process and no owned session. Protected staging
is removed only after cleanup succeeds and its absolute path is checked. A
pre-existing development registration causes refusal; unrelated services and trace
sessions are never swept or stopped.

Each service lifetime has a new collector instance ID, UTC start time and fresh
cumulative counters. One deterministic project-specific ETW name/GUID and protected
single-owner guard prevent competing compliant collectors. Startup recovery checks
the exact identity and session properties before removing a trace left by a crash.
A mismatched session fails closed. The service explicitly requests ETW stop on
hybrid shutdown; guarded recovery also recognizes the exact legacy mode Windows
assigned to a Session 0 logger before this setting was explicit. Unexpected
worker termination is a service
failure, rather than a healthy SCM state with silent loss of capture; the validation
policy restarts twice with delays and then takes no action.

Service pipe security must account for a different Windows identity. Only the
service owner/service SID and intended local user are authorized. Remote clients
are rejected at native pipe creation, and the normal client verifies the pipe's
server against the exact SCM-owned process and configured built-in account. Pipe
names are public identifiers. There are no shell, process-launch, arbitrary file,
registry, provider-selection or service-control operations available over IPC.
Service stop/start belongs to SCM. The standalone App Usage 002 v1 workflow remains
available; service health uses an explicitly versioned contract and incompatible
versions fail clearly.

Snapshots contain process identities/PIDs/lifetimes, RX/TX, IPv4/IPv6, locality,
System/Unknown buckets, event loss and collector health. Endpoints stay in parser
memory. Packet contents, URLs, DNS contents, HTTP data, credentials, VPN/SSH data and
browser history are neither captured nor persisted. Physical usage, logical app
traffic and VPN transport are separate layers; totals are not reconciled.

No snapshots or JSON serialization are generated merely because the service is
running. Process identity lookups are bounded and cached. Diagnostic clients poll
at a low cadence with bounded request deadlines and reconnect backoff. Performance
evidence distinguishes no-client, passive and local-workload scenarios and labels
background activity honestly; a short window is not a production guarantee.

Suspend/resume is covered through deterministic lifecycle tests. Validation does
not force the development machine to sleep. Real sleep/resume must be explicitly
reported as untested until an actual suspend/resume run is performed.

The eventual startup policy is selected from measured lifecycle/performance
evidence. [Delayed automatic startup](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_delayed_auto_start_info)
reduces boot contention but has no guaranteed start time and misses attribution
before capture starts; plain automatic startup favors earlier accounting. Neither
automatic mode is installed by this prototype.

References: [ETW session lifecycle](https://learn.microsoft.com/en-us/windows/win32/etw/configuring-and-starting-an-event-tracing-session),
[service failure actions](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_failure_actionsw),
[service configuration](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-changeserviceconfig2w).
