# App Usage collector service (003 lifecycle, 004 persistence, 005 installation/setup)

This is a separate, headless Windows Service collector. It owns metadata-only
Kernel-Network ETW capture, in-memory application counters and local named-pipe
snapshots. App Usage 004.4.1 adds a page to normal NetTrace through authenticated
read-only IPC, and 004.4.2 adds Today/7D/30D selection using the existing range
queries. Normal launch remains available without this
service. App Usage 005.2 shows an explicit setup action when the production service
is missing, and a concise stopped state when it is installed but stopped. Startup,
monitoring, physical adapter accounting, desktop database and permissions remain
unchanged. The collector persists qualifying event deltas
through the separate 004.1 daily application store.

App Usage 005.1 adds an explicit elevated [production installation tool](../NetTrace.AppUsageInstaller/README.md).
It registers `NetTraceAppUsageCollector` under LocalSystem with Automatic (Delayed
Start), protected Program Files binaries and bounded SCM restart recovery.
Normal NetTrace remains unelevated and uses authenticated read-only IPC against
that production name. The separate `NetTraceAppUsageCollectorDev` name remains
available to the historical temporary validation workflows. These services share
the existing exact collector identity and cannot run together.

Production uninstall stops and removes the service, verifies exact ETW cleanup
and leaves `%ProgramData%\NetTrace\app-usage.db` intact. Installation and uninstall
never run automatically from the desktop application. See
[005.1 live validation](VALIDATION0051.md) for installation, page recovery and
database preservation evidence.

App Usage 005.2 adds **Install App Tracking** to the missing-service setup state.
Only an explicit click launches the existing production installer through UAC;
NetTrace remains unelevated. Build and publish outputs include the companion
installer and service packages in `AppTracking/Installer` and `AppTracking/Service`.
A cancelled UAC prompt or failed installation returns to setup with a short error.
After successful installation and start, the visible page reconnects and loads its
selected Today/7D/30D range. Running-service behavior, hidden-page polling rules,
ETW capture, storage, accounting, IPC and service configuration remain unchanged.
The App Usage page has no uninstall action or automatic installation at startup.

```powershell
dotnet run --project tests/NetTrace.WpfTests -- --app-usage-setup-checks
```

App Usage 005.3 adds an **APP TRACKING** section to Settings. It shows Running,
Stopped or Not installed for `NetTraceAppUsageCollector` and makes **Uninstall
App Tracking** available only for an installed service. A confirmation defaults
to No and explains that history is preserved. Only a confirmed click launches
the existing production tool with `uninstall` through UAC; NetTrace stays
unelevated. Cancellation or failure shows a short error and permits retry.

Successful uninstall immediately updates Settings and clears App Usage's old
presentation into its existing missing-service setup state without restarting
the desktop. Range selection remains unchanged; hidden App Usage sends no IPC
requests. The existing uninstall still preserves `app-usage.db` and history.
The 005.3 phase adds no Start/Stop controls, service configuration changes or
installer semantics. See [005.3 validation](VALIDATION0053.md).

```powershell
dotnet run --project tests/NetTrace.WpfTests -- --app-tracking-settings-checks
```

App Usage 005.4 adds **Clear App Usage History** in Settings with confirmation
and installer-only UAC. A separate local SYSTEM/Administrator management pipe
performs a running-service clear without stopping capture or changing the
read-only v2 protocol. SQLite rows and old pending batches are cleared together;
buffered old observations are filtered and new traffic continues accumulating.
The schema, database file, unrelated data and service registration remain.
Stopped/uninstalled storage uses an exclusive history lease with ownership and
service-state checks. Older installed collectors return update-required feedback;
clear never updates or restarts them. See [005.4 validation](VALIDATION0054.md).

App Usage 005.5 adds **Update App Tracking** to Settings when the installed
collector package differs from the bundled service kit. Settings shows Running,
Stopped, Update available or Not installed. The comparison uses the installer's
existing complete-file SHA-256 inventory, including PDBs, rather than the constant
assembly version. Unelevated status checks read the installed identity from the
owned SCM command's protected `Versions/<hash>` path and hash the bundled kit;
they do not open protected collector files or the database.

