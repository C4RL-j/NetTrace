# App Usage 005.2 setup UI validation

The deterministic `--app-usage-setup-checks` suite never launches UAC or changes
SCM, ETW or production data. It covers missing, running and stopped states; explicit
install success, cancellation and failure; token/launch boundaries; retained range;
hidden completion; and serialized installation across recreated windows.

The separate workflow below verifies the real page button and existing 005.1
installer. Run it only after ordinary WPF tests finish, from a **normal terminal**
with the complete prebuilt WPF verification output and bundled `AppTracking` kits:

```powershell
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File tools/NetTrace.AppUsageService/Validate-SetupUi.ps1
```

It refuses an existing production or development registration. The normal-token
harness opens a visible native MainWindow at 760 x 600 with isolated physical
monitoring fixtures, selects 30D, verifies the exact missing-service setup text and
button, and checks no installer or IPC range request started automatically. The
workflow explicitly invokes the actual button's routed Click. That button alone
launches the real bundled installer through UAC; there is no initial elevated
install helper or temporary-service preparation.

After the installer succeeds, the same selected 30D page must reconnect without
navigation. Its summary and ordered application rows must match authenticated
production IPC. The harness verifies the desktop token and identity remain normal,
the selected range survives, the install button disappears, and the compact page
has no horizontal overflow. It captures setup and connected-page previews. A
cancelled installation UAC prompt is recorded as cancellation and must return to
the setup state with its short error; the workflow does not retry automatically.

A separate final cleanup UAC prompt elevates only an ownership-guarded uninstall
worker under the same Windows account. The worker compares the recorded exact
registration, LocalSystem identity, 005.1 ownership description, intended client
SID, bundled service kit inventory and protected production image layout before
disabling startup and performing a clean stop. It then uses the
existing 005.1 uninstall tool, which verifies protected manifests and binary
inventory. A database hash is taken only after clean stop and compared after
uninstall. Default history is preserved; no database is copied, removed or reset.
SCM 1060, absent collector process, exact owned ETW session and pipe are checked.
No existing desktop NetTrace process is stopped.

The normal-token harness and page observation are bounded; the elevated installer
is allowed to finish safely if its window closes. Cleanup refuses an unknown or
still-running install outcome instead of racing that child or adopting another
registration. Cancelling the separate cleanup UAC prompt leaves the installed
service in place and is reported as a cleanup issue.

Evidence remains under `bin/AppUsage0052Verification/run-<guid>`: setup/connected
screenshots, normal-token JSON and logs, exact read-only SCM status, uninstall log,
database hashes and final cleanup summary. Verification supports a prebuilt custom
output under that directory through `-WpfBuildDirectory ABSOLUTE_PATH`. The script
does not build, alter icon assets, stage or commit files.

## Recorded result

Passed on 2026-10-13 after the ordinary full WPF suite completed. The harness used
`AllBuilds/NetTrace.WpfTests` and its bundled production service/installer kits.
Evidence: `bin/AppUsage0052Verification/run-c39a9a8fa57c4fdc8a1a149f92c3f172`.

- The real production service was absent before the window opened. The visible
  setup page showed the exact required two text lines and enabled install button.
  No install call or IPC range query occurred before the explicit button action.
- One explicit install succeeded through the existing 005.1 installer. Independent
  read-only status confirmed Running, LocalSystem, Automatic (Delayed Start) and
  the existing 5/15/60-second restart recovery with a 24-hour reset.
- The client token stayed unelevated before and after installation with the same
  Windows user identity. The installer ran separately; no desktop relaunch occurred.
- Selected 30D remained selected throughout and loaded automatically without
  navigation after installation. One authenticated 30D query returned five apps,
  27.19 MB download, 115 KB upload and 27.30 MB logical total. Summary and ordered
  rows matched the typed IPC values. Both setup and connected 760 x 600 previews
  were inspected; neither had horizontal overflow.
- The separate ownership-checked cleanup worker disabled startup, cleanly stopped
  the collector and uninstalled through the existing tool. SCM returned 1060;
  the collector process, exact owned ETW session and pipe were absent.
- The database was preserved byte for byte across uninstall: 12288 bytes, SHA-256
  `C129F656A92819E2F082F3603C30E7F6E2B1878F02A4A23BD04F06FE7AB23397`
  before and after. Existing production history and protected binaries remain.

Actual UAC cancellation was not forced during this successful live install;
cancellation and operational failure are covered by deterministic setup checks.
No existing desktop NetTrace process or unrelated icon edit was modified.
