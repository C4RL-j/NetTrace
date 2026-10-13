# App Usage 006.1 — Application row icons and duplicate names

This change adds presentation metadata to App Usage rows. The collector,
service management, ETW, persistence, IPC, accounting, Dashboard and connection
History keep their existing behavior. No service operations or UAC are used.

## Implementation

- A 24x24 icon with a 10-pixel gap precedes the existing row title and subtitle.
  Existing totals and row statistics keep their layout.
- Local executable icons are read on background tasks with at most three native
  extractions in progress. Frozen WPF images are shared through a normalized,
  case-insensitive path cache, including pending requests and failed extractions.
  The cache keeps up to 1024 paths for the app session; additional paths use the
  fallback. Relative, network and device paths use fallback without shell access.
- Generic application, System and Unknown / Unattributed vector icons match the
  dark palette. A fallback appears immediately while extraction runs. Native
  icon handles are disposed after their pixels have been copied and frozen.
- Titles prefer the supplied friendly name, falling back to the executable name.
  Subtitles hide when empty or equal to the title after trimming and ignoring
  case. System and Unknown / Unattributed show only their canonical title.
- Icon completion updates only its originating row on the dispatcher. It cannot
  overwrite a newer selected range. Existing controller, query and timer sources
  remain unchanged.

## Verification

Release verification output and logs are under ignored
`bin/AppUsage0061Verification`. Builds use separate output directories because
the manually opened NetTrace process is left running.

```powershell
dotnet build tests/NetTrace.WpfTests/NetTrace.WpfTests.csproj -c Release --nologo -v:minimal -o bin/AppUsage0061Verification/NetTrace.WpfTests
dotnet build NetTrace.csproj -c Release --nologo -v:minimal -o bin/AppUsage0061Verification/NetTrace
dotnet build tests/NetTrace.RegressionTests/NetTrace.RegressionTests.csproj -c Release --nologo -v:minimal -o bin/AppUsage0061Verification/NetTrace.RegressionTests

& bin/AppUsage0061Verification/NetTrace.WpfTests/NetTrace.WpfTests.exe --app-usage-row-icon-checks
& bin/AppUsage0061Verification/NetTrace.WpfTests/NetTrace.WpfTests.exe --app-usage-today-checks
& bin/AppUsage0061Verification/NetTrace.WpfTests/NetTrace.WpfTests.exe --app-usage-range-checks
& bin/AppUsage0061Verification/NetTrace.WpfTests/NetTrace.WpfTests.exe
& bin/AppUsage0061Verification/NetTrace.RegressionTests/NetTrace.RegressionTests.exe

git diff --check
```

| Check | Result |
| --- | --- |
| App Usage row icon/name checks | 78/78 passed |
| Existing Today checks | 75/75 passed |
| Existing range checks | 70/70 passed |
| Full WPF suite | 1283/1283 passed |
| Full regression suite | 651/651 passed |
| WPF, application and regression Release builds | Passed, zero warnings/errors |
| Tracked and new-file whitespace checks | Passed |
| Protected sources and unrelated ICO | All 69 baseline hashes unchanged |

The 760x600 preview is generated beneath
`bin/AppUsage0061Verification/NetTrace.WpfTests/owned-app-usage-row-icons-*/app-usage-row-icon-previews/760x600-icons-and-subtitles.png`.
Inspection confirms icon alignment, readable summary totals and the existing
vertical scroll behavior. Tests inspect duplicate, System, Unknown and long-name
rows, verify compact horizontal bounds and switch all three ranges while checking
the rows and totals. Native extraction from the existing Windows notepad
executable passed. No issues were found.

No commit is made. Changes from earlier App Usage phases remain in the working
tree and are excluded from this phase's change list.

## Files changed in 006.1

- `MainWindow.AppUsage.cs`
- `MainWindow.xaml`
- `Presentation/AppUsageTodayPresentation.cs`
- `Services/AppUsageIconCache.cs` (new)
- `tests/NetTrace.WpfTests/AppUsageRowIconChecks.cs` (new)
- `tests/NetTrace.WpfTests/Program.cs`
- `tests/NetTrace.WpfTests/README.md`
- `tests/NetTrace.WpfTests/VALIDATION0061.md` (new)
- `README.md`
