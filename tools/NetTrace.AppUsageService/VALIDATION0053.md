# App Usage 005.3 Settings management validation

Settings manages the existing production registration, `NetTraceAppUsageCollector`.
Its status read uses the existing unelevated SCM query. Opening Settings and the
existing window timer refresh the status while Settings is visible. No new timer,
automatic install/uninstall or desktop elevation is introduced.

The confirmed Uninstall action launches the same bundled 005.1 installer with
the single literal argument `uninstall` and `runas`. The native confirmation
defaults to No and tells the user that recorded usage history is kept. The
uninstaller's configuration, ownership checks, clean stop, exact ETW cleanup and
database preservation remain unchanged.

Success immediately shows Not installed in Settings and invalidates App Usage's
cached view and in-flight generations. App Usage becomes its existing setup
state without an application restart. Today/7D/30D selection survives, including
when uninstall finishes while the page is hidden. Hidden App Usage does not send
IPC requests. A cancelled UAC prompt or failed uninstall shows a short error and
permits another confirmed attempt after a fresh status read.

## Deterministic checks

```powershell
dotnet run --project tests/NetTrace.WpfTests -- --app-tracking-settings-checks
dotnet run --project tests/NetTrace.WpfTests -- --app-usage-setup-checks
dotnet run --project tests/NetTrace.WpfTests -- --app-usage-range-checks
dotnet run --project tests/NetTrace.WpfTests -- --app-usage-today-checks
dotnet run --project tests/NetTrace.WpfTests
dotnet run --project tests/NetTrace.RegressionTests
dotnet run --project tests/NetTrace.AppUsageInstallerTests
git diff --check
```

Settings checks use injected status, confirmation and uninstall boundaries. Real
WPF controls run with owned physical-monitoring fixtures, without changing SCM,
launching UAC or reading/writing production history. They cover installed states,
button availability, confirmation cancellation, success, cancellation/failure,
retry, no automatic launch, normal desktop identity/token, page transition and
range retention. Existing installer tests verify history preservation byte for
byte in successful and failed uninstall paths.

All 21 project builds use Release, the unchanged source ICO and separate output
directories under ignored `bin/AppUsage0053Verification/AllBuilds`:

```powershell
dotnet build <project.csproj> -c Release --nologo -o <absolute-project-output>
```

Projects build sequentially to avoid shared intermediate-output races. Nine
non-UI suites run their built executables directly from those outputs. Logs and
JSON summaries remain under the ignored verification directory. No files are
staged or committed.

## Live-validation boundary

At the beginning of this change, both production and development collector
registrations were absent. A real Settings uninstall requires an installed
collector and an explicitly accepted UAC prompt; this change's ordinary checks
do not create a service to manufacture that condition. No live UAC uninstall is
run for 005.3. Real WPF interaction with owned fixtures exercises the control and
page transition; elevated process launch and cancellation are injected.

The retained production database is protected against direct normal-token
access. Its actual hash is not read or newly verified in this run. The desktop
continues to use authenticated IPC for usage; it gains no direct database access.
Preservation is covered by the unchanged installer tests and the previously
recorded real 005.1/005.2 uninstall evidence. This is a validation boundary, not
an implementation dependency or an automatic elevation trigger.

## Changed files for 005.3

- `MainWindow.AppTracking.cs` (new), `MainWindow.xaml`, `MainWindow.xaml.cs`.
- `Presentation/AppTrackingSettingsController.cs` (new),
  `Presentation/AppTrackingSettingsPresentation.cs` (new),
  `Presentation/AppUsageTodayController.cs`.
- `Services/AppTrackingSetupService.cs`, `Services/IAppTrackingSetupService.cs`.
- `tests/NetTrace.WpfTests/AppTrackingSettingsChecks.cs` (new),
  `tests/NetTrace.WpfTests/Program.cs`, `tests/NetTrace.WpfTests/README.md`.
- `README.md`, `tools/NetTrace.AppUsageService/README.md`, this validation document.

Earlier uncommitted App Usage 004.4.2, 005.1 and 005.2 work is the baseline.
Unrelated ICO work is left untouched.

## Recorded result

Validated on 2026-10-13. Focused Settings checks passed 73/73, setup 58/58,
range switching 70/70 and Today 75/75. All 21 normal Release project builds
passed with zero warnings and errors. The compact owned Settings previews were
inspected: the retry state showed Running, an enabled uninstall action and short
feedback; Not installed hid the button and error. Neither preview had horizontal
overflow. Evidence is beneath `bin/AppUsage0053Verification`, including
`Focused/owned-app-tracking-settings-6946e3c83c26424baefc4b27bb55d52f` and the
per-project `AllBuilds` logs and JSON summaries.

The nine non-UI suites passed: regression 651/651; read-only queries 71/71;
checkpoints 56/56; storage 72/72; service lifecycle 67/67; IPC 53/53; probe 61/61;
non-elevated probe 22/22; installer 53/53. The eight App Usage suites total 455/455,
including unchanged uninstall/database-preservation coverage. Source ICO SHA-256
remained `3CD7CB62D9DFE9207A33AE3745D2E5F4F7852B6A00C6A5AC980EB789C11CC011`,
HEAD remained `e43b835e94647cd578b48e8e15ee030d1c61a416`, and the staged diff was
empty. XAML parsing and tracked/new-file whitespace checks passed.

The full WPF suite passed 952/952 on an unchanged retry from the authoritative
`AllBuilds/NetTrace.WpfTests` output. The first full run stopped after 262 checks
at an existing ICMP quality assertion about returning to Health and the newest
twelve chart/history samples. That assertion passed on the unchanged retry;
neither Health production code nor its checks were edited. Both logs are kept:
`bin/AppUsage0053Verification/full-wpf.log` and `full-wpf-retry.log`.

No actual UAC uninstall was run and no new protected production database hash
is claimed. The actual Settings controls, retry and missing-service transitions
were exercised in the same normal-token owned window. Uninstall preservation
continues to use the unchanged 005.1 command and passed its installer suite.
