using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using NetTrace;
using NetTrace.Models;
using NetTrace.Services;

// Explicit opt-in only. Real identity and passive adapter reads use an owned database;
// health transports are fakes and the user's profiles and network settings are untouched.
internal static class LiveProfileValidation
{
    public static int Run()
    {
        MainWindow? main = null;
        ProfileManagerWindow? profiles = null;
        MonitoringService? monitoring = null;
        var root = System.IO.Path.Combine(AppContext.BaseDirectory, "profiles-live-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (Application.Current is null)
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var store = new UsageStore(root);
            monitoring = new MonitoringService(store, new NetworkIdentityService(),
                vpnVerifier: new VpnRouteVerifier(new VpnDetectionService(new NoTunnel()), new NoRoutes()),
                networkMonitor: new NetworkMonitor(), icmpMonitor: new IcmpMonitor(new NoPing()),
                reachabilityMonitor: TestReachability.Create());
            monitoring.Start();
            Wait(() => monitoring.GetSnapshot().Connection.Status != ConnectionStatus.Detecting);
            var initial = monitoring.GetSnapshot();
            if (initial.Connection.Network is not { HasValidIdentifier: true } network ||
                initial.Connection.Status != ConnectionStatus.ConnectedUnassigned)
            {
                Console.WriteLine("Live profile validation skipped: " +
                    (initial.Connection.Message ?? "No usable current connection identity."));
                Console.WriteLine("No real profile data or network configuration was changed.");
                return 0;
            }

            main = new MainWindow(monitoring, new ReadOnlyStartup())
                { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            main.Show();
            Control<TabControl>(main, "MainNavigation").SelectedItem = Control<TabItem>(main, "ConnectionsTab");
            profiles = new ProfileManagerWindow(store: monitoring.Profiles, monitoring: monitoring)
                { Owner = main, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            profiles.ProfilesChanged += (_, _) => monitoring.RefreshRecognition();
            profiles.Show();
            Control<TextBox>(profiles, "TelcoNameBox").Text = "Live validation";
            Control<TextBox>(profiles, "DeviceNameBox").Text = "Temporary profile";
            Click(profiles, "SaveProfileButton");
            var profile = monitoring.Profiles.GetProfiles().Single();
            if (!Control<Button>(profiles, "AssignNetworkButton").IsEnabled)
                throw new InvalidOperationException("The latest current connection must be available to map.");
            Click(profiles, "AssignNetworkButton");
            Wait(() => monitoring.GetSnapshot().Connection.Profile?.Id == profile.Id &&
                Control<TextBlock>(main, "AdapterProfileText").Text == profile.DisplayName);
            var mapping = monitoring.Profiles.GetMappings(profile.Id).Single();
            if (mapping.NetworkType != network.NetworkType || mapping.NetworkIdentifier != network.NetworkIdentifier)
                throw new InvalidOperationException("The current connection changed during live validation.");
            Console.WriteLine($"Live mapping passed: {network.DisplayName} → {profile.DisplayName}; main Connections resolved the profile.");

            Control<ListBox>(profiles, "MappingsList").SelectedItem =
                Control<ListBox>(profiles, "MappingsList").Items.OfType<NetworkMapping>().Single(item => item.Id == mapping.Id);
            Click(profiles, "RemoveMappingButton");
            Wait(() => monitoring.GetSnapshot().Connection.Status == ConnectionStatus.ConnectedUnassigned &&
                Control<TextBlock>(main, "AdapterProfileText").Text == "Unassigned");
            if (monitoring.Profiles.GetMappings(profile.Id).Count != 0 ||
                monitoring.GetSnapshot().Session?.Id != initial.Session?.Id)
                throw new InvalidOperationException("Live mapping removal must retain the same connection session.");
            monitoring.Profiles.DeleteProfile(profile.Id);
            monitoring.RefreshRecognition();
            Console.WriteLine("Temporary mapping removed and temporary profile deleted; connection session retained.");
            Console.WriteLine($"Owned validation data: {root}. The user's real profile database was never opened.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Live profile validation failed: " + exception.Message);
            return 1;
        }
        finally
        {
            try { profiles?.Close(); }
            finally
            {
                try { main?.Close(); }
                finally { monitoring?.Dispose(); }
            }
        }
    }

    private static T Control<T>(Window window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"Missing profile control {name}.");

    private static void Click(Window window, string name) =>
        Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static void Wait(Func<bool> predicate)
    {
        var elapsed = Stopwatch.StartNew();
        while (!predicate())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("Live profile presentation did not reach its expected state.");
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }

    private sealed class NoPing : IPingSource
    {
        public Task<IcmpPingReply> SendAsync(IPAddress target, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new IcmpPingReply(IPStatus.TimedOut));
    }

    private sealed class NoTunnel : IVpnInterfaceSource { public VpnInterfaceInventory Read() => new([], []); }
    private sealed class NoRoutes : IVpnRouteSource
    {
        public VpnRouteInventory Read(AddressFamily family) => new(family, [], Availability: IpFamilyAvailability.Unavailable);
    }

    private sealed class ReadOnlyStartup : IWindowsStartupRegistration
    {
        public bool IsEnabled() => false;
        public void Enable() => throw new InvalidOperationException("Live validation does not modify startup registration.");
        public void Disable() => throw new InvalidOperationException("Live validation does not modify startup registration.");
    }
}