An explicit button click requests UAC for the existing installer's `install`
update invocation. NetTrace stays unelevated, and no update runs automatically.
The existing installer switches the stopped service to its staged protected package,
retains the database, schema and history, restores Automatic (Delayed Start),
LocalSystem and the existing recovery policy, then restarts and verifies IPC.
Its rollback and ownership checks remain unchanged. Cancellation or failure
shows retry feedback. App Usage reconnects with its selected Today/7D/30D range,
and hidden App Usage still sends no refresh requests. A pre-005.4 collector gains
clear-history capability after update without any history being cleared.
ETW, accounting, persistence and query protocol semantics are unchanged. See
[005.5 validation](VALIDATION0055.md).

```powershell
dotnet run --project tests/NetTrace.WpfTests -- --app-tracking-update-checks
```

App Usage 004.2 checkpoints pending non-loopback event bytes every five seconds
in one storage batch. RX is download and TX is upload. The pending key combines
the event's local date with the normalized executable identity, or the existing
System/Unknown fallback; PIDs never become daily database identities. Loopback,
local-to-local and unclassified diagnostic traffic remain excluded. The event
path reuses cached process metadata and does no disk I/O. A failed transaction
is requeued for the next interval. Clean stop stops event acceptance and performs
a bounded final flush. Forced termination can lose approximately the latest
checkpoint window, and a persistence outage can extend that window.

The service uses `%ProgramData%\NetTrace\app-usage.db`. Retention cleanup runs
once per service lifetime at successful storage initialization, not every
checkpoint. Persistence availability, last successful checkpoint, degraded state
and counters appear in the internal final diagnostic report and bounded service
log. App Usage 004.3 extends service protocol version 2 additively with the
read-only `GetAppUsageRange` operation. Existing Hello, capabilities and snapshot
payloads remain compatible; the standalone App Usage 002 protocol stays version 1.

The typed range is exactly Today, 7D or 30D: inclusive local calendar dates ending
today, with the previous 0, 6 or 29 days. The service uses the existing storage
abstraction and aggregates persisted rows by application identity, excluding
zero-use apps and retaining System/Unknown rows. Results are sorted by total bytes
descending, then application identity in ordinal ascending order. Download, upload
and logical totals describe application traffic; Dashboard physical usage is a
separate accounting system. Empty history is a valid available empty result.
Storage failures return a bounded typed unavailable/degraded diagnostic without
stack traces. Query requests accept no SQL, database paths, free-form dates or
mutation commands; response rows retain historical executable metadata.

From a **normal terminal**, the existing development-service diagnostic client can request a range:

```powershell
dotnet run --project .\tools\AppUsageDiagnosticClient -- --service --app-usage Today --output .\bin\today-client.json
dotnet run --project .\tools\AppUsageDiagnosticClient -- --service --app-usage 7D --output .\bin\7d-client.json
dotnet run --project .\tools\AppUsageDiagnosticClient -- --service --app-usage 30D --output .\bin\30d-client.json
```

Query mode does not discover physical adapters or request collector snapshots.
It displays application rows and records typed responses, query latency and client
resources. `--app-usage-all --query-count 5 --seconds 45 --quiet` performs five
reads per range, separated by one second; `--recover` retains the established
bounded reconnect backoff for a stopped/restarting service. Query counts are
bounded to 1–20, and the normal elevation guard still applies.

