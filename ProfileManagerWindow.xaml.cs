using System.Windows;
using System.Windows.Controls;
using NetTrace.Models;
using NetTrace.Services;

namespace NetTrace;

public partial class ProfileManagerWindow : Window
{
    private readonly ProfileStore _store;
    private readonly NetworkIdentity? _initialNetwork;
    private readonly MonitoringService? _monitoring;
    private readonly bool _assignmentWorkflow;
    private ConnectionProfile? _selected;
    private bool _loadingProfiles;
    private volatile bool _closed;

    public event EventHandler? ProfilesChanged;

    public ProfileManagerWindow(MonitoringService monitoring, NetworkIdentity? network = null)
        : this((monitoring ?? throw new ArgumentNullException(nameof(monitoring))).Profiles, network, monitoring)
    {
    }

    public ProfileManagerWindow(ProfileStore store, NetworkIdentity? network = null, MonitoringService? monitoring = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (network is not null && !network.HasValidIdentifier)
            throw new ArgumentException("Windows did not provide a valid network identity for assignment.", nameof(network));
        _store = store;
        _initialNetwork = network;
        _monitoring = monitoring;
        _assignmentWorkflow = network is not null;
        InitializeComponent();
        if (_assignmentWorkflow) HeadingText.Text = "Assign a connection profile";
        LoadProfiles();
        Closed += OnClosed;
        if (_monitoring is not null) _monitoring.Changed += Monitoring_OnChanged;
        RefreshConnectionAvailability();
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        _closed = true;
        if (_monitoring is not null) _monitoring.Changed -= Monitoring_OnChanged;
        Closed -= OnClosed;
    }

