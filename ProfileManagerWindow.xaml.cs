using System.Windows;
using System.Windows.Controls;
using NetTrace.Models;
using NetTrace.Services;

namespace NetTrace;

public partial class ProfileManagerWindow : Window
{
    private readonly ProfileStore _store;
    private readonly NetworkIdentity? _network;
    private ConnectionProfile? _selected;

    public event EventHandler? ProfilesChanged;

    public ProfileManagerWindow(ProfileStore store, NetworkIdentity? network = null)
    {
        if (network is not null && !network.HasValidIdentifier)
            throw new ArgumentException("Windows did not provide a valid network identity for assignment.", nameof(network));
        _store = store;
        _network = network;
        InitializeComponent();
        if (network is not null)
        {
            HeadingText.Text = "Assign a connection profile";
            AssignmentText.Text = $"{network.DisplayName}\nChoose an existing profile, or create a new one.";
            AssignNetworkButton.Visibility = Visibility.Visible;
        }
        LoadProfiles();
    }

    private void LoadProfiles(long? selectedId = null)
    {
        var profiles = _store.GetProfiles();
        ProfilesList.ItemsSource = profiles;
        ProfilesList.SelectedItem = profiles.FirstOrDefault(profile => profile.Id == selectedId)
            ?? profiles.FirstOrDefault();
        if (ProfilesList.SelectedItem is null)
            PrepareNewProfile();
    }

    private void ProfilesList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = ProfilesList.SelectedItem as ConnectionProfile;
        if (_selected is null) return;
        TelcoNameBox.Text = _selected.TelcoName;
        DeviceNameBox.Text = _selected.DeviceName;
        SaveProfileButton.Content = "Save profile";
        AssignNetworkButton.Content = "Assign this network";
        MessageText.Text = "";
        try
        {
            MappingsList.ItemsSource = _store.GetMappings(_selected.Id);
        }
        catch (Exception exception)
        {
            MappingsList.ItemsSource = Array.Empty<NetworkMapping>();
            MessageText.Text = $"Could not load network mappings: {exception.Message}";
        }
        UpdatePreview();
    }

    private void PrepareNewProfile()
    {
        _selected = null;
        ProfilesList.SelectedItem = null;
        TelcoNameBox.Text = "";
        DeviceNameBox.Text = "";
        SaveProfileButton.Content = _network is null ? "Create profile" : "Create and assign";
        AssignNetworkButton.IsEnabled = false;
        MappingsList.ItemsSource = Array.Empty<NetworkMapping>();
        MessageText.Text = "";
        UpdatePreview();
        TelcoNameBox.Focus();
    }

    private void NewProfile_OnClick(object sender, RoutedEventArgs e) => PrepareNewProfile();

    private void ProfileFields_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (PreviewText is not null) UpdatePreview();
    }

    private void UpdatePreview()
    {
        var telco = TelcoNameBox.Text.Trim();
        var device = DeviceNameBox.Text.Trim();
        PreviewText.Text = $"{(telco.Length == 0 ? "Telco / ISP" : telco)} — {(device.Length == 0 ? "Device" : device)}";
        PreviewText.ToolTip = PreviewText.Text;
        SaveProfileButton.IsEnabled = telco.Length > 0 && device.Length > 0;
        AssignNetworkButton.IsEnabled = _selected is not null
            && _selected.TelcoName == telco && _selected.DeviceName == device;
        AssignNetworkButton.ToolTip = AssignNetworkButton.IsEnabled ? null : "Save profile edits before assigning this network.";
    }

    private void SaveProfile_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var createAndAssign = _selected is null && _network is not null;
            var saved = _selected is not null
                ? _store.UpdateProfile(_selected.Id, TelcoNameBox.Text, DeviceNameBox.Text)
                : createAndAssign
                    ? _store.CreateAndAssignProfile(TelcoNameBox.Text, DeviceNameBox.Text,
                        _network!.NetworkType, _network.NetworkIdentifier, _network.FriendlyName)
                    : _store.CreateProfile(TelcoNameBox.Text, DeviceNameBox.Text);
            LoadProfiles(saved.Id);
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
            MessageText.Text = "Profile saved.";
            if (createAndAssign) DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageText.Text = $"Could not save the profile: {exception.Message}";
        }
    }

    private void AssignNetwork_OnClick(object sender, RoutedEventArgs e)
    {
        if (_network is null || _selected is null) return;
        try
        {
            _store.AssignMapping(_selected.Id, _network.NetworkType, _network.NetworkIdentifier, _network.FriendlyName);
            ProfilesChanged?.Invoke(this, EventArgs.Empty);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageText.Text = $"Could not assign this network: {exception.Message}";
        }
    }
}
