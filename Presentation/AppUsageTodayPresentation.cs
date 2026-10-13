using System.Globalization;
using AppUsageIpc;
using NetTrace.Utils;

namespace NetTrace.Presentation;

public enum AppUsageTodayState { Loading, Available, Empty, Unavailable }

public sealed record AppUsageApplicationRow(string ApplicationIdentity, string DisplayName,
    string ExecutableName, string DownloadText, string UploadText, string TotalText)
{
    public bool HasExecutableName => !string.IsNullOrWhiteSpace(ExecutableName);
}

public sealed record AppUsageTodayPresentation(AppUsageTodayState State, string Message, string Detail,
    string TrackedAppsText, string DownloadText, string UploadText, string LogicalTotalText,
    IReadOnlyList<AppUsageApplicationRow> Applications)
{
    public static AppUsageTodayPresentation Loading { get; } = Status(AppUsageTodayState.Loading, "Loading app usage...");
    public static AppUsageTodayPresentation Unavailable { get; } = Status(AppUsageTodayState.Unavailable, "App tracking unavailable.");

    public static AppUsageTodayPresentation FromResult(IpcResult<AppUsageQueryResult> result)
    {
        if (result.State != IpcState.Connected || result.Value is not { Range: AppUsageRange.Today } usage
            || usage.Availability != AppUsageAvailability.Available) return Unavailable;
        // IPC has already aggregated and ordered these rows. Preserve its identity
        // and System/Unknown/NetMod values without any physical reconciliation.
        var applications = usage.Applications.Select(application => new AppUsageApplicationRow(
            application.ApplicationIdentity, application.DisplayName, application.ExecutableName ?? "",
            ByteFormatter.Format(application.DownloadBytes), ByteFormatter.Format(application.UploadBytes),
            ByteFormatter.Format(application.TotalBytes))).ToArray();
        var empty = applications.Length == 0;
        return new(empty ? AppUsageTodayState.Empty : AppUsageTodayState.Available,
            empty ? "No app network usage recorded today." : "", "",
            usage.TrackedApplicationCount.ToString(CultureInfo.CurrentCulture),
            ByteFormatter.Format(usage.TotalDownloadBytes), ByteFormatter.Format(usage.TotalUploadBytes),
            ByteFormatter.Format(usage.LogicalTotalBytes), Array.AsReadOnly(applications));
    }

    private static AppUsageTodayPresentation Status(AppUsageTodayState state, string message) =>
        new(state, message, "", "—", "—", "—", "—", Array.Empty<AppUsageApplicationRow>());
}