    private void Monitoring_OnChanged(object? sender, EventArgs args)
    {
        if (_closed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_closed) RefreshConnectionAvailability();
        }));
    }

    private NetworkIdentity? CurrentNetwork()
    {
        if (_monitoring is null) return _initialNetwork;
        var snapshot = _monitoring.GetSnapshot();
        return !snapshot.IsSuspended && snapshot.IsRunning &&
            snapshot.Connection.Status is ConnectionStatus.ConnectedRecognized or ConnectionStatus.ConnectedUnassigned &&
            snapshot.Connection.Network is { HasValidIdentifier: true } network ? network : null;
    }

    private void RefreshConnectionAvailability()
    {
        var network = CurrentNetwork();
        AssignmentText.Text = network is null
            ? "Current connection unavailable. Select a profile to edit, or create a new one."
            : _assignmentWorkflow
                ? $"{network.DisplayName}\nChoose an existing profile, or create a new one."
                : $"Current connection: {network.DisplayName}";
        AssignmentText.ToolTip = AssignmentText.Text;
        UpdateActionAvailability(network);
    }

    private void LoadProfiles(long? selectedId = null)
    {
        var profiles = _store.GetProfiles();
        var selected = profiles.FirstOrDefault(profile => profile.Id == selectedId) ?? profiles.FirstOrDefault();
        _loadingProfiles = true;
        try
        {
            ProfilesList.ItemsSource = profiles;
            ProfilesList.SelectedItem = selected;
        }
        finally { _loadingProfiles = false; }
        if (selected is null) PrepareNewProfile();
        else LoadSelectedProfile(selected);
    }

    private void ProfilesList_OnSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loadingProfiles) return;
        if (ProfilesList.SelectedItem is ConnectionProfile profile)
        {
            try { LoadSelectedProfile(profile); }
            catch (Exception exception)
            {
                MappingsList.ItemsSource = Array.Empty<NetworkMapping>();
                ShowMessage($"Could not load network mappings: {exception.Message}");
                UpdatePreview();
            }
        }
    }

    private void LoadSelectedProfile(ConnectionProfile profile)
    {
        _selected = profile;
        TelcoNameBox.Text = profile.TelcoName;
        DeviceNameBox.Text = profile.DeviceName;
        EditorStateText.Text = "Editing profile";
        SaveProfileButton.Content = "Save profile";
        ShowMessage("");
        LoadMappings();
        UpdatePreview();
    }

    private void LoadMappings()
    {
        MappingsList.ItemsSource = _selected is null ? Array.Empty<NetworkMapping>() : _store.GetMappings(_selected.Id);
        UpdateActionAvailability(CurrentNetwork());
    }

    private void PrepareNewProfile()
    {
        _selected = null;
        _loadingProfiles = true;
        try { ProfilesList.SelectedItem = null; }
        finally { _loadingProfiles = false; }
        TelcoNameBox.Text = "";
        DeviceNameBox.Text = "";
        EditorStateText.Text = "New profile";
        SaveProfileButton.Content = _assignmentWorkflow ? "Create and assign" : "Create profile";
        MappingsList.ItemsSource = Array.Empty<NetworkMapping>();
        ShowMessage("");
        UpdatePreview();
        TelcoNameBox.Focus();
    }

    private void NewProfile_OnClick(object sender, RoutedEventArgs args) => PrepareNewProfile();

    private void Close_OnClick(object sender, RoutedEventArgs args) => Close();

    private void ProfileFields_OnTextChanged(object sender, TextChangedEventArgs args)
    {
        if (PreviewText is not null) UpdatePreview();
    }

    private void UpdatePreview()
    {
        var telco = TelcoNameBox.Text.Trim();
        var device = DeviceNameBox.Text.Trim();
        PreviewText.Text = $"{(telco.Length == 0 ? "Telco / ISP" : telco)} — {(device.Length == 0 ? "Device" : device)}";
        PreviewText.ToolTip = PreviewText.Text;
        UpdateActionAvailability(CurrentNetwork());
    }

    private bool HasUnsavedEdits() => _selected is null ||
        _selected.TelcoName != TelcoNameBox.Text.Trim() || _selected.DeviceName != DeviceNameBox.Text.Trim();

    private void UpdateActionAvailability(NetworkIdentity? network)
    {
        SaveProfileButton.IsEnabled = TelcoNameBox.Text.Trim().Length > 0 && DeviceNameBox.Text.Trim().Length > 0 &&
            !(_assignmentWorkflow && _selected is null && network is null);
        AssignNetworkButton.IsEnabled = network is not null && _selected is not null && !HasUnsavedEdits();
        AssignNetworkButton.ToolTip = _selected is null ? "Save a profile before mapping the current connection." :
            HasUnsavedEdits() ? "Save profile edits before mapping the current connection." :
            network is null ? "A current network identity is unavailable." : $"Map {network.DisplayName} to {_selected.DisplayName}.";
        DeleteProfileButton.IsEnabled = _selected is not null;
        RemoveMappingButton.IsEnabled = _selected is not null &&
            MappingsList.SelectedItem is NetworkMapping mapping && mapping.ProfileId == _selected.Id;
    }

    private void SaveProfile_OnClick(object sender, RoutedEventArgs args)
    {
        try
        {
            var createAndAssign = _selected is null && _assignmentWorkflow;
            var network = createAndAssign ? CurrentNetwork() ??
                throw new InvalidOperationException("The current connection is unavailable. Reconnect before creating and assigning a profile.") : null;
            var saved = _selected is not null
                ? _store.UpdateProfile(_selected.Id, TelcoNameBox.Text, DeviceNameBox.Text)
                : createAndAssign
                    ? _store.CreateAndAssignProfile(TelcoNameBox.Text, DeviceNameBox.Text,
                        network!.NetworkType, network.NetworkIdentifier, network.FriendlyName)
                    : _store.CreateProfile(TelcoNameBox.Text, DeviceNameBox.Text);
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
            ReloadAfterWrite(saved.Id, "Profile saved.");
            if (createAndAssign) DialogResult = true;
        }
        catch (Exception exception) { ShowMessage($"Could not save the profile: {exception.Message}"); }
    }

    private void AssignNetwork_OnClick(object sender, RoutedEventArgs args)
    {
        if (_selected is null || HasUnsavedEdits())
        {
            ShowMessage("Save the profile and any edits before mapping the current connection.");
            return;
        }
        var network = CurrentNetwork();
        if (network is null)
        {
            RefreshConnectionAvailability();
            ShowMessage("The current connection is unavailable. Reconnect before mapping it.");
            return;
        }
        try
        {
            var mapping = _store.GetMapping(network.NetworkType, network.NetworkIdentifier);
            if (mapping is not null && mapping.ProfileId != _selected.Id)
            {
                var owner = _store.GetProfile(mapping.ProfileId) ??
                    throw new InvalidOperationException("The existing mapping's profile no longer exists. Try mapping again.");
                var target = _store.GetProfile(_selected.Id) ??
                    throw new InvalidOperationException("The selected profile no longer exists. Select another profile.");
                var confirmation = new ProfileConfirmationWindow("Move network mapping",
                    $"“{network.DisplayName}” is mapped to “{owner.DisplayName}”.\n\nMove this mapping to “{target.DisplayName}”?", "Move") { Owner = this };
                if (confirmation.ShowDialog() != true) return;
                if (!SameNetwork(network, CurrentNetwork()))
                    throw new InvalidOperationException("The current connection changed while confirming. Check the current network and map it again.");
                var latestMapping = _store.GetMapping(network.NetworkType, network.NetworkIdentifier);
                if (latestMapping is null || latestMapping.Id != mapping.Id || latestMapping.ProfileId != mapping.ProfileId)
                    throw new InvalidOperationException("This network mapping changed while confirming. Check its current assignment and map it again.");
                _store.MoveMapping(mapping.Id, mapping.ProfileId, target.Id);
            }
            else _store.AssignMapping(_selected.Id, network.NetworkType, network.NetworkIdentifier, network.FriendlyName);
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
            ReloadMappingsAfterWrite("Current connection mapped.");
            if (_assignmentWorkflow) DialogResult = true;
        }
        catch (Exception exception) { ShowMessage($"Could not map the current connection: {exception.Message}"); }
    }

    private void MappingsList_OnSelectionChanged(object sender, SelectionChangedEventArgs args) =>
        UpdateActionAvailability(CurrentNetwork());

    private void RemoveMapping_OnClick(object sender, RoutedEventArgs args)
    {
        if (_selected is null || MappingsList.SelectedItem is not NetworkMapping mapping || mapping.ProfileId != _selected.Id) return;
        try
        {
            _store.RemoveMapping(mapping.Id, _selected.Id);
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
            ReloadMappingsAfterWrite("Network mapping removed.");
        }
        catch (Exception exception) { ShowMessage($"Could not remove the mapping: {exception.Message}"); }
    }

    private void DeleteProfile_OnClick(object sender, RoutedEventArgs args)
    {
        if (_selected is null) return;
        var selected = _selected;
        var confirmation = new ProfileConfirmationWindow("Delete connection profile",
            $"Delete “{selected.DisplayName}”?\n\nIts saved network mappings will be removed. Existing connection history will be retained.", "Delete") { Owner = this };
        if (confirmation.ShowDialog() != true) return;
        try
        {
            _store.DeleteProfile(selected.Id);
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
            ReloadAfterWrite(null, "Profile deleted.");
        }
        catch (Exception exception) { ShowMessage($"Could not delete the profile: {exception.Message}"); }
    }

    private void ReloadAfterWrite(long? selectedId, string successMessage)
    {
        try
        {
            LoadProfiles(selectedId);
            ShowMessage(successMessage);
        }
        catch (Exception exception) { ShowMessage($"{successMessage} Could not refresh profiles: {exception.Message}"); }
    }

    private void ReloadMappingsAfterWrite(string successMessage)
    {
        try
        {
            LoadMappings();
            ShowMessage(successMessage);
        }
        catch (Exception exception) { ShowMessage($"{successMessage} Could not refresh mappings: {exception.Message}"); }
    }

    private void ShowMessage(string message)
    {
        MessageText.Text = message;
        MessageText.ToolTip = message;
    }

    private static bool SameNetwork(NetworkIdentity expected, NetworkIdentity? current) => current is not null &&
        expected.NetworkType == current.NetworkType && expected.NetworkIdentifier == current.NetworkIdentifier &&
        string.Equals(expected.AdapterId, current.AdapterId, StringComparison.OrdinalIgnoreCase);
}
