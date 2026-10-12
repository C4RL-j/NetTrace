# App Usage 002 elevated collector

This bounded console prototype requires an explicitly elevated launch. A normal token exits with code 2 before opening ETW or IPC; it never requests UAC itself. The normal NetTrace UI and monitoring code are unchanged.

Build with `dotnet build tools/AppUsageElevatedCollector/AppUsageElevatedCollector.csproj`. Follow the [diagnostic client workflow](../AppUsageDiagnosticClient/README.md) to launch this collector elevated and the separate client unelevated. The collector defaults to a 90-second observation window; `--seconds` accepts 1 through 300. `--allow-test-shutdown` explicitly enables the authenticated diagnostic shutdown operation. Optional `--log` and `--output` write collector-owned diagnostic artifacts; client communication uses only the named pipe.

The collector reuses App Usage 000's installed provider schemas, metadata-only parser, process lifetime resolver and ETW ownership. A lock protects aggregation and snapshot copying. IPC receives immutable copies of cumulative RX/TX, IPv4/IPv6 and locality counters, application identities, PID lifetime metadata, System/Unknown buckets and diagnostic metrics. Endpoint addresses remain inside the parser and do not appear in snapshots. Physical adapter accounting remains separate.

Live loss queries use the ETW controller count. A count unavailable from the controller is exposed as `LostEventCount = -1` with `LostEventCountAvailable = false`. The final loss count is captured before stopping the session and remains readable after disposal.

Cancellation, the bounded duration and enabled diagnostic shutdown all flush/stop the owned session, drain and dispose its consumer, then close IPC. The final artifact reports whether the exact owned session is absent. An abrupt process termination can leave a machine-wide ETW session alive because Windows does not reclaim ETW sessions automatically; this prototype does not sweep unrelated sessions. Production startup design must address crash ownership and recovery before always-on deployment. See [TraceEvent session ownership and disposal](https://github.com/microsoft/perfview/blob/v3.2.8/src/TraceEvent/TraceEventSession.cs).

No service, scheduled task, startup registration, database, browser or generated network traffic is created by the collector. Controlled transfers belong to the unelevated diagnostics.
