# App Usage 000 ETW capability probe

This standalone Windows diagnostic tests whether the installed
`Microsoft-Windows-Kernel-Network` provider can support a future per-app prototype.
It never starts with NetTrace, opens its database, changes VPN settings, or persists
app accounting. Normal NetTrace only excludes diagnostic source from compilation.

```powershell
dotnet build tools/AppUsageEtwProbe/AppUsageEtwProbe.csproj
dotnet run --project tools/AppUsageEtwProbe/AppUsageEtwProbe.csproj -- --seconds 20 --vpn-process netmod.exe
dotnet run --project tools/AppUsageEtwProbe/AppUsageEtwProbe.csproj -- --seconds 8 --offline-test --vpn-process netmod.exe
dotnet run --project tests/NetTrace.AppUsageProbeTests/NetTrace.AppUsageProbeTests.csproj
```

Session creation is attempted with the current token. Access denial produces an
explicit elevation requirement and a failed result, without an opaque crash. Run
the built executable in an administrator terminal if required. An elevated-only
run cannot determine whether a non-admin token would have worked; compare its
result with the non-admin run. Ctrl+C cancels and removes only this tool's uniquely
named session. Duration is limited to five minutes.

The probe validates the registered provider GUID and installed manifest at startup.
Installed opcode names determine send/receive; installed keyword names determine
IPv4/IPv6. Validated PID, size, address and port field names/types are matched to
observed TDH-decoded payloads. Event IDs, offsets and process-header attribution
are not assumed. Retransmit, copied data and connection lifecycle events do not
enter byte totals. Eight representative raw metadata samples are bounded; addresses
and ports stay in memory. Missing process paths, expired PIDs and System/unattributed
events remain explainable. Executable identities retain separate PID/start-time
observations; unresolved name identities are provisional, unsuitable for durable history.

`Microsoft.Diagnostics.Tracing.TraceEvent` **3.2.8** is confined to this tool. It
provides maintained realtime ETW session ownership and TDH decoding, avoiding a
custom native consumer. The Windows desktop framework supplies installed provider
metadata. No packet capture, payloads, URLs, DNS queries or credentials are collected.

The offline option starts owned TCP listeners and separate controlled child
processes on IPv4 and IPv6 loopback. Each uploads 65,536 bytes and receives 131,072
bytes plus one completion byte. The report verifies actual ETW client PID, direction,
exact bytes and loopback classification, separately from deterministic injected tests.
Local TCP behavior is evidence of plumbing, not physical-network equivalence.

Default physical adapter selection follows NetTrace's automatic physical identity
preference; `--adapter ID` selects the same hardware interface if the UI was manually
changed. The diagnostic links existing source files and calls the unchanged
`NetworkMonitor.Capture` counter implementation. Counter timestamps define the ETW
window; reset, disconnect, identity changes and overflow invalidate comparison.
No NetTrace monitoring service, sampler or session tracker is constructed.

Console and JSON distinguish all logical events, loopback, known local-to-local,
non-loopback, unknown endpoints, designated VPN traffic, and physical adapter deltas.
IPv4-mapped loopback and the entire 127/8 range are recognized. Local classification
requires both endpoints to be known local addresses; remote VPN addresses are not
guessed. `--vpn-process NAME.exe` supplies a user-identified process; executable-name
heuristics are only unconfirmed candidates. Brave's local IPv4/IPv6 bytes and the
designated VPN's external IPv4/IPv6 bytes show whether the expected pattern coexists.
Coexistence exposes double-counting risk but does not prove individual connection
pairing, proxy ownership or a reliable deduplication rule. Owned test traffic is
excluded from that finding.

A coverage percentage is withheld when the VPN is active/uncertain or selected
interface attribution is missing. Network events can represent multiple logical
and transport layers; protocol overhead and retransmissions also differ from
adapter counters. These numbers are not carrier-billed usage. Physical counters
remain the basis for mobile-link usage; per-app telemetry answers application
attribution; VPN transport reports the outer layer. They must not be summed together.

The default JSON is `bin/.../capability-result.json`; `--output FILE` and
`--log FILE` support temporary diagnostics and hidden elevated validation. Generated
outputs are ignored and must never be committed. The JSON includes installed schemas,
bounded metadata samples, the top 50 process lifetimes, the top 30 applications,
omission counts, loss/shutdown evidence, transfer checks, independent byte buckets,
VPN caveats, and approximate CPU, working set and event rate. CPU is the tool's
average percent of one core including startup/draining, not total-system overhead.

Success requires consumed bytes, meaningful process attribution, both directions,
zero event loss and verified session removal. Unsupported capabilities or permission
failures are reported honestly. A successful probe recommends **GO WITH LIMITATIONS**;
the lack of interface/layer attribution still requires design work before production.
