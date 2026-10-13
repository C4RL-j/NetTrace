# App Usage 004.4.1 Today page validation

`Validate-TodayPage.ps1` provides the explicit live validation workflow. Start it
from a normal terminal. Only its separate SCM worker requests UAC; neither normal
NetTrace startup nor the Today page installs, starts or elevates a service.

The workflow builds separate Release service and WPF harness outputs in the
ignored `bin/AppUsage00441Verification` directory. When the unrelated baseline
ICO issue requires substitution, pass the existing temporary paths with
`-ApprovedBaselineIcon` and `-VerificationTargets`. These MSBuild overrides apply
only to validation output and never modify or include source assets.

The worker refuses an existing development service and stages an exact owned
demand-start LocalSystem image beneath protected ProgramData. Every run uses a
new isolated `--data-root`; the real `%ProgramData%\NetTrace\app-usage.db` is not
opened, adopted, copied or deleted. The original normal-token parent confirms
direct database access is denied, then launches the real `MainWindow` Today page
after a logged successful five-second checkpoint.

The WPF harness wraps the production `AppUsageTodayClient` to count requests.
Every Today value comes through the existing authenticated service pipe. Other
pages use an isolated physical-monitoring fixture to avoid affecting real
history or generating probes. The real window is visible, opaque and unelevated
at 760×600; summaries use the existing cumulative `ByteFormatter`.

The bounded observation checks every returned display name, exact summary
binding, natural cumulative growth, zero App Usage requests during at least eight
seconds on Dashboard, a fresh read after returning, one concurrent query maximum,
compact horizontal bounds and vertical scrolling when the live list overflows.
Top/bottom/returned-page PNG renders and typed JSON remain under the ignored run
directory. The harness generates no downloads or intentional network traffic.
Brave and NetMod examples depend on passive activity; NetMod is an ordinary row
and contributes to the logical application total.

The worker's `finally` cleanup disables, stops and deletes only its owned
registration, verifies SCM 1060, no owned collector process, no exact owned ETW
session and no stale pipe, then removes only the checked isolated staging path
and database. No validation database copy is retained. A final normal-token real
page probe against the absent service must display `App tracking unavailable.`.

Focused WPF tests cover controlled empty/unavailable/loading states, System and
Unknown rows, deterministic ordering, long names, refresh coalescing and
navigation races without requiring the service. Exact application accounting,
query semantics and checkpoint concurrency remain covered by the unchanged
004.3 and 004.2 suites.

Validation completed on 2026-10-13 against the approved `30fce96` baseline:

| Check | Result |
| --- | --- |
| Focused Today WPF/presentation | 75/75 passed |
| Read-only usage queries (004.3) | 71/71 passed |
| Persistence checkpoints (004.2) | 56/56 passed |
| Storage / service / IPC / probe / normal-token probe | 72/72, 62/62, 53/53, 61/61, 22/22 passed |
| Full regression / WPF | 651/651 and 751/751 passed |
| Release builds | All 19 projects passed with zero warnings/errors; final live harness rebuild also passed |
| 760×600 layout | Live and synthetic rows fit horizontally; vertical scrolling and long-name ellipsis passed |
| Normal-token Today client | Unelevated; protected App Usage database access denied; IPC summary and rows bound correctly |
| Passive Today growth | Logical total grew from 5.39 MB to 15.45 MB; final summary: 4 apps, 15.38 MB download, 77.1 KB upload |
| NetMod example | 7.73 MB download, 45.1 KB upload, 7.78 MB total; ordinary logical row |
| Brave example | Not observed during the bounded passive run |
| Hidden page | 8.5007 seconds on Dashboard; request count stayed 2 → 2; returning produced request 3 with fresh data |
| Refresh concurrency | One request maximum; existing UI timer uses the four-second cadence |
| Empty / absent service | Controlled empty page passed; real absent-service page displayed `App tracking unavailable.` |
| Final service cleanup | SCM 1060, zero collector processes, zero owned pipes, no ETW sessions and isolated database removed |

The successful live artifacts are under
`bin/AppUsage00441Verification/run-d04e4032c23d476fa3652feccae4b5db`:
`Today-client.json`, `Absent-client.json`, `client-summary.json`,
`admin-summary.json`, final SCM/session logs and compact page PNGs. The visible
window uses the real `MainWindow` and production IPC client; the isolated physical
fixture is intentionally limited to the other pages. The live navigation helper
moves native tab keyboard focus with selection and checks that Dashboard stays
selected and Today stays invisible throughout the hidden interval.

The unrelated source ICO retained its original working-tree hash and was excluded
from staging. The approved baseline compiler/resource icon override was used only
for validation builds. No persistence, checkpoint, retention, ETW or IPC semantics
were changed for this page.
