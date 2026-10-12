# App Usage 004.2 deterministic checkpoint tests

Run `dotnet run --project tests/NetTrace.AppUsageCheckpointTests` with a normal
token. The console suite uses the existing 004.1 SQLite store in unique temporary
roots and controlled in-memory backends. It creates no native ETW session,
service installation, named pipe, production database, or public network traffic.

Coverage includes parser-to-persistence attribution, RX/TX, IPv4/IPv6 locality,
midnight and new collector lifetimes, atomic batched deltas, slow I/O concurrency,
failure/retry, cancellation and bounded final flush, startup retention, health,
pending bounds, and a manually advanced five-second timer.
