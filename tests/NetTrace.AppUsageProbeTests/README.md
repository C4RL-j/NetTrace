# App Usage 000 deterministic checks

Run from the repository root:

```powershell
dotnet run --project tests/NetTrace.AppUsageProbeTests/NetTrace.AppUsageProbeTests.csproj
```

This separate console harness runs 61 checks against the diagnostic probe without starting an
ETW session or requiring administrator rights. Automated traffic stays on the
local machine; the checks do not contact the public internet. A failed check
produces a nonzero exit code and names the failing condition.

Coverage includes schema-driven event parsing, payload PID attribution, send and
receive aggregation, IPv4 and IPv6, loopback and known local endpoints,
executable grouping with process lifetime identity, unknown processes, byte
formatting, invalid counts, unsupported events, and cancellation/shutdown.
Pure physical-counter comparison checks cover counter resets, interface changes,
numeric overflow, and cases where a VPN or missing interface evidence makes a
coverage ratio misleading.
Capture boundary checks use the exact physical counter read timestamps and
verify that delayed events remain inside that window. Interface coverage checks
use non-loopback evidence separately from loopback and local proxy observations.

The controlled local TCP child-process check validates the traffic generator's
known byte volumes and process separation. Actual ETW reception during that
transfer is validated separately by the diagnostic tool's `--offline-test`
capability run and may require elevation on the current Windows installation.
