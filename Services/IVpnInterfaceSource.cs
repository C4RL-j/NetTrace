using NetTrace.Models;

namespace NetTrace.Services;

public interface IVpnInterfaceSource
{
    VpnInterfaceInventory Read();
}
