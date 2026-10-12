# App Usage 001 deterministic checks

Run from the repository root:

```powershell
dotnet run --project tests/NetTrace.NonElevatedProbeTests/NetTrace.NonElevatedProbeTests.csproj
```

This diagnostic console harness runs 22 checks for provider suitability without starting
an ETW session or sending network traffic. It does not require administrator
rights or access to the public internet.

Classification checks cover unavailable providers, permission denial, missing
events, missing PID, byte count or direction fields, full suitability, partial
suitability, and unsuitable telemetry. Required accounting fields must belong to
the same observed event; separate incomplete events cannot be combined to claim
a complete capability.

Observation checks verify that an event-header PID cannot substitute for missing
payload attribution, and that incomplete occurrences of one schema cannot
synthesize coherent accounting. Injected consumer checks cover normal-token
permission stages, elevated-token rejection, empty sessions, and cancellation.
A failed check names its condition and returns a nonzero exit code.

Actual candidate-provider access and controlled local TCP visibility are
validated separately through the non-elevated diagnostic probe.
