using NetTrace.Models;

namespace NetTrace.Services;

// A passive observation of the selected interface's current Wi-Fi association.
// Implementations must not scan for nearby networks or alter adapter settings.
public interface IWifiRadioSource
{
    WifiRadioSnapshot Read(string adapterId);
}
