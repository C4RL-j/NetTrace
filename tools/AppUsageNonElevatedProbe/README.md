# App Usage 001 non-elevated telemetry probe

This standalone diagnostic tests the installed Network-DataUsage and TCPIP
providers using the current **unelevated** token. It rejects elevated execution.
It never asks for UAC, changes permissions/groups/services, restores SRUM, or
adds monitoring, persistence, startup integration or UI to NetTrace.

Run from a normal terminal at the repository root:

```powershell
dotnet build tools/AppUsageNonElevatedProbe/AppUsageNonElevatedProbe.csproj
dotnet run --project tools/AppUsageNonElevatedProbe/AppUsageNonElevatedProbe.csproj -- --seconds 20
dotnet run --project tests/NetTrace.NonElevatedProbeTests/NetTrace.NonElevatedProbeTests.csproj
```

The diagnostic references App Usage 000 solely for its existing process lookup,
local TCP workload, passive adapter/VPN inventory and diagnostics-only TraceEvent
dependency. It does not rewrite or launch the elevated Kernel-Network consumer.
Normal NetTrace already excludes all `tools` source files from compilation; its
runtime and dependencies remain unchanged. No new NuGet dependency is introduced.

For each candidate, installed `ProviderMetadata` must match the requested GUID.
The manifest supplies actual event IDs, versions, task names, payload field names
and types, and keyword values. Task semantics plus registered direction keywords
validate transfer events; payload indexes and event numbers are not assumed.
Schema possibilities and observed capabilities are separate evidence.

The installed Network-DataUsage catalog can contain helper/response-time events
with empty templates. Empty payload events are still counted if reception is
permitted. An empty template or denied capture does **not** prove that live events
depend on SRUM or that the provider emits nothing. The probe never accesses the
historical Windows usage database.

TCPIP UDP message-transfer schemas can directly expose PID, `NumBytes`, direction
and socket endpoints. TCP transfer schemas use `Tcb` without PID; correlation
with connection/lifetime events would be separate design work. The probe does
not implement that correlation. Cumulative connection summaries, retransmission,
offload, congestion, requests and connection lifecycle events are not added as
transferred-byte accounting. Lifecycle PID alone is only partial evidence.

Session creation, provider enablement and consumer attachment are reported
separately. An access-denied creation result establishes that this normal token
cannot independently create the required session under unchanged permissions;
it does not establish a provider-specific permission failure or a universal
Windows requirement. Windows can delegate ETW permissions, but this phase does
not change any such policy. No existing sessions are attached to or stopped.

The same owned local TCP workload runs for both candidates: IPv4 then IPv6,
separate controlled child PID, 65,536 uploaded bytes and 131,072 downloaded bytes
plus one completion byte. These transfers still run when ETW startup is denied,
so the report distinguishes a successful workload from unobserved telemetry.
Client and server PID, both directions, exact bytes and loopback are checked for
every permitted consumer. With both consumers blocked, the passive observation
is unavailable; the tool does not wait out a fictitious live window. A permitted
window otherwise includes passive existing traffic. It sends no public requests,
does not launch Brave or NetMod, and does not change VPN configuration.

Only explicitly selected scalar metadata and typed endpoint blobs are decoded.
Endpoints stay in memory; console/JSON never contain addresses, ports, DNS contents,
URLs, credentials, packet payloads or arbitrary payload strings. Up to eight
metadata samples, 40 representative schemas and 30 process lifetimes are saved.
Header PID cannot replace the payload PID. Complementary incomplete occurrences
cannot manufacture coherent attribution. Unresolved process identity is provisional.

The default JSON is `bin/.../non-elevated-result.json`. `--output FILE` selects
another temporary diagnostic location. Generated results are ignored and must
not be committed. Approximate whole-diagnostic CPU/working set and permitted
event rates are recorded; failed startup is not an always-on overhead measurement.
Ctrl+C cancels the owned work and cleans up only this tool's sessions/processes.

`--baseline FILE` reads a previously recorded elevated App Usage 000 JSON result;
by default this is `tools/AppUsageEtwProbe/bin/live-result.json`. The report verifies
its required capabilities, positive traffic, zero event loss and clean session
removal. It clearly labels this as a separate prior observation, never a simultaneous
comparison. No fresh elevated run is performed. If baseline evidence is absent,
the diagnostic reports inconclusive and returns nonzero rather than guessing.
Use App Usage 000's already-approved saved result as the capability reference.

Provider classification requires live evidence: **FullySuitable** needs coherent
attributed directional bytes and successful controlled checks; **PartiallySuitable**
has useful activity with missing accounting/reliability; **Unsuitable** includes
unavailable providers, denied consumption, no events or irrelevant events. A
trustworthy negative capability result can exit successfully: success means the
investigation is complete, not that the provider supports per-app usage.

When the candidates are blocked but Kernel-Network's recorded capability baseline
is reliable, the recommendation is **C: elevated helper required under current
permissions**. A future helper should have no UI, the minimum ETW privileges,
metadata-only capture and a narrow authenticated local IPC interface exposing
counters/status, with no arbitrary commands or packet payloads. The ordinary tray
UI stays unelevated. App Usage 001 implements none of that architecture.

Physical adapter usage, per-app logical usage and VPN transport remain separate
layers. This phase characterizes visibility and permissions; it does not reconcile
or deduplicate their bytes.
