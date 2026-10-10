using System.Windows;

namespace NetTrace.Resources;

public partial class StatusColors : ResourceDictionary
{
    public StatusColors()
    {
        InitializeComponent();
        // Alias the loaded brush instances explicitly: deferred StaticResource aliases
        // do not retain the merged dictionary's lookup scope at runtime.
        this["PositiveBrush"] = this["StatusPositiveBrush"];
        this["CautionBrush"] = this["StatusCautionBrush"];
        this["WarningBrush"] = this["StatusNegativeBrush"];
        this["AccentBrush"] = this["StatusInfoBrush"];
        this["SubtleBrush"] = this["StatusNeutralBrush"];
    }
}
