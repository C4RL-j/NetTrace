using NetTrace.Models;

namespace NetTrace.Services;

public interface INetworkMonitor
{
    IReadOnlyList<AdapterInfo> GetActiveAdapters();
    NetworkSnapshot? Capture(string adapterId);
    NetworkAdapterDetails? GetAdapterDetails(string adapterId) => null;
}
