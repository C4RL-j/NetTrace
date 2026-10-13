Run these deterministic tests from an ordinary, unelevated terminal:

```powershell
dotnet run --project tests/NetTrace.AppUsageServiceTests/NetTrace.AppUsageServiceTests.csproj
```

The suite uses fake ETW sessions, process-lifetime leases, a manual monotonic clock,
and actual owned local named pipes. It exercises shared collector runtime and
service host lifecycle, bounded cancellation/disposal, suspend/resume design,
fresh counter lifetimes, bounded process cache and PID reuse, exact stale-session
eligibility, protocol 1/2 compatibility, health/loss serialization, malformed and
unauthorized peers, disconnect/reconnect and finite backoff.
The production cases additionally check the exact production/development service
identity boundary, production service SID and pipe ACL, host option propagation,
invalid host options, and rejection of an unrelated pipe as a production peer.

It does not install or control a service, start native ETW, stop any trace session,
change system permissions, or suspend the machine. Real SCM operations, built-in
identity capability, forced-kill/stale-session recovery, effective service pipe
permissions, and final removal belong to the explicit elevated development
workflow in `tools/NetTrace.AppUsageService/Validate-Service.ps1`. Passing these
tests does not establish that real Windows suspend/resume was validated.
