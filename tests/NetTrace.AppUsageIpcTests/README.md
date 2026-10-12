# App Usage 002 deterministic checks

Run from the repository root:

```powershell
dotnet run --project tests/NetTrace.AppUsageIpcTests/NetTrace.AppUsageIpcTests.csproj
```

This separate diagnostic harness tests the framed local IPC protocol, client
failure handling, connection authorization, and collector aggregation. It does
not start an ETW session or access the public internet. Local named-pipe checks
use an explicit test seam for a normal-token server; the production client's
elevated-server verification remains enabled by default.

The snapshot round trip carries only approved process identities, aggregate
byte counters, and capability/health metadata. Endpoints, ports, raw events,
packet contents, and credentials are outside the IPC contract.

Accounting checks distinguish send/receive, address families, loopback,
non-loopback, known local traffic, and unclassified observations. Executable
grouping preserves individual process lifetimes. Snapshots remain cumulative
for one collector instance and start fresh for a new instance.
