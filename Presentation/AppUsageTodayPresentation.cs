using System.Globalization;
using System.ComponentModel;
using System.Windows.Media;
using AppUsageIpc;
using NetTrace.Services;
using NetTrace.Utils;

namespace NetTrace.Presentation;

public enum AppUsageTodayState { Loading, Available, Empty, Unavailable, Missing, Stopped, Installing }

public sealed record AppUsageApplicationRow(string ApplicationIdentity, string DisplayName,
    string ExecutableName, string DownloadText, string UploadText, string TotalText) : INotifyPropertyChanged
{
    private ImageSource? _icon;
    public string? ExecutablePath { get; init; }
    public AppUsageIconKind IconKind { get; init; } =
        string.Equals(ApplicationIdentity, "system", StringComparison.OrdinalIgnoreCase) ? AppUsageIconKind.System
        : string.Equals(ApplicationIdentity, "unknown", StringComparison.OrdinalIgnoreCase) ? AppUsageIconKind.Unknown
        : AppUsageIconKind.Application;
    public ImageSource Icon => _icon ?? AppUsageIconCache.Default.GetFallback(IconKind);
    public bool HasExecutableName => IconKind == AppUsageIconKind.Application
        && !string.Equals(ApplicationIdentity, "system", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(ApplicationIdentity, "unknown", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(ExecutableName)
        && !string.Equals(DisplayName.Trim(), ExecutableName.Trim(), StringComparison.OrdinalIgnoreCase);

    public event PropertyChangedEventHandler? PropertyChanged;

    // Called by the view on its dispatcher. Await retains that context while
    // the shared cache performs filesystem and native icon work in the background.
    public async Task LoadIconAsync(IAppUsageIconCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        SetIcon(cache.GetFallback(IconKind));
        ImageSource icon;
        try { icon = await cache.GetIconAsync(ExecutablePath, IconKind); }
        catch (Exception) { icon = cache.GetFallback(IconKind); }
        SetIcon(icon);
    }

    private void SetIcon(ImageSource icon)
    {
        var previous = Icon;
        _icon = icon;
        if (ReferenceEquals(previous, icon)) return;
        PropertyChanged?.Invoke(this, new(nameof(Icon)));
    }
}

public sealed record AppUsageTodayPresentation(AppUsageTodayState State, string Message, string Detail,
    string TrackedAppsText, string DownloadText, string UploadText, string LogicalTotalText,
    IReadOnlyList<AppUsageApplicationRow> Applications, string SetupError = "")
{
    public static AppUsageTodayPresentation Loading { get; } = Status(AppUsageTodayState.Loading, "Loading app usage...");
    public static AppUsageTodayPresentation Unavailable { get; } = Status(AppUsageTodayState.Unavailable, "App tracking unavailable.");
    public static AppUsageTodayPresentation Stopped { get; } = Status(AppUsageTodayState.Stopped,
        "App tracking is stopped.", "The app tracking service is not running.");
    public static AppUsageTodayPresentation Installing { get; } = Status(AppUsageTodayState.Installing,
        "Installing app tracking...", "Complete the administrator prompt to continue.");
    public static AppUsageTodayPresentation SetupRequired(string error = "") => Status(AppUsageTodayState.Missing,
        "App tracking is not installed.", "Track network usage by application in the background.", error);

    public static AppUsageTodayPresentation Cleared(AppUsageRange range) => new(AppUsageTodayState.Empty,
        EmptyMessage(range), "", "0", ByteFormatter.Format(0), ByteFormatter.Format(0), ByteFormatter.Format(0),
        Array.Empty<AppUsageApplicationRow>());

    public static AppUsageTodayPresentation FromResult(IpcResult<AppUsageQueryResult> result,
        AppUsageRange expectedRange = AppUsageRange.Today)
    {
        if (!Enum.IsDefined(expectedRange) || result.State != IpcState.Connected || result.Value is not { } usage
            || usage.Range != expectedRange
            || usage.Availability != AppUsageAvailability.Available) return Unavailable;
        // IPC has already aggregated and ordered these rows. Preserve its identity
        // and System/Unknown/NetMod values without any physical reconciliation.
        var applications = usage.Applications.Select(application =>
        {
            var kind = application.IsSystem || string.Equals(application.ApplicationIdentity, "system", StringComparison.OrdinalIgnoreCase)
                ? AppUsageIconKind.System
                : application.IsUnknown || string.Equals(application.ApplicationIdentity, "unknown", StringComparison.OrdinalIgnoreCase)
                    ? AppUsageIconKind.Unknown : AppUsageIconKind.Application;
            var executableName = application.ExecutableName?.Trim() ?? "";
            var title = kind switch
            {
                AppUsageIconKind.System => "System",
                AppUsageIconKind.Unknown => "Unknown / Unattributed",
                _ => !string.IsNullOrWhiteSpace(application.DisplayName) ? application.DisplayName.Trim()
                    : executableName.Length > 0 ? executableName : "Unknown / Unattributed"
            };
            return new AppUsageApplicationRow(application.ApplicationIdentity, title, executableName,
                ByteFormatter.Format(application.DownloadBytes), ByteFormatter.Format(application.UploadBytes),
                ByteFormatter.Format(application.TotalBytes))
            { ExecutablePath = application.ExecutablePath, IconKind = kind };
        }).ToArray();
        var empty = applications.Length == 0;
        return new(empty ? AppUsageTodayState.Empty : AppUsageTodayState.Available,
            empty ? EmptyMessage(expectedRange) : "", "",
            usage.TrackedApplicationCount.ToString(CultureInfo.CurrentCulture),
            ByteFormatter.Format(usage.TotalDownloadBytes), ByteFormatter.Format(usage.TotalUploadBytes),
            ByteFormatter.Format(usage.LogicalTotalBytes), Array.AsReadOnly(applications));
    }

    private static string EmptyMessage(AppUsageRange range) => range switch
    {
        AppUsageRange.SevenDays => "No app network usage recorded in the last 7 days.",
        AppUsageRange.ThirtyDays => "No app network usage recorded in the last 30 days.",
        _ => "No app network usage recorded today."
    };

    private static AppUsageTodayPresentation Status(AppUsageTodayState state, string message, string detail = "", string error = "") =>
        new(state, message, detail, "—", "—", "—", "—", Array.Empty<AppUsageApplicationRow>(), error);
}