Run the isolated 004.3 service/query validation from a **normal terminal**:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tools\NetTrace.AppUsageService\Validate-UsageQueries.ps1
```

The wrapper builds separate Release service/client outputs and requests the
established explicit UAC worker for a temporary demand-start LocalSystem service.
It refuses a pre-existing development registration, protects the service image,
and uses a new isolated `--data-root` beneath protected ProgramData staging. The
real default database is never adopted or deleted. The original normal-token
parent confirms direct database access is denied, then queries only authenticated
IPC after a logged successful checkpoint of passive traffic. It runs warm reads,
five repeated reads per range, a service stop/restart with reconnect, and a final
absent-service check. No intentional network traffic is generated. Brave/NetMod
examples depend on existing activity. Client and service handles/private memory
are measured after warmup. Exact aggregation and concurrency are tested separately
against controlled fixtures. The worker removes the owned registration and entire
isolated data root in `finally`, after verifying SCM 1060, no collector process,
no exact owned ETW session and no stale pipe. JSON evidence remains in the ignored
`bin/AppUsage0043Verification` folder; no validation SQLite copy is retained.

The Today-page live workflow uses the same protected temporary-service pattern:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tools\NetTrace.AppUsageService\Validate-TodayPage.ps1
```

It runs a visible, normal-token real WPF page with the production Today IPC
client, checks exact summary/row binding and naturally growing totals, then
verifies an eight-second hidden-page window starts no Today queries and returning
refreshes the page. Compact 760×600 renders and scrolling checks accompany the
typed evidence. Physical-monitoring fixtures are isolated; no intentional traffic
is generated. Only the temporary SCM worker requests UAC. It removes its exact
owned service, session and isolated data before a final unavailable-page probe.
See [004.4.1 validation](VALIDATION00441.md) for validation-only icon overrides and
the evidence boundaries.

Run the separate 004.2 workflow from a **normal terminal**:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\tools\NetTrace.AppUsageService\Validate-Persistence.ps1
```

It builds a separate diagnostic helper and uses the established explicit UAC
worker pattern. Only the temporary LocalSystem service is installed, with
protected ProgramData images and demand startup. The helper runs with the
original normal token and sends one tiny IPv4 and one IPv6 DNS question per
collector lifetime. It records byte counts, family and attribution, never
response contents or endpoints. Brave/NetMod are observed passively if active.
If the IPv6 DNS peer does not reply, the report preserves that limitation: the
controlled IPv6 send must still be captured, and passive non-loopback IPv6 RX/TX
must be proven against the persisted application row. No browser or VPN traffic
is generated to force that passive evidence.

The workflow refuses any pre-existing development service or default database
(including companion files). It verifies that SYSTEM created the actual default
database without granting Everyone write access. After two clean service
lifetimes, the 004.1 API verifies that the second lifetime's event deltas extend
the same daily application row. It measures idle and normal CPU/RAM, checkpoint
frequency, approximate database-file changes and database size. JSON evidence
remains under the ignored verification folder; the database is removed.
Its `finally` block removes the exact registration, owned database and protected
staging after confirming SCM 1060, no collector process, no owned ETW session and
no stale pipe. It preserves the existing ProgramData directory and ACL.

The original App Usage 003 lifecycle script and its phases below remain
unchanged. The current service enables persistence by default, so running that
older script also uses the default application database; the 004.2 workflow above
owns the database creation and cleanup checks for persistence validation.

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
The diagnostic clients are launched by the original normal-token parent. Normal
NetTrace startup never requests UAC, installs or starts a service. Production
installation uses the separate explicit 005.1 tool, including the 005.2 desktop
button that launches it; this development
wrapper retains its temporary demand-start configuration.

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

Production 005.1 uses [delayed automatic startup](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_delayed_auto_start_info).
Windows chooses the delayed start time; attribution begins when capture starts.
Historical development validation continues using demand start.

References: [ETW session lifecycle](https://learn.microsoft.com/en-us/windows/win32/etw/configuring-and-starting-an-event-tracing-session),
[service failure actions](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_failure_actionsw),
[service configuration](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-changeserviceconfig2w).

App Usage 005.6 repairs native query-pipe reuse after client EOF. The production
service's existing identity, delayed startup, recovery, ETW and storage settings
are preserved. [Lifecycle validation](VALIDATION0056.md) documents actual update,
SCM restart/recovery and sleep/resume, read-only history checks, bounded remote
traffic, automatic unelevated UI reconnect and the deferred reboot check.
