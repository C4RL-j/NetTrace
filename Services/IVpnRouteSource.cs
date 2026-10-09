using System.Net.Sockets;
using NetTrace.Models;

namespace NetTrace.Services;

public interface IVpnRouteSource
{
    VpnRouteInventory Read(AddressFamily addressFamily);
}
