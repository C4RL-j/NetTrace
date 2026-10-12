using NetTrace.Utils;

namespace NetTrace.Presentation;

public sealed record LiveSpeedPresentation(string ByteRateText, string MbpsText)
{
    public static LiveSpeedPresentation Create(double bytesPerSecond)
    {
        // Both values describe this one raw sampler measurement, with no
        // conversion through either rounded display string.
        var rate = ByteRateFormatter.Normalize(bytesPerSecond);
        return new(ByteRateFormatter.FormatNormalized(rate), ByteFormatter.FormatRate(rate));
    }
}
