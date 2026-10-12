# App Usage 004.1 — persistent daily application usage

This standalone .NET 8 Windows library provides SQLite storage for daily
application aggregates. It reuses NetTrace's `Microsoft.Data.Sqlite` 8.0.11
dependency. The 004.2 service checkpoint integration writes these aggregates;
the diagnostic collector, named-pipe protocol and WPF application do not query it.
No raw ETW events are persisted.

## Database location

The production default is `%ProgramData%\NetTrace\app-usage.db`, resolved through
Windows' common application-data folder. This machine-local path supports the
future LocalSystem service without depending on an interactive user's profile.
It is separate from the existing `%LOCALAPPDATA%\NetTrace\usage.db`; dashboard
and connection history keep their existing database and accounting semantics.

The `DailyAppUsageStore` constructor accepts an alternate common-data root for
deterministic tests. Tests supply owned temporary roots, so they never open the production
database or require administrator privileges. Initialization creates the NetTrace
directory and database within the selected root. This phase does not install a
service or provision machine permissions.

## Schema and migration

The database uses `PRAGMA user_version = 1` and application ID `0x4E544155`
(`NTAU`). An empty version-0 SQLite database is initialized transactionally.
Existing version-1 databases are validated before use. Unknown populated
unversioned databases, newer versions, unexpected user tables/indexes/views/triggers,
and incompatible or corrupt schemas are rejected without replacing their contents.

`daily_app_usage` is a `WITHOUT ROWID` table with one row for each combination of
`local_date` and `application_identity`. It stores:

| Column | Meaning |
| --- | --- |
| `local_date` | Local calendar date, stored as `yyyy-MM-dd` |
| `application_identity` | Stable executable-path identity, System or Unknown |
| `display_name` | Historical display metadata |
| `executable_name` | Historical executable filename, when available |
| `executable_path` | Normalized executable path, when available |
| `download_bytes` | Nonnegative accumulated download bytes |
| `upload_bytes` | Nonnegative accumulated upload bytes |
| `last_seen_utc` | Latest observation timestamp in UTC |

The primary key supports date and date-range queries. The additional
`ix_daily_app_usage_application_date` index covers application identity followed
by local date. Schema changes and usage batches use transactions; statements use
parameters for dates, identities, metadata and byte values.

## Identity and observed dates

Executable identities use a normalized absolute Windows path with a `path:`
prefix and an uppercase key. Case differences and equivalent path separators
therefore aggregate into the same app/day row. Process IDs are not storage keys.
Multiple processes using that executable accumulate together. Stable System and
Unknown / Unattributed identities retain traffic that has no executable identity.

Identity normalization does not require the executable to exist. Saved display
name, executable name and path remain available after a file is deleted. This
phase does not introduce publisher or package identity resolution.

`UsageDelta.FromObservation` converts an observation timestamp using an explicitly
supplied time zone and selects its local calendar date. For example, observations
at 23:59 on October 12 and 00:01 on October 13 belong to separate daily rows.
Process and connection-session start dates have no role in these buckets.

## Operations and failures

Constructing `DailyAppUsageStore` initializes or validates its database;
`Initialize()` can repeat that check explicitly. `AddUsageDelta(localDate,
application, downloadDelta, uploadDelta, lastSeen)` accumulates one observation,
with an explicit observation timestamp, and `AddUsageDeltas` commits a batch
atomically.
Negative deltas are rejected. A signed 64-bit byte-total overflow or failed write
rolls back the whole batch. `LastSeen` retains the latest UTC observation, with
display metadata updated by observations at or after the saved timestamp. An older
observation adds its bytes without replacing newer historical metadata.

`GetUsageForDate` returns one local day's rows. `GetUsageForRange` includes both
date boundaries, so Today uses the same start/end date, 7D uses today minus six
days through today, and 30D uses today minus 29 days through today. These are
generic storage queries without UI period selection or live IPC queries.

Storage failures use `AppUsageStorageException`, with the database path and an
`AppUsageStorageFailure` classification: `OpenFailed`, `IncompatibleSchema`,
`CorruptDatabase`, `TransactionFailed` or `QueryFailed`. Invalid caller arguments,
including negative deltas, use standard argument exceptions before writes begin.
Failed writes retain committed data and surface the failure to the caller.
SQLite uses its default rollback journal; this phase does not configure WAL or
add service retry logic.

## Retention

The initial policy is 90 local calendar days including today. The corresponding
cutoff is today minus 89 days, supplied by `GetRetentionCutoff(today)`.
`RetentionCleanup(cutoffDate)` removes only rows
strictly before the cutoff; the cutoff day is retained. Cleanup is an explicit
operation and has no timer or automatic schedule.

## Verification

Run from the repository root in an ordinary Windows terminal:

```powershell
dotnet run --project tests/NetTrace.AppUsageStorageTests/NetTrace.AppUsageStorageTests.csproj
dotnet run --project tests/NetTrace.AppUsageServiceTests/NetTrace.AppUsageServiceTests.csproj
dotnet run --project tests/NetTrace.RegressionTests/NetTrace.RegressionTests.csproj
dotnet run --project tests/NetTrace.WpfTests/NetTrace.WpfTests.csproj
dotnet build tools/AppUsageStorage/AppUsageStorage.csproj
dotnet build NetTrace.csproj
git diff --check
```

Storage checks use isolated temporary databases and synthetic observations. They
cover creation/migration, aggregation, inclusive date ranges, midnight/time-zone
boundaries, fallback identities, nonnegative values, atomic rollback, retention,
historical metadata and a many-app/many-day size fixture. Existing service and WPF
tests continue to use their established fake sources and owned data. None of
these commands installs the service or enables service-to-database live writes.
