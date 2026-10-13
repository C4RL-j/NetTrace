using AppUsageIpc;

namespace NetTrace.Presentation;

// Owned by the application lifetime; no selection is written to storage.
public sealed class AppUsageRangeSelection
{
    public AppUsageRange SelectedRange { get; internal set; } = AppUsageRange.Today;
}
