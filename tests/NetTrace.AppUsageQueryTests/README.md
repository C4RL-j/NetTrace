# App Usage 004.3 query checks

Run from a normal unelevated Windows terminal:

```powershell
dotnet run --project tests/NetTrace.AppUsageQueryTests
```

The focused console suite uses fixed local clocks, isolated temporary SQLite databases,
and real authenticated same-user local named pipes. It covers the approved calendar
ranges, exact aggregation, stable sorting, stored metadata, failure states, strict
wire validation, legacy protocol bytes, peer rejection, reconnect, cancellation,
atomic concurrent checkpoints, latency and modest repeated-query resource growth.

The client-side server verifier seam accepts the owned synthetic pipe for tests; the
server still authenticates the actual current-user token. Production service identity
verification is covered by the existing 003 checks and temporary-service validation.
The suite verifies that its client process remains unelevated. It creates no native
ETW session, installs no SCM service, and sends no public network traffic.

Today fixture: Brave 1,000 download / 200 upload; NetMod 500 / 500. Yesterday:
Brave 2,000 / 300. Today is 2,200 logical bytes; 7D and 30D are 4,500. Query
latency and before/after resource measurements print with the test results. Database
roots are removed only after their resolved path is checked against the test parent.
