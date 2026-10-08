Run the isolated data migration and persistence regression checks from the repository root:

```powershell
dotnet run --project tests/NetTrace.RegressionTests/NetTrace.RegressionTests.csproj
```

The console harness uses the application's existing SQLite dependency and temporary data roots. It never opens the user's application data. It verifies clean initialization, legacy and current unversioned databases, repeat-launch precedence, persisted daily/date-range totals, uncheckpointed WAL migration, and safe rejection of invalid schemas, future versions, corrupt files, locked legacy sources, legacy database paths that are directories, and obstructed destination directories. Legacy file hashes and schema versions are checked for preservation. Test directories are removed after each case.

Actual WPF startup and live traffic sampling are verified separately from these storage checks.
