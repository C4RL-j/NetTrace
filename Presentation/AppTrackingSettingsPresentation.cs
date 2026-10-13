using NetTrace.Services;

namespace NetTrace.Presentation;

public sealed record AppTrackingSettingsPresentation(string StatusText, bool IsInstalled, bool CanUninstall,
    string Error = "", bool CanClearHistory = false, bool StorageAvailable = false,
    bool UpdateAvailable = false, bool CanUpdate = false, string? InstalledVersion = null, string? BundledVersion = null)
{
    public static AppTrackingSettingsPresentation Checking { get; } = new("Checking...", false, false);

    public static AppTrackingSettingsPresentation FromState(AppTrackingServiceState state,
        bool supportsUninstall, string error = "") => state switch
    {
        AppTrackingServiceState.Running => new("Running", true, supportsUninstall, error),
        AppTrackingServiceState.Stopped => new("Stopped", true, supportsUninstall, error),
        AppTrackingServiceState.Missing => new("Not installed", false, false, error),
        AppTrackingServiceState.Uninstalling => new("Uninstalling...", true, false, error),
        AppTrackingServiceState.Installing => new("Installing...", false, false, error),
        AppTrackingServiceState.Updating => new("Updating...", true, false, error),
        _ => new("Unavailable", false, false, error)
    };
}
