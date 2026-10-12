# App Usage 002 elevated collector and local IPC prototype

The normal NetTrace application, monitoring, physical-adapter accounting, database,
UI and startup behavior are unchanged. These standalone diagnostics prove the
privilege boundary before any production integration. The collector has no UI,
network workload or database; the client runs under a normal token. Nothing
installs a service, scheduled task or startup entry.

Build from the repository root:

```powershell
dotnet build tools/AppUsageElevatedCollector/AppUsageElevatedCollector.csproj
dotnet build tools/AppUsageDiagnosticClient/AppUsageDiagnosticClient.csproj
dotnet run --project tests/NetTrace.AppUsageIpcTests/NetTrace.AppUsageIpcTests.csproj
```

Explicitly launch the collector with elevation. This action belongs to the
diagnostic workflow; NetTrace never requests elevation or launches it:

```powershell
$collectorExe = (Resolve-Path tools/AppUsageElevatedCollector/bin/Debug/net8.0-windows/AppUsageElevatedCollector.exe).Path
Start-Process -FilePath $collectorExe -Verb RunAs -WindowStyle Hidden -ArgumentList '--seconds 90'
```

Then start the client from a **normal**, unelevated terminal:

```powershell
dotnet run --project tools/AppUsageDiagnosticClient/AppUsageDiagnosticClient.csproj -- --seconds 20 --controlled
```

Both programs use the same SID-derived pipe name by default. A custom `--pipe NAME`
allows isolated diagnostic runs. Only short local names are accepted. The client
refuses elevated execution. The collector refuses normal execution. The
`--controlled` flag runs App Usage 000's bounded local TCP transfers from the
unelevated client and a separate child PID: 65,536 uploaded bytes and 131,072
downloaded bytes plus one completion byte, once for IPv4 and once for IPv6.
Without this flag the client only requests snapshots; it does not generate traffic.
Existing Brave and NetMod traffic can be observed passively without launching or
reconfiguring either application.

IPC uses native Windows named pipes, never HTTP, TCP or shared files. The pipe has
an explicit protected same-user SID DACL, a medium integrity label for normal-token
access, `PIPE_REJECT_REMOTE_CLIENTS`, and first-instance protection against an
existing pipe owner. The server verifies the client process token's SID; the client
verifies the server's SID and actual elevated token. Client SQOS uses identification
only. This is an authenticated **same-user** prototype boundary, not an application
signature allowlist or an installed service's multi-user policy.

Protocol version 1 uses a four-byte little-endian length followed by strict JSON.
Requests are at most 4 KiB and responses at most 4 MiB. The only operations are
`Hello`, `GetCapabilities`, `GetSnapshot`, and optional `ShutdownForTest`. A
successful version handshake precedes the other operations. There are no command,
file, registry, process-launch or arbitrary-provider operations. The collector
enables `ShutdownForTest` only with explicit `--allow-test-shutdown`; normal
prototype runs do not accept it.

The ETW-free `AppUsageIpc` assembly contains the future UI contract. The collector
reuses App Usage 000's installed manifest discovery, metadata parser, process
resolver, aggregation and owned session lifecycle. TraceEvent 3.2.8 remains
confined to diagnostic projects. The diagnostic client references App Usage 000
for its existing local validation workload; it never starts ETW.

Snapshots carry a collector instance GUID, UTC timestamp, uptime, event/loss
counts, capabilities, metrics, application groups and explicit System/Unknown
buckets. They retain process PID, name, path when available, start time and lifetime
identity. Unresolved identities retain their diagnostic PID in Unknown. Counters
are cumulative since collector start; a restart creates a fresh instance and
zero counters. Immutable copies under an aggregation lock prevent clients from
observing partially updated counters. Up to three clients can connect independently.

Only summarized RX/TX, IPv4/IPv6 and locality counters cross IPC. Addresses and ports
remain in parser memory. Packet/socket payloads, DNS contents, URLs, HTTP headers,
credentials and browser history are never decoded or transmitted. Live ETW loss
comes from the owning controller, not the real-time source's startup snapshot;
`-1` with `LostEventCountAvailable=false` means unavailable. Nonzero loss makes
incompleteness visible. Logical application traffic and VPN transport may describe
different layers and are never added to physical adapter Today accounting.

Connection attempts and exchanges have bounded cancellation-friendly waits.
The client requests snapshots about once per second. It reports absent collector,
denied access, rejected peer, incompatible protocol, malformed response, timeout,
disconnection and cancellation as diagnostic states. Default runs stop on failure.
`--recover` permits bounded reconnect attempts with a two-second delay, within the
requested observation window; it does not start or elevate a collector.

For a stop/reconnect diagnostic, start a collector with `--allow-test-shutdown`,
then use a normal client with `--seconds 45 --recover --shutdown-after 10` and
explicitly restart the collector after it stops. The client should show Connected,
Disconnected, then Connected with a new instance GUID and fresh counters. Ctrl+C,
the bounded collector window, and diagnostic shutdown all use the owned ETW stop,
flush, drain and disposal path. The final collector report checks that its exact
session name is absent; it never attaches to or stops unrelated sessions.

JSON files written with `--output FILE` are optional ignored diagnostic reports,
not a transport or live shared-file interface. No usage history persists across
collector restarts. Performance figures are approximate whole-process CPU as a
percentage of one core and working set; controlled validation includes its own
diagnostic overhead. Client metrics distinguish the complete window (including
IPC/JSON initialization) from recurring polling after its first successful snapshot.

For the next phase, recommend **A: a Windows Service**, with an installer, a
restricted dedicated service identity, explicitly validated ETW rights, a service
SID and an administrator-owned executable directory. SCM lifecycle/recovery best
fits always-on tray monitoring without repeated UAC. This has higher installation
complexity and requires explicit capability checks on stripped/custom Windows.
Scheduled helpers have weaker lifecycle/health management; on-demand UAC interrupts
always-on use; delegating ETW rights to the ordinary UI broadens its privilege
boundary and depends on machine policy. Service accounts and narrowly delegated
ETW rights must be validated for this provider before selecting the minimum account;
this prototype does not prove that a low-privilege service token can capture it.
No startup model is installed here.

References: [Microsoft named pipe security](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights),
[ETW access control](https://learn.microsoft.com/en-us/windows/win32/api/evntcons/nf-evntcons-eventaccesscontrol),
and [service startup](https://learn.microsoft.com/en-us/windows/win32/services/service-startup).
