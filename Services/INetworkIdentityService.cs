using NetTrace.Models;

namespace NetTrace.Services;

public interface INetworkIdentityService : IDisposable
{
    event EventHandler? NetworkChanged;
    NetworkIdentityResult GetCurrentNetwork();
}
