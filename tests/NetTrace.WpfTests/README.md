# NetTrace WPF integration tests

The 194 Windows-only STA integration checks use isolated temporary databases and injected
physical/VPN identities. They exercise the existing dashboard, persistence,
profile, power, recovery and ownership behavior together with Task 005A tray and
background lifecycle behavior. They retain the previous 77 WPF lifecycle checks,
adapt the dashboard read-failure case to report an error while monitoring continues,
and add background/tray checks for window recreation, timer/subscription cleanup,
sampling, VPN persistence, power handling and clean shutdown. An owned child WPF
process verifies that dashboard closure keeps its process alive and actual Tray
Exit terminates its application loop and releases database ownership.

Task 005B adds checks for normal/background launch arguments, no dashboard construction
in background mode, live physical/VPN sampling before opening a view, tray restoration,
idempotent initialization and clean exit. Startup checkbox checks inject an in-memory
registration service: they verify immediate updates, read-back after failed or partial
writes, external changes, transient read failures and dashboard recreation. Separate
owned WPF child processes exercise normal and `--background` startup through the real
application loop and Tray Exit. These checks never modify the Windows Run key.

Network Health 001 adds eight background integration checks using an injected ping
source. They verify repeated completed cycles before any dashboard is constructed,
continued probing after dashboard closure, raw measurement access, all-failure loss
without changing connection status, the existing suspend/resume lifecycle and clean
Tray Exit. All existing monitoring fixtures use fake ICMP sources, including child
processes; the harness sends no ICMP traffic to the public internet.

UI Foundation 001 adds 37 checks for all five navigation pages, the active underline,
header hit targets, stable sampler/session state, dashboard summaries, startup
registration and the existing Profiles, Assign Profile and History dialogs. They
also exercise dashboard closure, tray restoration and clean exit through the new
shell. The harness renders each page at the normal and minimum window sizes into
`navigation-previews` beneath its isolated data directory for layout review.

UI Foundation 001.1 adds 20 checks for the latest completed ICMP result on Health, the initial
waiting message, all-failed latency, local sample time and updates through the
existing window refresh timer. Controlled ping replies also verify immediate
refresh on returning to Health, unchanged probe ownership across navigation,
continued tray-only monitoring and the latest result after dashboard recreation.

```powershell
dotnet run --project tests/NetTrace.WpfTests/NetTrace.WpfTests.csproj
```

The harness requires an active Ethernet or Wi-Fi adapter for existing sampler
integration checks. It never modifies Windows VPN settings, machine power
state, or the real NetTrace database. Test power notifications are sent only
to its own hidden lifecycle HWND. Test data is created beneath the test output
directory.
