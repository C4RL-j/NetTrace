Run the isolated data migration and persistence regression checks from the repository root:

```powershell
dotnet run --project tests/NetTrace.RegressionTests/NetTrace.RegressionTests.csproj
```

The console harness uses the application's existing SQLite dependency and temporary data roots. It never opens the user's application data. Test directories are removed after each case.

The original Task 001 checks remain: clean initialization, legacy and current unversioned databases, repeat-launch precedence, persisted daily/date-range totals, uncheckpointed WAL migration, and safe rejection of invalid schemas, future versions, corrupt files, locked legacy sources, legacy database paths that are directories, and obstructed destination directories. Legacy file hashes and schema versions are checked for preservation. Successful startup now expects schema version 2; unsupported future-version fixtures use version 3.

Task 002 checks cover transactional v1 → v2 upgrades without replacing the usage table, rollback after a conflicting schema, profile/mapping persistence, multiple SSIDs per profile, unknown networks staying unassigned, duplicate and conflicting assignments, edits preserving recognition, required names, exact SSID case/whitespace, parameterized user text, atomic create-and-assign rollback, and restricted profile deletion. The native SSID decoder is exercised with UTF-8, invalid UTF-8 and control-byte fixtures to verify identities cannot collapse. A fake network identity source also checks Wi-Fi/unknown/disconnected/Ethernet transitions, suppressed repeat notifications, immediate assignment/edit refresh, detection failure diagnostics, disposal, and current-state reads during a deliberately blocked native query. These checks do not require changing the PC's actual connection.

Task 002.1 checks distinguish recognized, valid unassigned, connected identity unavailable, and disconnected states. Simulated Windows permission/API failures cannot expose assignment or consult a mapping, even when the source retains a stale SSID. Empty or malformed identities are rejected before any database lookup; nonempty whitespace SSIDs retain their exact identity. Repeated permission failures do not repeat UI state notifications, usage totals persist during unavailability, and a recovered real SSID can be assigned to an existing profile. The complete harness contains 39 regression checks.

Actual WPF startup and live traffic sampling are verified separately from these storage checks.
