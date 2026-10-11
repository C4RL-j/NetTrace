namespace NetTrace.Utils;

// Standard primary-channel centers, without inferring a band from an ambiguous
// channel number. Unsupported frequencies retain their measurement but no channel.
public static class WifiRadioFrequency
{
    public static (string? Band, int? Channel) Classify(int frequencyMHz)
    {
        if (frequencyMHz == 2484) return ("2.4 GHz", 14);
        if (frequencyMHz is >= 2412 and <= 2472 && (frequencyMHz - 2412) % 5 == 0)
            return ("2.4 GHz", (frequencyMHz - 2407) / 5);
        if (frequencyMHz == 5935) return ("6 GHz", 2);
        if (frequencyMHz is >= 5955 and <= 7115 && (frequencyMHz - 5955) % 20 == 0)
            return ("6 GHz", (frequencyMHz - 5950) / 5);
        if (frequencyMHz > 5000 && (frequencyMHz - 5000) % 5 == 0)
        {
            var channel = (frequencyMHz - 5000) / 5;
            if (channel is >= 36 and <= 64 && channel % 4 == 0 ||
                channel is >= 100 and <= 144 && channel % 4 == 0 ||
                channel is >= 149 and <= 177 && (channel - 149) % 4 == 0 ||
                channel is 32 or 34 or 38 or 42 or 46)
                return ("5 GHz", channel);
        }
        return (null, null);
    }
}
