using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using NetTrace;
using NetTrace.Models;
using NetTrace.Services;

internal static class ProfileManagementChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string AdapterId = "profiles-owned-wifi";
    private const string CurrentNetwork = "PROFILES_CURRENT";

    public static void Run(string root, Action<bool, string> check)
    {
        using var fixture = new Fixture(System.IO.Path.Combine(root, "profile-management"));
        CheckEditor(fixture, root, check);
        CheckMappingsAndDelete(fixture, root, check);
        CheckLifecycle(fixture, check);
        CheckStandalone(root, check);
    }

    private static void CheckEditor(Fixture fixture, string root, Action<bool, string> check)
    {
        var manager = fixture.OpenManager();
        try
        {
            SelectProfile(manager, fixture.First.Id);
            check(Control<ListBox>(manager, "ProfilesList").Items.Count == 2 &&
                Control<TextBox>(manager, "TelcoNameBox").Text == "Alpha ISP" &&
                Control<TextBox>(manager, "DeviceNameBox").Text == "First router" &&
                Text(manager, "EditorStateText") == "Editing profile" &&
                Text(manager, "PreviewText") == "Alpha ISP — First router" &&
                Button(manager, "SaveProfileButton").Content?.ToString() == "Save profile",
                "Profiles opens with its saved list, selected editor fields and explicit Editing profile state");
            check(Button(manager, "AssignNetworkButton").Content?.ToString() == "Map current connection" &&
                Button(manager, "AssignNetworkButton").IsVisible && Button(manager, "AssignNetworkButton").IsEnabled &&
                Button(manager, "DeleteProfileButton").IsEnabled && !Button(manager, "RemoveMappingButton").IsEnabled,
                "A clean saved profile exposes mapping and deletion while removal requires a selected mapping");
            SavePreview(manager, root, "existing-profile");
            SavePreview(manager, root, "no-mapping-selected");
            var list = Control<ListBox>(manager, "ProfilesList");
            var editor = Control<TextBox>(manager, "TelcoNameBox");
            var listPosition = list.TranslatePoint(new Point(), manager);
            var editorPosition = editor.TranslatePoint(new Point(), manager);
            check(list.ActualWidth >= 150 && editor.ActualWidth >= 150 && listPosition.X + list.ActualWidth < editorPosition.X &&
                manager.MinWidth == 620 && manager.MinHeight == 500 && Button(manager, "CloseButton").IsVisible,
                "Profiles retains a compact left list and right editor with a usable Close action and the existing utility minimum size");

            var count = fixture.Profiles.GetProfiles().Count;
            Click(manager, "NewProfileButton");
            check(Control<ListBox>(manager, "ProfilesList").SelectedItem is null &&
                Control<TextBox>(manager, "TelcoNameBox").Text == "" && Control<TextBox>(manager, "DeviceNameBox").Text == "" &&
                Control<ListBox>(manager, "MappingsList").Items.Count == 0 && Text(manager, "EditorStateText") == "New profile" &&
                Button(manager, "SaveProfileButton").Content?.ToString() == "Create profile" &&
                !Button(manager, "SaveProfileButton").IsEnabled && !Button(manager, "AssignNetworkButton").IsEnabled &&
                !Button(manager, "DeleteProfileButton").IsEnabled && !Button(manager, "RemoveMappingButton").IsEnabled &&
                fixture.Profiles.GetProfiles().Count == count,
                "New profile clears selection, editor and mappings without writing a row or enabling saved-profile actions");
            SavePreview(manager, root, "new-state");
            SetNames(manager, "  New ISP  ", "  New device  ");
            check(Text(manager, "PreviewText") == "New ISP — New device" && Button(manager, "SaveProfileButton").IsEnabled &&
                !Button(manager, "AssignNetworkButton").IsEnabled && !Button(manager, "DeleteProfileButton").IsEnabled,
                "A valid unsaved draft previews trimmed names and enables creation without exposing mapping or deletion");
            Click(manager, "SaveProfileButton");
            var created = SelectedProfile(manager)!;
            var reloaded = new ProfileStore(fixture.Store).GetProfile(created.Id);
            check(reloaded is { TelcoName: "New ISP", DeviceName: "New device" } &&
                fixture.Profiles.GetProfiles().Count == count + 1 && Text(manager, "EditorStateText") == "Editing profile" &&
                fixture.Profiles.GetMappings(created.Id).Count == 0 && manager.IsVisible,
                "Creating a profile persists trimmed names, selects the saved row and keeps general profile management open without auto-mapping");
            Click(manager, "NewProfileButton");
            SetNames(manager, "New ISP", "New device");
            Click(manager, "SaveProfileButton");
            check(SelectedProfile(manager)!.Id != created.Id && fixture.Profiles.GetProfiles().Count == count + 2,
                "Profile creation preserves the existing rule allowing separate profiles with duplicate display names");

            SelectProfile(manager, fixture.First.Id);
            SetNames(manager, "Alpha Updated", "Updated router");
            check(Button(manager, "SaveProfileButton").IsEnabled && !Button(manager, "AssignNetworkButton").IsEnabled &&
                Button(manager, "DeleteProfileButton").IsEnabled && fixture.Profiles.GetProfile(fixture.First.Id)!.TelcoName == "Alpha ISP",
                "Editing a saved profile preserves its identity, keeps changes local and requires saving before mapping");
            var mappings = fixture.Profiles.GetMappings(fixture.First.Id).Select(mapping => mapping.Id).ToArray();
            Click(manager, "SaveProfileButton");
            Wait(() => fixture.Monitoring.GetSnapshot().Connection.Profile?.DisplayName == "Alpha Updated — Updated router");
            check(SelectedProfile(manager)!.Id == fixture.First.Id &&
                fixture.Profiles.GetMappings(fixture.First.Id).Select(mapping => mapping.Id).SequenceEqual(mappings) &&
                fixture.Monitoring.GetSnapshot().Session!.Id == fixture.SessionId &&
                Text(fixture.Window, "AdapterProfileText") == "Alpha Updated — Updated router" &&
                ReferenceEquals(fixture.Baseline, Baseline(fixture.Monitoring)),
                "Saving profile edits updates the same row and Connections profile while preserving mappings, physical session and sampler baseline");
            SetNames(manager, "   ", "Updated router");
            check(!Button(manager, "SaveProfileButton").IsEnabled && !Button(manager, "AssignNetworkButton").IsEnabled &&
                fixture.Profiles.GetProfile(fixture.First.Id)!.TelcoName == "Alpha Updated",
                "Whitespace-only names disable saving and mapping without modifying the persisted profile");
            SelectProfile(manager, fixture.Second.Id);
            SelectProfile(manager, fixture.First.Id);

            Sql(fixture.Store.DatabasePath, "CREATE TRIGGER fail_profile_edit BEFORE UPDATE ON ConnectionProfiles BEGIN SELECT RAISE(ABORT, 'owned profile save failure'); END;");
            SetNames(manager, "Failed edit remains local", "Draft router");
            Click(manager, "SaveProfileButton");
            check(fixture.Profiles.GetProfile(fixture.First.Id)!.TelcoName == "Alpha Updated" &&
                SelectedProfile(manager)!.Id == fixture.First.Id &&
                Control<TextBox>(manager, "TelcoNameBox").Text == "Failed edit remains local" &&
                Text(manager, "MessageText").Contains("Could not", StringComparison.OrdinalIgnoreCase),
                "A failed profile save retains the selected row and draft fields and shows an actionable error");
            Sql(fixture.Store.DatabasePath, "DROP TRIGGER fail_profile_edit;");
            SelectProfile(manager, fixture.Second.Id);
            SelectProfile(manager, fixture.First.Id);

            SetNames(manager, "Unchanged local draft", "Draft device");
            fixture.Identity.Set(NetworkIdentityAvailability.Disconnected, null);
            Wait(() => fixture.Monitoring.GetSnapshot().Connection.Status == ConnectionStatus.Disconnected);
            check(!Button(manager, "AssignNetworkButton").IsEnabled &&
                Control<TextBox>(manager, "TelcoNameBox").Text == "Unchanged local draft" &&
                Control<TextBox>(manager, "DeviceNameBox").Text == "Draft device",
                "Live disconnect disables mapping while preserving unsaved editor fields");
            fixture.Identity.Set(NetworkIdentityAvailability.Unavailable, new NetworkIdentity("WiFi", "", "", AdapterId));
            Wait(() => fixture.Monitoring.GetSnapshot().Connection.Status == ConnectionStatus.ConnectedIdentityUnavailable);
            check(!Button(manager, "AssignNetworkButton").IsEnabled &&
                Control<TextBox>(manager, "TelcoNameBox").Text == "Unchanged local draft",
                "Unavailable live identity keeps mapping disabled without replacing the current draft");
            fixture.Identity.Set(NetworkIdentityAvailability.Available, Identity("PROFILES_LATEST", "Latest hotspot"));
            Wait(() => fixture.Monitoring.GetSnapshot().Connection.Network?.NetworkIdentifier == "PROFILES_LATEST");
            SelectProfile(manager, fixture.Second.Id);
            check(Button(manager, "AssignNetworkButton").IsEnabled,
                "A newly recognized valid live network enables mapping for a clean saved selection");
            Click(manager, "AssignNetworkButton");
            Wait(() => fixture.Monitoring.GetSnapshot().Connection.Profile?.Id == fixture.Second.Id);
            check(fixture.Profiles.GetMapping("WiFi", "PROFILES_LATEST")?.ProfileId == fixture.Second.Id &&
                fixture.Profiles.GetMapping("WiFi", CurrentNetwork)?.ProfileId == fixture.First.Id && manager.IsVisible,
                "Map current connection uses the latest live identity, keeps prior mappings intact and leaves general management open");
            fixture.Identity.Set(NetworkIdentityAvailability.Available, Identity(CurrentNetwork, "Owned hotspot"));
            Wait(() => fixture.Monitoring.GetSnapshot().Connection.Network?.NetworkIdentifier == CurrentNetwork);
            fixture.SessionId = fixture.Monitoring.GetSnapshot().Session!.Id;
            fixture.Monitoring.SampleNow();
            fixture.Baseline = Baseline(fixture.Monitoring);
        }
        finally { manager.Close(); }
    }

    private static void CheckMappingsAndDelete(Fixture fixture, string root, Action<bool, string> check)
    {
        var manager = fixture.OpenManager();
        try
        {
            SelectProfile(manager, fixture.First.Id);
            var current = fixture.Profiles.GetMapping("WiFi", CurrentNetwork)!;
            var before = fixture.Profiles.GetMappings(fixture.First.Id).Count;
            Click(manager, "AssignNetworkButton");
            Pump();
            check(fixture.Profiles.GetMappings(fixture.First.Id).Count == before &&
                fixture.Profiles.GetMapping("WiFi", CurrentNetwork)!.Id == current.Id,
                "Mapping an already owned current network is idempotent and creates no duplicate mapping");
            SelectProfile(manager, fixture.Second.Id);
            var cancel = Confirm(manager, "AssignNetworkButton", false, root, "conflict-confirm");
            check(cancel.Seen && cancel.Action == "Move" && cancel.Message.Contains("Alpha Updated", StringComparison.Ordinal) &&
                cancel.Message.Contains("Beta ISP", StringComparison.Ordinal) &&
                (cancel.Message.Contains(CurrentNetwork, StringComparison.Ordinal) || cancel.Message.Contains("Owned hotspot", StringComparison.Ordinal)) &&
                fixture.Profiles.GetMapping("WiFi", CurrentNetwork)!.ProfileId == fixture.First.Id &&
                SelectedProfile(manager)!.Id == fixture.Second.Id,
                "Mapping to another profile opens a named Move confirmation and Cancel leaves ownership and selection unchanged");
            // Make this mapping the highest row ID so SQLite's ordinary INTEGER
            // PRIMARY KEY allocation can reuse it after the modal race deletes it.
            fixture.Profiles.RemoveMapping(current.Id, fixture.First.Id);
            current = fixture.Profiles.AssignMapping(fixture.First.Id, "WiFi", CurrentNetwork, "Owned hotspot");
            NetworkMapping? replacement = null;
            var rejectedMove = Confirm(manager, "AssignNetworkButton", true, beforeResponse: () =>
            {
                fixture.Profiles.RemoveMapping(current.Id, fixture.First.Id);
                replacement = fixture.Profiles.AssignMapping(fixture.First.Id, "WiFi", "PROFILES_REPLACEMENT", "Different hotspot");
            });
            var rejected = rejectedMove.Seen && replacement is not null && replacement.Id == current.Id &&
                fixture.Profiles.GetMapping("WiFi", CurrentNetwork) is null &&
                fixture.Profiles.GetMapping("WiFi", "PROFILES_REPLACEMENT")?.ProfileId == fixture.First.Id &&
                Text(manager, "MessageText").Contains("changed", StringComparison.OrdinalIgnoreCase);
            fixture.Profiles.RemoveMapping(replacement!.Id, fixture.First.Id);
            current = fixture.Profiles.AssignMapping(fixture.First.Id, "WiFi", CurrentNetwork, "Owned hotspot");
            check(rejected,
                "A mapping removed and replaced under the same SQLite row ID during confirmation cannot move an unrelated network");
            var move = Confirm(manager, "AssignNetworkButton", true);
            Wait(() => fixture.Monitoring.GetSnapshot().Connection.Profile?.Id == fixture.Second.Id);
            check(move.Seen && fixture.Profiles.GetMapping("WiFi", CurrentNetwork) is { } moved &&
                moved.Id == current.Id && moved.ProfileId == fixture.Second.Id &&
                !fixture.Profiles.GetMappings(fixture.First.Id).Any(mapping => mapping.Id == current.Id) &&
                fixture.Profiles.GetMappings(fixture.Second.Id).Count(mapping => mapping.Id == current.Id) == 1 &&
                fixture.Monitoring.GetSnapshot().Session!.Id == fixture.SessionId &&
                Text(fixture.Window, "AdapterProfileText") == "Beta ISP — Second router" &&
                ReferenceEquals(fixture.Baseline, Baseline(fixture.Monitoring)),
                "Confirming Move transfers the same unique mapping and refreshes Connections without resetting the session or sampler");

            var mappings = Control<ListBox>(manager, "MappingsList");
            mappings.SelectedItem = mappings.Items.OfType<NetworkMapping>().Single(mapping => mapping.NetworkIdentifier == "PROFILES_LATEST");
            check(Button(manager, "RemoveMappingButton").IsEnabled &&
                mappings.SelectedItem is NetworkMapping selected && selected.ProfileId == fixture.Second.Id,
                "Selecting a known mapping enables removal for that saved profile only");
            SavePreview(manager, root, "mapping-selected");
            Click(manager, "RemoveMappingButton");
            check(fixture.Profiles.GetMapping("WiFi", "PROFILES_LATEST") is null &&
                fixture.Profiles.GetMapping("WiFi", CurrentNetwork)?.ProfileId == fixture.Second.Id &&
                fixture.Profiles.GetMapping("WiFi", "PROFILES_OTHER")?.ProfileId == fixture.First.Id &&
                fixture.Monitoring.GetSnapshot().Session!.Id == fixture.SessionId,
                "Removing a selected inactive mapping preserves the active and unrelated profile mappings");
            mappings.SelectedItem = mappings.Items.OfType<NetworkMapping>().Single(mapping => mapping.Id == current.Id);
            Sql(fixture.Store.DatabasePath, "CREATE TRIGGER fail_mapping_remove BEFORE DELETE ON NetworkMappings BEGIN SELECT RAISE(ABORT, 'owned mapping remove failure'); END;");
            Click(manager, "RemoveMappingButton");
            check(fixture.Profiles.GetMapping("WiFi", CurrentNetwork)?.ProfileId == fixture.Second.Id &&
                mappings.SelectedItem is NetworkMapping unchanged && unchanged.Id == current.Id &&
                Text(manager, "MessageText").Contains("Could not", StringComparison.OrdinalIgnoreCase),
                "A failed mapping removal leaves the selected mapping and persisted ownership intact and displays an error");
            Sql(fixture.Store.DatabasePath, "DROP TRIGGER fail_mapping_remove;");
            var bytes = fixture.Monitoring.GetSnapshot().SessionDownloaded;
            Click(manager, "RemoveMappingButton");
            Wait(() => fixture.Monitoring.GetSnapshot().Connection.Status == ConnectionStatus.ConnectedUnassigned);
            check(fixture.Profiles.GetMapping("WiFi", CurrentNetwork) is null &&
                fixture.Monitoring.GetSnapshot().Session is { } unassigned && unassigned.Id == fixture.SessionId && unassigned.ProfileId is null &&
                fixture.Monitoring.GetSnapshot().SessionDownloaded == bytes &&
                Text(fixture.Window, "AdapterProfileText") == "Unassigned" &&
                !Button(manager, "RemoveMappingButton").IsEnabled && fixture.Profiles.GetProfile(fixture.Second.Id) is not null,
                "Removing the active mapping immediately makes Connections unassigned while preserving its physical session, profile row and counters");
            Click(manager, "AssignNetworkButton");
            Wait(() => fixture.Monitoring.GetSnapshot().Connection.Profile?.Id == fixture.Second.Id);

            SetNames(manager, "Unsaved delete draft", "Unsaved device");
            var canceled = Confirm(manager, "DeleteProfileButton", false, root, "delete-confirm");
            check(canceled.Seen && canceled.Action == "Delete" && canceled.Message.Contains("Beta ISP", StringComparison.Ordinal) &&
                fixture.Profiles.GetProfile(fixture.Second.Id) is not null &&
                fixture.Profiles.GetMapping("WiFi", CurrentNetwork)?.ProfileId == fixture.Second.Id &&
                Control<TextBox>(manager, "TelcoNameBox").Text == "Unsaved delete draft" &&
                Control<TextBox>(manager, "DeviceNameBox").Text == "Unsaved device" && SelectedProfile(manager)!.Id == fixture.Second.Id,
                "Delete confirmation names the saved profile and Cancel preserves its mappings, selection and unsaved editor draft");
            Sql(fixture.Store.DatabasePath, "CREATE TRIGGER fail_profile_delete BEFORE DELETE ON ConnectionProfiles BEGIN SELECT RAISE(ABORT, 'owned profile delete failure'); END;");
            Confirm(manager, "DeleteProfileButton", true);
            check(fixture.Profiles.GetProfile(fixture.Second.Id) is not null &&
                fixture.Profiles.GetMapping("WiFi", CurrentNetwork)?.ProfileId == fixture.Second.Id &&
                SelectedProfile(manager)!.Id == fixture.Second.Id &&
                Control<TextBox>(manager, "TelcoNameBox").Text == "Unsaved delete draft" &&
                Text(manager, "MessageText").Contains("Could not", StringComparison.OrdinalIgnoreCase),
                "A failed confirmed deletion rolls back mappings and retains the selected profile and draft with an error");
            Sql(fixture.Store.DatabasePath, "DROP TRIGGER fail_profile_delete;");
            fixture.Monitoring.Flush();
            var sessions = fixture.History.GetRecentSessions();
            var events = fixture.History.GetRecentEvents();
            var usage = fixture.Monitoring.GetSnapshot();
            var deleted = Confirm(manager, "DeleteProfileButton", true);
            Wait(() => fixture.Monitoring.GetSnapshot().Connection.Status == ConnectionStatus.ConnectedUnassigned);
            var remainingSessions = fixture.History.GetRecentSessions();
            var remainingEvents = fixture.History.GetRecentEvents();
            check(deleted.Seen && fixture.Profiles.GetProfile(fixture.Second.Id) is null &&
                fixture.Profiles.GetMappings(fixture.Second.Id).Count == 0 && fixture.Profiles.GetMapping("WiFi", CurrentNetwork) is null &&
                fixture.Monitoring.GetSnapshot().Session is { } active && active.Id == fixture.SessionId && active.ProfileId is null &&
                fixture.Monitoring.GetSnapshot().SessionDownloaded == usage.SessionDownloaded &&
                fixture.Monitoring.GetSnapshot().SessionUploaded == usage.SessionUploaded &&
                Text(fixture.Window, "AdapterProfileText") == "Unassigned",
                "Confirmed deletion removes the saved profile and mappings and refreshes the active connection without changing its session or counters");
            check(sessions.Count == remainingSessions.Count && sessions.All(old => remainingSessions.Any(currentSession =>
                    currentSession.Id == old.Id && currentSession.NetworkIdentifier == old.NetworkIdentifier &&
                    currentSession.StartedAtUtc == old.StartedAtUtc && currentSession.EndedAtUtc == old.EndedAtUtc &&
                    currentSession.EndReason == old.EndReason && currentSession.DownloadBytes == old.DownloadBytes &&
                    currentSession.UploadBytes == old.UploadBytes && (old.ProfileId != fixture.Second.Id || currentSession.ProfileId is null))) &&
                events.All(old => remainingEvents.Any(currentEvent => currentEvent.Id == old.Id && currentEvent.Message == old.Message &&
                    currentEvent.OccurredAtUtc == old.OccurredAtUtc && currentEvent.SessionId == old.SessionId &&
                    (old.ProfileId != fixture.Second.Id || currentEvent.ProfileId is null))),
                "Deleting a profile preserves historical session and event rows, timestamps and byte totals while detaching its nullable references");
            check(SelectedProfile(manager)?.Id != fixture.Second.Id &&
                !Control<ListBox>(manager, "ProfilesList").Items.OfType<ConnectionProfile>().Any(profile => profile.Id == fixture.Second.Id),
                "Successful deletion refreshes the profile list and selects a valid remaining editor state");
            check(Text(fixture.Window, "AdapterStatusText") == "Connected" && Text(fixture.Window, "Ipv4Text") == "192.0.2.10" &&
                Text(fixture.Window, "Ipv6Text") == "2001:db8::10" && Text(fixture.Window, "DefaultGatewayText") == "192.0.2.1" &&
                Text(fixture.Window, "DnsText") == "1.1.1.1, 8.8.8.8" && Text(fixture.Window, "LinkSpeedText") == "86 Mbps",
                "Profile mapping and deletion workflows preserve every Connections 001 network detail row");
        }
        finally { manager.Close(); }
    }

    private static void CheckLifecycle(Fixture fixture, Action<bool, string> check)
    {
        var snapshot = fixture.Monitoring.GetSnapshot();
        var baseline = Baseline(fixture.Monitoring);
        var captureCount = fixture.Counters.Captures;
        var timer = Field<System.Threading.Timer>(fixture.Monitoring, "_timer");
        var profileTimer = Field<System.Threading.Timer>(Field<ConnectionProfileMonitor>(fixture.Monitoring, "_profileMonitor"), "_refreshTimer");
        var icmpWorker = fixture.Icmp.Completion;
        var reachabilityWorker = fixture.Reachability.Completion;
        var handlers = SubscriberCount(fixture.Monitoring);
        var detached = true;
        for (var i = 0; i < 3; i++)
        {
            var manager = fixture.OpenManager();
            manager.Close();
            Pump();
            detached &= SubscriberCount(fixture.Monitoring) == handlers &&
                !Subscribers(fixture.Monitoring).Any(handler => ReferenceEquals(handler.Target, manager));
        }
        check(detached && !fixture.Window.OwnedWindows.OfType<ProfileManagerWindow>().Any(),
            "Repeated Profiles open and close removes all manager monitoring subscriptions and leaves no utility windows");
        check(ReferenceEquals(timer, Field<System.Threading.Timer>(fixture.Monitoring, "_timer")) &&
            ReferenceEquals(profileTimer, Field<System.Threading.Timer>(Field<ConnectionProfileMonitor>(fixture.Monitoring, "_profileMonitor"), "_refreshTimer")) &&
            ReferenceEquals(icmpWorker, fixture.Icmp.Completion) && ReferenceEquals(reachabilityWorker, fixture.Reachability.Completion) &&
            ReferenceEquals(baseline, Baseline(fixture.Monitoring)) && captureCount == fixture.Counters.Captures &&
            fixture.Monitoring.GetSnapshot().Session!.Id == snapshot.Session!.Id &&
            fixture.Monitoring.GetSnapshot().SessionDownloaded == snapshot.SessionDownloaded &&
            fixture.Monitoring.GetSnapshot().SessionUploaded == snapshot.SessionUploaded,
            "Opening and closing Profiles preserves existing worker and timer instances, sampler baseline, session identity and usage totals");
    }

    private static void CheckStandalone(string root, Action<bool, string> check)
    {
        var store = new UsageStore(System.IO.Path.Combine(root, "profiles-standalone"));
        var profiles = new ProfileStore(store);
        var only = profiles.CreateProfile("Only ISP", "Only router");
        profiles.AssignMapping(only.Id, "WiFi", "ONLY_NETWORK");
        var manager = Show(new ProfileManagerWindow(profiles));
        try
        {
            check(Button(manager, "AssignNetworkButton").IsVisible && !Button(manager, "AssignNetworkButton").IsEnabled,
                "Standalone profile management without a monitoring context clearly disables Map current connection");
            Confirm(manager, "DeleteProfileButton", true);
            check(profiles.GetProfiles().Count == 0 && Control<ListBox>(manager, "ProfilesList").Items.Count == 0 &&
                Control<ListBox>(manager, "ProfilesList").SelectedItem is null && Text(manager, "EditorStateText") == "New profile" &&
                Control<TextBox>(manager, "TelcoNameBox").Text == "" && Control<TextBox>(manager, "DeviceNameBox").Text == "" &&
                Control<ListBox>(manager, "MappingsList").Items.Count == 0 &&
                !Button(manager, "DeleteProfileButton").IsEnabled && !Button(manager, "RemoveMappingButton").IsEnabled,
                "Deleting the final profile enters a clean empty New profile state without stale mappings or destructive actions");
            SavePreview(manager, root, "empty-state");
            Click(manager, "NewProfileButton");
            SetNames(manager, "Unsaved ISP", "Unsaved router");
            check(!Button(manager, "DeleteProfileButton").IsEnabled && profiles.GetProfiles().Count == 0,
                "An unsaved new draft cannot invoke profile deletion or create an implicit saved row");
            var longTelco = "Long ISP " + new string('A', 170);
            var longDevice = "Long device " + new string('B', 160);
            SetNames(manager, longTelco, longDevice);
            Click(manager, "SaveProfileButton");
            profiles.AssignMapping(SelectedProfile(manager)!.Id, "WiFi", "Long network " + new string('C', 150));
            manager.Close();
            manager = Show(new ProfileManagerWindow(profiles));
            SavePreview(manager, root, "long-names");
            manager.Width = 620;
            manager.Height = 500;
            manager.UpdateLayout();
            Pump();
            var preview = Control<TextBlock>(manager, "PreviewText");
            var close = Button(manager, "CloseButton");
            var closePosition = close.TranslatePoint(new Point(), manager);
            var list = Control<ListBox>(manager, "ProfilesList");
            var editor = Control<TextBox>(manager, "TelcoNameBox");
            check(list.ActualWidth >= 150 && editor.ActualWidth >= 150 && preview.TextTrimming == TextTrimming.CharacterEllipsis &&
                preview.ToolTip?.ToString() == preview.Text && closePosition.X >= 0 && closePosition.Y >= 0 &&
                closePosition.X + close.ActualWidth <= manager.ActualWidth &&
                closePosition.Y + close.ActualHeight <= manager.ActualHeight &&
                ScrollsWithin(list).All(scroll => scroll.ScrollableWidth < 0.01) &&
                ScrollsWithin(Control<ListBox>(manager, "MappingsList")).All(scroll => scroll.ScrollableWidth < 0.01),
                "At 620 by 500, long profile and mapping names preserve the utility columns and Close action without horizontal list overflow");
            SavePreview(manager, root, "620x500-utility");
        }
        finally { manager.Close(); }
    }

    private sealed record ConfirmationResult(bool Seen, string Message, string Action);
    private static ConfirmationResult Confirm(ProfileManagerWindow owner, string trigger, bool accept,
        string? root = null, string? previewName = null, Action? beforeResponse = null)
    {
        var seen = false;
        var message = "";
        var action = "";
        Exception? error = null;
        var elapsed = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            var dialog = owner.OwnedWindows.OfType<ProfileConfirmationWindow>().SingleOrDefault();
            if (dialog is null)
            {
                if (elapsed.Elapsed <= TimeSpan.FromSeconds(5)) return;
                error = new TimeoutException("Profile confirmation did not appear.");
                timer.Stop();
                owner.Close();
                return;
            }
            timer.Stop();
            seen = true;
            dialog.ShowActivated = false;
            dialog.ShowInTaskbar = false;
            dialog.Opacity = 0;
            try
            {
                message = Text(dialog, "ConfirmationMessageText");
                action = Button(dialog, "ConfirmActionButton").Content?.ToString() ?? "";
                if (root is not null && previewName is not null) SavePreview(dialog, root, previewName);
                beforeResponse?.Invoke();
                Click(dialog, accept ? "ConfirmActionButton" : "CancelActionButton");
            }
            catch (Exception exception) { error = exception; }
            finally { if (dialog.IsVisible) dialog.Close(); }
        };
        timer.Start();
        try { Click(owner, trigger); }
        finally { timer.Stop(); }
        if (error is not null) throw new InvalidOperationException("Owned profile confirmation failed.", error);
        if (!seen) throw new InvalidOperationException("Expected profile confirmation was not shown.");
        Pump();
        return new(seen, message, action);
    }

    private static ProfileManagerWindow Show(ProfileManagerWindow manager)
    {
        manager.ShowActivated = false;
        manager.ShowInTaskbar = false;
        manager.Opacity = 0;
        manager.Show();
        manager.UpdateLayout();
        Pump();
        return manager;
    }
    private static void SelectProfile(ProfileManagerWindow manager, long id)
    {
        var list = Control<ListBox>(manager, "ProfilesList");
        list.SelectedItem = list.Items.OfType<ConnectionProfile>().Single(profile => profile.Id == id);
        manager.UpdateLayout();
        Pump();
    }
    private static ConnectionProfile? SelectedProfile(ProfileManagerWindow manager) => Control<ListBox>(manager, "ProfilesList").SelectedItem as ConnectionProfile;
    private static void SetNames(ProfileManagerWindow manager, string telco, string device)
    {
        Control<TextBox>(manager, "TelcoNameBox").Text = telco;
        Control<TextBox>(manager, "DeviceNameBox").Text = device;
        manager.UpdateLayout();
        Pump();
    }
    private static void Click(Window window, string name) => Button(window, name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static Button Button(Window window, string name) => Control<Button>(window, name);
    private static string Text(Window window, string name) => Control<TextBlock>(window, name).Text;
    private static T Control<T>(Window window, string name) where T : class => window.FindName(name) as T
        ?? throw new InvalidOperationException($"Missing profile check control {name}.");
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, PrivateInstance)!.GetValue(owner)!;
    private static object? Baseline(MonitoringService monitoring) => Field<NetworkSnapshot?>(monitoring, "_previousSnapshot");
    private static Delegate[] Subscribers(MonitoringService monitoring) =>
        (typeof(MonitoringService).GetField("Changed", PrivateInstance)!.GetValue(monitoring) as Delegate)?.GetInvocationList() ?? [];
    private static int SubscriberCount(MonitoringService monitoring) => Subscribers(monitoring).Length;
    private static IEnumerable<ScrollViewer> ScrollsWithin(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scroll) yield return scroll;
            foreach (var nested in ScrollsWithin(child)) yield return nested;
        }
    }
    private static NetworkIdentity Identity(string identifier, string name) => new("WiFi", identifier, name, AdapterId);
    private static void Wait(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Owned profile UI did not reach its expected state.");
            Pump();
        }
        Pump();
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
    private static void Sql(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private static void SavePreview(Window window, string root, string name)
    {
        window.UpdateLayout();
        Pump();
        var content = (FrameworkElement)window.Content;
        var margin = content.Margin;
        var width = content.ActualWidth + margin.Left + margin.Right;
        var height = content.ActualHeight + margin.Top + margin.Bottom;
        var offset = VisualTreeHelper.GetOffset(content);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(content)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(offset.X, offset.Y, content.ActualWidth, content.ActualHeight),
                Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top
            }, null, new Rect(margin.Left, margin.Top, content.ActualWidth, content.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var path = System.IO.Path.Combine(root, "profile-management-previews", name + ".png");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = System.IO.File.Create(path);
        encoder.Save(output);
    }

    private sealed class Fixture : IDisposable
    {
        public UsageStore Store { get; }
        public ProfileStore Profiles { get; }
        public ConnectionHistoryStore History { get; }
        public ConnectionProfile First { get; }
        public ConnectionProfile Second { get; }
        public OwnedIdentity Identity { get; }
        public OwnedCounters Counters { get; }
        public IcmpMonitor Icmp { get; }
        public InternetReachabilityMonitor Reachability { get; }
        public MonitoringService Monitoring { get; }
        public TrayApplicationContext Context { get; }
        public MainWindow Window { get; }
        public long SessionId { get; set; }
        public object? Baseline { get; set; }
        public Fixture(string root)
        {
            Store = new UsageStore(root);
            Profiles = new ProfileStore(Store);
            History = new ConnectionHistoryStore(Store);
            First = Profiles.CreateProfile("Alpha ISP", "First router");
            Second = Profiles.CreateProfile("Beta ISP", "Second router");
            Profiles.AssignMapping(First.Id, "WiFi", CurrentNetwork, "Owned hotspot");
            Profiles.AssignMapping(First.Id, "WiFi", "PROFILES_OTHER", "Other hotspot");
            Identity = new OwnedIdentity(new(NetworkIdentityAvailability.Available,
                ProfileManagementChecks.Identity(CurrentNetwork, "Owned hotspot"), null));
            Counters = new OwnedCounters();
            Icmp = new IcmpMonitor(new OwnedPing());
            Reachability = TestReachability.Create();
            Monitoring = new MonitoringService(Store, Identity,
                vpnVerifier: new VpnRouteVerifier(new VpnDetectionService(new NoTunnel()), new NoRoutes()),
                networkMonitor: Counters, icmpMonitor: Icmp, reachabilityMonitor: Reachability);
            Context = new TrayApplicationContext(Application.Current, Monitoring,
                dashboardFactory: service => new MainWindow(service, new ReadOnlyStartup())
                    { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 }, shutdownAction: () => { });
            Context.Start();
            Field<System.Threading.Timer>(Monitoring, "_timer").Change(Timeout.Infinite, Timeout.Infinite);
            Window = Context.Dashboard!;
            Field<DispatcherTimer>(Window, "_timer").Stop();
            Wait(() => Monitoring.GetSnapshot().Connection.Profile?.Id == First.Id && Monitoring.GetSnapshot().Session is not null);
            Control<TabControl>(Window, "MainNavigation").SelectedItem = Control<TabItem>(Window, "ConnectionsTab");
            Monitoring.SampleNow();
            Counters.Advance(2048, 1024);
            Monitoring.SampleNow();
            Monitoring.Flush();
            Window.RefreshDashboard();
            Pump();
            SessionId = Monitoring.GetSnapshot().Session!.Id;
            Baseline = ProfileManagementChecks.Baseline(Monitoring);
        }
        public ProfileManagerWindow OpenManager()
        {
            var manager = new ProfileManagerWindow(Monitoring) { Owner = Window };
            // Match MainWindow's existing mutation notification wiring while the
            // injected monitor supplies live availability to the utility itself.
            manager.ProfilesChanged += (_, _) => Monitoring.RefreshRecognition();
            return Show(manager);
        }
        public void Dispose() => Context.Dispose();
    }
    private sealed class OwnedIdentity(NetworkIdentityResult initial) : INetworkIdentityService
    {
        private volatile NetworkIdentityResult _current = initial;
        public event EventHandler? NetworkChanged;
        public NetworkIdentityResult GetCurrentNetwork() => _current;
        public void Set(NetworkIdentityAvailability availability, NetworkIdentity? network)
        {
            _current = new(availability, network, availability == NetworkIdentityAvailability.Available ? null : "Owned identity unavailable.");
            NetworkChanged?.Invoke(this, EventArgs.Empty);
        }
        public void Dispose() { }
    }
    private sealed class OwnedCounters : INetworkMonitor
    {
        private NetworkSnapshot _snapshot = new(1000, 500, DateTimeOffset.UtcNow);
        private int _captures;
        public int Captures => Volatile.Read(ref _captures);
        public IReadOnlyList<AdapterInfo> GetActiveAdapters() =>
            [new(AdapterId, "Owned Wi-Fi", "Deterministic profile adapter", "Wireless80211", 86_000_000, "192.0.2.10", true)];
        public NetworkSnapshot? Capture(string adapterId)
        {
            Interlocked.Increment(ref _captures);
            return adapterId == AdapterId ? _snapshot : null;
        }
        public void Advance(long downloaded, long uploaded) =>
            _snapshot = new(_snapshot.BytesReceived + downloaded, _snapshot.BytesSent + uploaded, _snapshot.Timestamp.AddSeconds(1));
        public NetworkAdapterDetails? GetAdapterDetails(string adapterId) => adapterId == AdapterId
            ? new(AdapterId, OperationalStatus.Up, [IPAddress.Parse("192.0.2.10"), IPAddress.Parse("2001:db8::10")],
                [IPAddress.Parse("192.0.2.1")], [IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8")], 86_000_000) : null;
    }
    private sealed class OwnedPing : IPingSource
    {
        public Task<IcmpPingReply> SendAsync(IPAddress target, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new IcmpPingReply(IPStatus.Success, 12));
    }
    private sealed class ReadOnlyStartup : IWindowsStartupRegistration
    {
        public bool IsEnabled() => false;
        public void Enable() => throw new InvalidOperationException("Profile checks do not write startup registration.");
        public void Disable() => throw new InvalidOperationException("Profile checks do not write startup registration.");
    }
    private sealed class NoTunnel : IVpnInterfaceSource { public VpnInterfaceInventory Read() => new([], []); }
    private sealed class NoRoutes : IVpnRouteSource
    {
        public VpnRouteInventory Read(AddressFamily family) => new(family, [], Availability: IpFamilyAvailability.Unavailable);
    }
}
