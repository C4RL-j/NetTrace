# App Usage 002 validation

Validated on this Windows machine on 2026-10-12, from clean baseline `056553e`
(App Usage 001), with App Usage 000 at `be63639`. No production NetTrace source,
project dependencies, UI, monitoring, physical accounting or startup behavior changed.

The collector ran under an elevated same-user token. The separate diagnostic
clients and their launching parent ran under actual normal tokens. Production
authentication was enabled during live tests: local native named pipe, SID ACL,
peer token checks and protocol version 1. Two concurrent clients worked.

Controlled TCP IPv4 and IPv6 each retained the child/server PIDs through IPC.
For each child, TX was exactly **65,536 bytes** and RX exactly **131,073 bytes**
(131,072 plus one completion byte). The server's family-specific counters were
the reverse. Both families and loopback classification survived serialization;
unresolved process metadata retained its PID in the Unknown bucket.

The passive Release observation used already-running Brave and NetMod with
`TunnelDetected`. No browser, VPN configuration or public download was started.
Observed process PIDs were Brave 5396 and NetMod 3904. Between the first and last
passive snapshots:

| Process | RX bytes | TX bytes | Loopback RX | Non-loopback RX | IPv6 RX |
| --- | ---: | ---: | ---: | ---: | ---: |
| brave.exe | 5,784 | 2,212 | 0 | 5,784 | 0 |
| NetMod.exe | 29,644,333 | 29,587,927 | 14,984,211 | 14,660,122 | 14,659,992 |

These are logical event-layer observations. Local app traffic and VPN transport
are separate; their sum is not physical adapter usage. No physical-accounting
integration or reconciliation was attempted. All live captures reported zero
controller event loss; loss-query unavailability is separately tested.

With two clients polling, the Release passive window observed approximately
**1,200 ETW events/sec**, collector **10.0% of one core / 52.8 MiB**, and client
**6.8% of one core / 42.8 MiB**, at **0.94 snapshots/sec per client**. Client CPU
after its first successful snapshot was **5.2% of one core**. These short-window
measurements include runtime/diagnostic costs and are not a production overhead
guarantee. The collector forwards aggregate snapshots rather than raw events;
further client overhead reduction remains future work. This traffic rate is much
higher than App Usage 000's 38.3 events/sec baseline.

A client stayed connected while the collector's bounded window expired, reported
Disconnected, and reconnected to an explicitly restarted collector. The instance
GUID changed; uptime reset from 27.64 sec to 2.23 sec and the observed cumulative
event count changed from 35,356 to 2,815. Deterministic tests establish zero initial
counters after restart and rejection of backwards counters on one connection.
The observer survived the second stop as well, with bounded reconnect attempts.
Enabled diagnostic shutdown also acknowledged and stopped the collector cleanly.

Every live collector report confirmed flush/stop/drain/disposal, closed IPC and
absence of its exact owned ETW session after exit. An independent normal-token
`logman query -ets` after all seven exits also found zero App Usage 002 sessions.
Abrupt pipe disconnection is
tested. Forced process termination is not claimed to clean ETW: Windows may retain
that session; crash ownership/recovery needs a production design before deployment.

Verification:

- App Usage 002: **53/53** deterministic tests, including actual DACL access denial,
  local pipe ACL/peer checks, handshake/version, malformed/oversized messages,
  aggregate serialization, timeout/cancellation, disconnection, multiple clients,
  out-of-order health queries, restart semantics and ETW cleanup status.
- Previous App Usage 000: **61/61**; App Usage 001: **22/22**.
- Full regression: **651/651**; WPF integration: **674/674**. The known queued-profile
  timing-sensitive test passed unchanged; no unrelated tests were edited.
- Collector/client Debug and Release, NetTrace, and all relevant test builds:
  **zero warnings/errors**. `git diff --check` passed.

Ignored JSON/log evidence is under `bin/AppUsage002Verification`. It contains
diagnostic aggregate summaries, not credentials or payloads, and is not IPC or
usage-history persistence. The final concurrent controlled smoke test repeated
the exact-byte and cleanup checks after the health-counter ordering fix.

Recommend **A: Windows Service** for the next phase, using a restricted dedicated
identity with validated ETW rights, protected installation and SCM lifecycle/recovery.
See the [workflow and startup tradeoffs](README.md). Nothing was installed here.
